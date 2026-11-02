using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.UI
{
    public enum DummyBehavior : byte { Contester, Waller, Driver, Committer, Shaker, Shooter, FinisherUser, Idle }

    public enum LessonGoal : byte { BlowBy, ChargeDrawn, MakeOverWall, Bites, AnkleBreak, PerfectReleases, AmpedTrick, FinisherLanded, Denial, Steal }

    /// <summary>One Court School lesson: a scripted dummy and a pass check read from sim events.</summary>
    public sealed class LessonDef
    {
        public string Id;
        public string Title;
        public string Body;
        public DummyBehavior Dummy;
        public LessonGoal Goal;
        public int Count = 1;
        public bool PlayerStartsWithBall = true;
        public bool InfiniteHype;

        public static LessonDef[] All()
        {
            return new[]
            {
                new LessonDef { Id = "read_drive", Title = Loc.T("school.read_drive.title", "The Read: drive past a contest"), Body = Loc.T("school.read_drive.body", "At the arc the defender jumps to contest. Hold TURBO and drive toward the hoop while they are in the air: that is a blow-by."), Dummy = DummyBehavior.Contester, Goal = LessonGoal.BlowBy },
                new LessonDef { Id = "read_wall", Title = Loc.T("school.read_wall.title", "The Read: wall the drive"), Body = Loc.T("school.read_wall.body", "The attacker loves to drive. Hold DOWN to plant your feet early: a drive into a planted defender is a charge."), Dummy = DummyBehavior.Driver, Goal = LessonGoal.ChargeDrawn, PlayerStartsWithBall = false },
                new LessonDef { Id = "read_shoot", Title = Loc.T("school.read_shoot.title", "The Read: shoot over the wall"), Body = Loc.T("school.read_shoot.body", "This defender plants to stop drives. A planted defender cannot contest: hold SHOOT and release at the top of your jump."), Dummy = DummyBehavior.Waller, Goal = LessonGoal.MakeOverWall },
                new LessonDef { Id = "shakes", Title = Loc.T("school.shakes.title", "Shakes bait commitments"), Body = Loc.T("school.shakes.body", "Use SHAKE with a direction: toward = crossover, away = step-back, down = spin, neutral = hesitation. A defender who commits during the move bites and loses Balance."), Dummy = DummyBehavior.Committer, Goal = LessonGoal.Bites, Count = 3 },
                new LessonDef { Id = "ankles", Title = Loc.T("school.ankles.title", "Ankle Break"), Body = Loc.T("school.ankles.body", "Chain shakes in rhythm. Empty the defender's Balance and they stumble: ANKLES."), Dummy = DummyBehavior.Committer, Goal = LessonGoal.AnkleBreak },
                new LessonDef { Id = "timing", Title = Loc.T("school.timing.title", "Release timing"), Body = Loc.T("school.timing.body", "Hold SHOOT to gather and let go at the peak of the jump. Land three Perfect releases."), Dummy = DummyBehavior.Idle, Goal = LessonGoal.PerfectReleases, Count = 3 },
                new LessonDef { Id = "steal", Title = Loc.T("school.steal.title", "Swipe the exposed ball"), Body = Loc.T("school.steal.body", "Every dribble move exposes the ball for a few frames. SHAKE on defense swipes: time it with their move. A whiff leaves you off-balance."), Dummy = DummyBehavior.Shaker, Goal = LessonGoal.Steal, PlayerStartsWithBall = false },
                new LessonDef { Id = "amped", Title = Loc.T("school.amped.title", "Hype and Amped Tricks"), Body = Loc.T("school.amped.body", "Hype comes from winning reads and scoring with style. Swipe up from TRICK to spend one bar on the Amped version."), Dummy = DummyBehavior.Idle, Goal = LessonGoal.AmpedTrick, InfiniteHype = true },
                new LessonDef { Id = "finisher", Title = Loc.T("school.finisher.title", "Signature Finisher"), Body = Loc.T("school.finisher.body", "With three bars, move into your trigger zone (shown in the hitbox view) and press FINISHER."), Dummy = DummyBehavior.Idle, Goal = LessonGoal.FinisherLanded, InfiniteHype = true },
                new LessonDef { Id = "denial", Title = Loc.T("school.denial.title", "Denial"), Body = Loc.T("school.denial.body", "When the opponent fires a Finisher and you hold a bar, a timing ring closes. Press DENY as it meets the target."), Dummy = DummyBehavior.FinisherUser, Goal = LessonGoal.Denial, PlayerStartsWithBall = false, InfiniteHype = true },
            };
        }
    }

    /// <summary>Scripted Court School opponent (player 2). Simple, readable rules; never reads the RNG.</summary>
    public sealed class LessonDummy : IInputSource
    {
        private readonly DummyBehavior _b;
        private readonly SimTables _t;
        private int _timer;
        private int _cycle;

        public LessonDummy(DummyBehavior behavior, SimTables tables)
        {
            _b = behavior;
            _t = tables;
        }

        public FrameInput NextInput(in GameState s, int player)
        {
            if (s.Phase != Phase.Live) return FrameInput.None;
            FighterState me = s.Get(player);
            FighterState opp = s.Get(1 - player);
            bool defending = s.Ball.Mode == BallMode.Held && s.Ball.Holder != player;
            bool attacking = s.Ball.Mode == BallMode.Held && s.Ball.Holder == player;
            Buttons b = Buttons.None;
            _timer++;
            float gap = (me.X - opp.X).ToFloat();

            if (defending)
            {
                // Stay in front, then the scripted commitment.
                float target = opp.X.ToFloat() + 1.05f;
                float dx = target - me.X.ToFloat();
                if (dx > 0.15f) b |= Buttons.Right;
                else if (dx < -0.15f) b |= Buttons.Left;
                switch (_b)
                {
                    case DummyBehavior.Contester:
                        if (opp.X <= _t.T.Court.ArcX + Fix.FromInt(1) && gap < 2f && _timer % 50 == 0 && me.Action == ActionId.Idle) b |= Buttons.Shoot;
                        break;
                    case DummyBehavior.Waller:
                        if (gap < 3f) b = Buttons.Down;
                        break;
                    case DummyBehavior.Committer:
                        if (opp.Action == ActionId.Shake && opp.ActionFrame == 3 && me.Action == ActionId.Idle)
                        {
                            _cycle++;
                            b |= (_cycle % 3) == 0 ? Buttons.Shake : ((_cycle % 3) == 1 ? Buttons.Shoot : Buttons.Down);
                        }
                        break;
                }
                return new FrameInput(b);
            }
            if (attacking)
            {
                switch (_b)
                {
                    case DummyBehavior.Driver:
                        if (_timer % 150 > 40) b |= Buttons.Right | Buttons.Turbo;
                        if (Fix.Abs(_t.T.Court.RimX - me.X) < _t.T.Shot.FinishRangeDriving && _timer % 20 == 0) b |= Buttons.Shoot;
                        break;
                    case DummyBehavior.Shaker:
                        if (_timer % 45 == 0) b |= Buttons.Shake | ((_timer / 45) % 2 == 0 ? Buttons.Right : Buttons.Down);
                        break;
                    case DummyBehavior.FinisherUser:
                    {
                        FinisherDefinition fd = _t.Fighters[me.CharIndex].Def.Finisher;
                        Fix mid = (fd.ZoneMin + fd.ZoneMax) / 2;
                        if (me.X < mid - Fix.Ratio(1, 4)) b |= Buttons.Right;
                        else if (me.X > mid + Fix.Ratio(1, 4)) b |= Buttons.Left;
                        else if (_timer % 30 == 0) b |= Buttons.Finisher;
                        break;
                    }
                    case DummyBehavior.Shooter:
                        if (_timer % 90 == 0) b |= Buttons.Shoot;
                        break;
                    default:
                        // Hand the ball back quickly: walk to the arc and shoot a set shot.
                        if (_timer % 120 == 0) b |= Buttons.Shoot;
                        break;
                }
                return new FrameInput(b);
            }
            // Loose ball: go get it.
            float bx = s.Ball.X.ToFloat() - me.X.ToFloat();
            if (bx > 0.2f) b |= Buttons.Right;
            else if (bx < -0.2f) b |= Buttons.Left;
            return new FrameInput(b);
        }
    }

    /// <summary>Tracks lesson progress from sim events.</summary>
    public sealed class LessonRunner
    {
        public readonly LessonDef Def;
        public readonly LessonDummy Dummy;
        public int Progress { get; private set; }
        public bool Passed => Progress >= Def.Count;
        private bool _defenderPlantedAtRelease;

        public LessonRunner(LessonDef def, SimTables tables)
        {
            Def = def;
            Dummy = new LessonDummy(def.Dummy, tables);
        }

        public void OnEvent(in SimEvent e, int local)
        {
            if (Passed) return;
            switch (Def.Goal)
            {
                case LessonGoal.BlowBy: if (e.Type == SimEventType.BlowBy && e.Actor == local) Progress++; break;
                case LessonGoal.ChargeDrawn: if (e.Type == SimEventType.Charge && e.Actor == local) Progress++; break;
                case LessonGoal.MakeOverWall:
                    if (e.Type == SimEventType.ShotRelease && e.Actor == local) _defenderPlantedAtRelease = true;
                    if (e.Type == SimEventType.ShotMade && e.Actor == local && _defenderPlantedAtRelease) Progress++;
                    if (e.Type == SimEventType.ShotMissed) _defenderPlantedAtRelease = false;
                    break;
                case LessonGoal.Bites: if (e.Type == SimEventType.Bite && e.B == local) Progress++; break;
                case LessonGoal.AnkleBreak: if (e.Type == SimEventType.AnkleBreak && e.A == local) Progress++; break;
                case LessonGoal.PerfectReleases: if (e.Type == SimEventType.ShotRelease && e.Actor == local && e.A == (int)ReleaseGrade.Perfect) Progress++; break;
                case LessonGoal.AmpedTrick: if (e.Type == SimEventType.TrickStart && e.Actor == local && e.B == 1) Progress++; break;
                case LessonGoal.FinisherLanded: if (e.Type == SimEventType.FinisherScore && e.Actor == local) Progress++; break;
                case LessonGoal.Denial: if (e.Type == SimEventType.Denied && e.Actor == local) Progress++; break;
                case LessonGoal.Steal: if (e.Type == SimEventType.Steal && e.Actor == local) Progress++; break;
            }
        }
    }
}
