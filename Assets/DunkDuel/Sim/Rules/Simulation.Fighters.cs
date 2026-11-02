using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    public sealed partial class Simulation
    {
        private enum Role : byte { Handler, Defender, Free }

        private Role RoleOf(ref GameState s, int i)
        {
            if (s.Ball.Mode != BallMode.Held) return Role.Free;
            return s.Ball.Holder == i ? Role.Handler : Role.Defender;
        }

        private FighterStats Stats(in FighterState f) => _tb.Fighters[f.CharIndex];

        private void TickTimers(ref FighterState f)
        {
            if (f.Beaten > 0) f.Beaten--;
            if (f.NoContest > 0) f.NoContest--;
            if (f.ComboTimer > 0)
            {
                f.ComboTimer--;
                if (f.ComboTimer == 0 && f.Action != ActionId.Shake) f.ComboLinks = 0;
            }
            f.Status = (ushort)((f.Status & ~(StatusFlag.WasInContact | StatusFlag.InContact | StatusFlag.Protecting | StatusFlag.Driving))
                | ((f.Status & StatusFlag.InContact) != 0 ? StatusFlag.WasInContact : 0));
        }

        private void UpdateFighter(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            Role role = RoleOf(ref s, i);
            if (f.Action != ActionId.Idle) f.ActionFrame++;

            switch (f.Action)
            {
                case ActionId.Shake: AdvanceShake(ref s, i, role); break;
                case ActionId.GatherJumper:
                case ActionId.GatherFinish: AdvanceGather(ref s, i); break;
                case ActionId.ShotRecover: AdvanceRecover(ref f, T.Shot.ShotLandRecovery); break;
                case ActionId.PostUp: AdvancePostUp(ref s, i, role); break;
                case ActionId.Trick: AdvanceTrick(ref s, i, role); break;
                case ActionId.Wall: AdvanceWall(ref s, i, role); break;
                case ActionId.Unplant:
                    f.VX = Fix.Zero;
                    if (f.ActionFrame >= T.Read.UnplantFrames)
                    {
                        f.PlantFrames = 0;
                        SetAction(ref f, ActionId.Idle);
                    }
                    break;
                case ActionId.Contest: AdvanceContest(ref s, i); break;
                case ActionId.Swipe: AdvanceSwipe(ref s, i); break;
                case ActionId.OffBalance:
                case ActionId.Knockdown:
                    Friction(ref f);
                    if (f.ActionFrame >= f.Param) SetAction(ref f, ActionId.Idle);
                    break;
                case ActionId.Stumble:
                    Friction(ref f);
                    if (f.ActionFrame >= T.Balance.StumbleFrames)
                    {
                        f.Balance = T.Balance.AfterStumble;
                        f.BalanceIdle = 0;
                        SetAction(ref f, ActionId.Idle);
                    }
                    break;
                case ActionId.Celebrate:
                case ActionId.Finisher:
                    SetAction(ref f, ActionId.Idle);
                    break;
            }

            if (f.Action == ActionId.Idle) HandleIdle(ref s, i, RoleOf(ref s, i));
        }

        private void HandleIdle(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            if (!f.Grounded)
            {
                // Airborne without an action (knocked off a jump): no control until landing.
                return;
            }

            if (f.Lag > 0)
            {
                // Lag Spike: the next action comes out late. Lag only drains while free to act.
                f.Lag--;
                Locomotion(ref s, i, role);
                return;
            }

            FrameInput held = f.HeldInput;
            switch (role)
            {
                case Role.Handler:
                    if (Pressed(f, Buttons.Finisher))
                    {
                        Consume(ref f, Buttons.Finisher);
                        if (TryStartFinisher(ref s, i)) return;
                    }
                    if (Pressed(f, Buttons.Trick))
                    {
                        StickDir d = PressDir(f);
                        TrickSlot slot = d.IsAway() || d.IsDown() ? TrickSlot.Back : TrickSlot.Forward;
                        bool amp = f.TrickAmp;
                        Consume(ref f, Buttons.Trick);
                        if (TryStartOffenseTrick(ref s, i, slot, amp)) return;
                    }
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
                    if (held.Dir == StickDir.DownToward && TryStartPostUp(ref s, i)) return;
                    break;

                case Role.Defender:
                    if (Pressed(f, Buttons.Trick))
                    {
                        bool amp = f.TrickAmp;
                        Consume(ref f, Buttons.Trick);
                        if (TryStartDefenseTrick(ref s, i, amp)) return;
                    }
                    if (Pressed(f, Buttons.Shoot))
                    {
                        Consume(ref f, Buttons.Shoot);
                        if (StartContest(ref s, i, 0)) return;
                    }
                    if (Pressed(f, Buttons.Shake))
                    {
                        Consume(ref f, Buttons.Shake);
                        StartSwipe(ref s, i);
                        return;
                    }
                    if (held.Vertical < 0)
                    {
                        StartWall(ref s, i);
                        return;
                    }
                    break;

                case Role.Free:
                    if (Pressed(f, Buttons.Shoot))
                    {
                        Consume(ref f, Buttons.Shoot);
                        if (StartContest(ref s, i, 0)) return;
                    }
                    break;
            }
            Locomotion(ref s, i, role);
        }

        // ------------------------------------------------------------------ locomotion

        private void Locomotion(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            FrameInput held = f.HeldInput;
            int h = held.Horizontal;
            bool turbo = held.Has(Buttons.Turbo);
            Fix speed;
            switch (role)
            {
                case Role.Handler:
                    if (held.Vertical < 0)
                    {
                        speed = st.WalkSpeed * T.Movement.ProtectScale;
                        f.Status |= StatusFlag.Protecting;
                    }
                    else
                    {
                        speed = turbo ? st.DriveSpeed : st.WalkSpeed;
                        if (turbo && h > 0) f.Status |= StatusFlag.Driving;
                    }
                    break;
                case Role.Defender:
                    speed = turbo ? st.SlideSpeed : st.WalkSpeed;
                    break;
                default:
                    speed = turbo ? st.DriveSpeed : st.WalkSpeed;
                    break;
            }

            Fix target = speed * h;
            if (role == Role.Defender && h == 0 && (f.Status & StatusFlag.SimpleMode) != 0)
            {
                // Simple mode: hold position a fixed gap in front of the ball handler.
                ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
                Fix ideal = o.X + T.Movement.AutoDefendGap;
                Fix diff = ideal - f.X;
                if (Fix.Abs(diff) > st.WalkSpeed) target = diff.IsPositive ? st.WalkSpeed : -st.WalkSpeed;
            }
            Accelerate(ref f, target);
        }

        private void Accelerate(ref FighterState f, Fix target)
        {
            bool speedingUp = (target.Sign != 0 && target.Sign != f.VX.Sign) || Fix.Abs(target) > Fix.Abs(f.VX);
            f.VX = Fix.MoveTowards(f.VX, target, speedingUp ? T.Physics.GroundAccel : T.Physics.GroundDecel);
        }

        private void Friction(ref FighterState f)
        {
            if (f.Grounded) f.VX = Fix.MoveTowards(f.VX, Fix.Zero, T.Physics.GroundDecel);
        }

        private void Integrate(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            bool hanging = f.Action == ActionId.GatherFinish && (f.ActionFlags & ActionFlag.HangUsed) != 0
                && f.ActionFrame >= f.Param && f.ActionFrame < f.Param + Stats(f).Def.MasteryParams.HangFrames;
            if (!f.Grounded || f.VY.IsPositive)
            {
                if (!hanging) f.VY += T.Physics.FighterGravity;
                f.Y += f.VY;
                if (f.Y <= Fix.Zero)
                {
                    f.Y = Fix.Zero;
                    f.VY = Fix.Zero;
                    f.Status |= StatusFlag.Grounded;
                    OnLand(ref s, i);
                }
                else
                {
                    f.Status &= unchecked((ushort)~StatusFlag.Grounded);
                }
            }
            f.X += f.VX;
            ClampToCourt(ref f);
        }

        private void ClampToCourt(ref FighterState f)
        {
            Fix w = Stats(f).PushW;
            Fix lo = T.Court.LeftBoundX + w;
            Fix hi = T.Court.BaselineX - w;
            if (f.X < lo)
            {
                f.X = lo;
                if (f.VX.IsNegative) f.VX = Fix.Zero;
            }
            else if (f.X > hi)
            {
                f.X = hi;
                if (f.VX.IsPositive) f.VX = Fix.Zero;
            }
        }

        private void OnLand(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            Emit(ref s, SimEventType.Land, i, 0, 0);
            switch (f.Action)
            {
                case ActionId.Contest:
                case ActionId.ShotRecover:
                    f.Param = f.ActionFrame; // landing frame, recovery counts from here
                    f.ActionFlags |= ActionFlag.Resolved;
                    f.VX = Fix.Zero;
                    break;
                case ActionId.Trick:
                    f.VX = Fix.Zero;
                    break;
            }
        }

        private void AdvanceRecover(ref FighterState f, int landRecovery)
        {
            if (!f.Grounded) return;
            if ((f.ActionFlags & ActionFlag.Resolved) == 0)
            {
                // Released from the ground (set shot): recovery counts from the release.
                f.ActionFlags |= ActionFlag.Resolved;
                f.Param = f.ActionFrame;
            }
            Friction(ref f);
            if (f.ActionFrame - f.Param >= landRecovery) SetAction(ref f, ActionId.Idle);
        }

        private void UpdateFacing(ref GameState s)
        {
            for (int i = 0; i < 2; i++)
            {
                ref FighterState f = ref GameState.Fighter(ref s, i);
                ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
                if (f.Action == ActionId.Finisher) continue;
                switch (RoleOf(ref s, i))
                {
                    case Role.Handler:
                        f.Facing = (sbyte)(f.Action == ActionId.PostUp ? -1 : 1);
                        break;
                    case Role.Defender:
                        if (o.X != f.X) f.Facing = (sbyte)(o.X > f.X ? 1 : -1);
                        break;
                    default:
                        if (s.Ball.X != f.X) f.Facing = (sbyte)(s.Ball.X > f.X ? 1 : -1);
                        break;
                }
            }
        }

        /// <summary>Non-live phases: finish jumps, land, slow to a stop, no new actions.</summary>
        private void SettleFighters(ref GameState s)
        {
            for (int i = 0; i < 2; i++)
            {
                ref FighterState f = ref GameState.Fighter(ref s, i);
                if (f.Action == ActionId.Stumble || f.Action == ActionId.Knockdown || f.Action == ActionId.OffBalance)
                {
                    f.ActionFrame++;
                    int length = f.Action == ActionId.Stumble ? T.Balance.StumbleFrames : f.Param;
                    if (f.ActionFrame >= length)
                    {
                        if (f.Action == ActionId.Stumble) f.Balance = T.Balance.AfterStumble;
                        SetAction(ref f, ActionId.Idle);
                    }
                }
                else if (f.Action != ActionId.Celebrate && f.Action != ActionId.Idle && f.Grounded)
                {
                    SetAction(ref f, ActionId.Idle);
                }
                else if (f.Action == ActionId.Celebrate)
                {
                    f.ActionFrame++;
                }
                Friction(ref f);
                Integrate(ref s, i);
                f.PlantFrames = 0;
            }
        }

        // ------------------------------------------------------------------ shakes

        private int ShakeSlot(StickDir d)
        {
            if (d.IsDown()) return 2;
            if (d.IsToward()) return 0;
            if (d.IsAway()) return 1;
            return 3;
        }

        private void StartShake(ref GameState s, int i, StickDir dir)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            bool chaining = f.ComboTimer > 0 || (f.Action == ActionId.Shake && InCancel(f, _tb.Shakes[f.MoveSlot]));
            int links = chaining && f.ComboLinks < st.MaxComboLinks ? f.ComboLinks + 1 : 1;
            if (chaining && f.ComboLinks >= st.MaxComboLinks) links = 1;
            int slot = ShakeSlot(dir);
            SetAction(ref f, ActionId.Shake);
            f.MoveSlot = (byte)slot;
            f.ComboLinks = (byte)links;
            f.ComboTimer = 0;
            Emit(ref s, SimEventType.ShakeStart, i, slot, links);
            if (links > 1) Emit(ref s, SimEventType.ComboLink, i, links, 0);
        }

        private static bool InCancel(in FighterState f, MoveDefinition m) =>
            f.ActionFrame >= m.CancelStart && f.ActionFrame < m.CancelEnd;

        private void AdvanceShake(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            MoveDefinition m = _tb.Shakes[f.MoveSlot];
            if (role != Role.Handler)
            {
                SetAction(ref f, ActionId.Idle);
                return;
            }
            ApplyMoveDisplacement(ref f, m, m.MoveDistance);

            if (InCancel(f, m))
            {
                if (Pressed(f, Buttons.Shake) && f.ComboLinks < Stats(f).MaxComboLinks)
                {
                    Consume(ref f, Buttons.Shake);
                    StartShake(ref s, i, PressDir(f));
                    return;
                }
                if (Pressed(f, Buttons.Shoot))
                {
                    Consume(ref f, Buttons.Shoot);
                    int links = f.ComboLinks;
                    if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
                    f.ComboLinks = (byte)links;
                }
                if (Pressed(f, Buttons.Trick))
                {
                    StickDir d = PressDir(f);
                    TrickSlot slot = d.IsAway() || d.IsDown() ? TrickSlot.Back : TrickSlot.Forward;
                    bool amp = f.TrickAmp;
                    Consume(ref f, Buttons.Trick);
                    if (TryStartOffenseTrick(ref s, i, slot, amp)) return;
                }
                FrameInput held = f.HeldInput;
                if (held.Has(Buttons.Turbo) && held.Horizontal != 0)
                {
                    EndShake(ref f);
                    return;
                }
            }
            if (f.ActionFrame >= m.Total) EndShake(ref f);
        }

        private void EndShake(ref FighterState f)
        {
            byte links = f.ComboLinks;
            SetAction(ref f, ActionId.Idle);
            f.ComboLinks = links;
            f.ComboTimer = T.Balance.ComboWindowFrames;
        }

        /// <summary>Spreads <paramref name="distance"/> evenly over the move's [MoveStart, MoveEnd) frames.</summary>
        private void ApplyMoveDisplacement(ref FighterState f, MoveDefinition m, Fix distance)
        {
            int frames = m.MoveEnd - m.MoveStart;
            if (frames > 0 && f.ActionFrame >= m.MoveStart && f.ActionFrame < m.MoveEnd)
            {
                f.VX = distance / frames;
            }
            else
            {
                Friction(ref f);
            }
        }

        // ------------------------------------------------------------------ defense basics

        private void StartWall(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            SetAction(ref f, ActionId.Wall);
            f.PlantFrames = 0;
            f.VX = Fix.Zero;
            Emit(ref s, SimEventType.Plant, i, 0, 0);
        }

        private void AdvanceWall(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            f.VX = Fix.Zero;
            f.PlantFrames++;
            if (f.HeldInput.Vertical >= 0 || role != Role.Defender)
            {
                SetAction(ref f, ActionId.Unplant);
            }
        }

        private bool StartContest(ref GameState s, int i, ushort flags)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            if (f.NoContest > 0 && RoleOf(ref s, i) == Role.Defender) return false;
            SetAction(ref f, ActionId.Contest);
            f.ActionFlags = flags;
            f.Param = -1;
            f.Takeoff = T.Read.ContestStartup;
            return true;
        }

        private void AdvanceContest(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FighterStats st = Stats(f);
            if (f.ActionFrame < f.Takeoff)
            {
                Friction(ref f);
                return;
            }
            if (f.ActionFrame == f.Takeoff)
            {
                f.VY = st.JumpV0;
                f.Status &= unchecked((ushort)~StatusFlag.Grounded);
                // Lunge toward the shooter (or the ball when it is loose).
                ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
                Fix targetX = s.Ball.Mode == BallMode.Held ? o.X : s.Ball.X;
                int dir = targetX > f.X ? 1 : (targetX < f.X ? -1 : 0);
                Fix lunge = T.Read.ContestLunge * dir;
                if ((f.ActionFlags & ActionFlag.FromBehindOk) != 0) lunge = f.VX; // chase-down keeps its sprint
                f.VX = lunge;
                Emit(ref s, SimEventType.Jump, i, 0, 0);
                return;
            }
            if ((f.ActionFlags & ActionFlag.Resolved) != 0 && f.Grounded)
            {
                Friction(ref f);
                if (f.ActionFrame - f.Param >= T.Read.ContestLandRecovery) SetAction(ref f, ActionId.Idle);
            }
        }

        private void StartSwipe(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            SetAction(ref f, ActionId.Swipe);
            f.VX = Fix.Zero;
            Emit(ref s, SimEventType.Swipe, i, 0, 0);
        }

        private void AdvanceSwipe(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            MoveDefinition m = _tb.Moves.Swipe;
            Friction(ref f);
            int activeEnd = m.Startup + m.Active;
            if ((f.ActionFlags & ActionFlag.Resolved) != 0)
            {
                if (f.ActionFrame >= activeEnd + T.Read.SwipeHitRecovery) SetAction(ref f, ActionId.Idle);
                return;
            }
            if (f.ActionFrame >= activeEnd) Whiff(ref f, m.WhiffRecovery);
        }

        /// <summary>A missed reach: off-balance (no pushbox) for at least the whiff minimum.</summary>
        private void Whiff(ref FighterState f, int recovery)
        {
            int frames = recovery > T.Read.WhiffOffBalanceFrames ? recovery : T.Read.WhiffOffBalanceFrames;
            SetAction(ref f, ActionId.OffBalance);
            f.Param = frames;
        }

        // ------------------------------------------------------------------ post control

        private bool TryStartPostUp(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
            if (!Stats(f).Def.MasteryParams.CanBackDown) return false;
            Fix dx = o.X - f.X;
            if (dx.IsNegative || dx > T.Post.ContactDistance) return false;
            if (T.Court.RimX - f.X < T.Post.BackDownMinDistance) return false;
            SetAction(ref f, ActionId.PostUp);
            f.BumpTimer = s.Frame; // first bump one interval after setting up the post
            f.Param = -T.Post.BumpExposedFrames - 1; // frame of the last bump
            return true;
        }

        private void AdvancePostUp(ref GameState s, int i, Role role)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            FrameInput held = f.HeldInput;
            if (role != Role.Handler || held.Vertical >= 0)
            {
                SetAction(ref f, ActionId.Idle);
                return;
            }
            if (Pressed(f, Buttons.Trick))
            {
                StickDir d = PressDir(f);
                TrickSlot slot = d.IsAway() ? TrickSlot.Back : TrickSlot.Forward;
                bool amp = f.TrickAmp;
                Consume(ref f, Buttons.Trick);
                if (TryStartOffenseTrick(ref s, i, slot, amp)) return;
            }
            if (Pressed(f, Buttons.Shoot))
            {
                Consume(ref f, Buttons.Shoot);
                if (TryStartShot(ref s, i, Buttons.Shoot, 0)) return;
            }
            Fix target = held.Horizontal > 0 && T.Court.RimX - f.X > T.Post.BackDownMinDistance ? T.Movement.BackDownSpeed : Fix.Zero;
            if (held.Horizontal < 0) target = -T.Movement.BackDownSpeed;
            Accelerate(ref f, target);
        }
    }
}
