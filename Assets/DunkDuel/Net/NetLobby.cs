using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Net
{
    /// <summary>
    /// Host Court / Join Court: the session handshake, then a <see cref="RollbackSession"/>.
    /// Handshake: protocol version; sim version (build hash of sim code + data tables, mismatches refused);
    /// fighters; court; seed = host seed XOR guest seed; RTT samples choose the input delay (1-3).
    /// Pump <see cref="Update"/> every tick while not in a match; the session pumps the transport during play.
    /// </summary>
    public sealed class NetLobby : IDisposable
    {
        private const int RttSamplesNeeded = 5;

        private readonly SimTables _tables;
        private readonly ITransport _transport;
        private readonly INetClock _clock;
        private readonly NetConfig _cfg;
        private readonly ulong _seedPart;
        private readonly string _name;
        private readonly byte[] _buf = new byte[256];
        private readonly ulong _simVersion;

        private int _localFighter;
        private int _court;
        private int _roundsToWin = 2;
        private ulong _peerSeedPart;
        private string _peerName = "";
        private int _peerFighter = -1;
        private bool _peerHelloReceived;
        private bool _configReceived;
        private int _inputDelay;
        private readonly int[] _rtts = new int[RttSamplesNeeded];
        private int _rttCount;
        private uint _pingId;
        private long _lastPing;
        private int _rematchCount;
        private bool _localRematch;
        private bool _peerRematch;
        private IInputSource _pendingLocalInput;

        public NetLobby(SimTables tables, ITransport transport, INetClock clock, NetConfig config, ulong localSeedPart, string playerName)
        {
            _tables = tables;
            _transport = transport;
            _clock = clock;
            _cfg = config;
            _seedPart = localSeedPart;
            _name = playerName ?? "";
            _simVersion = SimBuildInfo.Version(tables);
            transport.OnReceive += OnReceive;
            transport.OnStateChanged += OnTransportState;
        }

        public LobbyState State { get; private set; }
        public NetFailure Failure { get; private set; }
        public bool IsHost { get; private set; }
        public LobbyConfig Config { get; private set; }
        public RollbackSession Session { get; private set; }
        public ITransport Transport => _transport;
        public int InputDelay => _inputDelay;
        public bool PeerWantsRematch => _peerRematch;
        public event Action<LobbyState> StateChanged;

        public void Dispose()
        {
            _transport.OnReceive -= OnReceive;
            _transport.OnStateChanged -= OnTransportState;
            Session?.Dispose();
        }

        // ------------------------------------------------------------------ user actions

        public void Host(string courtCode, int fighter, int court, int roundsToWin)
        {
            IsHost = true;
            _localFighter = fighter;
            _court = court;
            _roundsToWin = roundsToWin;
            _transport.Host(courtCode, _name);
            SetState(LobbyState.Hosting);
        }

        public void Join(PeerInfo peer, int fighter)
        {
            IsHost = false;
            _localFighter = fighter;
            _transport.Join(peer);
            SetState(LobbyState.Joining);
        }

        /// <summary>Called by the UI once the lobby is Ready: creates the session with the local input source.</summary>
        public void SetLocalInput(IInputSource input)
        {
            _pendingLocalInput = input;
            TryStartSession();
        }

        public void RequestRematch(int fighter)
        {
            _localFighter = fighter;
            _localRematch = true;
            var w = new MsgWriter(_buf, ControlType.Rematch);
            w.U8(fighter);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
            TryRematch();
        }

        public void Leave()
        {
            var w = new MsgWriter(_buf, ControlType.Quit);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
            _transport.Disconnect();
            SetState(LobbyState.Ended);
        }

        // ------------------------------------------------------------------ pump

        public void Update()
        {
            if (Session != null && State == LobbyState.InMatch)
            {
                if (Session.Failure != NetFailure.None) Fail(Session.Failure);
                else if (Session.Paused) SetState(LobbyState.Paused);
                else if (Session.State.Phase == Phase.MatchEnd) SetState(LobbyState.Ended);
                return;
            }
            if (Session != null && State == LobbyState.Paused)
            {
                if (Session.Failure != NetFailure.None) Fail(Session.Failure);
                else if (!Session.Paused) SetState(LobbyState.InMatch);
                return;
            }
            _transport.Poll();
            if (State == LobbyState.Handshaking && IsHost && _peerHelloReceived && !_configReceived)
            {
                long now = _clock.NowMicros;
                if (_rttCount < RttSamplesNeeded && now - _lastPing > 50000)
                {
                    _lastPing = now;
                    var w = new MsgWriter(_buf, ControlType.Ping);
                    w.U32(++_pingId);
                    w.U64((ulong)now);
                    _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
                }
                if (_rttCount >= RttSamplesNeeded) SendConfig();
            }
        }

        // ------------------------------------------------------------------ handshake

        private void OnTransportState(TransportState s)
        {
            switch (s)
            {
                case TransportState.Connected:
                    if (State == LobbyState.Hosting || State == LobbyState.Joining)
                    {
                        SetState(LobbyState.Handshaking);
                        SendHello();
                    }
                    break;
                case TransportState.Failed:
                    if (Session == null) Fail(NetFailure.TransportFailed);
                    break;
                case TransportState.Disconnected:
                    if (Session == null && State == LobbyState.Handshaking) Fail(NetFailure.PeerLeft);
                    break;
            }
        }

        private void SendHello()
        {
            var w = new MsgWriter(_buf, ControlType.Hello);
            w.U16(SimBuildInfo.ProtocolVersion);
            w.U64(_simVersion);
            w.U8(IsHost ? 0 : 1);
            w.U8(_localFighter);
            w.U8(_court);
            w.U8(_roundsToWin);
            w.U64(_seedPart);
            w.Str(_name, 16);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
        }

        private void SendConfig()
        {
            int median = Median();
            // One-way latency in ticks, rounded up, clamped to the adaptive 1-3 range.
            int oneWayTicks = (median / 2 + 16666) / 16667;
            _inputDelay = Math.Max(_cfg.MinInputDelay, Math.Min(_cfg.MaxInputDelay, Math.Max(oneWayTicks, _cfg.InputDelay - 1)));
            ulong seed = _seedPart ^ _peerSeedPart ^ (ulong)_rematchCount * 0x9E3779B97F4A7C15UL;
            Config = new LobbyConfig
            {
                HostFighter = _localFighter,
                GuestFighter = _peerFighter,
                Court = _court,
                RoundsToWin = _roundsToWin,
                Seed = seed,
                HostName = _name,
                GuestName = _peerName,
            };
            var w = new MsgWriter(_buf, ControlType.Config);
            w.U8(Config.HostFighter);
            w.U8(Config.GuestFighter);
            w.U8(Config.Court);
            w.U8(Config.RoundsToWin);
            w.U64(Config.Seed);
            w.U8(_inputDelay);
            w.Str(_name, 16);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
            _configReceived = true; // host side: config is now fixed
        }

        private int Median()
        {
            var tmp = new int[_rttCount];
            Array.Copy(_rtts, tmp, _rttCount);
            Array.Sort(tmp);
            return _rttCount == 0 ? 0 : tmp[_rttCount / 2];
        }

        private void OnReceive(TransportChannel channel, byte[] buf, int len)
        {
            if (channel != TransportChannel.Control || len < 1) return;
            if (Session != null && State != LobbyState.Ended)
            {
                // During a match the session owns the link; only lobby-level messages matter here.
                if ((ControlType)buf[0] == ControlType.Rematch) HandleRematch(buf, len);
                else if ((ControlType)buf[0] == ControlType.Quit) Fail(NetFailure.PeerLeft);
                return;
            }
            var r = new MsgReader(buf, len);
            try
            {
                switch (r.Type)
                {
                    case ControlType.Hello: HandleHello(ref r); break;
                    case ControlType.Config: HandleConfig(ref r); break;
                    case ControlType.Ready:
                        if (IsHost)
                        {
                            var w = new MsgWriter(_buf, ControlType.Start);
                            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
                            SetState(LobbyState.Ready);
                            TryStartSession();
                        }
                        break;
                    case ControlType.Start:
                        if (!IsHost)
                        {
                            SetState(LobbyState.Ready);
                            TryStartSession();
                        }
                        break;
                    case ControlType.Ping:
                        {
                            uint id = r.U32();
                            ulong t = r.U64();
                            var w = new MsgWriter(_buf, ControlType.Pong);
                            w.U32(id);
                            w.U64(t);
                            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
                            break;
                        }
                    case ControlType.Pong:
                        {
                            r.U32();
                            long sent = (long)r.U64();
                            if (_rttCount < RttSamplesNeeded) _rtts[_rttCount++] = (int)Math.Max(0, _clock.NowMicros - sent);
                            break;
                        }
                    case ControlType.Reject:
                        Fail((NetFailure)r.U8());
                        break;
                    case ControlType.Rematch:
                        HandleRematch(buf, len);
                        break;
                    case ControlType.Quit:
                        Fail(NetFailure.PeerLeft);
                        break;
                }
            }
            catch (FormatException)
            {
                Fail(NetFailure.ProtocolMismatch);
            }
        }

        private void HandleHello(ref MsgReader r)
        {
            int proto = r.U16();
            ulong version = r.U64();
            r.U8(); // role
            int fighter = r.U8();
            int court = r.U8();
            int rounds = r.U8();
            ulong seed = r.U64();
            string name = r.Str();
            if (proto != SimBuildInfo.ProtocolVersion)
            {
                Reject(NetFailure.ProtocolMismatch);
                return;
            }
            if (version != _simVersion)
            {
                Reject(NetFailure.VersionMismatch);
                return;
            }
            if (fighter >= _tables.FighterCount)
            {
                Reject(NetFailure.ProtocolMismatch);
                return;
            }
            _peerFighter = fighter;
            _peerSeedPart = seed;
            _peerName = name;
            _peerHelloReceived = true;
            if (!IsHost)
            {
                _court = court;
                _roundsToWin = rounds;
            }
        }

        private void HandleConfig(ref MsgReader r)
        {
            if (IsHost) return;
            int hf = r.U8();
            int gf = r.U8();
            int court = r.U8();
            int rounds = r.U8();
            ulong seed = r.U64();
            int delay = r.U8();
            string hostName = r.Str();
            _inputDelay = delay;
            Config = new LobbyConfig
            {
                HostFighter = hf,
                GuestFighter = gf,
                Court = court,
                RoundsToWin = rounds,
                Seed = seed,
                HostName = hostName,
                GuestName = _name,
            };
            _configReceived = true;
            var w = new MsgWriter(_buf, ControlType.Ready);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
        }

        private void Reject(NetFailure reason)
        {
            var w = new MsgWriter(_buf, ControlType.Reject);
            w.U8((int)reason);
            _transport.Send(TransportChannel.Control, _buf, 0, w.Pos);
            Fail(reason);
        }

        private void TryStartSession()
        {
            if (State != LobbyState.Ready || _pendingLocalInput == null || Session != null) return;
            LobbyConfig c = Config;
            var setup = new MatchSetup
            {
                Fighter0 = c.HostFighter,
                Fighter1 = c.GuestFighter,
                Seed = c.Seed,
                RoundsToWin = c.RoundsToWin,
                Mode = MatchMode.Versus,
                FirstOffense = (int)(c.Seed & 1),
                CourtIndex = c.Court,
            };
            int local = IsHost ? 0 : 1;
            Session = new RollbackSession(_tables, setup, local, IsHost, _transport, _pendingLocalInput, _clock, _cfg, _inputDelay);
            SetState(LobbyState.InMatch);
        }

        private void HandleRematch(byte[] buf, int len)
        {
            if (len >= 2) _peerFighter = buf[1];
            _peerRematch = true;
            TryRematch();
        }

        private void TryRematch()
        {
            if (!_localRematch || !_peerRematch) return;
            if (Session == null || Session.State.Phase != Phase.MatchEnd) return;
            Session.Dispose();
            Session = null;
            _localRematch = false;
            _peerRematch = false;
            _rematchCount++;
            _configReceived = false;
            SetState(LobbyState.Handshaking);
            if (IsHost) SendConfig();
        }

        private void Fail(NetFailure reason)
        {
            Failure = reason;
            SetState(LobbyState.Failed);
        }

        private void SetState(LobbyState s)
        {
            if (State == s) return;
            State = s;
            StateChanged?.Invoke(s);
        }
    }
}
