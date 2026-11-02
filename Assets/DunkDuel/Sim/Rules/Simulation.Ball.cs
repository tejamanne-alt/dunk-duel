using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>Ball: carried, on a shot path, loose (rebound scramble) or dead.</summary>
    public sealed partial class Simulation
    {
        private void UpdateBall(ref GameState s)
        {
            ref BallState ball = ref s.Ball;
            switch (ball.Mode)
            {
                case BallMode.Held:
                    {
                        ref FighterState h = ref GameState.Fighter(ref s, ball.Holder);
                        ball.X = h.X + T.Physics.HoldForward * h.Facing;
                        ball.Y = h.Y + T.Physics.HoldHeight;
                        ball.VX = h.VX;
                        ball.VY = h.VY;
                        break;
                    }
                case BallMode.Flight:
                    ShotFlight(ref s);
                    break;
                case BallMode.Loose:
                    LoosePhysics(ref s);
                    if (s.Phase == Phase.Live) TryGrab(ref s);
                    break;
                case BallMode.Dead:
                    DeadPhysics(ref s);
                    break;
            }
        }

        private void LoosePhysics(ref GameState s)
        {
            ref BallState ball = ref s.Ball;
            Fix r = T.Court.BallRadius;
            Fix prevX = ball.X;
            ball.VY += T.Physics.BallGravity;
            ball.X += ball.VX;
            ball.Y += ball.VY;

            // Backboard: a vertical segment facing -X.
            if (prevX + r <= T.Court.BackboardX && ball.X + r > T.Court.BackboardX
                && ball.Y >= T.Court.BackboardBottom && ball.Y <= T.Court.BackboardTop)
            {
                ball.X = T.Court.BackboardX - r;
                ball.VX = -ball.VX * T.Physics.BackboardRestitution;
                Emit(ref s, SimEventType.BackboardHit, 2, 0, 0);
            }

            Floor(ref ball, r);

            Fix lo = T.Court.LeftBoundX + r;
            Fix hi = T.Court.BaselineX - r;
            if (ball.X < lo)
            {
                ball.X = lo;
                ball.VX = -ball.VX * T.Physics.WallRestitution;
            }
            else if (ball.X > hi)
            {
                ball.X = hi;
                ball.VX = -ball.VX * T.Physics.WallRestitution;
            }
            if (ball.NoGrab > 0) ball.NoGrab--;
        }

        private void Floor(ref BallState ball, Fix r)
        {
            if (ball.Y > r) return;
            ball.Y = r;
            if (-ball.VY > T.Physics.RestBounceSpeed) ball.VY = -ball.VY * T.Physics.FloorRestitution;
            else ball.VY = Fix.Zero;
            ball.VX = ball.VX * T.Physics.RollFriction;
        }

        private void DeadPhysics(ref GameState s)
        {
            ref BallState ball = ref s.Ball;
            ball.VY += T.Physics.BallGravity;
            ball.X += ball.VX;
            ball.Y += ball.VY;
            Floor(ref ball, T.Court.BallRadius);
        }

        private bool CanGrab(in FighterState f)
        {
            switch (f.Action)
            {
                case ActionId.Idle:
                case ActionId.Contest:
                case ActionId.Celebrate:
                    return true;
                default:
                    return false;
            }
        }

        private Fix GrabHeight(in FighterState f) => f.Y + Stats(f).Reach + T.Rebound.GrabReachYExtra;

        private bool InGrabRange(ref GameState s, in FighterState f)
        {
            if (!CanGrab(f)) return false;
            Fix dx = Fix.Abs(s.Ball.X - f.X);
            if (dx > T.Rebound.GrabReachX) return false;
            return s.Ball.Y - T.Court.BallRadius <= GrabHeight(f);
        }

        private void TryGrab(ref GameState s)
        {
            if (s.Ball.NoGrab > 0) return;
            bool a = InGrabRange(ref s, s.F0);
            bool b = InGrabRange(ref s, s.F1);
            if (!a && !b) return;
            int who;
            if (a && b)
            {
                // Highest hand wins; then the closer fighter; then the defense (box-out advantage).
                Fix ha = GrabHeight(s.F0), hb = GrabHeight(s.F1);
                if (ha != hb && (s.F0.Y.IsPositive || s.F1.Y.IsPositive)) who = ha > hb ? 0 : 1;
                else
                {
                    Fix da = Fix.Abs(s.Ball.X - s.F0.X), db = Fix.Abs(s.Ball.X - s.F1.X);
                    if (da != db) who = da < db ? 0 : 1;
                    else who = 1 - s.Offense;
                }
            }
            else
            {
                who = a ? 0 : 1;
            }
            Rebound(ref s, who);
        }

        private void Rebound(ref GameState s, int who)
        {
            bool offensive = who == s.Offense;
            bool rimTouched = s.Ball.RimTouched;
            // Offensive boards keep the clear status and get a fresh shot clock only off the rim.
            if (offensive) GiveBall(ref s, who, false, rimTouched);
            else GiveBall(ref s, who, true, true);
            Emit(ref s, SimEventType.Rebound, who, offensive ? 1 : 0, 0);
        }
    }
}
