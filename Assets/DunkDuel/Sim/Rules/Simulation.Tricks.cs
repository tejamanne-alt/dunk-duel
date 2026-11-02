using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Signature Tricks. Each fighter has two offensive Tricks (TRICK toward/neutral = forward slot,
    /// TRICK away/down = back slot) and one defensive Trick. Amped versions cost one Hype bar, start
    /// 3 frames faster and add one property. All numbers come from the move's params.
    /// </summary>
    public sealed partial class Simulation
    {
        private static bool ScoresDirectly(TrickBehavior b) =>
            b == TrickBehavior.LaunchStep || b == TrickBehavior.DoubleClutch || b == TrickBehavior.Drizzle || b == TrickBehavior.GraniteHook;

        private bool TryStartOffenseTrick(ref GameState s, int i, TrickSlot slot, bool amp)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
            FighterStats st = Stats(f);
            MoveDefinition m = st.Tricks[(int)slot];
            TrickParams p = m.Params;
            bool amped = amp && CanSpend(f, T.Hype.AmpedCost);
            Fix dist = Fix.Abs(T.Court.RimX - f.X);
            Fix maxRange = amped && p.AmpedMaxRange > Fix.Zero ? p.AmpedMaxRange : m.MaxRange;
            if (m.MinRange > Fix.Zero && dist < m.MinRange) return false;
            if (maxRange > Fix.Zero && dist > maxRange) return false;
            if (ScoresDirectly(m.Behavior) && s.NeedsClear) return false;
            if (m.Behavior == TrickBehavior.DropStep)
            {
                Fix dx = o.X - f.X;
                if (dx.IsNegative || dx > T.Post.ContactDistance + m.Reach) return false;
            }
            if (amped) Spend(ref f, T.Hype.AmpedCost);
            Emit(ref s, SimEventType.TrickStart, i, (int)slot, amped ? 1 : 0);

            switch (m.Behavior)
            {
                case TrickBehavior.DoubleClutch:
                    {
                        ushort flags = amped ? (ushort)(ActionFlag.Amped | ActionFlag.Uncontestable) : (ushort)0;
                        StartFinish(ref s, i, Buttons.Trick, flags, false, st.Def.FinishGatherFrames + p.PumpDelay, 1, false);
                        return true;
                    }
                case TrickBehavior.GraniteHook:
                    StartJumper(ref s, i, Buttons.Trick, amped ? ActionFlag.Amped : (ushort)0, 0, m);
                    return true;
            }

            SetAction(ref f, ActionId.Trick);
            f.MoveSlot = (byte)slot;
            if (amped) f.ActionFlags |= ActionFlag.Amped;
            if (m.Behavior == TrickBehavior.LaunchStep)
            {
                Fix takeoff = amped ? p.AmpedTakeoffDistance : p.TakeoffDistance;
                int frames = Startup(f, m);
                Fix travel = T.Court.RimX - takeoff - f.X;
                if (travel.IsNegative) travel = Fix.Zero;
                f.VX = Fix.Max(travel / frames, st.WalkSpeed);
            }
            return true;
        }

        private bool TryStartDefenseTrick(ref GameState s, int i, bool amp)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            MoveDefinition m = st.Tricks[(int)TrickSlot.Defense];
            Fix dist = Fix.Abs(T.Court.RimX - f.X);
            if (m.MaxRange > Fix.Zero && dist > m.MaxRange) return false;
            if (m.MinRange > Fix.Zero && dist < m.MinRange) return false;
            bool amped = amp && CanSpend(f, T.Hype.AmpedCost);
            if (amped) Spend(ref f, T.Hype.AmpedCost);
            SetAction(ref f, ActionId.Trick);
            f.MoveSlot = (byte)TrickSlot.Defense;
            if (amped) f.ActionFlags |= ActionFlag.Amped;
            f.VX = Fix.Zero;
            if (m.Behavior == TrickBehavior.RimLock) f.PlantFrames = 0;
            Emit(ref s, SimEventType.TrickStart, i, (int)TrickSlot.Defense, amped ? 1 : 0);
            return true;
        }

        private void AdvanceTrick(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            MoveDefinition m = st.Tricks[f.MoveSlot];
            TrickParams p = m.Params;
            bool amped = (f.ActionFlags & ActionFlag.Amped) != 0;
            int start = Startup(f, m);
            int fr = f.ActionFrame;
            int total = start + m.Active + m.Recovery;

            if (m.Kind == MoveKind.OffenseTrick && role != Role.Handler)
            {
                SetAction(ref f, ActionId.Idle);
                return;
            }
            if (m.Kind == MoveKind.DefenseTrick && role == Role.Handler)
            {
                SetAction(ref f, ActionId.Idle);
                return;
            }

            switch (m.Behavior)
            {
                case TrickBehavior.LaunchStep:
                    if (fr >= start)
                    {
                        // Take off at once into the dunk; Amped glides from the free-throw line with an apex hang.
                        ushort flags = amped ? (ushort)(ActionFlag.Amped | ActionFlag.DrainOnMake) : (ushort)0;
                        StartFinish(ref s, i, Buttons.Trick, flags, true, 1 + st.Def.JumpRiseFrames, 2, amped);
                    }
                    break;

                case TrickBehavior.Drizzle:
                    ApplyMoveDisplacement(ref f, m, m.MoveDistance);
                    if (fr >= m.MoveEnd)
                    {
                        ushort flags = ActionFlag.NoExposure;
                        if (amped) flags |= (ushort)(ActionFlag.Amped | ActionFlag.HalfContest);
                        StartJumper(ref s, i, Buttons.Trick, flags, p.GatherReduction, null);
                    }
                    break;

                case TrickBehavior.MonsoonStep:
                    {
                        bool second = (f.ActionFlags & ActionFlag.Repeat) != 0;
                        ApplyMoveDisplacement(ref f, m, second ? -p.AmpedSecondDistance : m.MoveDistance);
                        if (InCancel(f, m) && Pressed(f, Buttons.Shoot))
                        {
                            Consume(ref f, Buttons.Shoot);
                            if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
                        }
                        if (fr >= total)
                        {
                            if (amped && !second)
                            {
                                ushort keep = (ushort)(f.ActionFlags & ActionFlag.Amped);
                                byte slot = f.MoveSlot;
                                SetAction(ref f, ActionId.Trick);
                                f.MoveSlot = slot;
                                f.ActionFlags = (ushort)(keep | ActionFlag.Repeat);
                            }
                            else
                            {
                                SetAction(ref f, ActionId.Idle);
                            }
                        }
                        break;
                    }

                case TrickBehavior.FrameSkip:
                    ApplyMoveDisplacement(ref f, m, m.MoveDistance);
                    if (InCancel(f, m))
                    {
                        if (Pressed(f, Buttons.Shoot))
                        {
                            Consume(ref f, Buttons.Shoot);
                            if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
                        }
                        if (Pressed(f, Buttons.Shake))
                        {
                            Consume(ref f, Buttons.Shake);
                            StartShake(ref s, i, PressDir(f));
                            return;
                        }
                    }
                    if (fr >= total)
                    {
                        if (amped && f.Param + 1 < p.AmpedRepeat)
                        {
                            int reps = f.Param + 1;
                            ushort keep = (ushort)(f.ActionFlags & ActionFlag.Amped);
                            byte slot = f.MoveSlot;
                            SetAction(ref f, ActionId.Trick);
                            f.MoveSlot = slot;
                            f.ActionFlags = keep;
                            f.Param = reps;
                        }
                        else
                        {
                            SetAction(ref f, ActionId.Idle);
                        }
                    }
                    break;

                case TrickBehavior.LagSpike:
                    Friction(ref f);
                    if (InCancel(f, m))
                    {
                        if (Pressed(f, Buttons.Shoot))
                        {
                            Consume(ref f, Buttons.Shoot);
                            if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
                        }
                        FrameInput held = f.HeldInput;
                        if (held.Has(Buttons.Turbo) && held.Horizontal != 0)
                        {
                            SetAction(ref f, ActionId.Idle);
                            return;
                        }
                    }
                    if (fr >= total) SetAction(ref f, ActionId.Idle);
                    break;

                case TrickBehavior.DropStep:
                    if (fr < start) Friction(ref f);
                    else ApplyMoveDisplacement(ref f, m, m.MoveDistance);
                    if (InCancel(f, m) && Pressed(f, Buttons.Shoot))
                    {
                        Consume(ref f, Buttons.Shoot);
                        if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
                    }
                    if (fr >= total) SetAction(ref f, ActionId.Idle);
                    break;

                case TrickBehavior.ChaseDown:
                    {
                        if (fr < start)
                        {
                            Friction(ref f);
                            break;
                        }
                        if (fr < start + m.Active)
                        {
                            ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
                            Fix speed = amped ? p.AmpedSprintSpeed : p.SprintSpeed;
                            f.VX = o.X > f.X ? speed : -speed;
                            break;
                        }
                        Whiff(ref f, m.WhiffRecovery);
                        break;
                    }

                case TrickBehavior.Undercurrent:
                    Friction(ref f);
                    if ((f.ActionFlags & ActionFlag.Resolved) != 0)
                    {
                        if (fr >= start + m.Active + T.Read.SwipeHitRecovery) SetAction(ref f, ActionId.Idle);
                    }
                    else if (fr >= start + m.Active)
                    {
                        Whiff(ref f, m.WhiffRecovery);
                    }
                    break;

                case TrickBehavior.Pickpocket:
                    {
                        if ((f.ActionFlags & ActionFlag.Resolved) != 0)
                        {
                            Friction(ref f);
                            if (fr >= start + m.Active + T.Read.SwipeHitRecovery) SetAction(ref f, ActionId.Idle);
                            break;
                        }
                        if (fr < start)
                        {
                            Friction(ref f);
                            if (fr == start - 1)
                            {
                                // Commit the dash direction at the end of start-up.
                                ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
                                f.Facing = (sbyte)(o.X >= f.X ? 1 : -1);
                            }
                            break;
                        }
                        if (fr < start + m.Active)
                        {
                            f.VX = p.DashDistance / m.Active * f.Facing;
                            break;
                        }
                        Whiff(ref f, m.WhiffRecovery);
                        break;
                    }

                case TrickBehavior.RimLock:
                    f.VX = Fix.Zero;
                    f.PlantFrames++;
                    if (fr >= start && !f.IsHeld(Buttons.Trick) && !amped)
                    {
                        SetAction(ref f, ActionId.Unplant);
                        break;
                    }
                    if (fr >= start + p.Duration) SetAction(ref f, ActionId.Unplant);
                    break;

                default:
                    if (fr >= total) SetAction(ref f, ActionId.Idle);
                    break;
            }
        }

        /// <summary>Trick effects that need both fighters: Drop-Step impact, Rim Lock and Chase-Down auto-contests.</summary>
        private void TrickInteractions(ref GameState s, int h, int d)
        {
            ref FighterState hf = ref GameState.Fighter(ref s, h);
            ref FighterState df = ref GameState.Fighter(ref s, d);

            if (hf.Action == ActionId.Trick)
            {
                MoveDefinition m = Stats(hf).Tricks[hf.MoveSlot];
                if (m.Behavior == TrickBehavior.DropStep && hf.ActionFrame == Startup(hf, m))
                {
                    TrickParams p = m.Params;
                    bool amped = (hf.ActionFlags & ActionFlag.Amped) != 0;
                    Fix dx = df.X - hf.X;
                    if (!dx.IsNegative && dx <= T.Post.ContactDistance + m.Reach)
                    {
                        df.X -= p.KnockbackDistance;
                        ClampToCourt(ref df);
                        if (df.Action == ActionId.Wall || df.Action == ActionId.Idle) SetAction(ref df, ActionId.Idle);
                        df.Beaten = m.Active + m.Recovery;
                        if (amped) df.NoContest = p.AmpedNoContestFrames;
                        Emit(ref s, SimEventType.PostBump, h, d, 1);
                        DamageBalance(ref s, d, p.BalanceDamage);
                    }
                }
            }

            if (df.Action == ActionId.Trick)
            {
                MoveDefinition m = Stats(df).Tricks[df.MoveSlot];
                TrickParams p = m.Params;
                int start = Startup(df, m);
                bool amped = (df.ActionFlags & ActionFlag.Amped) != 0;
                bool finishing = hf.Action == ActionId.GatherFinish
                    || (hf.Action == ActionId.GatherJumper && (hf.ActionFlags & ActionFlag.Hook) != 0);
                if (!finishing) return;
                int framesToPeak = hf.Peak - hf.ActionFrame;
                int jumpLead = T.Read.ContestStartup + Stats(df).Def.JumpRiseFrames;

                if (m.Behavior == TrickBehavior.RimLock && df.ActionFrame >= start)
                {
                    Fix hd = Fix.Abs(T.Court.RimX - hf.X);
                    if (hd <= p.AutoContestRange && framesToPeak <= jumpLead)
                    {
                        ushort flags = ActionFlag.AutoContest;
                        if (amped)
                        {
                            flags |= ActionFlag.Amped;
                            if (p.AmpedCatch) flags |= ActionFlag.CatchBlock;
                        }
                        StartContest(ref s, d, flags);
                        // Time the apex to the finisher's release peak.
                        int t = framesToPeak - Stats(df).Def.JumpRiseFrames;
                        df.Takeoff = t < 1 ? 1 : t;
                    }
                }
                else if (m.Behavior == TrickBehavior.ChaseDown && df.ActionFrame >= start && df.ActionFrame < start + m.Active)
                {
                    if (Fix.Abs(hf.X - df.X) <= p.TriggerDistance)
                    {
                        Fix vx = df.VX;
                        StartContest(ref s, d, (ushort)(ActionFlag.FromBehindOk | (amped ? ActionFlag.Amped : 0)));
                        df.VX = vx;
                        int lead = framesToPeak - Stats(df).Def.JumpRiseFrames;
                        df.Takeoff = lead < 1 ? 1 : lead;
                    }
                }
            }
        }
    }
}
