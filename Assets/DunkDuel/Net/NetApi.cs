using System;

namespace DunkDuel.Net
{
    /// <summary>Netcode tuning. Defaults follow the design: 2 frames of delay (adaptive 1-3), 8 frames of rollback.</summary>
    public sealed class NetConfig
    {
        public int InputDelay = 2;
        public int MinInputDelay = 1;
        public int MaxInputDelay = 3;
        public int MaxRollback = 8;
        public int SnapshotRing = 16;
        public int HashInterval = 30;
        public int SilencePauseMicros = 3000000;
        public int ReconnectWindowMicros = 20000000;
        public int PingIntervalMicros = 250000;
        public int StateChunkBytes = 160;
        /// <summary>Dev builds dump both states on a desync for diffing.</summary>
        public bool DumpDesyncs;
    }

    public enum LobbyState : byte
    {
        Idle = 0,
        Hosting = 1,      // advertising, waiting for a guest
        Joining = 2,      // connecting to a host
        Handshaking = 3,  // exchanging versions, fighters, seeds, RTT samples
        Ready = 4,        // match configured; Session is valid
        InMatch = 5,
        Paused = 6,       // 3 s of silence: waiting up to 20 s for the link to return
        Ended = 7,        // match over (rematch possible)
        Failed = 8,       // version mismatch, transport failure, no contest
    }

    /// <summary>Live netcode telemetry for the debug overlay and the session log.</summary>
    public struct NetStats
    {
        public int RttMicros;
        public int JitterMicros;
        public int LossPermille;
        public int InputDelay;
        public int LastRollback;
        public int MaxRollback;
        public int RollbackP95;
        public int Desyncs;
        public int DesyncRecoveries;
        public int StallTicks;
        public int SlowdownTicks;
        public long Resimulated;
        /// <summary>Histogram of rollback depth in frames, index 0..8 (9 = beyond the window).</summary>
        public int[] RollbackHistogram;
        /// <summary>0 (bad) .. 4 (great) for the lobby's connection-quality bar.</summary>
        public int QualityBars;
    }

    /// <summary>Shared match configuration agreed during the handshake.</summary>
    public struct LobbyConfig
    {
        public int HostFighter;
        public int GuestFighter;
        public int Court;
        public int RoundsToWin;
        public ulong Seed;
        public string HostName;
        public string GuestName;
    }

    /// <summary>Monotonic microsecond clock injected into the netcode (real time on device, simulated in soak tests).</summary>
    public interface INetClock
    {
        long NowMicros { get; }
    }

    public sealed class StopwatchClock : INetClock
    {
        private readonly System.Diagnostics.Stopwatch _sw = System.Diagnostics.Stopwatch.StartNew();
        public long NowMicros => _sw.ElapsedTicks * 1000000L / System.Diagnostics.Stopwatch.Frequency;
    }

    /// <summary>Errors surfaced to the lobby UI.</summary>
    public enum NetFailure : byte
    {
        None = 0,
        VersionMismatch = 1,
        ProtocolMismatch = 2,
        TransportFailed = 3,
        NoContest = 4,     // reconnect window expired
        PeerLeft = 5,
    }
}
