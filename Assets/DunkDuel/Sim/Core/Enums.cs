namespace DunkDuel.Sim
{
    public enum Phase : byte
    {
        RoundIntro = 0,
        CheckBall = 1,
        Live = 2,
        Score = 3,
        StopPause = 4,
        Finisher = 5,
        RoundEnd = 6,
        MatchEnd = 7,
    }

    /// <summary>Fighter action state. Every action has a frame counter (<see cref="FighterState.ActionFrame"/>).</summary>
    public enum ActionId : byte
    {
        Idle = 0,         // free: locomotion and new actions
        Shake = 1,        // dribble move (offense)
        GatherJumper = 2, // jump-shot gather, release at the peak
        GatherFinish = 3, // layup or dunk gather
        ShotRecover = 4,  // after a release until landing + recovery
        PostUp = 5,       // backing down (Post Control)
        Trick = 6,        // Signature Trick (offense or defense)
        Wall = 7,         // planted feet (defense)
        Unplant = 8,      // leaving a plant
        Contest = 9,      // jump with hand up (defense, rebounds)
        Swipe = 10,       // reach at the ball (defense)
        OffBalance = 11,  // whiff recovery
        Stumble = 12,     // Ankle Break
        Knockdown = 13,   // charge taken / posterized
        Finisher = 14,    // cinematic (both fighters are locked)
        Celebrate = 15,
    }

    public enum BallMode : byte
    {
        Held = 0,
        Flight = 1,  // a released shot on its solved path
        Loose = 2,   // rebound / deflection, free physics
        Dead = 3,    // through the net or out of play
    }

    public enum ShotKind : byte
    {
        Jumper = 0,
        Layup = 1,
        Dunk = 2,
        Hook = 3,
        Finisher = 4,
    }

    public enum ShotZone : byte
    {
        Rim = 0,
        Mid = 1,
        Deep = 2,
    }

    public enum ReleaseGrade : byte
    {
        Perfect = 0,
        Good = 1,
        Late = 2,
        Early = 3,
    }

    public enum ShotResult : byte
    {
        None = 0,
        Swish = 1,
        RimIn = 2,
        Bank = 3,
        RimOut = 4,
        Airball = 5,
        Blocked = 6,
    }

    /// <summary>Which stage of its path a shot is on.</summary>
    public enum ShotStage : byte
    {
        None = 0,
        ToTarget = 1,   // first leg from the release point
        OnRim = 2,      // rim-in: rolling on the rim before dropping
        BankLeg = 3,    // off the glass toward the rim
        Dropping = 4,   // through the net
    }

    public enum TrickSlot : byte
    {
        Forward = 0,
        Back = 1,
        Defense = 2,
    }

    public enum MatchMode : byte
    {
        Versus = 0,
        Training = 1,
    }

    /// <summary>Bit flags on <see cref="FighterState.ActionFlags"/>.</summary>
    public static class ActionFlag
    {
        public const ushort Amped = 1 << 0;
        public const ushort ReleaseOnPress = 1 << 1;   // release by tapping SHOOT/TRICK instead of releasing a held button
        public const ushort StartedWithTrick = 1 << 2; // TRICK is the held release button
        public const ushort Resolved = 1 << 3;         // swipe/steal/bite already resolved
        public const ushort NoExposure = 1 << 4;       // gather is hard to strip
        public const ushort ForcedDunk = 1 << 5;
        public const ushort FromBehindOk = 1 << 6;     // contest has no behind penalty (Chase-Down)
        public const ushort HangUsed = 1 << 7;
        public const ushort Uncontestable = 1 << 8;    // Amped Double Clutch
        public const ushort HalfContest = 1 << 9;      // Amped Drizzle fadeaway
        public const ushort DrainOnMake = 1 << 10;     // Amped Launch Step
        public const ushort Hook = 1 << 11;
        public const ushort AutoContest = 1 << 12;     // Rim Lock jump
        public const ushort CatchBlock = 1 << 13;      // Amped Rim Lock
        public const ushort Repeat = 1 << 14;          // Amped Frame Skip / Monsoon Step second hop pending
        public const ushort Deep = 1 << 15;            // gather started beyond the arc
    }

    /// <summary>Bit flags on <see cref="FighterState.Status"/>.</summary>
    public static class StatusFlag
    {
        public const ushort SimpleMode = 1 << 0;
        public const ushort HeatCheck = 1 << 1;
        public const ushort BlowByDone = 1 << 2;  // this possession's blow-by already fired
        public const ushort InContact = 1 << 3;   // drive/post contact this frame
        public const ushort WasInContact = 1 << 4;
        public const ushort Protecting = 1 << 5;
        public const ushort Driving = 1 << 6;
        public const ushort Grounded = 1 << 7;
    }
}
