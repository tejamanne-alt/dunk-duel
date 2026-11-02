namespace DunkDuel.Sim.Data
{
    // All gameplay numbers live in the JSON tables under Assets/DunkDuel/Data/Resources/DunkDuelData.
    // These classes are bound once at bake time (TableBinder) and must be treated as immutable afterwards.
    // Distances are meters, X increases toward the hoop, Y is height. Speeds/accelerations are written in
    // JSON per second and baked per 60 Hz frame. Frame counts are written as frames.

    public sealed class CourtTable
    {
        public Fix RimX;
        public Fix RimY;
        public Fix RimRadius;
        public Fix BallRadius;
        public Fix BackboardX;
        public Fix BackboardBottom;
        public Fix BackboardTop;
        public Fix BaselineX;
        public Fix ArcX;
        public Fix CheckX;
        public Fix LeftBoundX;
        public Fix FreeThrowX;
        /// <summary>Horizontal distance from the rim center inside which a make counts as a rim finish.</summary>
        public Fix RimZone;
    }

    public sealed class MatchTable
    {
        public int RoundTargetPoints;
        [JsonKey("roundSeconds"), Unit(Unit.Seconds)] public int RoundFrames;
        [JsonKey("shotClockSeconds"), Unit(Unit.Seconds)] public int ShotClockFrames;
        public int RoundsToWinBestOf3;
        public int RoundsToWinBestOf5;
        public int RoundIntroFrames;
        public int CheckFrames;
        public int ScorePauseFrames;
        public int StopPauseFrames;
        public int RoundEndFrames;
        public int PointsInside;
        public int PointsDeep;
        public int PointsFinisher;
        /// <summary>Defender's distance in front of the check spot at a check.</summary>
        public Fix CheckGap;
    }

    public sealed class InputTable
    {
        public int BufferFrames;
        public int SimultaneousFrames;
    }

    public sealed class PhysicsTable
    {
        [Unit(Unit.PerSecondSq)] public Fix BallGravity;
        [Unit(Unit.PerSecondSq)] public Fix FighterGravity;
        public Fix FloorRestitution;
        public Fix WallRestitution;
        public Fix BackboardRestitution;
        /// <summary>Per-frame horizontal speed multiplier for a rolling ball.</summary>
        public Fix RollFriction;
        [Unit(Unit.PerSecond)] public Fix RestBounceSpeed;
        /// <summary>Feet height above which a fighter counts as airborne for the Read.</summary>
        public Fix AirborneHeight;
        [Unit(Unit.PerSecondSq)] public Fix GroundAccel;
        [Unit(Unit.PerSecondSq)] public Fix GroundDecel;
        public Fix HoldForward;
        public Fix HoldHeight;
        public Fix ContactSlack;
    }

    public sealed class MovementTable
    {
        [Unit(Unit.PerSecond)] public Fix WalkBase;
        [Unit(Unit.PerSecond)] public Fix WalkPerSpeed;
        [Unit(Unit.PerSecond)] public Fix DriveBase;
        [Unit(Unit.PerSecond)] public Fix DrivePerSpeed;
        [Unit(Unit.PerSecond)] public Fix SlideBase;
        [Unit(Unit.PerSecond)] public Fix SlidePerSpeed;
        public Fix ProtectScale;
        [Unit(Unit.PerSecond)] public Fix BackDownSpeed;
        /// <summary>Simple mode: defender auto-positions this far in front of the ball handler.</summary>
        public Fix AutoDefendGap;
    }

    public sealed class ReadTable
    {
        public int PlantFramesForCharge;
        public int UnplantFrames;
        public int GatherExposedFrames;
        public int WhiffOffBalanceFrames;
        public int BiteBeatenFrames;
        public int BlowByBeatenFrames;
        [Unit(Unit.PerSecond)] public Fix DriveMinSpeed;
        public Fix DrivePushBase;
        public Fix DrivePushPerStrength;
        public Fix DrivePushMin;
        public Fix DrivePushMax;
        public int DriveBumpInterval;
        /// <summary>A drive that loses contact for fewer frames than this is still the same meeting.</summary>
        public int ContactGraceFrames;
        public int ChargeKnockdownFrames;
        public int ContestStartup;
        public int ContestLandRecovery;
        [Unit(Unit.PerSecond)] public Fix ContestLunge;
        public int SwipeHitRecovery;
        public Fix SwipeReachPerDefense;
        public Fix WallPushScale;
        /// <summary>Quickness battle when a drive first meets a solid defender who neither walled nor jumped.</summary>
        public Fix DriveBeatBase;
        public Fix DriveBeatPerSpeed;
        public Fix DriveBeatPerHandles;
        public Fix DriveBeatPerDefense;
        public Fix DriveBeatMin;
        public Fix DriveBeatMax;
        /// <summary>Multiplier when the defender is already sliding with the drive.</summary>
        public Fix DriveBeatSlidingScale;
    }

    public sealed class BalanceTable
    {
        public Fix Max;
        public int RegenDelayFrames;
        public Fix RegenPerFrame;
        public int StumbleFrames;
        public Fix AfterStumble;
        public Fix DriveBumpDamage;
        public Fix PostBumpDamage;
        public Fix PosterizedDamage;
        public int PosterizedKnockdownFrames;
        public Fix ResistPerStrength;
        public Fix ResistPerDefense;
        public Fix ResistMin;
        public Fix ResistMax;
        public Fix HandlesMultBase;
        public Fix HandlesMultPerPoint;
        /// <summary>Balance damage multiplier per shake link (index 0 = first move of a chain).</summary>
        public Fix[] ComboMultipliers;
        public int ComboWindowFrames;
    }

    public sealed class HypeTable
    {
        public int Max;
        public int Bar;
        public int Make2;
        public int Make3;
        public int Dunk;
        public int Steal;
        public int Block;
        public int ChargeDrawn;
        public int AnkleBreak;
        public int Posterizer;
        public int BiteDrawn;
        public int ScoredOn;
        public int AmpedCost;
        public int DenialCost;
        public int FinisherCost;
        public Fix SimpleModeScale;
        public int AmpedStartupDelta;
    }

    public sealed class HeatTable
    {
        public int Streak;
        public Fix PerfectWiden;
        public int MissHype;
    }

    public sealed class ShotTable
    {
        public Fix BaseRim;
        public Fix BaseMid;
        public Fix BaseDeep;
        public Fix PerPoint;
        public Fix BonusPerfect;
        public Fix BonusGood;
        public Fix PenaltyLate;
        public Fix ContestMax;
        public Fix BalancePenaltyMax;
        public Fix BalancePenaltyBelow;
        public Fix MinChance;
        public Fix MaxChance;
        public int PerfectFrames;
        public int GoodFrames;
        public Fix SimpleWindowScale;
        public int AutoReleaseAfterPeak;
        public int JumperRiseFrames;
        public int LayupRiseFrames;
        public int ShotLandRecovery;
        public Fix ReleaseHeightFactor;
        public Fix ReleaseForward;
        public Fix FinishRange;
        public Fix FinishRangeDriving;
        public Fix DunkClearance;
        public Fix DunkRange;
        public Fix DunkApproachOffset;
        public Fix LayupApproachOffset;
        public int FlightBaseFrames;
        public Fix FlightFramesPerMeter;
        public int LayupFlightFrames;
        public int DunkFlightFrames;
        public int RimInFrames;
        public int BankLegFrames;
        public int NetDropFrames;
        public int RimNoGrabFrames;
    }

    public sealed class ContestTable
    {
        public Fix CloseDistance;
        public Fix FarDistance;
        public Fix ReachScale;
        public Fix ReferenceReach;
        public Fix BehindFactor;
        /// <summary>How far past the shooter a defender must be before the behind penalty applies.</summary>
        public Fix BehindTolerance;
        public Fix GroundFactor;
        /// <summary>Verticality: a grounded defender in front of a rim finish contests with standing reach.</summary>
        public Fix GroundFactorRim;
        /// <summary>Frames from the defender's jump apex at which timing quality reaches zero.</summary>
        public int TimingWindow;
        public Fix HeightFalloff;
        public Fix DefensePerPoint;
        public Fix BlockTimingMin;
        public Fix BlockDistanceMin;
        public Fix BlockThreshold;
        public Fix BlockPowerPerDefense;
        public Fix BlockHeightBonusPerMeter;
        public Fix ProtectPerfect;
        public Fix ProtectGood;
        public Fix ProtectDunk;
        public Fix ProtectHook;
        public Fix ProtectLayup;
        public Fix ProtectJumper;
        public Fix ProtectPerFinishing;
        public Fix PosterizerMinContest;
        [Unit(Unit.PerSecond)] public Fix BlockDeflectVx;
        [Unit(Unit.PerSecond)] public Fix BlockDeflectVy;
    }

    public sealed class OutcomeTable
    {
        public Fix SwishPerfect;
        public Fix SwishGood;
        public Fix SwishLate;
        public Fix BankShare;
        public Fix AirballBase;
        public Fix AirballPerContest;
        public Fix AirballShortBy;
        public Fix BankHeight;
        [Unit(Unit.PerSecond)] public Fix ReboundVxMin;
        [Unit(Unit.PerSecond)] public Fix ReboundVxMax;
        [Unit(Unit.PerSecond)] public Fix ReboundVyMin;
        [Unit(Unit.PerSecond)] public Fix ReboundVyMax;
        public Fix RimEdge;
    }

    public sealed class ReboundTable
    {
        public Fix GrabReachX;
        public Fix GrabReachYExtra;
    }

    public sealed class PostTable
    {
        public int BumpInterval;
        public Fix BumpPush;
        public int BumpExposedFrames;
        public Fix ContactDistance;
        public Fix BackDownMinDistance;
    }

    /// <summary>Hit-stop and time-scale part of an impact recipe (camera and callout live in presentation data).</summary>
    public sealed class ImpactDef
    {
        public SimEventType Event;
        public int HitStop;
        public int SlowFrames;
        public int SlowDiv;
        public string Camera;
        public string Callout;
        public string Crowd;
    }

    public sealed class Tuning
    {
        public int TickRate;
        public string Label;
        public CourtTable Court;
        public MatchTable Match;
        public InputTable Input;
        public PhysicsTable Physics;
        public MovementTable Movement;
        public ReadTable Read;
        public BalanceTable Balance;
        public HypeTable Hype;
        public HeatTable Heat;
        public ShotTable Shot;
        public ContestTable Contest;
        public OutcomeTable Outcome;
        public ReboundTable Rebound;
        public PostTable Post;
        public string[] Fighters;
    }

    public sealed class ImpactsTable
    {
        public ImpactDef[] Impacts;
    }

    // ------------------------------------------------------------------ moves and fighters

    public enum MoveKind : byte
    {
        Shake = 0,
        Swipe = 1,
        OffenseTrick = 2,
        DefenseTrick = 3,
    }

    public enum TrickBehavior : byte
    {
        None = 0,
        LaunchStep,
        DoubleClutch,
        ChaseDown,
        MonsoonStep,
        Drizzle,
        Undercurrent,
        FrameSkip,
        LagSpike,
        Pickpocket,
        DropStep,
        GraniteHook,
        RimLock,
    }

    /// <summary>Behavior-specific numbers for Signature Tricks. Only the fields a behavior uses appear in its JSON.</summary>
    public sealed class TrickParams
    {
        [Optional] public Fix TakeoffDistance;
        [Optional] public Fix AmpedMaxRange;
        [Optional] public Fix AmpedTakeoffDistance;
        [Optional] public Fix AmpedBalanceDrain;
        [Optional] public int AmpedHangFrames;
        [Optional] public Fix ApproachSpeedScale;
        [Optional] public int PeakFrames;
        [Optional] public int PumpDelay;
        [Optional] public int AmpedUncontestableFrames;
        [Optional, Unit(Unit.PerSecond)] public Fix SprintSpeed;
        [Optional, Unit(Unit.PerSecond)] public Fix AmpedSprintSpeed;
        [Optional] public Fix BlockBonus;
        [Optional] public Fix AmpedBlockBonus;
        [Optional] public Fix TriggerDistance;
        [Optional] public Fix AmpedSecondDistance;
        [Optional] public Fix AmpedBitePower;
        [Optional] public int GatherReduction;
        [Optional] public Fix AmpedContestScale;
        [Optional] public Fix AmpedFadeDistance;
        [Optional] public bool AmpedSkipClear;
        [Optional] public int AmpedRepeat;
        [Optional] public int LagFrames;
        [Optional] public int AmpedLagFrames;
        [Optional] public Fix AmpedBalanceDamage;
        [Optional] public Fix DashDistance;
        [Optional] public Fix AmpedReach;
        [Optional] public Fix KnockbackDistance;
        [Optional] public Fix BalanceDamage;
        [Optional] public int AmpedNoContestFrames;
        [Optional] public Fix ReleaseFactor;
        [Optional] public int Duration;
        [Optional] public Fix AutoContestRange;
        [Optional] public bool AmpedCatch;
    }

    /// <summary>Frame data for a dribble move, swipe or Signature Trick. Windows are [start, end) in move frames.</summary>
    public sealed class MoveDefinition
    {
        public string Id;
        public string Name;
        public MoveKind Kind;
        public TrickBehavior Behavior;
        public int Startup;
        public int Active;
        public int Recovery;
        public int CancelStart;
        public int CancelEnd;
        public int BaitStart;
        public int BaitEnd;
        public int ExposedStart;
        public int ExposedEnd;
        public int MoveStart;
        public int MoveEnd;
        public Fix MoveDistance;
        public Fix Power;
        public Fix Reach;
        public Fix MinRange;
        public Fix MaxRange;
        public bool CanDrawCharge;
        public bool SlipThrough;
        public int WhiffRecovery;
        [Optional] public TrickParams Params;

        public int Total => Startup + Active + Recovery;
    }

    public sealed class CommonMovesTable
    {
        public MoveDefinition Crossover;
        public MoveDefinition Stepback;
        public MoveDefinition Spin;
        public MoveDefinition Hesitation;
        public MoveDefinition Swipe;
    }

    public sealed class AttributeBlock
    {
        public int Speed;
        public int Handles;
        public int Range;
        public int Finishing;
        public int Defense;
        public int Strength;

        public int Total => Speed + Handles + Range + Finishing + Defense + Strength;
    }

    public enum Mastery : byte
    {
        HangTime = 0,
        Rhythm = 1,
        HandlesCombo = 2,
        PostControl = 3,
    }

    public sealed class MasteryParams
    {
        [Optional] public int HangFrames;
        [Optional] public Fix HangNudge;
        [Optional] public int RhythmMaxStacks;
        [Optional] public int RhythmFramesPerStack;
        [Optional] public bool CanBackDown;
    }

    public sealed class FinisherDefinition
    {
        public string Id;
        public string Name;
        public Fix ZoneMin;
        public Fix ZoneMax;
        public int Frames;
        public int DenyStart;
        public int DenyFrames;
        /// <summary>Where the attacker ends the cinematic (sim keeps positions continuous for the camera).</summary>
        public Fix EndX;
    }

    public sealed class CharacterDefinition
    {
        public string Id;
        public string Name;
        public string Nickname;
        public Mastery Mastery;
        public Fix Height;
        public AttributeBlock Attributes;
        public Fix ReachFactor;
        public Fix PushboxHalfWidth;
        public int JumpRiseFrames;
        public int JumperGatherFrames;
        public int FinishGatherFrames;
        public int PerfectFrames;
        public int ComboLinks;
        public Fix ResistBonus;
        public Fix BumpVulnerability;
        public MasteryParams MasteryParams;
        public MoveDefinition TrickForward;
        public MoveDefinition TrickBack;
        public MoveDefinition TrickDefense;
        public FinisherDefinition Finisher;
    }
}
