using System;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Everything presentation needs to react to. Events are facts about a frame; presentation must never
    /// feed anything back into the sim. Values are appended only (never reorder) because replays and
    /// impact tables refer to them by name.
    /// </summary>
    public enum SimEventType : byte
    {
        None = 0,
        RoundIntro,
        CheckBall,
        BallLive,
        ShakeStart,      // A = move slot, B = combo link (1-based)
        TrickStart,      // A = trick slot (0 fwd, 1 back, 2 defense), B = 1 if Amped
        GatherStart,     // A = ShotKind
        ShotRelease,     // A = ReleaseGrade, B = ShotZone
        ShotMade,        // A = points, B = ShotResult
        ShotMissed,      // A = ShotResult
        RimHit,
        BackboardHit,
        NetSnap,
        Splash,          // deep swish
        Airball,
        Block,
        Steal,
        Charge,
        BlowBy,
        Bite,            // A = balance damage (whole points)
        AnkleBreak,
        Posterizer,
        PostBump,
        DriveBump,
        Rebound,         // A = 1 offensive, 0 defensive
        LooseBall,
        Cleared,
        ShotClockViolation,
        HeatCheckOn,
        HeatCheckMiss,
        RhythmStack,     // A = stacks
        HypeBar,         // A = bars now full
        FinisherStart,   // A = fighter index in tables
        DenialWindow,
        DenialAttempt,   // A = 1 if inside the window
        Denied,
        FinisherScore,
        Jump,
        Land,
        Plant,
        Swipe,
        RoundEnd,        // A = round winner
        MatchEnd,        // A = match winner
        SuddenDeath,
        ClockExpired,
        BuzzerBeater,
        HangTime,
        ComboLink,       // A = link count
        Lag,             // A = lag frames applied
        Knockdown,
        Count,
    }

    /// <summary>
    /// One sim event. (Frame, Type, Actor, Seq) is the identity used by the rollback-aware event ledger:
    /// Seq numbers events of the same type and actor within one frame, so a resimulated frame produces the
    /// same keys for the same outcomes.
    /// </summary>
    [Serializable]
    public struct SimEvent
    {
        public int Frame;
        public SimEventType Type;
        public byte Actor;
        public byte Seq;
        public int A;
        public int B;
        public Fix X;
        public Fix Y;

        public long Key => ((long)Frame << 24) | ((long)Type << 16) | ((long)Actor << 8) | Seq;

        public override string ToString() => "f" + Frame + " " + Type + " p" + Actor + " #" + Seq + " (" + A + "," + B + ")";
    }

    /// <summary>Fixed-capacity, allocation-free event list for one Step. Reused by the simulation.</summary>
    public sealed class SimEventBuffer
    {
        public const int Capacity = 64;
        private readonly SimEvent[] _events = new SimEvent[Capacity];
        public int Count { get; private set; }

        public SimEvent this[int index] => _events[index];

        public void Clear() => Count = 0;

        public void Add(int frame, SimEventType type, int actor, int a, int b, Fix x, Fix y)
        {
            if (Count >= Capacity) return; // never expected; dropping is safer than throwing mid-frame
            byte seq = 0;
            for (int i = 0; i < Count; i++)
            {
                if (_events[i].Type == type && _events[i].Actor == actor) seq++;
            }
            _events[Count++] = new SimEvent
            {
                Frame = frame,
                Type = type,
                Actor = (byte)actor,
                Seq = seq,
                A = a,
                B = b,
                X = x,
                Y = y,
            };
        }

        public void CopyTo(SimEventBuffer other)
        {
            other.Clear();
            for (int i = 0; i < Count; i++) other._events[i] = _events[i];
            other.Count = Count;
        }

        public bool Contains(SimEventType type)
        {
            for (int i = 0; i < Count; i++) if (_events[i].Type == type) return true;
            return false;
        }
    }
}
