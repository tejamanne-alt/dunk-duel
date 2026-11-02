using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DunkDuel.Net;

namespace DunkDuel.Transport.LAN
{
    /// <summary>
    /// Same-Wi-Fi / hotspot fallback: UDP datagrams with broadcast discovery, a reliable ordered CONTROL channel
    /// (sequence numbers, acks, retransmits) and an unreliable INPUT channel. Sockets are non-blocking and pumped
    /// from <see cref="Poll"/> on the caller's thread, so events never fire on background threads.
    /// </summary>
    public sealed class LanTransport : ITransport
    {
        private const byte KindInput = 0;
        private const byte KindControl = 1;
        private const byte KindAck = 2;
        private const byte KindConnect = 0x10;
        private const byte KindWelcome = 0x11;
        private const byte KindBye = 0x12;
        private const byte KindPing = 0xFE;
        private const byte KindPong = 0xFF;
        private const int HeaderSize = 5; // kind u8, seq u16, ack u16
        private const string QueryMagic = "DDQ1";
        private const string HostMagic = "DDH1";
        private const long RetransmitMicros = 100000;
        private const long HeartbeatMicros = 500000;
        private const long DiscoveryMicros = 500000;
        private const long SilenceDisconnectMicros = 10000000;
        private const int MaxDatagram = 1200;

        private readonly int _port;
        private readonly int _discoveryPort;
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly byte[] _rx = new byte[2048];
        private readonly byte[] _tx = new byte[2048];
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private readonly Queue<TransportState> _stateEvents = new Queue<TransportState>();

        private Socket _data;
        private Socket _discovery;
        private EndPoint _peer;
        private string _courtCode = "";
        private string _playerName = "";
        private long _lastDiscovery;
        private long _lastHeartbeat;
        private long _lastReceive;
        private TransportStats _stats;

        // Reliable control channel.
        private ushort _ctrlSendSeq;
        private ushort _ctrlRecvNext;
        private readonly List<Pending> _unacked = new List<Pending>();
        private readonly SortedList<ushort, byte[]> _outOfOrder = new SortedList<ushort, byte[]>();
        private ushort _inputSendSeq;
        private int _inputLastSeq = -1;
        private long _inputGaps;
        private long _inputReceived;

        private struct Pending
        {
            public ushort Seq;
            public byte[] Datagram;
            public long LastSent;
        }

        public LanTransport(int port = 47700)
        {
            _port = port;
            _discoveryPort = port + 1;
            _stats.MaxPayload = MaxDatagram - HeaderSize;
        }

        public string Name => "LAN";
        public TransportState State { get; private set; }
        public TransportError Error { get; private set; }
        public TransportStats Stats => _stats;
        public bool CanHost => true;
        public IReadOnlyList<PeerInfo> Peers => _peers;

        public event Action<TransportChannel, byte[], int> OnReceive;
        public event Action<TransportState> OnStateChanged;

        private long Now => _clock.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency;

        // ------------------------------------------------------------------ lifecycle

        public void Host(string courtCode, string playerName)
        {
            CloseSockets();
            _courtCode = courtCode ?? "";
            _playerName = playerName ?? "";
            try
            {
                _data = Open(_port, false);
                _discovery = Open(_discoveryPort, true);
                SetState(TransportState.Hosting);
            }
            catch (SocketException)
            {
                Fail(TransportError.Internal);
            }
        }

        public void StartScan()
        {
            _peers.Clear();
            try
            {
                if (_data == null) _data = Open(0, true);
                _lastDiscovery = 0;
                SetState(TransportState.Scanning);
            }
            catch (SocketException)
            {
                Fail(TransportError.Internal);
            }
        }

        public void StopScan()
        {
            if (State == TransportState.Scanning) SetState(TransportState.Idle);
        }

        public void Join(PeerInfo peer)
        {
            try
            {
                if (_data == null) _data = Open(0, true);
                _peer = ParseEndPoint(peer.Address);
                ResetLink();
                SendRaw(KindConnect, 0, null, 0, 0);
                SetState(TransportState.Connecting);
                _lastReceive = Now;
            }
            catch (Exception e) when (e is SocketException || e is FormatException)
            {
                Fail(TransportError.ConnectFailed);
            }
        }

        public void Disconnect()
        {
            if (_peer != null && _data != null) SendRaw(KindBye, 0, null, 0, 0);
            _peer = null;
            SetState(TransportState.Disconnected);
        }

        public void Dispose()
        {
            CloseSockets();
        }

        private static Socket Open(int port, bool broadcast)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            if (broadcast) s.EnableBroadcast = true;
            s.Blocking = false;
            s.Bind(new IPEndPoint(IPAddress.Any, port));
            return s;
        }

        private void CloseSockets()
        {
            _data?.Close();
            _discovery?.Close();
            _data = null;
            _discovery = null;
        }

        private static EndPoint ParseEndPoint(string address)
        {
            int colon = address.LastIndexOf(':');
            if (colon <= 0) throw new FormatException("expected ip:port");
            return new IPEndPoint(IPAddress.Parse(address.Substring(0, colon)), int.Parse(address.Substring(colon + 1)));
        }

        private void ResetLink()
        {
            _ctrlSendSeq = 0;
            _ctrlRecvNext = 0;
            _unacked.Clear();
            _outOfOrder.Clear();
            _inputSendSeq = 0;
            _inputLastSeq = -1;
        }

        // ------------------------------------------------------------------ send

        public void Send(TransportChannel channel, byte[] data, int offset, int count)
        {
            if (State != TransportState.Connected || _peer == null) return;
            if (count > MaxDatagram - HeaderSize) throw new ArgumentException("message too large for LAN datagram");
            if (channel == TransportChannel.Input)
            {
                SendRaw(KindInput, _inputSendSeq++, data, offset, count);
                return;
            }
            ushort seq = _ctrlSendSeq++;
            byte[] dgram = Build(KindControl, seq, data, offset, count, out int len);
            var copy = new byte[len];
            Array.Copy(dgram, copy, len);
            _unacked.Add(new Pending { Seq = seq, Datagram = copy, LastSent = Now });
            Transmit(copy, len);
        }

        private void SendRaw(byte kind, ushort seq, byte[] data, int offset, int count)
        {
            byte[] dgram = Build(kind, seq, data, offset, count, out int len);
            Transmit(dgram, len);
        }

        private byte[] Build(byte kind, ushort seq, byte[] data, int offset, int count, out int len)
        {
            _tx[0] = kind;
            _tx[1] = (byte)seq;
            _tx[2] = (byte)(seq >> 8);
            _tx[3] = (byte)_ctrlRecvNext;
            _tx[4] = (byte)(_ctrlRecvNext >> 8);
            if (count > 0) Array.Copy(data, offset, _tx, HeaderSize, count);
            len = HeaderSize + count;
            return _tx;
        }

        private void Transmit(byte[] dgram, int len)
        {
            if (_peer == null || _data == null) return;
            try
            {
                _data.SendTo(dgram, 0, len, SocketFlags.None, _peer);
                _stats.PacketsSent++;
                _stats.BytesSent += len;
            }
            catch (SocketException)
            {
                // Transient (e.g. would-block or no route); reliability and redundancy cover it.
            }
        }

        // ------------------------------------------------------------------ poll

        public void Poll()
        {
            long now = Now;
            if (_discovery != null) PumpDiscovery();
            if (_data != null) PumpData(now);

            if (State == TransportState.Scanning && now - _lastDiscovery > DiscoveryMicros && _data != null)
            {
                _lastDiscovery = now;
                byte[] q = Encoding.ASCII.GetBytes(QueryMagic);
                try
                {
                    _data.SendTo(q, new IPEndPoint(IPAddress.Broadcast, _discoveryPort));
                }
                catch (SocketException)
                {
                }
            }

            if (State == TransportState.Connected)
            {
                for (int i = 0; i < _unacked.Count; i++)
                {
                    Pending p = _unacked[i];
                    if (now - p.LastSent >= RetransmitMicros)
                    {
                        Transmit(p.Datagram, p.Datagram.Length);
                        p.LastSent = now;
                        _unacked[i] = p;
                    }
                }
                if (now - _lastHeartbeat > HeartbeatMicros)
                {
                    _lastHeartbeat = now;
                    var stamp = new byte[8];
                    for (int i = 0; i < 8; i++) stamp[i] = (byte)(now >> (8 * i));
                    SendRaw(KindPing, 0, stamp, 0, 8);
                }
                if (now - _lastReceive > SilenceDisconnectMicros) SetState(TransportState.Disconnected);
            }

            while (_stateEvents.Count > 0)
            {
                // Dequeue before invoking: `?.` would skip evaluating the argument when nobody is subscribed.
                TransportState next = _stateEvents.Dequeue();
                OnStateChanged?.Invoke(next);
            }
        }

        private void PumpDiscovery()
        {
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    if (_discovery.Available == 0) return;
                    n = _discovery.ReceiveFrom(_rx, ref from);
                }
                catch (SocketException)
                {
                    return;
                }
                string msg = Encoding.ASCII.GetString(_rx, 0, n);
                if (msg == QueryMagic && State == TransportState.Hosting)
                {
                    string reply = HostMagic + "|" + _courtCode + "|" + Sanitize(_playerName) + "|" + _port;
                    byte[] b = Encoding.UTF8.GetBytes(reply);
                    try
                    {
                        _discovery.SendTo(b, from);
                    }
                    catch (SocketException)
                    {
                    }
                }
            }
        }

        private static string Sanitize(string s) => (s ?? "").Replace("|", "/");

        private void PumpData(long now)
        {
            while (true)
            {
                EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                int n;
                try
                {
                    if (_data.Available == 0) return;
                    n = _data.ReceiveFrom(_rx, ref from);
                }
                catch (SocketException)
                {
                    return;
                }
                if (n <= 0) continue;

                if (State == TransportState.Scanning)
                {
                    string msg = Encoding.UTF8.GetString(_rx, 0, n);
                    if (msg.StartsWith(HostMagic + "|", StringComparison.Ordinal)) AddPeer(msg, from);
                    continue;
                }
                if (n < HeaderSize) continue;
                byte kind = _rx[0];
                ushort seq = (ushort)(_rx[1] | (_rx[2] << 8));
                ushort ack = (ushort)(_rx[3] | (_rx[4] << 8));

                if (kind == KindConnect && State == TransportState.Hosting)
                {
                    _peer = from;
                    ResetLink();
                    SendRaw(KindWelcome, 0, null, 0, 0);
                    _lastReceive = now;
                    SetState(TransportState.Connected);
                    continue;
                }
                if (_peer == null || !SameEndPoint(from, _peer)) continue;
                _lastReceive = now;
                _stats.PacketsReceived++;
                _stats.BytesReceived += n;
                if (State == TransportState.Disconnected) SetState(TransportState.Connected);

                switch (kind)
                {
                    case KindWelcome:
                        if (State == TransportState.Connecting) SetState(TransportState.Connected);
                        break;
                    case KindBye:
                        _peer = null;
                        SetState(TransportState.Disconnected);
                        return;
                    case KindInput:
                        TrackInputSeq(seq);
                        DeliverEvent(TransportChannel.Input, HeaderSize, n - HeaderSize);
                        break;
                    case KindControl:
                        AckControl(seq);
                        ReceiveControl(seq, n);
                        break;
                    case KindAck:
                        RemoveAcked(seq);
                        break;
                    case KindPing:
                        SendRaw(KindPong, 0, _rx, HeaderSize, Math.Min(8, n - HeaderSize));
                        break;
                    case KindPong:
                        if (n >= HeaderSize + 8)
                        {
                            long sent = 0;
                            for (int i = 0; i < 8; i++) sent |= (long)_rx[HeaderSize + i] << (8 * i);
                            int rtt = (int)Math.Max(0, now - sent);
                            if (_stats.RttMicros == 0) _stats.RttMicros = rtt;
                            int dev = Math.Abs(rtt - _stats.RttMicros);
                            _stats.RttMicros += (rtt - _stats.RttMicros) / 8;
                            _stats.JitterMicros += (dev - _stats.JitterMicros) / 8;
                        }
                        break;
                }
                _ = ack;
            }
        }

        private static bool SameEndPoint(EndPoint a, EndPoint b) => a is IPEndPoint ia && b is IPEndPoint ib && ia.Address.Equals(ib.Address) && ia.Port == ib.Port;

        private void AddPeer(string msg, EndPoint from)
        {
            string[] parts = msg.Split('|');
            if (parts.Length < 4 || !int.TryParse(parts[3], out int port)) return;
            string address = ((IPEndPoint)from).Address + ":" + port;
            for (int i = 0; i < _peers.Count; i++) if (_peers[i].Address == address) return;
            _peers.Add(new PeerInfo { Address = address, CourtCode = parts[1], PlayerName = parts[2], SignalDbm = 0, Platform = "lan" });
        }

        private void TrackInputSeq(ushort seq)
        {
            _inputReceived++;
            if (_inputLastSeq >= 0)
            {
                int gap = (ushort)(seq - (ushort)_inputLastSeq) - 1;
                if (gap > 0 && gap < 1000) _inputGaps += gap;
            }
            _inputLastSeq = seq;
            long total = _inputReceived + _inputGaps;
            _stats.LossPermille = total > 0 ? (int)(_inputGaps * 1000 / total) : 0;
        }

        private void AckControl(ushort seq)
        {
            var tmp = new byte[0];
            byte[] dgram = Build(KindAck, seq, tmp, 0, 0, out int len);
            Transmit(dgram, len);
        }

        private void RemoveAcked(ushort seq)
        {
            for (int i = _unacked.Count - 1; i >= 0; i--)
            {
                if (_unacked[i].Seq == seq) _unacked.RemoveAt(i);
            }
        }

        private void ReceiveControl(ushort seq, int n)
        {
            short delta = (short)(seq - _ctrlRecvNext);
            if (delta < 0) return; // duplicate of something already delivered
            if (delta > 0)
            {
                if (!_outOfOrder.ContainsKey(seq))
                {
                    var copy = new byte[n - HeaderSize];
                    Array.Copy(_rx, HeaderSize, copy, 0, copy.Length);
                    _outOfOrder.Add(seq, copy);
                }
                return;
            }
            DeliverEvent(TransportChannel.Control, HeaderSize, n - HeaderSize);
            _ctrlRecvNext++;
            while (_outOfOrder.TryGetValue(_ctrlRecvNext, out byte[] next))
            {
                _outOfOrder.Remove(_ctrlRecvNext);
                OnReceive?.Invoke(TransportChannel.Control, next, next.Length);
                _ctrlRecvNext++;
            }
        }

        private void DeliverEvent(TransportChannel channel, int offset, int count)
        {
            var payload = new byte[count];
            Array.Copy(_rx, offset, payload, 0, count);
            OnReceive?.Invoke(channel, payload, count);
        }

        private void Fail(TransportError e)
        {
            Error = e;
            SetState(TransportState.Failed);
        }

        private void SetState(TransportState s)
        {
            if (State == s) return;
            State = s;
            _stateEvents.Enqueue(s);
        }
    }
}
