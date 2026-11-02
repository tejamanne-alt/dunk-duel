using System;

namespace DunkDuel.Sim
{
    /// <summary>Per-fighter dynamic state. Plain value type: copying the struct snapshots it.</summary>
    [Serializable]
    public struct FighterState
    {
        public byte CharIndex;
        public ushort Status;

        // Kinematics (meters, meters per frame)
        public Fix X;
        public Fix Y;
        public Fix VX;
        public Fix VY;
        public sbyte Facing;

        // Action state machine
        public ActionId Action;
        public byte MoveSlot;
        public ushort ActionFlags;
        public int ActionFrame;
        public int Peak;
        public int Takeoff;
        public int Param;
        public ShotKind PendingKind;
        public byte PendingPoints;

        // Defensive status timers
        public int PlantFrames;
        public int Beaten;
        public int Lag;
        public int NoContest;
        public int BumpTimer;
        public int LastContactFrame;

        // Meters
        public int Hype;
        public Fix Balance;
        public int BalanceIdle;
        public int Score;
        public int RoundsWon;
        public int Streak;
        public byte Rhythm;
        public byte ComboLinks;
        public int ComboTimer;

        // Input processing (presses survive hit-stop and age per gameplay frame)
        public ushort Held;
        public ushort PrevHeld;
        public byte AgeShoot;
        public byte AgeShake;
        public byte AgeTrick;
        public byte AgeFinisher;
        public bool TrickAmp;
        public uint DirHistory;

        // Match stats (HUD, results screen, Balance Lab)
        public ushort FgMade;
        public ushort FgAtt;
        public ushort DeepMade;
        public ushort DeepAtt;
        public ushort Dunks;
        public ushort Steals;
        public ushort Blocks;
        public ushort Charges;
        public ushort AnkleBreaks;
        public ushort Posters;
        public ushort Finishers;
        public ushort FinisherAtt;
        public ushort Denials;
        public ushort Turnovers;
        public ushort BitesDrawn;

        public bool Has(ushort statusFlag) => (Status & statusFlag) != 0;
        public bool HasFlag(ushort actionFlag) => (ActionFlags & actionFlag) != 0;
        public bool Grounded => (Status & StatusFlag.Grounded) != 0;
        public bool IsHeld(Buttons b) => (Held & (ushort)b) != 0;
        public FrameInput HeldInput => new FrameInput(Held);
    }

    [Serializable]
    public struct BallState
    {
        public BallMode Mode;
        public byte Holder;
        public byte LastTouch;
        public Fix X;
        public Fix Y;
        public Fix VX;
        public Fix VY;
        public int NoGrab;
        public bool RimTouched;
    }

    [Serializable]
    public struct ShotState
    {
        public bool Active;
        public byte Shooter;
        public ShotKind Kind;
        public ShotZone Zone;
        public ReleaseGrade Grade;
        public ShotResult Result;
        public ShotStage Stage;
        public bool Made;
        public byte Points;
        public bool Posterizer;
        public bool HeatShot;
        public bool Buzzer;
        public bool DrainOnMake;
        public int Frame;
        public int StageFrames;
        public Fix TargetX;
        public Fix TargetY;
        public Fix MakeChance;
        public Fix Contest;
    }

    [Serializable]
    public struct FinisherState
    {
        public bool Active;
        public byte Attacker;
        public int Frame;
        public int Frames;
        public int DenyStart;
        public int DenyFrames;
        public bool HasRing;
        public bool Attempted;
        public Fix StartX;
        public Fix EndX;
    }

    /// <summary>
    /// Complete deterministic match state: a value type with no references, well under 2 KB.
    /// <see cref="Clone"/> is a struct copy; <see cref="Hash"/> covers every field in a fixed order.
    /// </summary>
    [Serializable]
    public struct GameState
    {
        public const byte None = 255;

        public int Tick;         // 60 Hz ticks since match start (includes hit-stop ticks)
        public int Frame;        // gameplay frames advanced
        public ulong Rng;

        public Phase Phase;
        public int PhaseFrame;
        public byte Round;
        public byte Offense;
        public bool NeedsClear;
        public bool SuddenDeath;
        public bool ClockExpired;
        public int RoundClock;
        public int ShotClock;

        public int HitStop;
        public int SlowFrames;
        public byte SlowDiv;
        public byte SlowPhase;

        public byte RoundWinner;
        public byte MatchWinner;
        public byte RoundsToWin;
        public MatchMode Mode;
        public bool InfiniteHype;
        public byte PendingRelease;

        public FighterState F0;
        public FighterState F1;
        public BallState Ball;
        public ShotState Shot;
        public FinisherState Fin;

        public GameState Clone() => this;

        public ulong Hash()
        {
            GameState copy = this;
            return StateCodec.Hash(ref copy);
        }

        public bool IsOver => Phase == Phase.MatchEnd;

        public static ref FighterState Fighter(ref GameState s, int index)
        {
            if (index == 0) return ref s.F0;
            return ref s.F1;
        }

        public FighterState Get(int index) => index == 0 ? F0 : F1;
    }
}
