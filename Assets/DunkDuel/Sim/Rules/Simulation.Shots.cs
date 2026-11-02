using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Gathers, release timing, contest and block resolution, the make roll
    /// P(make) = clamp(B + R - C - K), outcome selection, ballistic paths and scoring.
    /// </summary>
    public sealed partial class Simulation
    {
        // ------------------------------------------------------------------ starting a shot

        /// <summary>SHOOT with the ball: a layup or dunk near the rim, otherwise a jumper.</summary>
        private bool TryStartShot(ref GameState s, int i, Buttons startButton, ushort flags)
        {
            if (s.NeedsClear) return false;
            ref FighterState f = ref GameState.Fighter(ref s, i);
            Fix dist = Fix.Abs(T.Court.RimX - f.X);
            bool driving = (f.Status & StatusFlag.Driving) != 0 || f.VX >= T.Read.DriveMinSpeed;
            Fix range = driving ? T.Shot.FinishRangeDriving : T.Shot.FinishRange;
            if (dist <= range) StartFinish(ref s, i, startButton, flags, false, 0, 0, false);
            else StartJumper(ref s, i, startButton, flags, 0, null);
            return true;
        }

        private ushort ReleaseFlags(in FighterState f, Buttons startButton, bool trickRelease)
        {
            ushort flags = 0;
            if (startButton == Buttons.Trick) flags |= ActionFlag.StartedWithTrick;
            // A gather started from a trick without the button still held is released with a tap.
            if (trickRelease && !f.IsHeld(startButton)) flags |= ActionFlag.ReleaseOnPress;
            return flags;
        }

        /// <param name="hook">Granite Hook definition (high release), or null for a regular jumper.</param>
        private void StartJumper(ref GameState s, int i, Buttons startButton, ushort flags, int gatherReduction, MoveDefinition hook)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            int peak = (hook != null ? hook.Params.PeakFrames : st.Def.JumperGatherFrames) - gatherReduction;
            if (st.Def.Mastery == Mastery.Rhythm) peak -= f.Rhythm * st.Def.MasteryParams.RhythmFramesPerStack;
            int rise = T.Shot.JumperRiseFrames;
            if (peak < rise + 1) peak = rise + 1;
            ushort release = ReleaseFlags(f, startButton, startButton == Buttons.Trick);
            SetAction(ref f, ActionId.GatherJumper);
            f.Peak = peak;
            f.Takeoff = peak - rise;
            f.ActionFlags = (ushort)(flags | release | (hook != null ? ActionFlag.Hook : 0));
            f.PendingKind = hook != null ? ShotKind.Hook : ShotKind.Jumper;
            bool deep = f.X <= T.Court.ArcX;
            f.PendingPoints = (byte)(deep ? T.Match.PointsDeep : T.Match.PointsInside);
            if (deep) f.ActionFlags |= ActionFlag.Deep;
            Emit(ref s, SimEventType.GatherStart, i, (int)f.PendingKind, 0);
        }

        /// <param name="marker">MoveSlot marker: 1 = Double Clutch pump (bait window), 2 = Launch Step.</param>
        /// <param name="autoHang">Take off at once and hang at the apex (Amped Launch Step from the free-throw line).</param>
        private void StartFinish(ref GameState s, int i, Buttons startButton, ushort flags, bool forcedDunk, int peakOverride, byte marker, bool autoHang)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            Fix dist = Fix.Abs(T.Court.RimX - f.X);
            bool dunk = st.CanDunk && (forcedDunk || dist <= T.Shot.DunkRange);
            int rise = dunk ? st.Def.JumpRiseFrames : T.Shot.LayupRiseFrames;
            int hang = autoHang ? st.Def.MasteryParams.HangFrames : 0;
            int peak = peakOverride > 0 ? peakOverride : st.Def.FinishGatherFrames;
            int takeoff = autoHang ? 1 : peak - rise;
            if (autoHang) peak = takeoff + rise + hang;
            if (takeoff < 1) takeoff = 1;
            Fix targetX = T.Court.RimX - (dunk ? T.Shot.DunkApproachOffset : T.Shot.LayupApproachOffset);
            Fix vx = (targetX - f.X) / peak;
            Fix cap = st.DriveSpeed;
            if (marker == 2) cap = cap * st.Tricks[(int)TrickSlot.Forward].Params.ApproachSpeedScale;
            vx = Fix.Clamp(vx, -cap, cap);

            ushort release = ReleaseFlags(f, startButton, startButton == Buttons.Trick);
            SetAction(ref f, ActionId.GatherFinish);
            f.Peak = peak;
            f.Takeoff = takeoff;
            f.VX = vx;
            f.ActionFlags = (ushort)(flags | release | (dunk && forcedDunk ? ActionFlag.ForcedDunk : 0));
            if (autoHang)
            {
                f.ActionFlags |= ActionFlag.HangUsed;
                f.Param = takeoff + rise;
            }
            f.PendingKind = dunk ? ShotKind.Dunk : ShotKind.Layup;
            f.PendingPoints = (byte)T.Match.PointsInside;
            f.MoveSlot = marker;
            Emit(ref s, SimEventType.GatherStart, i, (int)f.PendingKind, 0);
        }

        private void AdvanceGather(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            if (RoleOf(ref s, i) != Role.Handler)
            {
                SetAction(ref f, ActionId.Idle);
                return;
            }
            FighterStats st = Stats(f);
            bool finish = f.Action == ActionId.GatherFinish;
            if (!finish && f.Grounded) Friction(ref f);

            if (f.ActionFrame == f.Takeoff && f.Grounded)
            {
                int rise = f.Peak - f.Takeoff;
                if ((f.ActionFlags & ActionFlag.HangUsed) != 0) rise -= st.Def.MasteryParams.HangFrames; // auto hang at the apex
                f.VY = -T.Physics.FighterGravity * rise;
                f.Status &= unchecked((ushort)~StatusFlag.Grounded);
                if ((f.ActionFlags & ActionFlag.HalfContest) != 0)
                {
                    // Amped Drizzle fadeaway drifts away from the hoop.
                    MoveDefinition dz = st.Tricks[(int)TrickSlot.Forward];
                    f.VX = -dz.Params.AmpedFadeDistance / (rise * 2);
                }
                Emit(ref s, SimEventType.Jump, i, 0, 0);
            }

            // Hang Time: one mid-air adjustment on a drive finish or dunk.
            if (finish && !f.Grounded && st.Def.Mastery == Mastery.HangTime && (f.ActionFlags & ActionFlag.HangUsed) == 0
                && Pressed(f, Buttons.Shake))
            {
                Consume(ref f, Buttons.Shake);
                MasteryParams mp = st.Def.MasteryParams;
                f.ActionFlags |= ActionFlag.HangUsed;
                f.Param = f.ActionFrame;
                f.Peak += mp.HangFrames;
                f.VY = Fix.Zero;
                int dir = PressDir(f).Horizontal();
                if (dir != 0 && mp.HangFrames > 0) f.VX += mp.HangNudge / mp.HangFrames * dir;
                Emit(ref s, SimEventType.HangTime, i, mp.HangFrames, dir);
            }

            bool release = false;
            if ((f.ActionFlags & ActionFlag.ReleaseOnPress) != 0)
            {
                if (Pressed(f, Buttons.Shoot)) { Consume(ref f, Buttons.Shoot); release = true; }
                else if (Pressed(f, Buttons.Trick)) { Consume(ref f, Buttons.Trick); release = true; }
            }
            else
            {
                Buttons b = (f.ActionFlags & ActionFlag.StartedWithTrick) != 0 ? Buttons.Trick : Buttons.Shoot;
                if (!f.IsHeld(b)) release = true;
            }
            if (f.ActionFrame >= f.Peak + T.Shot.AutoReleaseAfterPeak) release = true;
            if (release) s.PendingRelease = (byte)i;
        }

        // ------------------------------------------------------------------ release

        private static bool InWindow(int offset, int width)
        {
            int lo = -(width / 2);
            int hi = width - (width / 2) - 1;
            return offset >= lo && offset <= hi;
        }

        public ReleaseGrade GradeRelease(in FighterState f, int offset, bool heat)
        {
            FighterStats st = Stats(f);
            Fix perfect = st.PerfectFrames > 0 ? st.PerfectFrames : T.Shot.PerfectFrames;
            Fix good = T.Shot.GoodFrames;
            if (heat) perfect = perfect * (Fix.One + T.Heat.PerfectWiden);
            if ((f.Status & StatusFlag.SimpleMode) != 0)
            {
                perfect = perfect * T.Shot.SimpleWindowScale;
                good = good * T.Shot.SimpleWindowScale;
            }
            int pw = perfect.RoundToInt();
            int gw = good.RoundToInt();
            if (gw < pw) gw = pw;
            if (InWindow(offset, pw)) return ReleaseGrade.Perfect;
            if (InWindow(offset, gw)) return ReleaseGrade.Good;
            return offset < 0 ? ReleaseGrade.Early : ReleaseGrade.Late;
        }

        private void ReleaseShot(ref GameState s, int shooter)
        {
            ref FighterState f = ref GameState.Fighter(ref s, shooter);
            ref FighterState d = ref GameState.Fighter(ref s, 1 - shooter);
            FighterStats st = Stats(f);
            ShotKind kind = f.PendingKind;
            ushort flags = f.ActionFlags;
            bool heat = (f.Status & StatusFlag.HeatCheck) != 0;
            int offset = f.ActionFrame - f.Peak;
            ReleaseGrade grade = GradeRelease(f, offset, heat);

            // Release point.
            Fix rx, ry;
            if (kind == ShotKind.Dunk || kind == ShotKind.Layup)
            {
                rx = f.X + T.Physics.HoldForward * f.Facing;
                ry = f.Y + st.Reach;
            }
            else
            {
                Fix factor = T.Shot.ReleaseHeightFactor;
                if (kind == ShotKind.Hook) factor = st.Tricks[(int)TrickSlot.Back].Params.ReleaseFactor;
                rx = f.X + T.Shot.ReleaseForward * f.Facing;
                ry = f.Y + st.Height * factor;
            }

            Fix horiz = Fix.Abs(T.Court.RimX - f.X);
            ShotZone zone;
            if (kind == ShotKind.Dunk || kind == ShotKind.Layup || horiz <= T.Court.RimZone) zone = ShotZone.Rim;
            else zone = (flags & ActionFlag.Deep) != 0 ? ShotZone.Deep : ShotZone.Mid;
            int points = f.PendingPoints;

            Fix q = ContestQuality(ref s, shooter, ry, out bool canBlock, out Fix blockScore, kind, grade);
            if ((flags & ActionFlag.Uncontestable) != 0
                && InWindow(offset, st.Tricks[(int)TrickSlot.Back].Params.AmpedUncontestableFrames))
            {
                // Amped Double Clutch: the reverse finish cannot be contested around its peak.
                q = Fix.Zero;
                canBlock = false;
            }

            f.FgAtt++;
            if (zone == ShotZone.Deep) f.DeepAtt++;
            Emit(ref s, SimEventType.ShotRelease, shooter, (int)grade, (int)zone);

            if (canBlock && blockScore >= T.Contest.BlockThreshold)
            {
                Block(ref s, shooter, rx, ry, heat);
                return;
            }

            // The roll: once, at release, with the match RNG.
            Fix b = zone == ShotZone.Rim ? st.BaseRim : (zone == ShotZone.Deep ? st.BaseDeep : st.BaseMid);
            Fix r = grade == ReleaseGrade.Perfect ? T.Shot.BonusPerfect : (grade == ReleaseGrade.Good ? T.Shot.BonusGood : T.Shot.PenaltyLate);
            Fix c = T.Shot.ContestMax * q;
            if ((flags & ActionFlag.HalfContest) != 0) c = c * st.Tricks[(int)TrickSlot.Forward].Params.AmpedContestScale;
            Fix k = Fix.Zero;
            if (f.Balance < T.Shot.BalancePenaltyBelow)
            {
                k = T.Shot.BalancePenaltyMax * (T.Shot.BalancePenaltyBelow - f.Balance) / T.Shot.BalancePenaltyBelow;
            }
            Fix p = Fix.Clamp(b + r - c - k, T.Shot.MinChance, T.Shot.MaxChance);
            bool made = Rng.Unit(ref s.Rng) < p;
            Fix u2 = Rng.Unit(ref s.Rng);
            Fix u3 = Rng.Unit(ref s.Rng);

            ShotResult result;
            if (made)
            {
                if (kind == ShotKind.Dunk) result = ShotResult.Swish;
                else
                {
                    Fix swish = grade == ReleaseGrade.Perfect ? T.Outcome.SwishPerfect : (grade == ReleaseGrade.Good ? T.Outcome.SwishGood : T.Outcome.SwishLate);
                    if (u2 < swish) result = ShotResult.Swish;
                    else if (zone == ShotZone.Mid && (u2 - swish) < (Fix.One - swish) * T.Outcome.BankShare) result = ShotResult.Bank;
                    else result = ShotResult.RimIn;
                }
            }
            else
            {
                Fix airball = kind == ShotKind.Jumper || kind == ShotKind.Hook ? T.Outcome.AirballBase + T.Outcome.AirballPerContest * q : Fix.Zero;
                result = u2 < airball ? ShotResult.Airball : ShotResult.RimOut;
            }

            Fix tx, ty;
            switch (result)
            {
                case ShotResult.Swish:
                    tx = T.Court.RimX;
                    ty = T.Court.RimY;
                    break;
                case ShotResult.RimIn:
                    tx = T.Court.RimX - T.Outcome.RimEdge;
                    ty = T.Court.RimY + T.Court.BallRadius;
                    break;
                case ShotResult.Bank:
                    tx = T.Court.BackboardX - T.Court.BallRadius;
                    ty = T.Court.RimY + T.Outcome.BankHeight;
                    break;
                case ShotResult.RimOut:
                    tx = T.Court.RimX + (u3 < Fix.Half ? -T.Outcome.RimEdge : T.Outcome.RimEdge);
                    ty = T.Court.RimY + T.Court.BallRadius;
                    break;
                default:
                    tx = T.Court.RimX - T.Outcome.AirballShortBy;
                    ty = T.Court.RimY;
                    break;
            }

            int frames;
            if (kind == ShotKind.Dunk) frames = T.Shot.DunkFlightFrames;
            else if (kind == ShotKind.Layup) frames = T.Shot.LayupFlightFrames;
            else frames = T.Shot.FlightBaseFrames + (T.Shot.FlightFramesPerMeter * Fix.Abs(tx - rx)).RoundToInt();
            if (frames < 1) frames = 1;

            bool posterizer = made && kind == ShotKind.Dunk && d.Action == ActionId.Contest && d.Y > T.Physics.AirborneHeight
                && q >= T.Contest.PosterizerMinContest;

            s.Shot = new ShotState
            {
                Active = true,
                Shooter = (byte)shooter,
                Kind = kind,
                Zone = zone,
                Grade = grade,
                Result = result,
                Stage = ShotStage.ToTarget,
                Made = made,
                Points = (byte)points,
                Posterizer = posterizer,
                HeatShot = heat,
                Buzzer = false,
                DrainOnMake = (flags & ActionFlag.DrainOnMake) != 0,
                Frame = 0,
                StageFrames = frames,
                TargetX = tx,
                TargetY = ty,
                MakeChance = p,
                Contest = q,
            };
            s.Ball.Mode = BallMode.Flight;
            s.Ball.X = rx;
            s.Ball.Y = ry;
            s.Ball.LastTouch = (byte)shooter;
            s.Ball.NoGrab = 0;
            SolveBallistic(ref s.Ball, tx, ty, frames);

            SetAction(ref f, ActionId.ShotRecover);
            if (kind == ShotKind.Dunk && made) f.VX = Fix.Zero;
        }

        /// <summary>Exact discrete ballistic solve: lands on (tx, ty) after exactly <paramref name="frames"/> steps.</summary>
        private void SolveBallistic(ref BallState ball, Fix tx, Fix ty, int frames)
        {
            Fix g = T.Physics.BallGravity;
            ball.VX = (tx - ball.X) / frames;
            ball.VY = (ty - ball.Y - g * (frames * (frames + 1)) / 2) / frames;
        }

        // ------------------------------------------------------------------ contest

        /// <summary>
        /// Contest quality Q in [0, 1] from timing (defender's height in the jump), distance and height.
        /// A planted defender cannot contest: leaving a plant takes 12 frames.
        /// </summary>
        private Fix ContestQuality(ref GameState s, int shooter, Fix releaseY, out bool canBlock, out Fix blockScore, ShotKind kind, ReleaseGrade grade)
        {
            canBlock = false;
            blockScore = Fix.Zero;
            ref FighterState f = ref GameState.Fighter(ref s, shooter);
            ref FighterState d = ref GameState.Fighter(ref s, 1 - shooter);
            FighterStats sf = Stats(f);
            FighterStats sd = Stats(d);
            ContestTable c = T.Contest;

            switch (d.Action)
            {
                case ActionId.Wall:
                case ActionId.Unplant:
                case ActionId.Stumble:
                case ActionId.Knockdown:
                case ActionId.OffBalance:
                    return Fix.Zero;
            }
            if (d.NoContest > 0) return Fix.Zero;

            bool airborne = d.Y > T.Physics.AirborneHeight;
            bool rimFinish = kind == ShotKind.Layup || kind == ShotKind.Dunk || kind == ShotKind.Hook;
            // A grounded hand: 0.2 against jumpers, verticality against rim finishes. A jump never contests worse.
            Fix floor = rimFinish ? c.GroundFactorRim : c.GroundFactor;
            Fix timing;
            if (d.Action == ActionId.Contest && airborne)
            {
                // Timing battle: quality falls off with frames from the defender's apex.
                int apex = d.Takeoff + sd.Def.JumpRiseFrames;
                int off = d.ActionFrame - apex;
                if (off < 0) off = -off;
                timing = Fix.Max(floor, Fix.Clamp01(Fix.One - Fix.Ratio(off, c.TimingWindow)));
            }
            else if (d.Action == ActionId.Idle || d.Action == ActionId.Contest || d.Action == ActionId.Swipe) timing = floor;
            else return Fix.Zero;

            Fix dx = d.X - f.X;
            Fix ad = Fix.Abs(dx);
            Fix far = c.FarDistance + c.ReachScale * (sd.Reach - c.ReferenceReach);
            Fix dist = ad <= c.CloseDistance ? Fix.One : Fix.Ramp(ad, far, c.CloseDistance);
            bool behind = dx < -c.BehindTolerance && f.Facing > 0 || dx > c.BehindTolerance && f.Facing < 0;
            bool fromBehindOk = (d.ActionFlags & ActionFlag.FromBehindOk) != 0;
            if (behind && !fromBehindOk) dist = dist * c.BehindFactor;

            Fix hand = d.Y + sd.Reach;
            Fix height = hand >= releaseY ? Fix.One : Fix.Clamp01(Fix.One - (releaseY - hand) / c.HeightFalloff);

            Fix q = Fix.Clamp01(timing * dist * height * sd.ContestMult);

            if (d.Action == ActionId.Contest && airborne && timing >= c.BlockTimingMin && dist >= c.BlockDistanceMin)
            {
                canBlock = true;
                Fix power = sd.BlockPower + (hand - releaseY) * c.BlockHeightBonusPerMeter;
                MoveDefinition dt = sd.Tricks[(int)TrickSlot.Defense];
                bool amped = (d.ActionFlags & ActionFlag.Amped) != 0;
                if ((d.ActionFlags & (ActionFlag.FromBehindOk | ActionFlag.AutoContest)) != 0 && dt.Params != null)
                {
                    power += amped ? dt.Params.AmpedBlockBonus : dt.Params.BlockBonus;
                }
                Fix protect = grade == ReleaseGrade.Perfect ? c.ProtectPerfect : (grade == ReleaseGrade.Good ? c.ProtectGood : Fix.Zero);
                switch (kind)
                {
                    case ShotKind.Dunk: protect += c.ProtectDunk + c.ProtectPerFinishing * (sf.Def.Attributes.Finishing - 5); break;
                    case ShotKind.Layup: protect += c.ProtectLayup + c.ProtectPerFinishing * (sf.Def.Attributes.Finishing - 5); break;
                    case ShotKind.Hook: protect += c.ProtectHook; break;
                    default: protect += c.ProtectJumper; break;
                }
                blockScore = q * power - protect;
            }
            return q;
        }

        private void Block(ref GameState s, int shooter, Fix rx, Fix ry, bool heat)
        {
            int blocker = 1 - shooter;
            ref FighterState f = ref GameState.Fighter(ref s, shooter);
            ref FighterState d = ref GameState.Fighter(ref s, blocker);
            d.Blocks++;
            bool catchIt = (d.ActionFlags & ActionFlag.CatchBlock) != 0;
            SetAction(ref f, ActionId.ShotRecover);
            MissBookkeeping(ref s, shooter, heat);
            GainHype(ref s, blocker, T.Hype.Block);
            EmitAt(ref s, SimEventType.Block, blocker, shooter, catchIt ? 1 : 0, rx, ry);
            Impact(ref s, SimEventType.Block);
            if (catchIt)
            {
                GiveBall(ref s, blocker, true, true);
                return;
            }
            s.Shot = default;
            s.Ball.Mode = BallMode.Loose;
            s.Ball.X = rx;
            s.Ball.Y = ry;
            s.Ball.VX = T.Contest.BlockDeflectVx * -f.Facing;
            s.Ball.VY = T.Contest.BlockDeflectVy;
            s.Ball.LastTouch = (byte)blocker;
            s.Ball.NoGrab = T.Shot.RimNoGrabFrames;
            Emit(ref s, SimEventType.LooseBall, 2, 0, 0);
        }

        // ------------------------------------------------------------------ flight

        private void ShotFlight(ref GameState s)
        {
            ref ShotState shot = ref s.Shot;
            ref BallState ball = ref s.Ball;
            switch (shot.Stage)
            {
                case ShotStage.ToTarget:
                case ShotStage.BankLeg:
                    ball.VY += T.Physics.BallGravity;
                    ball.X += ball.VX;
                    ball.Y += ball.VY;
                    shot.Frame++;
                    if (shot.Frame >= shot.StageFrames)
                    {
                        ball.X = shot.TargetX;
                        ball.Y = shot.TargetY;
                        if (shot.Stage == ShotStage.BankLeg) StartDrop(ref s);
                        else Arrive(ref s);
                    }
                    break;
                case ShotStage.OnRim:
                    shot.Frame++;
                    ball.X = Fix.MoveTowards(ball.X, T.Court.RimX, T.Outcome.RimEdge / T.Shot.RimInFrames);
                    if (shot.Frame >= shot.StageFrames) StartDrop(ref s);
                    break;
            }
        }

        private void Arrive(ref GameState s)
        {
            ref ShotState shot = ref s.Shot;
            switch (shot.Result)
            {
                case ShotResult.Swish:
                    StartDrop(ref s);
                    break;
                case ShotResult.RimIn:
                    s.Ball.RimTouched = true;
                    Emit(ref s, SimEventType.RimHit, 2, 1, 0);
                    shot.Stage = ShotStage.OnRim;
                    shot.Frame = 0;
                    shot.StageFrames = T.Shot.RimInFrames;
                    break;
                case ShotResult.Bank:
                    Emit(ref s, SimEventType.BackboardHit, 2, 0, 0);
                    shot.Stage = ShotStage.BankLeg;
                    shot.Frame = 0;
                    shot.StageFrames = T.Shot.BankLegFrames;
                    shot.TargetX = T.Court.RimX;
                    shot.TargetY = T.Court.RimY;
                    SolveBallistic(ref s.Ball, shot.TargetX, shot.TargetY, shot.StageFrames);
                    break;
                case ShotResult.RimOut:
                    {
                        s.Ball.RimTouched = true;
                        Emit(ref s, SimEventType.RimHit, 2, 0, 0);
                        Fix ux = Rng.Unit(ref s.Rng), uy = Rng.Unit(ref s.Rng);
                        s.Ball.VX = Fix.Lerp(T.Outcome.ReboundVxMin, T.Outcome.ReboundVxMax, ux);
                        s.Ball.VY = Fix.Lerp(T.Outcome.ReboundVyMin, T.Outcome.ReboundVyMax, uy);
                        Miss(ref s);
                        break;
                    }
                default:
                    Emit(ref s, SimEventType.Airball, shot.Shooter, 0, 0);
                    Miss(ref s);
                    break;
            }
        }

        private void Miss(ref GameState s)
        {
            int shooter = s.Shot.Shooter;
            bool heat = s.Shot.HeatShot;
            Emit(ref s, SimEventType.ShotMissed, shooter, (int)s.Shot.Result, 0);
            MissBookkeeping(ref s, shooter, heat);
            s.Shot.Active = false;
            s.Shot.Stage = ShotStage.None;
            s.Ball.Mode = BallMode.Loose;
            s.Ball.NoGrab = T.Shot.RimNoGrabFrames;
            Emit(ref s, SimEventType.LooseBall, 2, 0, 0);
            if (s.ClockExpired) ResolveClockExpiry(ref s);
        }

        private void MissBookkeeping(ref GameState s, int shooter, bool heatShot)
        {
            ref FighterState f = ref GameState.Fighter(ref s, shooter);
            f.Streak = 0;
            f.Rhythm = 0;
            if ((f.Status & StatusFlag.HeatCheck) != 0)
            {
                f.Status &= unchecked((ushort)~StatusFlag.HeatCheck);
                if (heatShot)
                {
                    GainHype(ref s, 1 - shooter, T.Heat.MissHype);
                    Emit(ref s, SimEventType.HeatCheckMiss, shooter, 0, 0);
                }
            }
        }

        private void StartDrop(ref GameState s)
        {
            ref ShotState shot = ref s.Shot;
            int shooter = shot.Shooter;
            EmitAt(ref s, SimEventType.NetSnap, shooter, (int)shot.Result, 0, T.Court.RimX, T.Court.RimY);
            if (shot.Zone == ShotZone.Deep && shot.Result == ShotResult.Swish)
            {
                Emit(ref s, SimEventType.Splash, shooter, 0, 0);
                Impact(ref s, SimEventType.Splash);
            }
            shot.Stage = ShotStage.Dropping;
            s.Ball.Mode = BallMode.Dead;
            s.Ball.X = T.Court.RimX;
            s.Ball.Y = T.Court.RimY;
            s.Ball.VX = Fix.Zero;
            s.Ball.VY = Fix.Zero;
            AwardScore(ref s, shooter, shot.Points, shot.Kind, shot.Zone, shot.Result, shot.Posterizer, shot.Buzzer, shot.DrainOnMake, false);
        }

        // ------------------------------------------------------------------ scoring

        private void AwardScore(ref GameState s, int scorer, int points, ShotKind kind, ShotZone zone, ShotResult result,
            bool posterizer, bool buzzer, bool drain, bool finisher)
        {
            ref FighterState f = ref GameState.Fighter(ref s, scorer);
            ref FighterState o = ref GameState.Fighter(ref s, 1 - scorer);
            FighterStats st = Stats(f);
            f.Score += points;
            if (finisher)
            {
                f.Finishers++;
            }
            else
            {
                f.FgMade++;
                if (zone == ShotZone.Deep) f.DeepMade++;
                if (kind == ShotKind.Dunk) f.Dunks++;
                int gain = kind == ShotKind.Dunk ? T.Hype.Dunk : (points >= T.Match.PointsDeep ? T.Hype.Make3 : T.Hype.Make2);
                GainHype(ref s, scorer, gain);
            }
            GainHype(ref s, 1 - scorer, T.Hype.ScoredOn);

            f.Streak++;
            o.Streak = 0;
            o.Status &= unchecked((ushort)~StatusFlag.HeatCheck);
            if ((f.Status & StatusFlag.HeatCheck) == 0 && f.Streak >= T.Heat.Streak)
            {
                f.Status |= StatusFlag.HeatCheck;
                Emit(ref s, SimEventType.HeatCheckOn, scorer, f.Streak, 0);
            }
            if (st.Def.Mastery == Mastery.Rhythm && zone == ShotZone.Deep && !finisher)
            {
                int max = st.Def.MasteryParams.RhythmMaxStacks;
                if (f.Rhythm < max)
                {
                    f.Rhythm++;
                    Emit(ref s, SimEventType.RhythmStack, scorer, f.Rhythm, 0);
                }
            }

            Emit(ref s, SimEventType.ShotMade, scorer, points, (int)result);
            if (posterizer)
            {
                f.Posters++;
                GainHype(ref s, scorer, T.Hype.Posterizer);
                Emit(ref s, SimEventType.Posterizer, scorer, 1 - scorer, 0);
                Impact(ref s, SimEventType.Posterizer);
                DamageBalance(ref s, 1 - scorer, T.Balance.PosterizedDamage);
                if (o.Action != ActionId.Stumble)
                {
                    SetAction(ref o, ActionId.Knockdown);
                    o.Param = T.Balance.PosterizedKnockdownFrames;
                }
            }
            if (drain)
            {
                DamageBalance(ref s, 1 - scorer, st.Tricks[(int)TrickSlot.Forward].Params.AmpedBalanceDrain);
            }
            if (buzzer)
            {
                Emit(ref s, SimEventType.BuzzerBeater, scorer, points, 0);
                Impact(ref s, SimEventType.BuzzerBeater);
            }

            if (s.Mode != MatchMode.Training)
            {
                if (f.Score >= T.Match.RoundTargetPoints || s.SuddenDeath)
                {
                    s.RoundWinner = (byte)scorer;
                }
                else if (s.ClockExpired)
                {
                    if (f.Score != o.Score) s.RoundWinner = (byte)(f.Score > o.Score ? scorer : 1 - scorer);
                    else
                    {
                        s.SuddenDeath = true;
                        Emit(ref s, SimEventType.SuddenDeath, scorer, 0, 0);
                    }
                }
            }
            s.Offense = (byte)scorer;
            s.NeedsClear = false;
            s.Phase = Phase.Score;
            s.PhaseFrame = 0;
            if (f.Grounded && f.Action == ActionId.Idle) SetAction(ref f, ActionId.Celebrate);
        }
    }
}
