using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>
    /// The Read: pushbox contact, charges, blow-bys, drive and post bumps, bites on bait windows, and
    /// steals on exposed frames. Runs after both fighters moved, so both sides are resolved symmetrically.
    /// </summary>
    public sealed partial class Simulation
    {
        private void Interactions(ref GameState s, bool handlerWasBehind)
        {
            if (s.Ball.Mode == BallMode.Held)
            {
                int h = s.Ball.Holder;
                int d = 1 - h;
                Bites(ref s, h, d);
                if (Steals(ref s, h, d)) return;
                if (s.Ball.Mode == BallMode.Held) TrickInteractions(ref s, h, d);
                if (s.Phase != Phase.Live) return;
            }
            Contact(ref s, handlerWasBehind);
        }

        // ------------------------------------------------------------------ helpers

        private static bool IsCommitment(ActionId a) =>
            a == ActionId.Wall || a == ActionId.Contest || a == ActionId.Swipe || a == ActionId.Trick;

        /// <summary>Defender is in a commitment (start-up, active or recovery), the window Frame Skip slips through.</summary>
        private static bool MidCommit(in FighterState f) =>
            f.Action == ActionId.Wall || f.Action == ActionId.Unplant || f.Action == ActionId.Contest || f.Action == ActionId.Swipe
            || f.Action == ActionId.Trick || f.Action == ActionId.OffBalance;

        /// <summary>Whether a fighter blocks the other's path (pushbox active).</summary>
        private bool Solid(in FighterState f)
        {
            if (f.Y > T.Physics.AirborneHeight) return false;
            if (f.Action == ActionId.OffBalance || f.Action == ActionId.Stumble || f.Action == ActionId.Knockdown) return false;
            if (f.Action == ActionId.Trick)
            {
                // Pickpocket dashes through the ball handler.
                MoveDefinition m = Stats(f).Tricks[f.MoveSlot];
                if (m.Behavior == TrickBehavior.Pickpocket)
                {
                    int start = Startup(f, m);
                    if (f.ActionFrame >= start && f.ActionFrame < start + m.Active) return false;
                }
            }
            return f.Beaten == 0;
        }

        /// <summary>Planted long enough to draw a charge (Wall, or Matas's Rim Lock stance).</summary>
        private bool PlantedForCharge(in FighterState f)
        {
            if (f.PlantFrames < T.Read.PlantFramesForCharge) return false;
            if (f.Action == ActionId.Wall) return true;
            return f.Action == ActionId.Trick && Stats(f).Tricks[f.MoveSlot].Behavior == TrickBehavior.RimLock;
        }

        private MoveDefinition CurrentMove(in FighterState f)
        {
            if (f.Action == ActionId.Shake) return _tb.Shakes[f.MoveSlot];
            if (f.Action == ActionId.Trick) return Stats(f).Tricks[f.MoveSlot];
            return null;
        }

        private int Startup(in FighterState f, MoveDefinition m)
        {
            int startup = m.Startup;
            if ((f.ActionFlags & ActionFlag.Amped) != 0) startup += T.Hype.AmpedStartupDelta;
            return startup < 1 ? 1 : startup;
        }

        /// <summary>Ball exposed to a reach this frame (dribble move exposure, early gather, post bump).</summary>
        private bool BallExposed(ref GameState s, int h, bool fromBehind)
        {
            ref FighterState f = ref GameState.Fighter(ref s, h);
            switch (f.Action)
            {
                case ActionId.Shake:
                    {
                        MoveDefinition m = _tb.Shakes[f.MoveSlot];
                        return f.ActionFrame >= m.ExposedStart && f.ActionFrame < m.ExposedEnd;
                    }
                case ActionId.Trick:
                    {
                        MoveDefinition m = Stats(f).Tricks[f.MoveSlot];
                        return f.ActionFrame >= m.ExposedStart && f.ActionFrame < m.ExposedEnd;
                    }
                case ActionId.GatherJumper:
                case ActionId.GatherFinish:
                    return (f.ActionFlags & ActionFlag.NoExposure) == 0 && f.ActionFrame < T.Read.GatherExposedFrames;
                case ActionId.PostUp:
                    return f.ActionFrame - f.Param < T.Post.BumpExposedFrames;
                case ActionId.Idle:
                    // Only a reach from behind (Pickpocket) can take a live dribble that is not protected.
                    return fromBehind && (f.Status & StatusFlag.Protecting) == 0;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------------ bites

        private void Bites(ref GameState s, int h, int d)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);
            if (df.ActionFrame != 0 || !IsCommitment(df.Action)) return;
            if ((hf.ActionFlags & ActionFlag.Resolved) != 0) return;
            MoveDefinition m = CurrentMove(hf);
            Fix power;
            int lag = 0;
            if (m != null)
            {
                if (hf.ActionFrame < m.BaitStart || hf.ActionFrame >= m.BaitEnd) return;
                power = m.Power;
                if (hf.Action == ActionId.Trick)
                {
                    TrickParams p = m.Params;
                    bool amped = (hf.ActionFlags & ActionFlag.Amped) != 0;
                    if (m.Behavior == TrickBehavior.LagSpike)
                    {
                        lag = amped ? p.AmpedLagFrames : p.LagFrames;
                        if (amped) power += p.AmpedBalanceDamage;
                    }
                    else if (m.Behavior == TrickBehavior.MonsoonStep && amped)
                    {
                        power += p.AmpedBitePower;
                    }
                }
            }
            else if (hf.Action == ActionId.GatherFinish && hf.MoveSlot == 1)
            {
                // Double Clutch pump: a block attempt during the pump bites.
                MoveDefinition dc = Stats(hf).Tricks[(int)TrickSlot.Back];
                int pumpStart = dc.BaitStart, pumpEnd = dc.BaitEnd;
                if (hf.ActionFrame < pumpStart || hf.ActionFrame >= pumpEnd || df.Action != ActionId.Contest) return;
                power = dc.Power;
            }
            else
            {
                return;
            }

            FighterStats hs = Stats(hf);
            int link = hf.ComboLinks < 1 ? 1 : hf.ComboLinks;
            Fix[] combo = T.Balance.ComboMultipliers;
            Fix comboMult = combo[(link - 1) < combo.Length ? link - 1 : combo.Length - 1];
            Fix damage = power * hs.HandlesMult * comboMult;
            hf.ActionFlags |= ActionFlag.Resolved;
            hf.BitesDrawn++;
            df.Beaten = T.Read.BiteBeatenFrames;
            if (lag > 0)
            {
                df.Lag = lag;
                Emit(ref s, SimEventType.Lag, d, lag, 0);
            }
            GainHype(ref s, h, T.Hype.BiteDrawn);
            Emit(ref s, SimEventType.Bite, d, (damage * (Fix.One - Stats(df).Resist)).RoundToInt(), h);
            DamageBalance(ref s, d, damage);
        }

        // ------------------------------------------------------------------ steals

        private bool Steals(ref GameState s, int h, int d)
        {
            ref FighterState df = ref GameState.Fighter(ref s, d);
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            if ((df.ActionFlags & ActionFlag.Resolved) != 0) return false;

            MoveDefinition m;
            Fix reach;
            bool fromBehind = false;
            bool skipClear = false;
            if (df.Action == ActionId.Swipe)
            {
                m = _tb.Moves.Swipe;
                reach = m.Reach + Stats(df).SwipeReachBonus;
            }
            else if (df.Action == ActionId.Trick)
            {
                m = Stats(df).Tricks[df.MoveSlot];
                bool amped = (df.ActionFlags & ActionFlag.Amped) != 0;
                if (m.Behavior == TrickBehavior.Undercurrent)
                {
                    reach = m.Reach + Stats(df).SwipeReachBonus;
                    skipClear = amped && m.Params.AmpedSkipClear;
                }
                else if (m.Behavior == TrickBehavior.Pickpocket)
                {
                    reach = amped ? m.Params.AmpedReach : m.Reach;
                    fromBehind = true;
                }
                else
                {
                    return false;
                }
            }
            else
            {
                return false;
            }

            int start = Startup(df, m);
            if (df.ActionFrame < start || df.ActionFrame >= start + m.Active) return false;
            if (Fix.Abs(hf.X - df.X) > reach) return false;
            if (!BallExposed(ref s, h, fromBehind)) return false;

            df.ActionFlags |= ActionFlag.Resolved;
            df.Steals++;
            hf.Turnovers++;
            SetAction(ref hf, ActionId.Idle);
            hf.VX = Fix.Zero;
            s.PendingRelease = GameState.None;
            GiveBall(ref s, d, !skipClear, true);
            if (skipClear) s.NeedsClear = false;
            GainHype(ref s, d, T.Hype.Steal);
            Emit(ref s, SimEventType.Steal, d, h, 0);
            Impact(ref s, SimEventType.Steal);
            return true;
        }

        // ------------------------------------------------------------------ contact

        private void Contact(ref GameState s, bool handlerWasBehind)
        {
            // Walking the ball back to clear it is a reset, not a contest: no pushbox fight that can run out the clock.
            // Reaches (swipes, steals) still apply.
            if (s.NeedsClear && s.Ball.Mode == BallMode.Held) return;

            ref FighterState a = ref s.F0;
            ref FighterState b = ref s.F1;
            Fix wa = Stats(a).PushW, wb = Stats(b).PushW;
            bool held = s.Ball.Mode == BallMode.Held;
            int h = held ? s.Ball.Holder : -1;

            if (!held)
            {
                Separate(ref s, wa, wb);
                return;
            }

            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, 1 - h);
            Fix wh = Stats(hf).PushW, wd = Stats(df).PushW;
            bool slip = SlipsThrough(ref s, h);
            bool passable = !Solid(df) || slip || !Solid(hf);

            if (passable)
            {
                // Blow-by: the handler drove past a defender who was airborne, off-balance or bitten.
                if (handlerWasBehind && hf.X > df.X && (hf.Status & StatusFlag.BlowByDone) == 0 && Solid(hf)
                    && hf.VX.IsPositive && hf.VX >= df.VX)
                {
                    hf.Status |= StatusFlag.BlowByDone;
                    df.Beaten = df.Beaten > T.Read.BlowByBeatenFrames ? df.Beaten : T.Read.BlowByBeatenFrames;
                    Emit(ref s, SimEventType.BlowBy, h, 1 - h, 0);
                }
                return;
            }

            Fix gap = df.X - hf.X;
            Fix minGap = wh + wd;
            bool inFront = gap >= Fix.Zero;
            Fix overlap = minGap - Fix.Abs(gap);
            if (overlap < -T.Physics.ContactSlack) return;

            if (inFront && IsDriving(ref s, h))
            {
                // Only the moment the drive meets the defender decides the Read; later frames are a push.
                bool firstContact = s.Frame - hf.LastContactFrame > T.Read.ContactGraceFrames;
                hf.LastContactFrame = s.Frame;
                if (firstContact && PlantedForCharge(df) && CanDrawCharge(hf))
                {
                    Charge(ref s, h, 1 - h);
                    return;
                }
                if (firstContact && df.Action != ActionId.Wall && QuicknessBeat(ref s, h, 1 - h))
                {
                    df.Beaten = df.Beaten > T.Read.BlowByBeatenFrames ? df.Beaten : T.Read.BlowByBeatenFrames;
                    hf.Status |= StatusFlag.InContact;
                    return;
                }
                if (overlap > Fix.Zero) DrivePush(ref s, h, 1 - h, overlap);
                else hf.Status |= StatusFlag.InContact;
                return;
            }

            if (hf.Action == ActionId.PostUp && inFront)
            {
                PostContact(ref s, h, 1 - h, overlap);
                return;
            }

            if (overlap > Fix.Zero) Separate(ref s, wa, wb);
        }

        private bool IsDriving(ref GameState s, int h)
        {
            ref FighterState f = ref GameState.Fighter(ref s, h);
            if (f.VX < T.Read.DriveMinSpeed) return false;
            if ((f.Status & StatusFlag.Driving) != 0) return true;
            if (f.Action == ActionId.GatherFinish && f.ActionFrame < f.Takeoff) return true;
            if (f.Action == ActionId.Trick)
            {
                TrickBehavior b = Stats(f).Tricks[f.MoveSlot].Behavior;
                return b == TrickBehavior.LaunchStep || b == TrickBehavior.FrameSkip || b == TrickBehavior.DropStep;
            }
            return f.Action == ActionId.Shake;
        }

        /// <summary>
        /// A drive into a defender who did not commit: speed and handles against speed and defense, rolled once
        /// with the match RNG at first contact. A defender already sliding with the drive is harder to beat.
        /// </summary>
        private bool QuicknessBeat(ref GameState s, int h, int d)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);
            AttributeBlock a = Stats(hf).Def.Attributes;
            AttributeBlock b = Stats(df).Def.Attributes;
            ReadTable r = T.Read;
            Fix chance = r.DriveBeatBase + r.DriveBeatPerSpeed * (a.Speed - b.Speed) + r.DriveBeatPerHandles * (a.Handles - 5)
                - r.DriveBeatPerDefense * (b.Defense - 5);
            if (df.VX.IsPositive && df.VX >= Stats(df).WalkSpeed) chance = chance * r.DriveBeatSlidingScale;
            chance = Fix.Clamp(chance, r.DriveBeatMin, r.DriveBeatMax);
            return Rng.Unit(ref s.Rng) < chance;
        }

        private bool CanDrawCharge(in FighterState f)
        {
            if (f.Action == ActionId.Trick) return Stats(f).Tricks[f.MoveSlot].CanDrawCharge;
            if (f.Action == ActionId.Shake) return _tb.Shakes[f.MoveSlot].CanDrawCharge;
            return true;
        }

        private bool SlipsThrough(ref GameState s, int h)
        {
            ref FighterState f = ref GameState.Fighter(ref s, h);
            if (f.Action != ActionId.Trick) return false;
            MoveDefinition m = Stats(f).Tricks[f.MoveSlot];
            if (!m.SlipThrough) return false;
            int start = Startup(f, m);
            if (f.ActionFrame < start || f.ActionFrame >= start + m.Active) return false;
            if (m.Behavior == TrickBehavior.FrameSkip)
            {
                ref FighterState d = ref GameState.Fighter(ref s, 1 - h);
                return MidCommit(d);
            }
            return true;
        }

        private void Separate(ref GameState s, Fix wa, Fix wb)
        {
            ref FighterState a = ref s.F0;
            ref FighterState b = ref s.F1;
            if (!Solid(a) || !Solid(b)) return;
            Fix gap = b.X - a.X;
            Fix overlap = wa + wb - Fix.Abs(gap);
            if (overlap <= Fix.Zero) return;
            Fix half = overlap / 2;
            int dir = gap.IsNegative ? -1 : 1; // b sits on this side of a
            a.X -= half * dir;
            b.X += half * dir;
            ClampToCourt(ref a);
            ClampToCourt(ref b);
            // If one of them is pinned to a bound, push the other the rest of the way.
            Fix remaining = wa + wb - Fix.Abs(b.X - a.X);
            if (remaining > Fix.Zero)
            {
                if (dir > 0)
                {
                    if (a.X <= T.Court.LeftBoundX + wa) b.X += remaining; else a.X -= remaining;
                }
                else
                {
                    if (b.X <= T.Court.LeftBoundX + wb) a.X += remaining; else b.X -= remaining;
                }
                ClampToCourt(ref a);
                ClampToCourt(ref b);
            }
        }

        private Fix PushFactor(in FighterState attacker, in FighterState defender)
        {
            int diff = Stats(attacker).Def.Attributes.Strength - Stats(defender).Def.Attributes.Strength;
            Fix k = T.Read.DrivePushBase + T.Read.DrivePushPerStrength * diff;
            if (defender.Action == ActionId.Wall) k *= T.Read.WallPushScale;
            return Fix.Clamp(k, T.Read.DrivePushMin, T.Read.DrivePushMax);
        }

        private void DrivePush(ref GameState s, int h, int d, Fix overlap)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);
            Fix k = PushFactor(hf, df);
            df.X += overlap * k;
            hf.X -= overlap * (Fix.One - k);
            ClampToCourt(ref df);
            Fix minGap = Stats(hf).PushW + Stats(df).PushW;
            if (df.X - hf.X < minGap) hf.X = df.X - minGap;
            hf.VX = hf.VX * k;
            hf.Status |= StatusFlag.InContact;
            df.Status |= StatusFlag.InContact;

            // BumpTimer holds the frame of the last bump: pacing survives contact flickering frame to frame.
            if (s.Frame - hf.BumpTimer < T.Read.DriveBumpInterval) return;
            hf.BumpTimer = s.Frame;
            Fix dmg = T.Balance.DriveBumpDamage * (Fix.One + Stats(df).Def.BumpVulnerability);
            Emit(ref s, SimEventType.DriveBump, h, d, 0);
            DamageBalance(ref s, d, dmg);
        }

        private void PostContact(ref GameState s, int h, int d, Fix overlap)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);
            hf.Status |= StatusFlag.InContact;
            df.Status |= StatusFlag.InContact;
            if (overlap > Fix.Zero)
            {
                // Backing down: the defender gives ground only on bumps; otherwise the post player is held.
                Fix minGap = Stats(hf).PushW + Stats(df).PushW;
                hf.X = df.X - minGap;
                ClampToCourt(ref hf);
            }
            if (hf.HeldInput.Horizontal <= 0) return;
            if (s.Frame - hf.BumpTimer < T.Post.BumpInterval) return;
            hf.BumpTimer = s.Frame;
            hf.Param = hf.ActionFrame; // bump frame: ball exposed briefly
            Fix push = T.Post.BumpPush * PushFactor(hf, df) / T.Read.DrivePushBase;
            if (T.Court.RimX - df.X > T.Post.BackDownMinDistance)
            {
                df.X += push;
                ClampToCourt(ref df);
            }
            Fix dmg = T.Balance.PostBumpDamage * (Fix.One + Stats(df).Def.BumpVulnerability);
            Emit(ref s, SimEventType.PostBump, h, d, 0);
            DamageBalance(ref s, d, dmg);
        }

        private void Charge(ref GameState s, int h, int d)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);
            hf.Turnovers++;
            df.Charges++;
            SetAction(ref hf, ActionId.Knockdown);
            hf.Param = T.Read.ChargeKnockdownFrames;
            hf.VX = Fix.Zero;
            SetAction(ref df, ActionId.Idle);
            df.PlantFrames = 0;
            s.PendingRelease = GameState.None;
            GiveBall(ref s, d, true, true);
            GainHype(ref s, d, T.Hype.ChargeDrawn);
            Emit(ref s, SimEventType.Charge, d, h, 0);
            Impact(ref s, SimEventType.Charge);
            s.Phase = Phase.StopPause;
            s.PhaseFrame = 0;
        }
    }
}
