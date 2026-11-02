using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// L2CAP connection-oriented channels as an alternative data path. GATT stays the default: the host still
    /// advertises and serves GATT (for discovery and the PSM), and the link falls back to GATT whenever the
    /// L2CAP channel fails. Keep only if it measures better in the P0 radio spike. Untested on hardware.
    /// </summary>
    public enum BleL2capOption : byte
    {
        Off = 0,
        Prefer = 1,
    }

    public sealed class BleOptions
    {
        public BleL2capOption L2cap = BleL2capOption.Off;
        public int HeartbeatIntervalMicros = 500000;
        /// <summary>Courts not heard from for this long drop off the nearby list.</summary>
        public int PeerStaleMicros = 6000000;
        public int PermissionTimeoutMicros = 60000000;
        /// <summary>Upper bound on native poll calls per frame.</summary>
        public int MaxPollBatches = 16;
        /// <summary>Optional session log (see <see cref="BleSessionLogFiles.DefaultPath"/>).</summary>
        public BleSessionLog Log;
    }

    /// <summary>
    /// Bluetooth Low Energy transport for iPhone-Android cross-play. Host = GATT peripheral advertising a court
    /// code; guest = central scanning by service UUID. INPUT packets ride write-without-response/notify, CONTROL
    /// messages are fragmented over write-with-response/indicate. Everything surfaces on the main thread inside
    /// <see cref="Poll"/>. In the Editor it reports <see cref="TransportState.Failed"/> (see <see cref="FailureReason"/>).
    /// </summary>
    public sealed class BleTransport : ITransport
    {
        private enum Pending : byte { None, Host, Scan, Join }

        private readonly IBleBackend _backend;
        private readonly BleOptions _options;
        private readonly Func<long> _clock;
        private readonly int _mainThread;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private readonly List<long> _peerSeen = new List<long>();
        private readonly Queue<TransportState> _queuedStates = new Queue<TransportState>();
        private readonly BleReassembler _reassembler = new BleReassembler();
        private readonly BleHeartbeat _heartbeat = new BleHeartbeat();
        private readonly SpscRing _inputRing = new SpscRing(16, 64);
        private readonly byte[] _frag = new byte[BleProtocol.MaxPayload];
        private readonly byte[] _hb = new byte[32];
        private readonly byte[] _tx = new byte[64];
        private byte[] _rx = new byte[64];
        private byte[] _pollBuffer = new byte[4096];

        private TransportState _state = TransportState.Idle;
        private TransportError _error = TransportError.None;
        private TransportStats _stats;
        private bool _canHost;
        private bool _inPoll;
        private bool _disposed;
        private byte _txSeq;
        private long _inputSends;
        private long _localDrops;
        private int _controlRefusals;
        private Pending _pending;
        private long _permissionSince;
        private string _pendingCode;
        private string _pendingName;
        private PeerInfo _pendingPeer;
        private string _failureReason;

        public BleTransport(BleOptions options = null, Func<long> clockMicros = null, IBleBackend backend = null)
        {
            _options = options ?? new BleOptions();
            _clock = clockMicros ?? DefaultClock();
            _mainThread = Thread.CurrentThread.ManagedThreadId;
            _heartbeat.IntervalMicros = _options.HeartbeatIntervalMicros;
            _stats.MaxPayload = BleProtocol.DefaultPayload;
            _backend = backend ?? CreatePlatformBackend();
            if (!_backend.Supported)
            {
                _failureReason = _backend.UnsupportedReason;
                return;
            }
            try
            {
                _backend.Init(_options.L2cap == BleL2capOption.Prefer);
                _canHost = _backend.CanHost();
            }
            catch (Exception e)
            {
                _failureReason = "The Bluetooth bridge failed to start (" + e.GetType().Name + ").";
                Fail(TransportError.Internal);
            }
        }

        public event Action<TransportChannel, byte[], int> OnReceive;
        public event Action<TransportState> OnStateChanged;

        public string Name => "BLE";
        public TransportState State => _state;
        public TransportError Error => _error;
        public TransportStats Stats => _stats;
        public bool CanHost => _canHost;
        public IReadOnlyList<PeerInfo> Peers => _peers;

        /// <summary>Why the transport failed or is unsupported, in words for the lobby UI.</summary>
        public string FailureReason => _failureReason;

        /// <summary>"ios", "android" or "editor".</summary>
        public string Platform => _backend.Platform;

        /// <summary>
        /// In mixed pairs the Android phone should host: the reported iOS-Android bug hits Android phones that scan.
        /// The lobby uses this to preselect Host Court.
        /// </summary>
        public bool PrefersHosting => _canHost && _backend.Platform == BleProtocol.PlatformNameAndroid;

        public int FramingErrors => _reassembler.Errors;
        public int ControlRefusals => _controlRefusals;
        public int HeartbeatSamples => _heartbeat.Samples;

        // ------------------------------------------------------------------ commands

        public void Host(string courtCode, string playerName)
        {
            ThrowIfDisposed();
            string code = BleAdvert.NormalizeCourtCode(courtCode);
            if (code == null) throw new ArgumentException("court code must be 4 characters A-Z or 0-9", nameof(courtCode));
            if (!CheckSupported()) return;
            _pendingCode = code;
            _pendingName = playerName ?? string.Empty;
            Begin(Pending.Host);
        }

        public void StartScan()
        {
            ThrowIfDisposed();
            if (!CheckSupported()) return;
            if (_state == TransportState.Connected || _state == TransportState.Connecting || _state == TransportState.Hosting)
            {
                _options.Log?.Line("scan ignored while " + _state);
                return;
            }
            Begin(Pending.Scan);
        }

        public void StopScan()
        {
            if (_disposed) return;
            if (_pending == Pending.Scan) _pending = Pending.None;
            if (!_backend.Supported) return;
            _backend.StopScan();
            if (_state == TransportState.Scanning) SetState(TransportState.Idle, TransportError.None);
        }

        public void Join(PeerInfo peer)
        {
            ThrowIfDisposed();
            if (string.IsNullOrEmpty(peer.Address)) throw new ArgumentException("peer has no address", nameof(peer));
            if (!CheckSupported()) return;
            _pendingPeer = peer;
            Begin(Pending.Join);
        }

        public void Disconnect()
        {
            if (_disposed) return;
            _pending = Pending.None;
            if (_backend.Supported) _backend.Disconnect();
            ResetLink();
            if (_state != TransportState.Failed) SetState(TransportState.Idle, TransportError.None);
        }

        public void Send(TransportChannel channel, byte[] data, int offset, int count)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || count < 0 || offset + count > data.Length) throw new ArgumentOutOfRangeException(nameof(count));
            if (_disposed) return;

            if (channel == TransportChannel.Input)
            {
                if (count > _tx.Length || count > _stats.MaxPayload)
                    throw new ArgumentException("INPUT packets must fit one BLE packet (" + _stats.MaxPayload + " bytes)", nameof(count));
                if (_state != TransportState.Connected) return; // nothing to send to yet; not a loss
                Interlocked.Increment(ref _inputSends);
                if (!_inputRing.TryWrite(data, offset, count))
                {
                    Interlocked.Increment(ref _localDrops);
                    return;
                }
                if (Thread.CurrentThread.ManagedThreadId == _mainThread) FlushInput();
                return;
            }

            if (Thread.CurrentThread.ManagedThreadId != _mainThread)
                throw new InvalidOperationException("CONTROL messages must be sent from the main thread");
            if (count < 1 || count > BleProtocol.MaxControlMessage)
                throw new ArgumentOutOfRangeException(nameof(count), "CONTROL messages are 1.." + BleProtocol.MaxControlMessage + " bytes");
            byte type = data[offset];
            if (type == BleProtocol.HeartbeatPing || type == BleProtocol.HeartbeatPong)
                throw new ArgumentException("CONTROL message types 0xFE and 0xFF are reserved for the transport heartbeat", nameof(data));
            if (_state != TransportState.Connected) return;
            SendControlInternal(data, offset, count);
        }

        // ------------------------------------------------------------------ pump

        public void Poll()
        {
            if (_disposed || _inPoll) return;
            _inPoll = true;
            try
            {
                long now = _clock();
                while (_queuedStates.Count > 0)
                {
                    TransportState s = _queuedStates.Dequeue();
                    OnStateChanged?.Invoke(s);
                    if (_disposed) return;
                }
                if (_pending != Pending.None) CheckPermissions(now);
                FlushInput();

                for (int batch = 0; batch < _options.MaxPollBatches; batch++)
                {
                    int n = _backend.Poll(ref _pollBuffer);
                    if (n <= 0) break;
                    Dispatch(_pollBuffer, n, now);
                    if (_disposed) return;
                }

                if (_state == TransportState.Connected)
                {
                    if (_heartbeat.Due(now))
                    {
                        int len = _heartbeat.WritePing(_hb, now);
                        SendControlInternal(_hb, 0, len);
                    }
                    FlushInput();
                }
                ExpirePeers(now);
                UpdateStats();
                if (_state == TransportState.Connected) _options.Log?.Sample(_stats);
            }
            finally
            {
                _inPoll = false;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_backend.Supported) _backend.Disconnect();
                _backend.Dispose();
            }
            catch (Exception e)
            {
                _options.Log?.Line("shutdown error " + e.GetType().Name);
            }
            OnReceive = null;
            OnStateChanged = null;
        }

        // ------------------------------------------------------------------ internals

        private void Begin(Pending op)
        {
            _pending = op;
            if (!BlePermissions.HasAll())
            {
                BlePermissions.Request();
                _permissionSince = _clock();
                _options.Log?.Line("requesting Bluetooth permissions");
                return; // resumes from Poll once the user answers
            }
            RunPending();
        }

        private void CheckPermissions(long now)
        {
            switch (BlePermissions.Check())
            {
                case BlePermissions.Status.Granted:
                    RunPending();
                    break;
                case BlePermissions.Status.Denied:
                    _pending = Pending.None;
                    _failureReason = "Bluetooth permission was denied. Allow Nearby devices for Dunk Duel in Settings.";
                    Fail(TransportError.PermissionDenied);
                    break;
                default:
                    if (now - _permissionSince > _options.PermissionTimeoutMicros)
                    {
                        BlePermissions.Abandon();
                        _pending = Pending.None;
                        _failureReason = "Bluetooth permission was not granted.";
                        Fail(TransportError.PermissionDenied);
                    }
                    break;
            }
        }

        private void RunPending()
        {
            Pending op = _pending;
            _pending = Pending.None;
            switch (op)
            {
                case Pending.Host:
                    {
                        if (!_canHost)
                        {
                            _failureReason = "This phone cannot advertise over Bluetooth LE. Join a court instead.";
                            Fail(TransportError.AdvertisingUnsupported);
                            return;
                        }
                        ResetLink();
                        byte platform = _backend.Platform == BleProtocol.PlatformNameIos ? BleProtocol.PlatformIos : BleProtocol.PlatformAndroid;
                        byte[] name = Encoding.UTF8.GetBytes(BleAdvert.BuildLocalName(_pendingCode, _pendingName));
                        byte[] sd = BleAdvert.BuildServiceData(_pendingCode, _pendingName, platform);
                        _failureReason = null;
                        _backend.Host(name, sd);
                        SetState(TransportState.Hosting, TransportError.None);
                        _options.Log?.Line("hosting court " + _pendingCode);
                        break;
                    }
                case Pending.Scan:
                    _peers.Clear();
                    _peerSeen.Clear();
                    _failureReason = null;
                    _backend.StartScan();
                    SetState(TransportState.Scanning, TransportError.None);
                    break;
                case Pending.Join:
                    ResetLink();
                    _failureReason = null;
                    _backend.Join(_pendingPeer.Address);
                    SetState(TransportState.Connecting, TransportError.None);
                    _options.Log?.Line("joining " + BleAdvert.ShortHash(_pendingPeer.Address) + " court " + _pendingPeer.CourtCode);
                    break;
            }
        }

        private bool CheckSupported()
        {
            if (_backend.Supported) return true;
            _failureReason = _backend.UnsupportedReason;
            Fail(TransportError.Internal);
            return false;
        }

        private void Dispatch(byte[] buf, int len, long now)
        {
            int cursor = 0;
            while (BleRecords.Next(buf, len, ref cursor, out BleRecord r))
            {
                switch (r.Kind)
                {
                    case BleProtocol.RecInput:
                        OnInputRecord(buf, r);
                        break;
                    case BleProtocol.RecControl:
                        OnControlRecord(buf, r, now);
                        break;
                    case BleProtocol.RecState:
                        OnStateRecord(buf, r);
                        break;
                    case BleProtocol.RecPeer:
                        OnPeerRecord(buf, r, now);
                        break;
                    case BleProtocol.RecLink:
                        if (BleRecords.TryParseLink(buf, r, out int payload, out int interval))
                        {
                            _stats.MaxPayload = Math.Max(BleProtocol.DefaultPayload, Math.Min(BleProtocol.MaxPayload, payload));
                            _stats.ConnectionIntervalMicros = interval;
                            _options.Log?.Line("link payload=" + _stats.MaxPayload + (interval > 0 ? " interval=" + interval + "us" : string.Empty));
                        }
                        break;
                    case BleProtocol.RecCanHost:
                        if (r.Length >= 1) _canHost = buf[r.Offset] != 0;
                        break;
                    case BleProtocol.RecDropped:
                        if (BleRecords.TryParseCount(buf, r, out int dropped)) Interlocked.Add(ref _localDrops, dropped);
                        break;
                }
                if (_disposed) return;
            }
        }

        private void OnInputRecord(byte[] buf, in BleRecord r)
        {
            if (_state != TransportState.Connected) return;
            _stats.PacketsReceived++;
            _stats.BytesReceived += r.Length;
            if (r.Length > _rx.Length) _rx = new byte[r.Length];
            Buffer.BlockCopy(buf, r.Offset, _rx, 0, r.Length);
            OnReceive?.Invoke(TransportChannel.Input, _rx, r.Length);
        }

        private void OnControlRecord(byte[] buf, in BleRecord r, long now)
        {
            if (_state != TransportState.Connected) return;
            _stats.PacketsReceived++;
            _stats.BytesReceived += r.Length;
            int m = _reassembler.Accept(buf, r.Offset, r.Length);
            if (m <= 0) return;
            byte[] msg = _reassembler.Message;
            if (msg[0] == BleProtocol.HeartbeatPing)
            {
                int n = BleHeartbeat.WritePong(msg, m, _hb);
                if (n > 0) SendControlInternal(_hb, 0, n);
                return;
            }
            if (msg[0] == BleProtocol.HeartbeatPong)
            {
                _heartbeat.OnPong(msg, m, now);
                return;
            }
            OnReceive?.Invoke(TransportChannel.Control, msg, m);
        }

        private void OnStateRecord(byte[] buf, in BleRecord r)
        {
            if (!BleRecords.TryParseState(buf, r, out TransportState s, out TransportError e)) return;
            // A link-loss report that races a local Disconnect() is stale.
            if (s == TransportState.Disconnected && (_state == TransportState.Idle || _state == TransportState.Failed)) return;
            if (s == TransportState.Connected && _state != TransportState.Connected)
            {
                _reassembler.Reset();
                _heartbeat.Reset();
                _txSeq = 0;
            }
            if (s == TransportState.Failed && _failureReason == null) _failureReason = Describe(e);
            if (s == TransportState.Disconnected || s == TransportState.Failed) DrainInputRing();
            SetState(s, e);
        }

        private void OnPeerRecord(byte[] buf, in BleRecord r, long now)
        {
            if (!BleRecords.TryParsePeer(buf, r, out int rssi, out string address, out string localName, out int sdo, out int sdl)) return;
            if (!BleAdvert.TryParse(localName, buf, sdo, sdl, out string code, out string name, out string platform)) return;
            var info = new PeerInfo { Address = address, CourtCode = code, PlayerName = name, SignalDbm = rssi, Platform = platform };
            for (int i = 0; i < _peers.Count; i++)
            {
                if (_peers[i].Address == address)
                {
                    _peers[i] = info;
                    _peerSeen[i] = now;
                    return;
                }
            }
            _peers.Add(info);
            _peerSeen.Add(now);
            _options.Log?.Peer(info);
        }

        private void ExpirePeers(long now)
        {
            for (int i = _peers.Count - 1; i >= 0; i--)
            {
                if (now - _peerSeen[i] > _options.PeerStaleMicros)
                {
                    _peers.RemoveAt(i);
                    _peerSeen.RemoveAt(i);
                }
            }
        }

        private void SendControlInternal(byte[] data, int offset, int count)
        {
            int payload = Math.Min(_stats.MaxPayload, _frag.Length);
            int fragments = BleFraming.FragmentCount(count, payload);
            for (int i = 0; i < fragments; i++)
            {
                int len = BleFraming.WriteFragment(data, offset, count, payload, i, _txSeq, _frag);
                _txSeq = BleFraming.NextSeq(_txSeq);
                if (_backend.Send(TransportChannel.Control, _frag, 0, len))
                {
                    _stats.PacketsSent++;
                    _stats.BytesSent += len;
                }
                else
                {
                    _controlRefusals++;
                }
            }
        }

        private void FlushInput()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            int len;
            while ((len = _inputRing.TryRead(_tx)) >= 0)
            {
                if (_state != TransportState.Connected) continue;
                if (_backend.Send(TransportChannel.Input, _tx, 0, len))
                {
                    _stats.PacketsSent++;
                    _stats.BytesSent += len;
                }
                else
                {
                    Interlocked.Increment(ref _localDrops);
                }
            }
        }

        private void DrainInputRing()
        {
            if (Thread.CurrentThread.ManagedThreadId != _mainThread) return;
            while (_inputRing.TryRead(_tx) >= 0) { }
        }

        private void UpdateStats()
        {
            _stats.RttMicros = _heartbeat.RttMicros;
            _stats.JitterMicros = _heartbeat.JitterMicros;
            long sends = Interlocked.Read(ref _inputSends);
            long drops = Interlocked.Read(ref _localDrops);
            _stats.LossPermille = sends > 0 ? (int)Math.Min(1000L, drops * 1000L / sends) : 0;
        }

        private void ResetLink()
        {
            _reassembler.Reset();
            _heartbeat.Reset();
            _txSeq = 0;
            _stats.MaxPayload = BleProtocol.DefaultPayload;
            _stats.ConnectionIntervalMicros = 0;
            DrainInputRing();
        }

        private void Fail(TransportError error)
        {
            _pending = Pending.None;
            ResetLink();
            SetState(TransportState.Failed, error);
        }

        private void SetState(TransportState state, TransportError error)
        {
            TransportError e = state == TransportState.Failed ? error : TransportError.None;
            if (_state == state && _error == e) return;
            _state = state;
            _error = e;
            _options.Log?.State(state, e);
            if (_inPoll) OnStateChanged?.Invoke(state);
            else _queuedStates.Enqueue(state);
        }

        private static string Describe(TransportError e)
        {
            switch (e)
            {
                case TransportError.RadioOff: return "Bluetooth is turned off.";
                case TransportError.PermissionDenied: return "Bluetooth permission was denied.";
                case TransportError.AdvertisingUnsupported: return "This phone cannot host over Bluetooth LE. Join a court instead.";
                case TransportError.ConnectFailed: return "Could not connect to that court.";
                case TransportError.ProtocolMismatch: return "That court is not running a compatible Dunk Duel build.";
                case TransportError.Internal: return "Bluetooth error.";
                default: return null;
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(BleTransport));
        }

        private static Func<long> DefaultClock()
        {
            Stopwatch sw = Stopwatch.StartNew();
            long f = Stopwatch.Frequency;
            return () =>
            {
                long t = sw.ElapsedTicks;
                return t / f * 1000000L + t % f * 1000000L / f;
            };
        }

        private static IBleBackend CreatePlatformBackend()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return new IosBleBackend();
#elif UNITY_ANDROID && !UNITY_EDITOR
            return new AndroidBleBackend();
#elif UNITY_EDITOR
            return new UnsupportedBleBackend("editor",
                "Bluetooth play is not available in the Unity Editor. Test on an iPhone or Android phone, or use the LAN transport.");
#else
            return new UnsupportedBleBackend("unsupported", "This platform has no Dunk Duel Bluetooth bridge. Use the LAN transport.");
#endif
        }
    }
}
