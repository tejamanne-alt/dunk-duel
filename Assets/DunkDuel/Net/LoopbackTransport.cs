using System;
using System.Collections.Generic;
using DunkDuel.Sim;

namespace DunkDuel.Net
{
    /// <summary>Simulated time for soak tests and in-editor two-session play.</summary>
    public sealed class ManualClock : INetClock
    {
        public long NowMicros { get; set; }
        public void Advance(long micros) => NowMicros += micros;
    }

    /// <summary>
    /// An in-memory radio between two <see cref="LoopbackTransport"/> endpoints with BLE-like conditions:
    /// latency, jitter and INPUT loss (CONTROL stays reliable and ordered, like GATT writes with response).
    /// Deterministic for a given seed, so soak tests are reproducible.
    /// </summary>
    public sealed class SimulatedLink
    {
        private struct Packet
        {
            public long DeliverAt;
            public long Order;
            public TransportChannel Channel;
            public byte[] Data;
        }

        private readonly INetClock _clock;
        private readonly List<Packet>[] _queues = { new List<Packet>(), new List<Packet>() };
        private readonly long[] _lastControlDelivery = new long[2];
        private ulong _rng;
        private long _order;

        public int LatencyMicros;
        public int JitterMicros;
        public int LossPermille;
        /// <summary>When set, nothing is delivered (simulates walking out of range).</summary>
        public bool Severed;

        public SimulatedLink(INetClock clock, ulong seed, int latencyMicros, int jitterMicros, int lossPermille)
        {
            _clock = clock;
            _rng = Rng.Seed(seed);
            LatencyMicros = latencyMicros;
            JitterMicros = jitterMicros;
            LossPermille = lossPermille;
        }

        public LoopbackTransport A { get; private set; }
        public LoopbackTransport B { get; private set; }

        public static SimulatedLink CreatePair(INetClock clock, ulong seed, int latencyMicros, int jitterMicros, int lossPermille)
        {
            var link = new SimulatedLink(clock, seed, latencyMicros, jitterMicros, lossPermille);
            link.A = new LoopbackTransport(link, 0);
            link.B = new LoopbackTransport(link, 1);
            return link;
        }

        internal void Send(int from, TransportChannel channel, byte[] data, int offset, int count)
        {
            if (Severed) return;
            int to = 1 - from;
            if (channel == TransportChannel.Input && Rng.Range(ref _rng, 0, 1000) < LossPermille) return;
            long jitter = JitterMicros > 0 ? Rng.Range(ref _rng, 0, JitterMicros + 1) : 0;
            long at = _clock.NowMicros + LatencyMicros + jitter;
            if (channel == TransportChannel.Control)
            {
                // Reliable and ordered: never deliver before an earlier control message.
                if (at < _lastControlDelivery[to]) at = _lastControlDelivery[to];
                _lastControlDelivery[to] = at;
            }
            var copy = new byte[count];
            Array.Copy(data, offset, copy, 0, count);
            _queues[to].Add(new Packet { DeliverAt = at, Order = _order++, Channel = channel, Data = copy });
        }

        internal void Deliver(int to, LoopbackTransport endpoint)
        {
            List<Packet> q = _queues[to];
            if (q.Count == 0 || Severed) return;
            long now = _clock.NowMicros;
            // Deliver due packets in (time, send order) order.
            q.Sort((a, b) => a.DeliverAt != b.DeliverAt ? a.DeliverAt.CompareTo(b.DeliverAt) : a.Order.CompareTo(b.Order));
            int n = 0;
            while (n < q.Count && q[n].DeliverAt <= now) n++;
            if (n == 0) return;
            var due = q.GetRange(0, n);
            q.RemoveRange(0, n);
            for (int i = 0; i < due.Count; i++) endpoint.Receive(due[i].Channel, due[i].Data);
        }
    }

    /// <summary>One end of a <see cref="SimulatedLink"/>. Implements the full transport contract.</summary>
    public sealed class LoopbackTransport : ITransport
    {
        private readonly SimulatedLink _link;
        private readonly int _side;
        private readonly List<PeerInfo> _peers = new List<PeerInfo>();
        private readonly Queue<TransportState> _stateEvents = new Queue<TransportState>();
        private TransportStats _stats;
        private string _court = "";

        internal LoopbackTransport(SimulatedLink link, int side)
        {
            _link = link;
            _side = side;
            _stats.MaxPayload = 182;
        }

        public string Name => "Loopback";
        public TransportState State { get; private set; }
        public TransportError Error => TransportError.None;
        public TransportStats Stats => _stats;
        public bool CanHost => true;
        public IReadOnlyList<PeerInfo> Peers => _peers;

        public event Action<TransportChannel, byte[], int> OnReceive;
        public event Action<TransportState> OnStateChanged;

        private LoopbackTransport Other => _side == 0 ? _link.B : _link.A;

        public void Host(string courtCode, string playerName)
        {
            _court = courtCode;
            SetState(TransportState.Hosting);
        }

        public void StartScan()
        {
            _peers.Clear();
            LoopbackTransport other = Other;
            if (other.State == TransportState.Hosting)
            {
                _peers.Add(new PeerInfo { Address = "loopback", CourtCode = other._court, PlayerName = "Host", SignalDbm = -40, Platform = "lan" });
            }
            SetState(TransportState.Scanning);
        }

        public void StopScan()
        {
            if (State == TransportState.Scanning) SetState(TransportState.Idle);
        }

        public void Join(PeerInfo peer)
        {
            LoopbackTransport other = Other;
            if (other.State != TransportState.Hosting && other.State != TransportState.Disconnected)
            {
                SetState(TransportState.Failed);
                return;
            }
            SetState(TransportState.Connected);
            other.SetState(TransportState.Connected);
        }

        public void Send(TransportChannel channel, byte[] data, int offset, int count)
        {
            if (State != TransportState.Connected) return;
            _stats.PacketsSent++;
            _stats.BytesSent += count;
            _link.Send(_side, channel, data, offset, count);
        }

        internal void Receive(TransportChannel channel, byte[] data)
        {
            _stats.PacketsReceived++;
            _stats.BytesReceived += data.Length;
            OnReceive?.Invoke(channel, data, data.Length);
        }

        public void Poll()
        {
            while (_stateEvents.Count > 0)
            {
                // Dequeue before invoking: `?.` would skip evaluating the argument when nobody is subscribed.
                TransportState next = _stateEvents.Dequeue();
                OnStateChanged?.Invoke(next);
            }
            _link.Deliver(_side, this);
            _stats.RttMicros = 2 * _link.LatencyMicros + _link.JitterMicros;
            _stats.JitterMicros = _link.JitterMicros;
            _stats.LossPermille = _link.LossPermille;
        }

        public void Disconnect()
        {
            SetState(TransportState.Disconnected);
            Other.SetState(TransportState.Disconnected);
        }

        public void Dispose() { }

        private void SetState(TransportState s)
        {
            if (State == s) return;
            State = s;
            _stateEvents.Enqueue(s);
        }
    }
}
