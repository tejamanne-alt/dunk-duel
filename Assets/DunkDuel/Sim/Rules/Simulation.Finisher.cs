using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Signature Finishers: 3 Hype bars, only from the fighter's trigger zone, a 120-180 frame cinematic
    /// that scores 3. A defender holding at least one bar gets one Denial timing ring.
    /// </summary>
    public sealed partial class Simulation
    {
        private bool TryStartFinisher(ref GameState s, int i)
        {
            ref FighterState f = ref GameState.Fighter(ref s, i);
            ref FighterState o = ref GameState.Fighter(ref s, 1 - i);
            FinisherDefinition fd = Stats(f).Def.Finisher;
            if (s.NeedsClear) return false;
            if (!CanSpend(f, T.Hype.FinisherCost)) return false;
            if (f.X < fd.ZoneMin || f.X > fd.ZoneMax) return false;

            Spend(ref f, T.Hype.FinisherCost);
            f.FinisherAtt++;
            s.Fin = new FinisherState
            {
                Active = true,
                Attacker = (byte)i,
                Frame = 0,
                Frames = fd.Frames,
                DenyStart = fd.DenyStart,
                DenyFrames = fd.DenyFrames,
                HasRing = o.Hype >= T.Hype.DenialCost,
                Attempted = false,
                StartX = f.X,
                EndX = fd.EndX,
            };
            s.Phase = Phase.Finisher;
            s.PhaseFrame = 0;
            s.PendingRelease = GameState.None;
            SetAction(ref f, ActionId.Finisher);
            SetAction(ref o, ActionId.Finisher);
            f.VX = Fix.Zero;
            o.VX = Fix.Zero;
            o.AgeFinisher = NoPress; // a stale press must not spend the ring
            Emit(ref s, SimEventType.FinisherStart, i, f.CharIndex, s.Fin.HasRing ? 1 : 0);
            return true;
        }

        private void FinisherFrame(ref GameState s)
        {
            s.PhaseFrame++;
            ref FinisherState fin = ref s.Fin;
            fin.Frame++;
            int a = fin.Attacker;
            int d = 1 - a;
            ref FighterState f = ref GameState.Fighter(ref s, a);
            ref FighterState o = ref GameState.Fighter(ref s, d);

            // Keep sim positions continuous for the camera; the cinematic itself is presentation.
            f.X = Fix.Lerp(fin.StartX, fin.EndX, Fix.Ratio(fin.Frame, fin.Frames));
            f.Y = Fix.Zero;
            f.VX = Fix.Zero;
            f.VY = Fix.Zero;
            s.Ball.X = f.X + T.Physics.HoldForward * f.Facing;
            s.Ball.Y = f.Y + T.Physics.HoldHeight;

            if (fin.Frame == fin.DenyStart) Emit(ref s, SimEventType.DenialWindow, d, fin.DenyFrames, 0);

            if (fin.HasRing && !fin.Attempted && Pressed(o, Buttons.Finisher))
            {
                Consume(ref o, Buttons.Finisher);
                fin.Attempted = true;
                Spend(ref o, T.Hype.DenialCost);
                bool inside = fin.Frame >= fin.DenyStart && fin.Frame < fin.DenyStart + fin.DenyFrames;
                Emit(ref s, SimEventType.DenialAttempt, d, inside ? 1 : 0, fin.Frame - fin.DenyStart);
                if (inside)
                {
                    o.Denials++;
                    fin.Active = false;
                    SetAction(ref f, ActionId.Idle);
                    SetAction(ref o, ActionId.Idle);
                    GiveBall(ref s, d, true, true);
                    Emit(ref s, SimEventType.Denied, d, a, 0);
                    Impact(ref s, SimEventType.Denied);
                    s.Phase = Phase.StopPause;
                    s.PhaseFrame = 0;
                    return;
                }
            }

            if (fin.Frame >= fin.Frames)
            {
                fin.Active = false;
                SetAction(ref f, ActionId.Idle);
                SetAction(ref o, ActionId.Idle);
                s.Ball.Mode = BallMode.Dead;
                s.Ball.X = T.Court.RimX;
                s.Ball.Y = T.Court.RimY;
                s.Ball.VX = Fix.Zero;
                s.Ball.VY = Fix.Zero;
                Emit(ref s, SimEventType.FinisherScore, a, T.Match.PointsFinisher, 0);
                EmitAt(ref s, SimEventType.NetSnap, a, (int)ShotResult.Swish, 1, T.Court.RimX, T.Court.RimY);
                ShotZone zone = fin.StartX <= T.Court.ArcX ? ShotZone.Deep : ShotZone.Rim;
                AwardScore(ref s, a, T.Match.PointsFinisher, ShotKind.Finisher, zone, ShotResult.Swish, false, false, false, true);
            }
        }
    }
}
