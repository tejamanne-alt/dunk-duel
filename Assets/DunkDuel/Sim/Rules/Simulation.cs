using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>Constant match parameters. Stored in replays and exchanged in the netcode handshake.</summary>
    public struct MatchSetup
    {
        public int Fighter0;
        public int Fighter1;
        public ulong Seed;
        public int RoundsToWin;
        public MatchMode Mode;
        public bool Simple0;
        public bool Simple1;
        public int FirstOffense;
        public bool InfiniteHype;
        public int CourtIndex;

        public static MatchSetup Versus(int fighter0, int fighter1, ulong seed, int roundsToWin = 2) => new MatchSetup
        {
            Fighter0 = fighter0,
            Fighter1 = fighter1,
            Seed = seed,
            RoundsToWin = roundsToWin,
            Mode = MatchMode.Versus,
            FirstOffense = 0,
        };
    }

    /// <summary>The deterministic rules: next state from the previous state and both players' inputs.</summary>
    public interface ISimulation
    {
        SimTables Tables { get; }
        GameState CreateInitialState(in MatchSetup setup);
        StepResult Step(in GameState state, FrameInput p1, FrameInput p2);
    }

    /// <summary>Next state plus the events of that tick. <see cref="Events"/> is reused by the next Step call.</summary>
    public readonly struct StepResult
    {
        public readonly GameState State;
        public readonly SimEventBuffer Events;

        public StepResult(in GameState state, SimEventBuffer events)
        {
            State = state;
            Events = events;
        }
    }

    /// <summary>
    /// Dunk Duel rules at a fixed 60 Hz tick. Pure function of (state, inputs, tables): no clocks, no floats,
    /// no hidden mutable state between calls except the reusable event buffer.
    /// </summary>
    public sealed partial class Simulation : ISimulation
    {
        private readonly SimTables _tb;
        private readonly Tuning T;
        private readonly SimEventBuffer _events = new SimEventBuffer();

        public Simulation(SimTables tables)
        {
            _tb = tables;
            T = tables.T;
        }

        public SimTables Tables => _tb;

        public GameState CreateInitialState(in MatchSetup setup)
        {
            if (setup.Fighter0 < 0 || setup.Fighter0 >= _tb.FighterCount || setup.Fighter1 < 0 || setup.Fighter1 >= _tb.FighterCount)
                throw new System.ArgumentOutOfRangeException(nameof(setup), "unknown fighter index");
            var s = new GameState
            {
                Rng = Rng.Seed(setup.Seed),
                RoundsToWin = (byte)(setup.RoundsToWin < 1 ? 1 : setup.RoundsToWin),
                Mode = setup.Mode,
                InfiniteHype = setup.InfiniteHype,
                RoundWinner = GameState.None,
                MatchWinner = GameState.None,
                PendingRelease = GameState.None,
            };
            s.F0.CharIndex = (byte)setup.Fighter0;
            s.F1.CharIndex = (byte)setup.Fighter1;
            if (setup.Simple0) s.F0.Status |= StatusFlag.SimpleMode;
            if (setup.Simple1) s.F1.Status |= StatusFlag.SimpleMode;
            ResetInput(ref s.F0);
            ResetInput(ref s.F1);
            if (setup.InfiniteHype)
            {
                s.F0.Hype = T.Hype.Max;
                s.F1.Hype = T.Hype.Max;
            }
            StartRound(ref s, 0, setup.FirstOffense & 1);
            return s;
        }

        public StepResult Step(in GameState state, FrameInput p1, FrameInput p2)
        {
            _events.Clear();
            GameState s = state;
            s.Tick++;
            Ingest(ref s.F0, p1);
            Ingest(ref s.F1, p2);

            if (s.HitStop > 0)
            {
                s.HitStop--;
                return new StepResult(s, _events);
            }
            if (s.SlowFrames > 0)
            {
                s.SlowPhase++;
                if (s.SlowPhase < s.SlowDiv) return new StepResult(s, _events);
                s.SlowPhase = 0;
                s.SlowFrames--;
            }

            s.Frame++;
            RecordDirection(ref s.F0);
            RecordDirection(ref s.F1);
            Advance(ref s);
            AgeInputs(ref s.F0);
            AgeInputs(ref s.F1);
            if (s.InfiniteHype)
            {
                s.F0.Hype = T.Hype.Max;
                s.F1.Hype = T.Hype.Max;
            }
            return new StepResult(s, _events);
        }

        // ------------------------------------------------------------------ phases

        private void Advance(ref GameState s)
        {
            switch (s.Phase)
            {
                case Phase.RoundIntro:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    if (s.PhaseFrame >= T.Match.RoundIntroFrames) EnterCheck(ref s, s.Offense);
                    break;
                case Phase.CheckBall:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    UpdateBall(ref s);
                    if (s.PhaseFrame >= T.Match.CheckFrames) GoLive(ref s);
                    break;
                case Phase.Live:
                    s.PhaseFrame++;
                    LiveFrame(ref s);
                    break;
                case Phase.Score:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    UpdateBall(ref s);
                    if (s.PhaseFrame >= T.Match.ScorePauseFrames)
                    {
                        if (s.RoundWinner != GameState.None) EndRound(ref s);
                        else EnterCheck(ref s, 1 - s.Offense); // loser's ball
                    }
                    break;
                case Phase.StopPause:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    UpdateBall(ref s);
                    if (s.PhaseFrame >= T.Match.StopPauseFrames)
                    {
                        if (s.RoundWinner != GameState.None) EndRound(ref s);
                        else GoLive(ref s);
                    }
                    break;
                case Phase.Finisher:
                    FinisherFrame(ref s);
                    break;
                case Phase.RoundEnd:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    UpdateBall(ref s);
                    if (s.PhaseFrame >= T.Match.RoundEndFrames)
                    {
                        if (s.MatchWinner != GameState.None)
                        {
                            s.Phase = Phase.MatchEnd;
                            s.PhaseFrame = 0;
                            Emit(ref s, SimEventType.MatchEnd, s.MatchWinner, s.MatchWinner, 0);
                        }
                        else
                        {
                            StartRound(ref s, s.Round + 1, 1 - s.RoundWinner); // round loser starts with the ball
                        }
                    }
                    break;
                case Phase.MatchEnd:
                    s.PhaseFrame++;
                    SettleFighters(ref s);
                    break;
            }
        }

        private void LiveFrame(ref GameState s)
        {
            TickTimers(ref s.F0);
            TickTimers(ref s.F1);

            bool held = s.Ball.Mode == BallMode.Held;
            int h = held ? s.Ball.Holder : 0;
            bool handlerBehind = held && GameState.Fighter(ref s, h).X < GameState.Fighter(ref s, 1 - h).X;

            UpdateFighter(ref s, 0);
            UpdateFighter(ref s, 1);
            if (s.Phase != Phase.Live) return; // a Finisher started

            Integrate(ref s, 0);
            Integrate(ref s, 1);
            Interactions(ref s, handlerBehind);
            if (s.Phase != Phase.Live) return; // charge or similar stop

            if (s.PendingRelease != GameState.None)
            {
                int shooter = s.PendingRelease;
                s.PendingRelease = GameState.None;
                if (s.Ball.Mode == BallMode.Held && s.Ball.Holder == shooter) ReleaseShot(ref s, shooter);
            }

            UpdateFacing(ref s);
            UpdateBall(ref s);
            if (s.Phase != Phase.Live) return;
            CheckClear(ref s);
            Clocks(ref s);
            Meters(ref s, 0);
            Meters(ref s, 1);
        }

        private void StartRound(ref GameState s, int round, int offense)
        {
            s.Round = (byte)round;
            s.Phase = Phase.RoundIntro;
            s.PhaseFrame = 0;
            s.RoundClock = T.Match.RoundFrames;
            s.ShotClock = T.Match.ShotClockFrames;
            s.SuddenDeath = false;
            s.ClockExpired = false;
            s.RoundWinner = GameState.None;
            s.NeedsClear = false;
            s.Shot = default;
            s.Fin = default;
            s.HitStop = 0;
            s.SlowFrames = 0;
            ResetForRound(ref s.F0);
            ResetForRound(ref s.F1);
            s.Offense = (byte)offense;
            PlaceForCheck(ref s);
            GiveBall(ref s, offense, false, true);
            Emit(ref s, SimEventType.RoundIntro, offense, round, 0);
        }

        private void ResetForRound(ref FighterState f)
        {
            f.Score = 0;
            f.Balance = T.Balance.Max;
            f.BalanceIdle = 0;
            f.Streak = 0;
            f.Status &= unchecked((ushort)~(StatusFlag.HeatCheck | StatusFlag.BlowByDone | StatusFlag.InContact | StatusFlag.WasInContact));
            f.Rhythm = 0;
            f.ComboLinks = 0;
            f.ComboTimer = 0;
            f.PlantFrames = 0;
            f.Beaten = 0;
            f.Lag = 0;
            f.NoContest = 0;
            f.LastContactFrame = -100000; // no recent contact
            f.BumpTimer = -100000;
            SetAction(ref f, ActionId.Idle);
        }

        private void EnterCheck(ref GameState s, int offense)
        {
            s.Phase = Phase.CheckBall;
            s.PhaseFrame = 0;
            s.Offense = (byte)offense;
            s.NeedsClear = false;
            s.Shot = default;
            PlaceForCheck(ref s);
            GiveBall(ref s, offense, false, true);
            Emit(ref s, SimEventType.CheckBall, offense, 0, 0);
        }

        private void PlaceForCheck(ref GameState s)
        {
            ref FighterState o = ref GameState.Fighter(ref s, s.Offense);
            ref FighterState d = ref GameState.Fighter(ref s, 1 - s.Offense);
            Place(ref o, T.Court.CheckX, 1);
            Place(ref d, T.Court.CheckX + T.Match.CheckGap, -1);
        }

        private void Place(ref FighterState f, Fix x, int facing)
        {
            f.X = x;
            f.Y = Fix.Zero;
            f.VX = Fix.Zero;
            f.VY = Fix.Zero;
            f.Facing = (sbyte)facing;
            f.Status |= StatusFlag.Grounded;
            SetAction(ref f, ActionId.Idle);
        }

        private void GoLive(ref GameState s)
        {
            s.Phase = Phase.Live;
            s.PhaseFrame = 0;
            Emit(ref s, SimEventType.BallLive, s.Offense, 0, 0);
        }

        private void EndRound(ref GameState s)
        {
            int w = s.RoundWinner;
            ref FighterState winner = ref GameState.Fighter(ref s, w);
            winner.RoundsWon++;
            s.Phase = Phase.RoundEnd;
            s.PhaseFrame = 0;
            s.HitStop = 0;
            s.SlowFrames = 0;
            Emit(ref s, SimEventType.RoundEnd, w, w, s.Round);
            if (winner.RoundsWon >= s.RoundsToWin) s.MatchWinner = (byte)w;
        }

        /// <summary>Possession change. <paramref name="stop"/> marks a steal/block/charge/rebound that needs a clear.</summary>
        private void GiveBall(ref GameState s, int who, bool stop, bool resetShotClock)
        {
            ref FighterState f = ref GameState.Fighter(ref s, who);
            bool flips = s.Offense != who;
            s.Offense = (byte)who;
            s.Ball.Mode = BallMode.Held;
            s.Ball.Holder = (byte)who;
            s.Ball.LastTouch = (byte)who;
            s.Ball.NoGrab = 0;
            s.Ball.VX = Fix.Zero;
            s.Ball.VY = Fix.Zero;
            s.Ball.X = f.X + T.Physics.HoldForward * f.Facing;
            s.Ball.Y = f.Y + T.Physics.HoldHeight;
            s.Shot.Active = false;
            s.Shot.Stage = ShotStage.None;
            if (stop) s.NeedsClear = f.X > T.Court.ArcX;
            if (resetShotClock || flips) s.ShotClock = T.Match.ShotClockFrames;
            if (flips || resetShotClock) s.Ball.RimTouched = false;
            s.F0.Status &= unchecked((ushort)~StatusFlag.BlowByDone);
            s.F1.Status &= unchecked((ushort)~StatusFlag.BlowByDone);
            s.F0.ComboLinks = 0;
            s.F1.ComboLinks = 0;
            s.F0.ComboTimer = 0;
            s.F1.ComboTimer = 0;
        }

        private void CheckClear(ref GameState s)
        {
            if (!s.NeedsClear || s.Ball.Mode != BallMode.Held) return;
            ref FighterState h = ref GameState.Fighter(ref s, s.Ball.Holder);
            if (h.X <= T.Court.ArcX)
            {
                s.NeedsClear = false;
                Emit(ref s, SimEventType.Cleared, s.Ball.Holder, 0, 0);
            }
        }

        private void Clocks(ref GameState s)
        {
            if (s.Mode == MatchMode.Training) return;

            if (s.Ball.Mode == BallMode.Held && s.Ball.Holder == s.Offense && s.ShotClock > 0)
            {
                s.ShotClock--;
                if (s.ShotClock == 0)
                {
                    int loser = s.Offense;
                    GameState.Fighter(ref s, loser).Turnovers++;
                    Emit(ref s, SimEventType.ShotClockViolation, loser, 0, 0);
                    Impact(ref s, SimEventType.ShotClockViolation);
                    EnterCheck(ref s, 1 - loser);
                    return;
                }
            }

            if (!s.SuddenDeath && s.RoundClock > 0)
            {
                s.RoundClock--;
                if (s.RoundClock == 0)
                {
                    s.ClockExpired = true;
                    Emit(ref s, SimEventType.ClockExpired, s.Offense, 0, 0);
                    if (s.Ball.Mode == BallMode.Flight && s.Shot.Active)
                    {
                        s.Shot.Buzzer = true;
                        ImpactDef d = _tb.ImpactByEvent[(int)SimEventType.BuzzerBeater];
                        if (d != null && d.SlowFrames > 0)
                        {
                            s.SlowFrames = s.Shot.StageFrames - s.Shot.Frame + d.SlowFrames;
                            s.SlowDiv = (byte)d.SlowDiv;
                            s.SlowPhase = 0;
                        }
                    }
                    else
                    {
                        ResolveClockExpiry(ref s);
                    }
                }
            }
        }

        /// <summary>Round clock is at zero and no shot is pending: decide the round or go to sudden death.</summary>
        private void ResolveClockExpiry(ref GameState s)
        {
            if (!s.ClockExpired || s.SuddenDeath || s.RoundWinner != GameState.None) return;
            if (s.F0.Score != s.F1.Score)
            {
                s.RoundWinner = (byte)(s.F0.Score > s.F1.Score ? 0 : 1);
                if (s.Phase == Phase.Live) EndRound(ref s);
            }
            else
            {
                s.SuddenDeath = true;
                Emit(ref s, SimEventType.SuddenDeath, s.Offense, 0, 0);
            }
        }

        // ------------------------------------------------------------------ meters

        private void Meters(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            if (f.Balance < T.Balance.Max && f.Action != ActionId.Stumble)
            {
                f.BalanceIdle++;
                if (f.BalanceIdle > T.Balance.RegenDelayFrames)
                {
                    f.Balance = Fix.Min(T.Balance.Max, f.Balance + T.Balance.RegenPerFrame);
                }
            }
        }

        private void GainHype(ref GameState s, int who, int amount)
        {
            ref FighterState f = ref GameState.Fighter(ref s, who);
            amount = _tb.ScaleHype(f, amount);
            if (amount <= 0) return;
            int before = f.Hype / T.Hype.Bar;
            f.Hype = f.Hype + amount > T.Hype.Max ? T.Hype.Max : f.Hype + amount;
            int after = f.Hype / T.Hype.Bar;
            if (after > before) Emit(ref s, SimEventType.HypeBar, who, after, 0);
        }

        private bool CanSpend(in FighterState f, int cost) => f.Hype >= cost;

        private void Spend(ref FighterState f, int cost)
        {
            f.Hype -= cost;
            if (f.Hype < 0) f.Hype = 0;
        }

        /// <summary>Applies Balance damage after the target's resistance. Empty Balance is an Ankle Break.</summary>
        private void DamageBalance(ref GameState s, int victim, Fix amount)
        {
            ref FighterState f = ref GameState.Fighter(ref s, victim);
            if (f.Action == ActionId.Stumble || amount <= Fix.Zero) return;
            FighterStats st = _tb.Fighters[f.CharIndex];
            Fix dmg = amount * (Fix.One - st.Resist);
            if (dmg <= Fix.Zero) return;
            f.Balance -= dmg;
            f.BalanceIdle = 0;
            if (f.Balance <= Fix.Zero)
            {
                f.Balance = Fix.Zero;
                AnkleBreak(ref s, victim);
            }
        }

        private void AnkleBreak(ref GameState s, int victim)
        {
            ref FighterState f = ref GameState.Fighter(ref s, victim);
            int attacker = 1 - victim;
            SetAction(ref f, ActionId.Stumble);
            f.VX = Fix.Zero;
            f.PlantFrames = 0;
            f.Beaten = T.Balance.StumbleFrames;
            GameState.Fighter(ref s, attacker).AnkleBreaks++;
            GainHype(ref s, attacker, T.Hype.AnkleBreak);
            Emit(ref s, SimEventType.AnkleBreak, victim, attacker, 0);
            Impact(ref s, SimEventType.AnkleBreak);
        }

        // ------------------------------------------------------------------ events and impacts

        private void Emit(ref GameState s, SimEventType type, int actor, int a, int b)
        {
            Fix x, y;
            if (actor == 0) { x = s.F0.X; y = s.F0.Y; }
            else if (actor == 1) { x = s.F1.X; y = s.F1.Y; }
            else { x = s.Ball.X; y = s.Ball.Y; }
            _events.Add(s.Frame, type, actor, a, b, x, y);
        }

        private void EmitAt(ref GameState s, SimEventType type, int actor, int a, int b, Fix x, Fix y)
        {
            _events.Add(s.Frame, type, actor, a, b, x, y);
        }

        /// <summary>Applies the hit-stop / time-scale part of an impact recipe.</summary>
        private void Impact(ref GameState s, SimEventType type)
        {
            ImpactDef d = _tb.ImpactByEvent[(int)type];
            if (d == null) return;
            if (d.HitStop > s.HitStop) s.HitStop = d.HitStop;
            if (d.SlowFrames > 0 && d.SlowFrames >= s.SlowFrames)
            {
                s.SlowFrames = d.SlowFrames;
                s.SlowDiv = (byte)d.SlowDiv;
                s.SlowPhase = 0;
            }
        }

        private static void SetAction(ref FighterState f, ActionId action)
        {
            f.Action = action;
            f.ActionFrame = 0;
            f.ActionFlags = 0;
            f.Param = 0;
            f.MoveSlot = 0;
        }
    }
}
