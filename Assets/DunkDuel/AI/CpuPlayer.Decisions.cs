using DunkDuel.Sim;
using DunkDuel.Sim.Data;

namespace DunkDuel.AI
{
    public sealed partial class CpuPlayer
    {
        private FighterStats MyStats(in GameState s) => _tb.Fighters[s.Get(_player).CharIndex];
        private FighterStats OppStats(in GameState s) => _tb.Fighters[s.Get(1 - _player).CharIndex];

        private static Buttons Dir(int h, int v)
        {
            Buttons b = Buttons.None;
            if (h > 0) b |= Buttons.Right;
            if (h < 0) b |= Buttons.Left;
            if (v > 0) b |= Buttons.Up;
            if (v < 0) b |= Buttons.Down;
            return b;
        }

        // ================================================================== offense

        private Buttons Offense(in GameState s, in GameState seen)
        {
            FighterState me = s.Get(_player);
            FighterState opp = seen.Get(1 - _player);
            FighterStats ms = MyStats(s);
            Personality p = Me(s);
            Fix dist = Fix.Abs(T.Court.RimX - me.X);

            // 1) In a gather: hold until the planned release frame.
            if (me.Action == ActionId.GatherJumper || me.Action == ActionId.GatherFinish)
            {
                return ManageGather(s, me, opp, ms);
            }

            // 2) Busy actions: shake follow-ups and cancels.
            if (me.Action == ActionId.Shake || me.Action == ActionId.Trick)
            {
                return FollowUp(s, seen, me, opp, ms, p);
            }
            if (me.Action == ActionId.PostUp) return PostLoop(s, seen, me, opp, ms, p);
            if (me.Action != ActionId.Idle) return Buttons.None;

            // 3) Must clear first.
            if (s.NeedsClear)
            {
                _plan = Plan.Clear;
                return Buttons.Left | Buttons.Turbo;
            }

            // 4) Reactive exploits of what the defender visibly did.
            Buttons react = Exploit(s, me, opp, ms, dist);
            if (react != Buttons.None) return react;

            // 5) Shot clock about to expire: shoot from anywhere.
            if (s.ShotClock <= Tier.ReactionFrames + ms.Def.JumperGatherFrames && CanPress(Buttons.Shoot))
            {
                return StartShot(s);
            }

            // 6) Signature Finisher.
            if (WantsFinisher(s, me, ms))
            {
                FinisherDefinition fd = ms.Def.Finisher;
                if (me.X >= fd.ZoneMin && me.X <= fd.ZoneMax)
                {
                    if (CanPress(Buttons.Finisher)) return Buttons.Finisher;
                }
                else
                {
                    _plan = Plan.Move;
                    _moveTarget = (fd.ZoneMin + fd.ZoneMax) / 2;
                }
            }

            // 7) Continue a running plan.
            switch (_plan)
            {
                case Plan.Drive:
                    return DriveLoop(s, me, opp, ms, dist);
                case Plan.Move:
                    {
                        Fix dx = _moveTarget - me.X;
                        if (Fix.Abs(dx) > Fix.Ratio(1, 4) && s.Tick < _planUntil)
                        {
                            return Dir(dx.Sign, 0) | (Fix.Abs(dx) > Fix.One ? Buttons.Turbo : Buttons.None);
                        }
                        _plan = Plan.None;
                        _nextDecision = s.Tick;
                        break;
                    }
                case Plan.Post:
                    return PostApproach(s, me, opp);
            }

            if (s.Tick < _nextDecision) return Buttons.None;
            if (s.Tick < _patienceUntil && s.ShotClock > T.Match.ShotClockFrames / 3) return DecideProbe(s, me, opp, ms, p);
            return DecideOffense(s, seen, me, opp, ms, p, dist);
        }

        /// <summary>Early in a possession: dribble moves to bait commitments, reposition, or hold the ball.</summary>
        private Buttons DecideProbe(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, Personality p)
        {
            _nextDecision = s.Tick + Tier.DecisionInterval + Rng.Range(ref _rng, 0, Tier.DecisionInterval + 1);
            AiTable t = _tiers.Table;
            Fix gap = Fix.Abs(opp.X - me.X);
            Fix toSpot = p.AttackX - me.X;
            Fix shake = t.ProbeShake * p.ShakeWeight * (gap < Fix.FromInt(2) ? Fix.One : Fix.Ratio(1, 4));
            Fix move = Fix.Abs(toSpot) > Fix.Ratio(3, 5) ? t.ProbeMove * (Fix.One + Fix.Abs(toSpot) / 3) : Fix.Zero;
            Fix hold = t.ProbeHold;
            Fix r = Unit() * (shake + move + hold);
            if (r < shake && CanPress(Buttons.Shake))
            {
                _plan = Plan.Shake;
                _shakeChain = 1;
                return ShakeInput(s, me, opp, gap);
            }
            if (r < shake + move)
            {
                _plan = Plan.Move;
                _moveTarget = p.AttackX;
                _planUntil = s.Tick + 90;
                return Dir(toSpot.Sign, 0);
            }
            return Buttons.None;
        }

        private Buttons ManageGather(in GameState s, in FighterState me, in FighterState opp, FighterStats ms)
        {
            bool trick = (me.ActionFlags & ActionFlag.StartedWithTrick) != 0;
            Buttons hold = trick ? Buttons.Trick : Buttons.Shoot;
            if (!_releasePlanned)
            {
                _releaseErr = RollReleaseError();
                _releasePlanned = true;
            }
            Buttons extra = Buttons.None;
            // Hang Time: slip a visible contest once.
            if (me.Action == ActionId.GatherFinish && ms.Def.Mastery == Mastery.HangTime && !me.Grounded
                && (me.ActionFlags & ActionFlag.HangUsed) == 0 && opp.Action == ActionId.Contest && opp.Y > T.Physics.AirborneHeight
                && CanPress(Buttons.Shake) && Chance(Tier.ReadChance + Fix.Ratio(1, 5)))
            {
                extra = Buttons.Shake | Buttons.Right;
            }
            int target = me.Peak + _releaseErr;
            bool releaseNow = NextTickAdvances(s) && me.ActionFrame + 1 >= target;
            if ((me.ActionFlags & ActionFlag.ReleaseOnPress) != 0)
            {
                if (releaseNow && CanPress(hold)) return hold | extra;
                return extra;
            }
            if (releaseNow)
            {
                _releasePlanned = false;
                return extra;
            }
            return hold | extra;
        }

        private Buttons StartShot(in GameState s)
        {
            _releasePlanned = false;
            _plan = Plan.Shoot;
            _nextDecision = s.Tick + Tier.DecisionInterval;
            return Buttons.Shoot;
        }

        /// <summary>Punish visible commitments: drive past an airborne or off-balance defender, shoot over a planted one.</summary>
        private Buttons Exploit(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, Fix dist)
        {
            Fix dx = opp.X - me.X;
            bool inFront = dx.IsPositive;
            bool airborne = opp.Y > T.Physics.AirborneHeight;
            bool broken = opp.Action == ActionId.OffBalance || opp.Action == ActionId.Stumble || opp.Action == ActionId.Knockdown || opp.Beaten > 0;
            if (!Chance(Tier.ReactChance)) return Buttons.None;
            if ((airborne && opp.Action == ActionId.Contest || broken) && inFront && dx < Fix.FromInt(3))
            {
                _plan = Plan.Drive;
                _planUntil = s.Tick + 90;
                return DriveLoop(s, me, opp, ms, dist);
            }
            if ((opp.Action == ActionId.Wall || opp.Action == ActionId.Unplant) && CanPress(Buttons.Shoot))
            {
                if (ShotEv(s, me, opp, ms, true) >= Me(s).ShootWeight * _tiers.Table.ShootThreshold / 2) return StartShot(s);
            }
            return Buttons.None;
        }

        private Buttons DriveLoop(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, Fix dist)
        {
            if (dist <= T.Shot.FinishRangeDriving && CanPress(Buttons.Shoot))
            {
                _plan = Plan.Shoot;
                _releasePlanned = false;
                return Buttons.Shoot | Buttons.Right | Buttons.Turbo;
            }
            // Cut off by a solid defender for a while: pull up or reset.
            if ((me.Status & StatusFlag.InContact) != 0 && me.VX < T.Read.DriveMinSpeed / 2 && s.Tick > _planUntil - 60)
            {
                _plan = Plan.None;
                _nextDecision = s.Tick;
                return Buttons.None;
            }
            if (s.Tick > _planUntil)
            {
                _plan = Plan.None;
                _nextDecision = s.Tick;
            }
            return Buttons.Right | Buttons.Turbo;
        }

        private bool WantsFinisher(in GameState s, in FighterState me, FighterStats ms)
        {
            if (me.Hype < T.Hype.FinisherCost || s.NeedsClear) return false;
            FighterState opp = s.Get(1 - _player);
            switch (Tier.HypeStyle)
            {
                case HypeStyle.Rarely: return Chance(Fix.Ratio(1, 50));
                case HypeStyle.WhenFull: return true;
                case HypeStyle.Purpose: return opp.Hype < T.Hype.DenialCost || Chance(Fix.Ratio(1, 30));
                default: return opp.Hype < T.Hype.DenialCost || Chance(Fix.Ratio(1, 60)) || s.ShotClock < T.Match.ShotClockFrames / 2;
            }
        }

        private bool WantsAmp(in GameState s, in FighterState me)
        {
            int reserve = 0;
            FighterState opp = s.Get(1 - _player);
            switch (Tier.HypeStyle)
            {
                case HypeStyle.Rarely: return false;
                case HypeStyle.WhenFull: return me.Hype >= T.Hype.Max && Chance(Fix.Ratio(1, 2));
                case HypeStyle.SaveForDenial:
                case HypeStyle.Optimal:
                    if (opp.Hype >= T.Hype.FinisherCost - T.Hype.Bar) reserve = T.Hype.DenialCost;
                    break;
            }
            // Saving toward a Finisher beats spending a bar early unless the bar would otherwise overflow.
            if (me.Hype + T.Hype.Bar <= T.Hype.Max && Tier.HypeStyle == HypeStyle.Optimal && me.Hype >= T.Hype.Bar * 2) return false;
            return me.Hype - reserve >= T.Hype.AmpedCost && Chance(Fix.Ratio(1, 2));
        }

        // ------------------------------------------------------------------ evaluation

        /// <summary>Gather frames to the release peak for my next jumper (Rhythm stacks shorten it).</summary>
        private int MyPeak(in FighterState me, FighterStats ms)
        {
            int peak = ms.Def.JumperGatherFrames;
            if (ms.Def.Mastery == Mastery.Rhythm) peak -= me.Rhythm * ms.Def.MasteryParams.RhythmFramesPerStack;
            return peak < T.Shot.JumperRiseFrames + 1 ? T.Shot.JumperRiseFrames + 1 : peak;
        }

        /// <summary>
        /// Expected contest penalty C for a jumper now, modelling a defender who reacts to the gather after a
        /// typical elite reaction and jumps: timing comes from frames between its apex and my release.
        /// </summary>
        private Fix ContestEstimate(in GameState s, in FighterState me, in FighterState opp, bool assumeNoReaction)
        {
            if (opp.Action == ActionId.Wall || opp.Action == ActionId.Unplant || opp.Action == ActionId.OffBalance
                || opp.Action == ActionId.Stumble || opp.Action == ActionId.Knockdown || opp.NoContest > 0) return Fix.Zero;
            FighterStats os = OppStats(s);
            FighterStats ms = MyStats(s);
            ContestTable c = T.Contest;
            int peak = MyPeak(me, ms);
            int apex = _tiers.Table.AssumedReaction + T.Read.ContestStartup + os.Def.JumpRiseFrames;
            int airborneBeforeRelease = peak - _tiers.Table.AssumedReaction - T.Read.ContestStartup;
            if (airborneBeforeRelease < 0) airborneBeforeRelease = 0;
            Fix d = Fix.Abs(opp.X - me.X) - T.Read.ContestLunge * airborneBeforeRelease;
            Fix far = c.FarDistance + c.ReachScale * (os.Reach - c.ReferenceReach);
            Fix distF = d <= c.CloseDistance ? Fix.One : Fix.Ramp(d, far, c.CloseDistance);
            if (opp.X < me.X - c.BehindTolerance) distF = distF * c.BehindFactor;
            Fix timing;
            if (opp.Action == ActionId.Contest && opp.Y > T.Physics.AirborneHeight) timing = Fix.Ratio(4, 5);
            else if (assumeNoReaction) timing = c.GroundFactor;
            else
            {
                int off = apex - peak;
                if (off < 0) off = -off;
                timing = Fix.Max(c.GroundFactor, Fix.Clamp01(Fix.One - Fix.Ratio(off, c.TimingWindow)));
            }
            Fix release = ms.Height * T.Shot.ReleaseHeightFactor - T.Physics.FighterGravity * (T.Shot.JumperRiseFrames * (T.Shot.JumperRiseFrames - 1)) / 2;
            Fix hand = os.Reach + os.JumpHeight;
            Fix heightF = hand >= release ? Fix.One : Fix.Clamp01(Fix.One - (release - hand) / c.HeightFalloff);
            return T.Shot.ContestMax * Fix.Clamp01(timing * distF * heightF * os.ContestMult);
        }

        private Fix ShotEv(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, bool assumeNoReaction)
        {
            Fix dist = Fix.Abs(T.Court.RimX - me.X);
            bool deep = me.X <= T.Court.ArcX;
            Fix b = dist <= T.Court.RimZone ? ms.BaseRim : (deep ? ms.BaseDeep : ms.BaseMid);
            Fix pr = b + _expectedGradeBonus - ContestEstimate(s, me, opp, assumeNoReaction);
            if (me.Balance < T.Shot.BalancePenaltyBelow) pr -= T.Shot.BalancePenaltyMax * (T.Shot.BalancePenaltyBelow - me.Balance) / T.Shot.BalancePenaltyBelow;
            pr = Fix.Clamp(pr, T.Shot.MinChance, T.Shot.MaxChance);
            int pts = deep ? T.Match.PointsDeep : T.Match.PointsInside;
            if ((me.Status & StatusFlag.HeatCheck) != 0) pr += Fix.Ratio(1, 20);
            return pr * pts;
        }

        private Fix RimEv(FighterStats ms) => (Fix.Min(ms.BaseRim + _expectedGradeBonus, T.Shot.MaxChance)) * T.Match.PointsInside;

        private Buttons DecideOffense(in GameState s, in GameState seen, in FighterState me, in FighterState opp, FighterStats ms, Personality p, Fix dist)
        {
            _nextDecision = s.Tick + Tier.DecisionInterval + Rng.Range(ref _rng, 0, Tier.DecisionInterval / 2 + 1);
            int zone = HabitModel.Zone(dist, T.Court.RimZone, T.Court.RimX - T.Court.ArcX);
            int bucket = HabitModel.Bucket(s.ShotClock, T.Match.ShotClockFrames);
            Fix dx = opp.X - me.X;
            Fix gap = Fix.Abs(dx);
            bool oppSolid = opp.Y <= T.Physics.AirborneHeight && opp.Action != ActionId.OffBalance && opp.Action != ActionId.Stumble;
            bool planted = opp.Action == ActionId.Wall || ((opp.Action == ActionId.Trick) && opp.PlantFrames > 0);

            // Recognize "hold" habits: no commitment for a while while we had the ball.
            if (Tier.LearnsHabits && s.Tick - _lastOppCommitTick > 45) _habits.SeeDefense(zone, bucket, DefenseChoice.Hold);

            Fix shoot = ShotEv(s, me, opp, ms, false) * p.ShootWeight;
            if (shoot < _tiers.Table.ShootThreshold * p.ShootWeight) shoot = shoot / 2;
            if (me.X <= T.Court.ArcX) shoot = shoot * (Fix.One + _tiers.Table.DeepPreference * (p.ShootWeight - Fix.One));

            Fix rim = RimEv(ms);
            Fix drive;
            if (planted) drive = -Fix.One;
            else if (!oppSolid) drive = rim;
            else
            {
                // Quickness battle at first contact (rules knowledge, not the RNG): beat = blow-by, else a push.
                AttributeBlock ma = ms.Def.Attributes, oa = OppStats(s).Def.Attributes;
                ReadTable r = T.Read;
                Fix beat = Fix.Clamp(r.DriveBeatBase + r.DriveBeatPerSpeed * (ma.Speed - oa.Speed) + r.DriveBeatPerHandles * (ma.Handles - 5)
                    - r.DriveBeatPerDefense * (oa.Defense - 5), r.DriveBeatMin, r.DriveBeatMax);
                int strDiff = ma.Strength - oa.Strength;
                Fix push = Fix.Clamp(Fix.Ratio(1, 5) + Fix.Ratio(strDiff, 30), Fix.Zero, Fix.Half);
                drive = rim * (beat + (Fix.One - beat) * push);
                if (dist > Fix.FromInt(7)) drive = drive / 2;
            }
            drive = drive * p.DriveWeight;

            Fix commit = _habits.CommitRate(zone);
            Fix shake = (Fix.Ratio(1, 2) + commit) * p.ShakeWeight * (gap < Fix.FromInt(2) ? Fix.One : Fix.Ratio(1, 3));
            Fix post = Fix.Zero;
            if (ms.Def.MasteryParams.CanBackDown && dist < Fix.FromInt(6) && dist > T.Post.BackDownMinDistance + Fix.Half)
                post = rim * p.PostWeight * Fix.Ratio(3, 4);

            Fix trickF = TrickScore(s, me, opp, ms, TrickSlot.Forward, dist, gap) * p.TrickForwardWeight;
            Fix trickB = TrickScore(s, me, opp, ms, TrickSlot.Back, dist, gap) * p.TrickBackWeight;

            Fix move = Fix.Zero;
            Fix toSpot = p.AttackX - me.X;
            if (Fix.Abs(toSpot) > Fix.One) move = Fix.Ratio(9, 10);

            // Read: counter the defender's habit here.
            if (Tier.LearnsHabits && Chance(Tier.ReadChance) && _habits.PredictDefense(zone, bucket, 4, out DefenseChoice pred))
            {
                switch (pred)
                {
                    case DefenseChoice.Wall: shoot = shoot * Fix.Ratio(3, 2); drive = drive * Fix.Ratio(1, 4); break;
                    case DefenseChoice.Contest: drive = drive * Fix.Ratio(3, 2) + rim / 4; shake = shake * Fix.Ratio(4, 3); shoot = shoot * Fix.Ratio(2, 3); break;
                    case DefenseChoice.Swipe: drive = drive * Fix.Ratio(4, 3); shake = shake / 2; break;
                    case DefenseChoice.Hold: shoot = shoot * Fix.Ratio(6, 5); post = post * Fix.Ratio(6, 5); break;
                }
            }

            // Sloppiness.
            Fix noise = Fix.Ratio(1, 5) + Tier.MistakeChance;
            shoot += Unit() * noise;
            drive += Unit() * noise;
            shake += Unit() * noise;
            if (post > Fix.Zero) post += Unit() * noise;
            if (trickF > Fix.Zero) trickF += Unit() * noise;
            if (trickB > Fix.Zero) trickB += Unit() * noise;
            if (move > Fix.Zero) move += Unit() * noise;

            Fix best = Fix.Max(Fix.Max(Fix.Max(shoot, drive), Fix.Max(shake, post)), Fix.Max(Fix.Max(trickF, trickB), move));
            if (best == move && move > Fix.Zero)
            {
                _plan = Plan.Move;
                _moveTarget = p.AttackX;
                _planUntil = s.Tick + 120;
                return Dir(toSpot.Sign, 0);
            }
            if (best == trickF && trickF > Fix.Zero) return StartTrick(s, me, TrickSlot.Forward, gap);
            if (best == trickB && trickB > Fix.Zero) return StartTrick(s, me, TrickSlot.Back, gap);
            if (best == post && post > Fix.Zero)
            {
                _plan = Plan.Post;
                _postBumpsSeen = 0;
                _planUntil = s.Tick + 150;
                return PostApproach(s, me, opp);
            }
            if (best == drive)
            {
                _plan = Plan.Drive;
                _planUntil = s.Tick + 70;
                return Buttons.Right | Buttons.Turbo;
            }
            if (best == shake && CanPress(Buttons.Shake))
            {
                _plan = Plan.Shake;
                _shakeChain = 1;
                return ShakeInput(s, me, opp, gap);
            }
            if (CanPress(Buttons.Shoot)) return StartShot(s);
            return Buttons.None;
        }

        private Buttons ShakeInput(in GameState s, in FighterState me, in FighterState opp, Fix gap)
        {
            Fix r = Unit();
            int h, v = 0;
            if (gap < Fix.Ratio(11, 10) && r < Fix.Ratio(3, 10)) h = -1;            // step back for space
            else if (r < Fix.Ratio(6, 10)) h = 1;                                   // crossover
            else if (r < Fix.Ratio(8, 10)) { h = 0; v = -1; }                       // spin
            else h = 0;                                                            // hesitation
            _shakeDir = h;
            return Buttons.Shake | Dir(h, v);
        }

        /// <summary>After a dribble move or trick: exploit a bite, chain the combo, or let it finish.</summary>
        private Buttons FollowUp(in GameState s, in GameState seen, in FighterState me, in FighterState opp, FighterStats ms, Personality p)
        {
            bool bit = opp.Beaten > 0 || (opp.Y > T.Physics.AirborneHeight && opp.Action == ActionId.Contest) || opp.Action == ActionId.OffBalance
                || opp.Action == ActionId.Stumble;
            bool planted = opp.Action == ActionId.Wall || opp.Action == ActionId.Unplant;
            MoveDefinition m = me.Action == ActionId.Shake ? _tb.Shakes[me.MoveSlot] : ms.Tricks[me.MoveSlot];
            bool inCancel = me.ActionFrame + 1 >= m.CancelStart && me.ActionFrame + 1 < m.CancelEnd;
            if (!inCancel) return Buttons.None;
            if (bit && CanPress(Buttons.Turbo))
            {
                _plan = Plan.Drive;
                _planUntil = s.Tick + 80;
                return Buttons.Right | Buttons.Turbo;
            }
            if (planted && CanPress(Buttons.Shoot)) return StartShot(s);
            if (me.Action == ActionId.Shake && _shakeChain < p.MaxShakeChain && me.ComboLinks < ms.MaxComboLinks && CanPress(Buttons.Shake)
                && Chance(Fix.Ratio(2, 3)))
            {
                _shakeChain++;
                return ShakeInput(s, me, opp, Fix.Abs(opp.X - me.X));
            }
            if (me.Action == ActionId.Trick && m.Behavior == TrickBehavior.MonsoonStep && CanPress(Buttons.Shoot)) return StartShot(s);
            if (me.Action == ActionId.Shake && me.MoveSlot == 1 && CanPress(Buttons.Shoot) && Chance(Fix.Ratio(3, 4))) return StartShot(s);
            return Buttons.None;
        }

        private Fix TrickScore(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, TrickSlot slot, Fix dist, Fix gap)
        {
            MoveDefinition m = ms.Tricks[(int)slot];
            bool canAmp = me.Hype >= T.Hype.AmpedCost;
            Fix maxRange = canAmp && m.Params.AmpedMaxRange > Fix.Zero ? m.Params.AmpedMaxRange : m.MaxRange;
            if (m.MinRange > Fix.Zero && dist < m.MinRange) return Fix.Zero;
            if (maxRange > Fix.Zero && dist > maxRange) return Fix.Zero;
            if (s.NeedsClear) return Fix.Zero;
            bool planted = opp.Action == ActionId.Wall;
            Fix rim = RimEv(ms);
            switch (m.Behavior)
            {
                case TrickBehavior.LaunchStep: return planted ? Fix.Zero : rim * Fix.Ratio(3, 4);
                case TrickBehavior.DoubleClutch: return rim * (opp.Action == ActionId.Contest ? Fix.One : Fix.Ratio(1, 2));
                case TrickBehavior.Drizzle: return ShotEv(s, me, opp, ms, true) * Fix.Ratio(4, 5);
                case TrickBehavior.MonsoonStep:
                    return gap < Fix.FromInt(2) ? Fix.FromInt(1) + (me.X - Fix.FromInt(2) <= T.Court.ArcX ? Fix.Half : Fix.Zero) : Fix.Zero;
                case TrickBehavior.FrameSkip: return gap < Fix.FromInt(2) ? Fix.One : Fix.Zero;
                case TrickBehavior.LagSpike: return gap < Fix.FromInt(2) ? Fix.Ratio(3, 4) + _habits.CommitRate(0) / 2 : Fix.Zero;
                case TrickBehavior.DropStep: return gap <= T.Post.ContactDistance + m.Reach ? rim : Fix.Zero;
                case TrickBehavior.GraniteHook: return rim * Fix.Ratio(4, 5);
                default: return Fix.Zero;
            }
        }

        private Buttons StartTrick(in GameState s, in FighterState me, TrickSlot slot, Fix gap)
        {
            if (!CanPress(Buttons.Trick)) return Buttons.None;
            bool amp = WantsAmp(s, me);
            _plan = Plan.Trick;
            _releasePlanned = false;
            _nextDecision = s.Tick + Tier.DecisionInterval;
            Buttons dir = slot == TrickSlot.Forward ? Buttons.Right : Buttons.Left;
            return Buttons.Trick | dir | (amp ? Buttons.Amp : Buttons.None);
        }

        private Buttons PostApproach(in GameState s, in FighterState me, in FighterState opp)
        {
            if (s.Tick > _planUntil)
            {
                _plan = Plan.None;
                _nextDecision = s.Tick;
                return Buttons.None;
            }
            // Walk into contact, then hold down-toward to back down.
            return Buttons.Down | Buttons.Right;
        }

        private Buttons PostLoop(in GameState s, in GameState seen, in FighterState me, in FighterState opp, FighterStats ms, Personality p)
        {
            Fix dist = Fix.Abs(T.Court.RimX - me.X);
            if (me.ActionFrame == me.Param) _postBumpsSeen++;
            bool close = dist <= T.Post.BackDownMinDistance + Fix.Ratio(4, 5);
            if (_postBumpsSeen >= _tiers.Table.PostBumpsBeforeMove || close || s.Tick > _planUntil || s.ShotClock < 90)
            {
                _plan = Plan.None;
                Fix r = Unit();
                if (r < Fix.Ratio(45, 100) && CanPress(Buttons.Trick))
                    return Buttons.Trick | Buttons.Right | (WantsAmp(s, me) ? Buttons.Amp : Buttons.None);
                if (r < Fix.Ratio(75, 100) && CanPress(Buttons.Trick) && dist <= ms.Tricks[(int)TrickSlot.Back].MaxRange)
                    return Buttons.Trick | Buttons.Left;
                if (CanPress(Buttons.Shoot)) return StartShot(s);
            }
            return Buttons.Down | Buttons.Right;
        }

        // ================================================================== defense

        private Buttons Defense(in GameState s, in GameState seen)
        {
            FighterState me = s.Get(_player);
            FighterState opp = seen.Get(1 - _player);
            FighterStats ms = MyStats(s);
            Personality p = Me(s);
            Fix gap = opp.X - me.X; // defender should be in front (toward the hoop): gap negative means handler is ahead of us
            Fix ad = Fix.Abs(gap);
            Fix oppDist = Fix.Abs(T.Court.RimX - opp.X);

            if (me.Action == ActionId.Wall)
            {
                bool shotVisible = opp.Action == ActionId.GatherJumper;
                if (s.Tick < _wallUntil && !shotVisible) return Buttons.Down;
                return Buttons.None;
            }
            if (me.Action == ActionId.Trick)
            {
                MoveDefinition t = ms.Tricks[me.MoveSlot];
                if (t.Behavior == TrickBehavior.RimLock) return oppDist < Fix.FromInt(5) ? Buttons.Trick : Buttons.None;
                return Buttons.None;
            }
            if (me.Action != ActionId.Idle) return Buttons.None;

            // --- reactions to what is visible
            bool gathering = opp.Action == ActionId.GatherJumper || opp.Action == ActionId.GatherFinish;
            if (!gathering) _contestAt = -1;
            if (gathering && ad < Fix.FromInt(3))
            {
                if (_contestAt < 0)
                {
                    // Time the jump so the apex meets the release: frames left to the shooter's peak now,
                    // minus my start-up and rise. Already late means jump at once.
                    if (!Chance(Tier.ReactChance))
                    {
                        _contestAt = int.MaxValue;
                    }
                    else
                    {
                        int framesToPeak = opp.Peak - (opp.ActionFrame + Tier.ReactionFrames);
                        int lead = T.Read.ContestStartup + ms.Def.JumpRiseFrames;
                        _contestAt = s.Tick + (framesToPeak > lead ? framesToPeak - lead : 0);
                    }
                }
                if (s.Tick >= _contestAt && CanPress(Buttons.Shoot))
                {
                    _contestAt = int.MaxValue;
                    return Buttons.Shoot;
                }
                return Buttons.None;
            }
            if (gathering && ad >= Fix.FromInt(3))
            {
                return Dir((opp.X - me.X).Sign, 0) | Buttons.Turbo; // close out
            }

            // Blown by: Chase-Down or sprint back.
            if (opp.X > me.X + Fix.Ratio(3, 10))
            {
                MoveDefinition dt = ms.Tricks[(int)TrickSlot.Defense];
                if (dt.Behavior == TrickBehavior.ChaseDown && CanPress(Buttons.Trick) && Chance(p.DefenseTrickWeight / 2))
                    return Buttons.Trick | (WantsAmp(s, me) ? Buttons.Amp : Buttons.None);
                return Buttons.Right | Buttons.Turbo;
            }

            bool driving = opp.VX >= T.Read.DriveMinSpeed && (opp.Status & StatusFlag.Driving) != 0;
            if (driving)
            {
                MoveDefinition dt = ms.Tricks[(int)TrickSlot.Defense];
                Fix rimDist = Fix.Abs(T.Court.RimX - me.X);
                if (dt.Behavior == TrickBehavior.RimLock && rimDist <= dt.MaxRange && CanPress(Buttons.Trick) && Chance(p.DefenseTrickWeight))
                    return Buttons.Trick | (WantsAmp(s, me) ? Buttons.Amp : Buttons.None);
                Fix closing = opp.VX;
                Fix space = ad - ms.PushW - OppStats(s).PushW;
                int framesToContact = closing.IsPositive ? (space / closing).FloorToInt() - Tier.ReactionFrames / 3 : 999;
                if (framesToContact >= T.Read.PlantFramesForCharge + 1 && Chance(Tier.ReactChance * p.WallWeight))
                {
                    _wallUntil = s.Tick + _tiers.Table.WallHoldFrames;
                    return Buttons.Down;
                }
                return Buttons.Right | Buttons.Turbo; // slide back with the drive
            }

            // --- reads at decision points
            if (s.Tick >= _nextDecision && s.Ball.Holder != _player)
            {
                _nextDecision = s.Tick + Tier.DecisionInterval + Rng.Range(ref _rng, 0, Tier.DecisionInterval + 1);
                bool threat = oppDist < Fix.FromInt(9) && ad < Fix.FromInt(3);
                if (threat && Chance(Tier.CommitChance))
                {
                    Buttons c = ChooseCommit(s, me, opp, ms, p, ad, oppDist);
                    if (c != Buttons.None) return c;
                }
            }

            // --- positioning
            return Guard(s, me, opp, ms, p);
        }

        private Buttons ChooseCommit(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, Personality p, Fix ad, Fix oppDist)
        {
            int zone = HabitModel.Zone(oppDist, T.Court.RimZone, T.Court.RimX - T.Court.ArcX);
            int bucket = HabitModel.Bucket(s.ShotClock, T.Match.ShotClockFrames);
            DefenseChoice choice;
            if (Tier.LearnsHabits && Chance(Tier.ReadChance) && _habits.PredictOffense(zone, bucket, 4, out OffenseChoice pred))
            {
                switch (pred)
                {
                    case OffenseChoice.Shoot:
                        // Expect a shot: crowd the shooter (better reactive contest); sometimes jump on the read.
                        _stance = 1;
                        _stanceUntil = s.Tick + _tiers.Table.StanceFrames;
                        choice = Chance(Fix.Ratio(1, 3)) ? DefenseChoice.Contest : DefenseChoice.Hold;
                        break;
                    case OffenseChoice.Drive:
                    case OffenseChoice.Post:
                        _stance = 2;
                        _stanceUntil = s.Tick + _tiers.Table.StanceFrames;
                        choice = DefenseChoice.Wall;
                        break;
                    default: choice = DefenseChoice.Hold; break;
                }
            }
            else if (Chance(Tier.MistakeChance))
            {
                // Unforced guess: lower tiers bite on dribble moves this way.
                Fix total = p.WallWeight + p.ContestWeight + p.SwipeWeight;
                Fix r = Unit() * total;
                if (r < p.WallWeight) choice = DefenseChoice.Wall;
                else if (r < p.WallWeight + p.ContestWeight) choice = DefenseChoice.Contest;
                else choice = DefenseChoice.Swipe;
            }
            else
            {
                choice = DefenseChoice.Hold;
            }
            // Never guess into a dribble move that is already visible: that is how ankles get broken.
            if ((opp.Action == ActionId.Shake || opp.Action == ActionId.Trick) && choice != DefenseChoice.Hold && !Chance(Tier.MistakeChance))
            {
                choice = DefenseChoice.Hold;
            }
            // Walls only make sense inside drive range.
            if (choice == DefenseChoice.Wall && oppDist > Fix.FromInt(7)) choice = DefenseChoice.Hold;

            MoveDefinition dt = ms.Tricks[(int)TrickSlot.Defense];
            switch (choice)
            {
                case DefenseChoice.Wall:
                    _wallUntil = s.Tick + _tiers.Table.WallHoldFrames;
                    return Buttons.Down;
                case DefenseChoice.Contest:
                    return CanPress(Buttons.Shoot) ? Buttons.Shoot : Buttons.None;
                case DefenseChoice.Swipe:
                    if ((dt.Behavior == TrickBehavior.Undercurrent || dt.Behavior == TrickBehavior.Pickpocket) && Chance(p.DefenseTrickWeight)
                        && ad <= (dt.Behavior == TrickBehavior.Pickpocket ? dt.Params.DashDistance : dt.Reach) && CanPress(Buttons.Trick))
                        return Buttons.Trick | (WantsAmp(s, me) ? Buttons.Amp : Buttons.None);
                    if (ad <= _tb.Moves.Swipe.Reach + ms.SwipeReachBonus && CanPress(Buttons.Shake)) return Buttons.Shake;
                    return Buttons.None;
                default:
                    return Buttons.None;
            }
        }

        private Buttons Guard(in GameState s, in FighterState me, in FighterState opp, FighterStats ms, Personality p)
        {
            Fix gapScale = Fix.One;
            if (s.Tick < _stanceUntil) gapScale = _stance == 1 ? _tiers.Table.TightGapScale : (_stance == 2 ? _tiers.Table.LooseGapScale : Fix.One);
            // Scout the matchup: crowd good shooters beyond the arc, give drivers a cushion.
            AttributeBlock oa = OppStats(s).Def.Attributes;
            Fix scout = Fix.One - _tiers.Table.ShooterRespect * (oa.Range - 5) + _tiers.Table.DriverRespect * (oa.Speed - 5);
            if (opp.X > T.Court.ArcX) scout = Fix.One + _tiers.Table.DriverRespect * (oa.Speed - 5);
            Fix target = opp.X + p.DefendGap * gapScale * scout;
            Fix maxX = T.Court.RimX - Fix.Ratio(1, 2);
            if (target > maxX) target = maxX;
            Fix dx = target - me.X;
            if (Fix.Abs(dx) < Fix.Ratio(1, 12)) return Buttons.None;
            Buttons b = Dir(dx.Sign, 0);
            if (Fix.Abs(dx) > Fix.Ratio(7, 10)) b |= Buttons.Turbo;
            return b;
        }

        // ================================================================== loose ball

        private Buttons Loose(in GameState s, in GameState seen)
        {
            FighterState me = s.Get(_player);
            FighterStats ms = MyStats(s);
            BallState ball = seen.Ball;
            if (me.Action != ActionId.Idle) return Buttons.None;
            if (ball.Mode == BallMode.Flight)
            {
                Fix spot = T.Court.RimX - Fix.FromInt(1);
                Fix dx = spot - me.X;
                return Fix.Abs(dx) > Fix.Ratio(1, 4) ? Dir(dx.Sign, 0) | Buttons.Turbo : Buttons.None;
            }
            if (ball.Mode != BallMode.Loose) return Buttons.None;

            // Extrapolate the delayed ball to "now".
            int lag = Tier.ReactionFrames;
            Fix bx = ball.X + ball.VX * lag;
            Fix by = ball.Y + ball.VY * lag + T.Physics.BallGravity * (lag * (lag + 1)) / 2;
            if (by < T.Court.BallRadius) by = T.Court.BallRadius;
            Fix ddx = bx - me.X;
            Fix reach = me.Y + ms.Reach;
            if (Fix.Abs(ddx) < Fix.Ratio(4, 5) && by > reach - Fix.Ratio(1, 5) && by < reach + ms.JumpHeight
                && CanPress(Buttons.Shoot) && me.Grounded)
            {
                return Buttons.Shoot;
            }
            if (Fix.Abs(ddx) < Fix.Ratio(1, 10)) return Buttons.None;
            return Dir(ddx.Sign, 0) | Buttons.Turbo;
        }
    }
}
