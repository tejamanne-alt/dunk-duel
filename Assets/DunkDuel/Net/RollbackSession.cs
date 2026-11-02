using System;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Net
{
    /// <summary>
    /// Rollback netcode for one peer. Each phone simulates every tick itself from inputs alone: local inputs are
    /// scheduled <c>InputDelay</c> ticks ahead, remote inputs are predicted by repeating the last confirmed one,
    /// and a late input that differs from its prediction rolls the sim back (at most 8 ticks) and resimulates.
    /// State hashes cross every 30 ticks; on a mismatch the host streams its state and the guest resyncs.
    /// </summary>
    public sealed class RollbackSession : IMatchSession
    {
        private const int Ring = 256;
        private const int Mask = Ring - 1;
        private const int HashSlots = 8;

        private readonly Simulation _sim;
        private readonly ITransport _transport;
        private readonly IInputSource _localSource;
        private readonly INetClock _clock;
        private readonly NetConfig _cfg;
        private readonly bool _isHost;

        private readonly FrameInput[] _local = new FrameInput[Ring];
        private readonly FrameInput[] _remote = new FrameInput[Ring];
        private readonly int[] _remoteTick = new int[Ring];
        private readonly FrameInput[] _usedRemote = new FrameInput[Ring];
        private readonly GameState[] _snap;
        private readonly int[] _snapTick;

        private GameState _state;
        private int _tick;
        private int _localLatest;
        private int _remoteConfirmed = -1;
        private int _remoteLatestSeen = -1;
        private int _rollbackFrom = -1;
        private readonly int _delay;

        private readonly byte[] _inputBuf = new byte[InputPacket.Size];
        private readonly byte[] _ctrlBuf = new byte[512];

        // hashes
        private readonly int[] _hashTick = new int[HashSlots];
        private readonly ulong[] _hashOurs = new ulong[HashSlots];
        private readonly bool[] _hashFinal = new bool[HashSlots];
        private readonly bool[] _hashSent = new bool[HashSlots];
        private readonly ulong[] _hashPeer = new ulong[HashSlots];
        private readonly bool[] _hashPeerHave = new bool[HashSlots];
        private readonly bool[] _hashCompared = new bool[HashSlots];
        private GameState _lastFinalHashedState;
        private int _lastFinalHashedTick = -1;

        // resync
        private byte[] _chunkAssembly;
        private int _chunkTick = -1;
        private int _chunkCount;
        private int _chunksReceived;
        private bool[] _chunkHave;
        private readonly byte[] _stateBytes = new byte[StateCodec.MaxBytes];

        // timing
        private long _lastRecvMicros;
        private long _lastPingMicros;
        private uint _pingId;
        private int _rttMicros;
        private int _jitterMicros;
        private int _rollbackSamples;
        private readonly int[] _histogram = new int[10];
        private NetStats _stats;
        private long _inputPacketsReceived;
        private long _inputFramesReceived;

        public RollbackSession(SimTables tables, in MatchSetup setup, int localPlayer, bool isHost, ITransport transport,
            IInputSource localInput, INetClock clock, NetConfig config, int inputDelay)
        {
            _sim = new Simulation(tables);
            Tables = tables;
            Setup = setup;
            LocalPlayer = localPlayer;
            _isHost = isHost;
            _transport = transport;
            _localSource = localInput;
            _clock = clock;
            _cfg = config;
            _delay = Math.Max(config.MinInputDelay, Math.Min(config.MaxInputDelay, inputDelay));
            _snap = new GameState[config.SnapshotRing];
            _snapTick = new int[config.SnapshotRing];
            for (int i = 0; i < _snapTick.Length; i++) _snapTick[i] = -1;
            for (int i = 0; i < Ring; i++) _remoteTick[i] = -1;
            for (int i = 0; i < HashSlots; i++) _hashTick[i] = -1;
            _state = _sim.CreateInitialState(setup);
            _tick = 0;
            for (int t = 0; t < _delay; t++) _local[t & Mask] = FrameInput.None;
            _localLatest = _delay - 1;
            Ledger = new EventLedger();
            Recorder = new ReplayRecorder(SimBuildInfo.Version(tables), setup);
            _lastRecvMicros = clock.NowMicros;
            _stats.RollbackHistogram = _histogram;
            _stats.InputDelay = _delay;
            transport.OnReceive += OnReceive;
        }

        public SimTables Tables { get; }
        public MatchSetup Setup { get; }
        public int LocalPlayer { get; }
        public GameState State => _state;
        public EventLedger Ledger { get; }
        public ReplayRecorder Recorder { get; }
        public bool Paused { get; private set; }
        public NetFailure Failure { get; private set; }
        public bool IsOver => _state.Phase == Phase.MatchEnd || Failure != NetFailure.None;
        public int CurrentTick => _tick;
        public int ConfirmedTick => _remoteConfirmed;
        public NetStats Stats => _stats;

        /// <summary>Raised for CONTROL messages the session does not handle itself (rematch, quit).</summary>
        public event Action<ControlType, byte[], int> ControlReceived;
        public event Action<int, ulong, ulong> DesyncDetected;

        public void Dispose()
        {
            _transport.OnReceive -= OnReceive;
        }

        // ------------------------------------------------------------------ tick

        public void Tick()
        {
            if (Failure != NetFailure.None) return;
            long now = _clock.NowMicros;
            _transport.Poll();

            if (now - _lastRecvMicros > _cfg.SilencePauseMicros)
            {
                Paused = true;
                if (now - _lastRecvMicros > _cfg.SilencePauseMicros + _cfg.ReconnectWindowMicros) Failure = NetFailure.NoContest;
                SendInputs();
                return;
            }
            Paused = false;

            if (now - _lastPingMicros > _cfg.PingIntervalMicros) SendPing(now);

            if (_rollbackFrom >= 0)
            {
                Rollback(_rollbackFrom);
                _rollbackFrom = -1;
            }

            if (_tick - _remoteConfirmed > _cfg.MaxRollback)
            {
                _stats.StallTicks++;
                SendInputs();
                UpdateStats();
                return;
            }

            // Time sync: the peer's local inputs run InputDelay ahead of its sim; a peer that leads slows down
            // one tick at a time until the frame advantage is back under one tick.
            if (_remoteLatestSeen >= 0)
            {
                int oneWayTicks = (_rttMicros / 2) / 16667;
                int peerTick = _remoteLatestSeen - _delay + oneWayTicks;
                int advantage = _tick - peerTick;
                if (advantage >= 2 && (_tick % 4) == 0)
                {
                    _stats.SlowdownTicks++;
                    SendInputs();
                    UpdateStats();
                    return;
                }
            }

            int scheduled = _tick + _delay;
            FrameInput li = _localSource.NextInput(_state, LocalPlayer);
            _local[scheduled & Mask] = li;
            _localLatest = scheduled;
            SendInputs();

            Advance(null);
            FinalizeHashes();
            UpdateStats();
        }

        private void Advance(EventLedger.RollbackScope scope)
        {
            int t = _tick;
            SaveSnapshot(t, _state);
            FrameInput local = _local[t & Mask];
            FrameInput remote = t <= _remoteConfirmed && _remoteTick[t & Mask] == t ? _remote[t & Mask] : Predict();
            _usedRemote[t & Mask] = remote;
            FrameInput p0 = LocalPlayer == 0 ? local : remote;
            FrameInput p1 = LocalPlayer == 0 ? remote : local;
            StepResult r = _sim.Step(_state, p0, p1);
            _state = r.State;
            _tick = t + 1;
            if (scope != null) scope.Resimulated(_tick, r.Events);
            else Ledger.Confirm(_tick, r.Events);
            if (_tick % _cfg.HashInterval == 0) RecordHash(_tick, _state);
        }

        private FrameInput Predict()
        {
            if (_remoteConfirmed < 0) return FrameInput.None;
            return _remote[_remoteConfirmed & Mask];
        }

        private void Rollback(int from)
        {
            int end = _tick;
            int slot = SnapshotSlot(from);
            if (slot < 0)
            {
                // Outside the ring: can only happen if MaxRollback >= SnapshotRing. Treat as a desync.
                _stats.Desyncs++;
                return;
            }
            _state = _snap[slot];
            _tick = from;
            EventLedger.RollbackScope scope = Ledger.BeginRollback(from + 1);
            while (_tick < end) Advance(scope);
            scope.End();
            int depth = end - from;
            _stats.LastRollback = depth;
            if (depth > _stats.MaxRollback) _stats.MaxRollback = depth;
            _stats.Resimulated += depth;
            _histogram[depth < 9 ? depth : 9]++;
            _rollbackSamples++;
        }

        private void SaveSnapshot(int tick, in GameState s)
        {
            int slot = tick % _snap.Length;
            _snap[slot] = s;
            _snapTick[slot] = tick;
        }

        private int SnapshotSlot(int tick)
        {
            int slot = tick % _snap.Length;
            return _snapTick[slot] == tick ? slot : -1;
        }

        // ------------------------------------------------------------------ send / receive

        private void SendInputs()
        {
            WriteInputPacket();
            _transport.Send(TransportChannel.Input, _inputBuf, 0, InputPacket.Size);
        }

        private void WriteInputPacket() => InputPacket.Write(_inputBuf, _localLatest, _remoteConfirmed, _local, Mask);

        private void OnReceive(TransportChannel channel, byte[] buf, int len)
        {
            _lastRecvMicros = _clock.NowMicros;
            if (channel == TransportChannel.Input)
            {
                if (len != InputPacket.Size) return;
                ReceiveInputs(buf);
                return;
            }
            if (len < 1) return;
            var r = new MsgReader(buf, len);
            try
            {
                switch (r.Type)
                {
                    case ControlType.Ping:
                        {
                            uint id = r.U32();
                            ulong t = r.U64();
                            var w = new MsgWriter(_ctrlBuf, ControlType.Pong);
                            w.U32(id);
                            w.U64(t);
                            _transport.Send(TransportChannel.Control, _ctrlBuf, 0, w.Pos);
                            break;
                        }
                    case ControlType.Pong:
                        {
                            r.U32();
                            long sent = (long)r.U64();
                            int rtt = (int)Math.Max(0, _clock.NowMicros - sent);
                            if (_rttMicros == 0) _rttMicros = rtt;
                            int dev = Math.Abs(rtt - _rttMicros);
                            _rttMicros += (rtt - _rttMicros) / 8;
                            _jitterMicros += (dev - _jitterMicros) / 8;
                            break;
                        }
                    case ControlType.Hash:
                        {
                            int tick = (int)r.U32();
                            ulong h = r.U64();
                            StorePeerHash(tick, h);
                            break;
                        }
                    case ControlType.StateChunk:
                        ReceiveChunk(ref r);
                        break;
                    default:
                        ControlReceived?.Invoke(r.Type, buf, len);
                        break;
                }
            }
            catch (FormatException)
            {
                // Malformed control message: ignore it rather than crash the match.
            }
        }

        private void ReceiveInputs(byte[] buf)
        {
            _inputPacketsReceived++;
            int reference = _remoteLatestSeen >= 0 ? _remoteLatestSeen : _tick;
            int latest = InputPacket.Unwrap(InputPacket.Latest(buf), reference);
            if (latest > _remoteLatestSeen) _remoteLatestSeen = latest;
            for (int i = 0; i < InputPacket.Frames; i++)
            {
                int f = latest - (InputPacket.Frames - 1) + i;
                if (f < 0 || f <= _remoteConfirmed) continue;
                if (f - _tick >= Ring / 2) continue; // absurdly far ahead
                _remote[f & Mask] = InputPacket.Input(buf, i);
                _remoteTick[f & Mask] = f;
                _inputFramesReceived++;
            }
            // Advance the confirmed frontier through contiguous frames and detect mispredictions.
            while (true)
            {
                int next = _remoteConfirmed + 1;
                if (_remoteTick[next & Mask] != next) break;
                _remoteConfirmed = next;
                if (next < _tick && _usedRemote[next & Mask] != _remote[next & Mask])
                {
                    if (_rollbackFrom < 0 || next < _rollbackFrom) _rollbackFrom = next;
                }
            }
        }

        private void SendPing(long now)
        {
            _lastPingMicros = now;
            var w = new MsgWriter(_ctrlBuf, ControlType.Ping);
            w.U32(++_pingId);
            w.U64((ulong)now);
            _transport.Send(TransportChannel.Control, _ctrlBuf, 0, w.Pos);
        }

        // ------------------------------------------------------------------ hashes and resync

        private void RecordHash(int tick, in GameState s)
        {
            int slot = (tick / _cfg.HashInterval) % HashSlots;
            if (_hashTick[slot] != tick)
            {
                _hashTick[slot] = tick;
                _hashSent[slot] = false;
                _hashPeerHave[slot] = false;
                _hashCompared[slot] = false;
            }
            _hashOurs[slot] = s.Hash();
            _hashFinal[slot] = false;
            if (tick - 1 <= _remoteConfirmed) FinalizeSlot(slot, s);
        }

        /// <summary>A hash is final once every input before its tick is confirmed (no rollback can change it).</summary>
        private void FinalizeHashes()
        {
            for (int slot = 0; slot < HashSlots; slot++)
            {
                int t = _hashTick[slot];
                if (t < 0 || _hashFinal[slot] || t - 1 > _remoteConfirmed) continue;
                int snap = SnapshotSlot(t);
                if (snap >= 0)
                {
                    // Recompute from the snapshot: the earlier hash may have been taken on a mispredicted timeline.
                    _hashOurs[slot] = _snap[snap].Hash();
                    FinalizeSlot(slot, _snap[snap]);
                }
                else if (t == _tick)
                {
                    _hashOurs[slot] = _state.Hash();
                    FinalizeSlot(slot, _state);
                }
            }
        }

        private void FinalizeSlot(int slot, in GameState s)
        {
            _hashFinal[slot] = true;
            if (_hashTick[slot] > _lastFinalHashedTick)
            {
                _lastFinalHashedTick = _hashTick[slot];
                _lastFinalHashedState = s;
            }
            if (!_hashSent[slot])
            {
                _hashSent[slot] = true;
                var w = new MsgWriter(_ctrlBuf, ControlType.Hash);
                w.U32((uint)_hashTick[slot]);
                w.U64(_hashOurs[slot]);
                _transport.Send(TransportChannel.Control, _ctrlBuf, 0, w.Pos);
            }
            Compare(slot);
        }

        private void StorePeerHash(int tick, ulong hash)
        {
            int slot = (tick / _cfg.HashInterval) % HashSlots;
            if (_hashTick[slot] != tick)
            {
                if (_hashTick[slot] > tick) return; // stale
                _hashTick[slot] = tick;
                _hashFinal[slot] = false;
                _hashSent[slot] = false;
                _hashCompared[slot] = false;
            }
            _hashPeer[slot] = hash;
            _hashPeerHave[slot] = true;
            Compare(slot);
        }

        private void Compare(int slot)
        {
            if (!_hashFinal[slot] || !_hashPeerHave[slot] || _hashCompared[slot]) return;
            _hashCompared[slot] = true;
            if (_hashOurs[slot] == _hashPeer[slot]) return;
            _stats.Desyncs++;
            DesyncDetected?.Invoke(_hashTick[slot], _hashOurs[slot], _hashPeer[slot]);
            if (_isHost && _lastFinalHashedTick == _hashTick[slot]) SendState(_lastFinalHashedTick, _lastFinalHashedState);
        }

        private void SendState(int tick, GameState s)
        {
            int len = StateCodec.Write(ref s, _stateBytes);
            int chunk = _cfg.StateChunkBytes;
            int count = (len + chunk - 1) / chunk;
            for (int i = 0; i < count; i++)
            {
                int off = i * chunk;
                int n = Math.Min(chunk, len - off);
                var w = new MsgWriter(_ctrlBuf, ControlType.StateChunk);
                w.U32((uint)tick);
                w.U16(i);
                w.U16(count);
                w.U16(len);
                w.Bytes(_stateBytes, off, n);
                _transport.Send(TransportChannel.Control, _ctrlBuf, 0, w.Pos);
            }
        }

        private void ReceiveChunk(ref MsgReader r)
        {
            int tick = (int)r.U32();
            int index = r.U16();
            int count = r.U16();
            int total = r.U16();
            if (_isHost || count <= 0 || count > 64 || total > StateCodec.MaxBytes) return;
            if (tick != _chunkTick)
            {
                _chunkTick = tick;
                _chunkCount = count;
                _chunksReceived = 0;
                _chunkAssembly = new byte[total];
                _chunkHave = new bool[count];
            }
            if (index >= _chunkCount || _chunkHave[index]) return;
            var tmp = new byte[_cfg.StateChunkBytes];
            int n = r.Bytes(tmp, 0);
            Array.Copy(tmp, 0, _chunkAssembly, index * _cfg.StateChunkBytes, n);
            _chunkHave[index] = true;
            _chunksReceived++;
            if (_chunksReceived == _chunkCount) ApplyResync(tick, _chunkAssembly);
        }

        /// <summary>Guest: adopt the host's state at <paramref name="tick"/> and resimulate to the present.</summary>
        private void ApplyResync(int tick, byte[] bytes)
        {
            GameState host;
            try
            {
                host = StateCodec.Read(bytes, bytes.Length);
            }
            catch (ArgumentException)
            {
                return;
            }
            if (host.Tick != tick || tick > _tick || _tick - tick >= Ring / 2) return;
            int end = _tick;
            _state = host;
            _tick = tick;
            EventLedger.RollbackScope scope = Ledger.BeginRollback(tick + 1);
            while (_tick < end) Advance(scope);
            scope.End();
            _stats.DesyncRecoveries++;
            _chunkTick = -1;
        }

        // ------------------------------------------------------------------ stats

        private void UpdateStats()
        {
            _stats.RttMicros = _rttMicros;
            _stats.JitterMicros = _jitterMicros;
            _stats.InputDelay = _delay;
            long expected = _inputPacketsReceived * InputPacket.Frames;
            _stats.LossPermille = 0;
            if (_remoteLatestSeen > 0)
            {
                // Each tick the peer sends one packet; compare packets received to frames the peer has advanced.
                long sent = _remoteLatestSeen + 1;
                long got = Math.Min(sent, _inputPacketsReceived);
                _stats.LossPermille = (int)((sent - got) * 1000 / Math.Max(1, sent));
            }
            _stats.RollbackP95 = Percentile(95);
            int q = 4;
            if (_rttMicros > 60000) q--;
            if (_rttMicros > 90000) q--;
            if (_jitterMicros > 15000) q--;
            if (_stats.LossPermille > 50) q--;
            _stats.QualityBars = Math.Max(0, q);
            _ = expected;
        }

        /// <summary>Percentile of rollback depth over all ticks simulated (ticks without rollback count as 0).</summary>
        private int Percentile(int p)
        {
            long total = _tick;
            if (total <= 0) return 0;
            long zero = total - _rollbackSamples;
            long threshold = total * p / 100;
            long acc = zero;
            if (acc >= threshold) return 0;
            for (int d = 1; d < _histogram.Length; d++)
            {
                acc += _histogram[d];
                if (acc >= threshold) return d;
            }
            return _histogram.Length - 1;
        }

        /// <summary>Corrupts the local state (soak tests use this to exercise desync detection and recovery).</summary>
        public void DebugCorruptState()
        {
            _state.F0.Score += 1;
        }
    }
}
