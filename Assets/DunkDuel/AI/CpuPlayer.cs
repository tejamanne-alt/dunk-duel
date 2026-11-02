using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.AI
{
    /// <summary>
    /// CPU opponent. Plays by human rules: it sees the match through a snapshot delayed by its tier's
    /// reaction frames (its own fighter it knows exactly), never reads the RNG or a shot's rolled outcome,
    /// and emits ordinary controller inputs into the same sim. Uses its own RNG stream.
    /// </summary>
    public sealed partial class CpuPlayer : IInputSource
    {
        private const int HistorySize = 64;

        private readonly SimTables _tb;
        private readonly Tuning T;
        private readonly CpuTiers _tiers;
        private readonly int _player;
        private readonly HabitModel _habits = new HabitModel();
        private readonly GameState[] _history = new GameState[HistorySize];
        private int _histCount;
        private int _histHead;
        private ulong _rng;

        private enum Plan : byte { None, Clear, Move, Drive, Shoot, Shake, Trick, Post, Finisher, Guard, Wall, Chase }

        private Plan _plan;
        private int _planUntil;
        private int _nextDecision;
        private Buttons _prev;
        private int _releaseErr;
        private bool _releasePlanned;
        private int _denyFrame = -1;
        private int _wallUntil;
        private int _shakeChain;
        private int _shakeDir;
        private int _postBumpsSeen;
        private Fix _moveTarget;
        private int _role = -1;
        private bool _seenPrevValid;
        private GameState _seenPrev;
        private int _lastOppCommitTick;
        private Fix _expectedGradeBonus;
        private int _patienceUntil;
        private int _stance; // 0 normal, 1 tight (expect a shot), 2 loose (expect a drive)
        private int _stanceUntil;
        private int _contestAt = -1;

        public CpuPlayer(SimTables tables, CpuTiers tiers, CpuTier tier, int player, ulong seed)
        {
            _tb = tables;
            T = tables.T;
            _tiers = tiers;
            Tier = tiers[tier];
            _player = player;
            _rng = Rng.Seed(seed ^ 0xA1A1A1A1UL);
            _personalityIndex = -1;
            _expectedGradeBonus = ExpectedGradeBonus(Tier.ReleaseError);
        }

        private int _personalityIndex;

        /// <summary>Current difficulty; Rival mode swaps this between rounds.</summary>
        public TierParams Tier { get; private set; }

        public void SetTier(TierParams tier)
        {
            Tier = tier;
            _expectedGradeBonus = ExpectedGradeBonus(tier.ReleaseError);
        }

        public HabitModel Habits => _habits;

        private Personality Me(in GameState s)
        {
            if (_personalityIndex < 0) _personalityIndex = s.Get(_player).CharIndex;
            return _tiers.ByFighter[_personalityIndex];
        }

        public FrameInput NextInput(in GameState state, int player)
        {
            Push(state);
            GameState seen = Seen();
            if (Tier.LearnsHabits) Observe(seen);

            Buttons b;
            switch (state.Phase)
            {
                case Phase.Live:
                    b = Live(state, seen);
                    break;
                case Phase.Finisher:
                    b = Denial(state);
                    break;
                default:
                    ResetPlan();
                    _denyFrame = -1;
                    b = Buttons.None;
                    break;
            }
            if (state.Phase != Phase.Finisher) _denyFrame = -1;
            _prev = b;
            return new FrameInput(b);
        }

        // ------------------------------------------------------------------ perception

        private void Push(in GameState s)
        {
            _histHead = (_histHead + 1) % HistorySize;
            _history[_histHead] = s;
            if (_histCount < HistorySize) _histCount++;
        }

        /// <summary>The state as the CPU perceives it: <c>ReactionFrames</c> ticks old.</summary>
        private GameState Seen()
        {
            int lag = Tier.ReactionFrames;
            if (lag >= _histCount) lag = _histCount - 1;
            int idx = (_histHead - lag + HistorySize) % HistorySize;
            return _history[idx];
        }

        private static bool NextTickAdvances(in GameState s)
        {
            if (s.HitStop > 0) return false;
            if (s.SlowFrames > 0) return s.SlowPhase + 1 >= s.SlowDiv;
            return true;
        }

        private bool Chance(Fix p) => Rng.Unit(ref _rng) < p;

        private Fix Unit() => Rng.Unit(ref _rng);

        /// <summary>Signed release error in frames for one shot, from the tier's spread.</summary>
        private int RollReleaseError() => (Rng.Gaussianish(ref _rng) * Tier.ReleaseError).RoundToInt();

        /// <summary>Expected release bonus R for a tier, estimated once with a fixed-seed sample (deterministic).</summary>
        private Fix ExpectedGradeBonus(Fix spread)
        {
            ulong r = 12345UL;
            Fix sum = Fix.Zero;
            const int n = 64;
            for (int i = 0; i < n; i++)
            {
                int off = (Rng.Gaussianish(ref r) * spread).RoundToInt();
                if (off < 0) off = -off;
                Fix bonus = off <= T.Shot.PerfectFrames / 2 ? T.Shot.BonusPerfect
                    : (off <= T.Shot.GoodFrames / 2 ? T.Shot.BonusGood : T.Shot.PenaltyLate);
                sum += bonus;
            }
            return sum / n;
        }

        private void ResetPlan()
        {
            _plan = Plan.None;
            _releasePlanned = false;
            _shakeChain = 0;
        }

        private bool CanPress(Buttons b) => (_prev & b) == 0;

        // ------------------------------------------------------------------ habits

        private void Observe(in GameState seen)
        {
            if (!_seenPrevValid)
            {
                _seenPrev = seen;
                _seenPrevValid = true;
                return;
            }
            if (seen.Tick == _seenPrev.Tick) return;
            int opp = 1 - _player;
            FighterState now = seen.Get(opp);
            FighterState before = _seenPrev.Get(opp);
            bool started = now.Action != before.Action || (now.ActionFrame < before.ActionFrame && now.Action != ActionId.Idle);
            if (seen.Phase == Phase.Live && seen.Ball.Mode == BallMode.Held && started)
            {
                Fix dist = Fix.Abs(T.Court.RimX - (seen.Ball.Holder == opp ? now.X : seen.Get(_player).X));
                int zone = HabitModel.Zone(dist, T.Court.RimZone, T.Court.RimX - T.Court.ArcX);
                int bucket = HabitModel.Bucket(seen.ShotClock, T.Match.ShotClockFrames);
                if (seen.Ball.Holder == opp)
                {
                    switch (now.Action)
                    {
                        case ActionId.Shake: _habits.SeeOffense(zone, bucket, OffenseChoice.Shake); break;
                        case ActionId.GatherJumper: _habits.SeeOffense(zone, bucket, OffenseChoice.Shoot); break;
                        case ActionId.GatherFinish:
                            _habits.SeeOffense(zone, bucket, before.VX >= T.Read.DriveMinSpeed ? OffenseChoice.Drive : OffenseChoice.Shoot);
                            break;
                        case ActionId.PostUp: _habits.SeeOffense(zone, bucket, OffenseChoice.Post); break;
                        case ActionId.Trick: _habits.SeeOffense(zone, bucket, OffenseChoice.Trick); break;
                    }
                    if ((now.Status & StatusFlag.Driving) != 0 && (before.Status & StatusFlag.Driving) == 0)
                    {
                        _habits.SeeOffense(zone, bucket, OffenseChoice.Drive);
                    }
                }
                else
                {
                    switch (now.Action)
                    {
                        case ActionId.Wall: _habits.SeeDefense(zone, bucket, DefenseChoice.Wall); _lastOppCommitTick = seen.Tick; break;
                        case ActionId.Contest: _habits.SeeDefense(zone, bucket, DefenseChoice.Contest); _lastOppCommitTick = seen.Tick; break;
                        case ActionId.Swipe: _habits.SeeDefense(zone, bucket, DefenseChoice.Swipe); _lastOppCommitTick = seen.Tick; break;
                        case ActionId.Trick: _habits.SeeDefense(zone, bucket, DefenseChoice.Swipe); _lastOppCommitTick = seen.Tick; break;
                    }
                }
            }
            _seenPrev = seen;
        }

        // ------------------------------------------------------------------ live

        private Buttons Live(in GameState s, in GameState seen)
        {
            int role = s.Ball.Mode == BallMode.Held ? (s.Ball.Holder == _player ? 0 : 1) : 2;
            if (role != _role)
            {
                ResetPlan();
                _role = role;
                _nextDecision = s.Tick + Tier.ReactionFrames / 2;
                _contestAt = -1;
                if (role == 0) _patienceUntil = s.Tick + Rng.Range(ref _rng, _tiers.Table.PatienceMin, _tiers.Table.PatienceMax + 1);
            }
            switch (role)
            {
                case 0: return Offense(s, seen);
                case 1: return Defense(s, seen);
                default: return Loose(s, seen);
            }
        }

        // ------------------------------------------------------------------ denial

        private Buttons Denial(in GameState s)
        {
            if (s.Fin.Attacker == _player || !s.Fin.HasRing || s.Fin.Attempted) return Buttons.None;
            if (_denyFrame < 0)
            {
                bool attempt = Tier.HypeStyle != HypeStyle.Rarely || Chance(Fix.Ratio(1, 3));
                if (!attempt)
                {
                    _denyFrame = int.MaxValue;
                    return Buttons.None;
                }
                int center = s.Fin.DenyStart + s.Fin.DenyFrames / 2;
                _denyFrame = center + (Rng.Gaussianish(ref _rng) * Tier.DenyError).RoundToInt();
                if (_denyFrame < 1) _denyFrame = 1;
            }
            if (NextTickAdvances(s) && s.Fin.Frame + 1 == _denyFrame && CanPress(Buttons.Finisher)) return Buttons.Finisher;
            return Buttons.None;
        }
    }
}
