using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.Presentation
{
    public enum PoseRole : byte { Handler, Defender, Free }

    /// <summary>Everything the poser needs, derived from sim state (frame-locked: same state, same pose).</summary>
    public struct PoseContext
    {
        public ActionId Action;
        public float Frame;          // action frame, interpolated
        public byte MoveSlot;
        public ushort Flags;
        public TrickBehavior Behavior;
        public ShotKind Kind;
        public int Peak;
        public int Takeoff;
        public bool Grounded;
        public float Y;
        public float VX;             // m/s, signed toward the hoop
        public int Facing;
        public PoseRole Role;
        public bool Protecting;
        public bool Driving;
        public float Tick;           // sim tick, for frame-locked cycles
        public float X;              // sim x, drives the stride phase
        public float Height;
        public Phase Phase;
        public bool IsFinisherAttacker;
        public float FinisherT;      // 0..1 through the cinematic
        public string FinisherId;
        public bool Scored;          // scorer during the score pause
        public int StumbleFrames;
        public int UnplantFrames;
    }

    /// <summary>
    /// Procedural graybox animation: maps sim action state + action frame to joint angles. Deterministic,
    /// so replays and both phones in a duel show the same body language for the same frame.
    /// Final art replaces this with <see cref="FrameLockedAnimator"/> sampling authored clips by sim frame.
    /// </summary>
    public static class FighterPoser
    {
        public const int DribblePeriod = 24;
        public const int DriveDribblePeriod = 16;

        public static Pose Compute(in PoseContext c)
        {
            Pose p = Base(c);
            switch (c.Action)
            {
                case ActionId.Idle: break;
                case ActionId.Shake: Shake(ref p, c); break;
                case ActionId.GatherJumper: Gather(ref p, c, false); break;
                case ActionId.GatherFinish: Gather(ref p, c, true); break;
                case ActionId.ShotRecover: Recover(ref p, c); break;
                case ActionId.PostUp: PostUp(ref p, c); break;
                case ActionId.Trick: Trick(ref p, c); break;
                case ActionId.Wall: Wall(ref p, 1f); break;
                case ActionId.Unplant: Wall(ref p, 1f - Mathf.Clamp01(c.Frame / Mathf.Max(1, c.UnplantFrames))); break;
                case ActionId.Contest: Contest(ref p, c); break;
                case ActionId.Swipe: Swipe(ref p, c, 1f); break;
                case ActionId.OffBalance: OffBalance(ref p, c); break;
                case ActionId.Stumble: Stumble(ref p, c); break;
                case ActionId.Knockdown: Knockdown(ref p, c); break;
                case ActionId.Celebrate: Celebrate(ref p, c); break;
                case ActionId.Finisher: Finisher(ref p, c); break;
            }
            if (!c.Grounded && c.Action != ActionId.Contest && c.Action != ActionId.GatherJumper && c.Action != ActionId.GatherFinish)
            {
                p.KneeL += 25f;
                p.KneeR += 35f;
                p.HipL -= 15f;
            }
            return p;
        }

        // ------------------------------------------------------------------ base stance and locomotion

        private static Pose Base(in PoseContext c)
        {
            var p = new Pose();
            float speed = Mathf.Abs(c.VX);
            float run = Mathf.Clamp01(speed / 5.5f);
            float stride = Mathf.Lerp(1.1f, 1.9f, run);
            float phase = c.X / stride * Mathf.PI * 2f * (c.Facing >= 0 ? 1f : -1f);
            float swing = Mathf.Sin(phase);
            bool moving = speed > 0.35f && c.Grounded;

            switch (c.Role)
            {
                case PoseRole.Handler:
                    p.Crouch = c.Protecting ? 0.45f : 0.25f;
                    p.Lean = c.Protecting ? 22f : 8f + run * 14f;
                    p.ShoulderL = -35f;
                    p.ArmOutL = 22f; // guard arm
                    p.ElbowL = -35f;
                    float dribble = DribblePhase(c);
                    p.ShoulderR = -32f + 18f * Mathf.Cos(dribble * Mathf.PI * 2f);
                    p.ElbowR = -45f + 15f * Mathf.Cos(dribble * Mathf.PI * 2f);
                    p.ArmOutR = 12f;
                    break;
                case PoseRole.Defender:
                    p.Crouch = 0.38f;
                    p.Lean = 12f;
                    p.ShoulderL = -45f;
                    p.ShoulderR = -45f;
                    p.ArmOutL = 48f;
                    p.ArmOutR = 48f;
                    p.ElbowL = -25f;
                    p.ElbowR = -25f;
                    break;
                default:
                    p.Crouch = 0.15f;
                    p.Lean = 6f + run * 16f;
                    p.ShoulderL = -10f - swing * 35f * run;
                    p.ShoulderR = -10f + swing * 35f * run;
                    p.ElbowL = -40f;
                    p.ElbowR = -40f;
                    break;
            }

            if (moving)
            {
                float amp = Mathf.Lerp(18f, 42f, run);
                p.HipL = -swing * amp;
                p.HipR = swing * amp;
                p.KneeL = Mathf.Max(0f, Mathf.Sin(phase + 1.2f)) * amp * 1.4f;
                p.KneeR = Mathf.Max(0f, -Mathf.Sin(phase + 1.2f)) * amp * 1.4f;
                p.RootY = Mathf.Abs(Mathf.Sin(phase)) * 0.025f * c.Height * run;
            }
            else
            {
                // Idle bounce on the balls of the feet, frame-locked to the sim tick.
                float bounce = Mathf.Sin(c.Tick / 30f * Mathf.PI * 2f);
                p.Crouch += bounce * 0.03f;
                p.HipL = -6f;
                p.HipR = 4f;
            }
            p.HeadPitch = -5f;
            return p;
        }

        /// <summary>0..1 phase of the current dribble bounce (0 = ball in hand, 0.5 = on the floor).</summary>
        public static float DribblePhase(in PoseContext c)
        {
            int period = c.Driving ? DriveDribblePeriod : DribblePeriod;
            return Mathf.Repeat(c.Tick / period, 1f);
        }

        // ------------------------------------------------------------------ offense

        private static void Shake(ref Pose p, in PoseContext c)
        {
            float u = Mathf.Clamp01(c.Frame / 18f);
            float bell = Mathf.Sin(u * Mathf.PI);
            switch (c.MoveSlot)
            {
                case 0: // crossover: low, the ball swings across the body
                    p.Crouch = 0.4f;
                    p.BodyYaw = Mathf.Lerp(-22f, 22f, u);
                    p.ArmOutR = Mathf.Lerp(25f, -35f, u);
                    p.ShoulderR = -25f - bell * 15f;
                    p.Lean = 18f;
                    break;
                case 1: // step-back: lean away and hop
                    p.Crouch = 0.3f + bell * 0.2f;
                    p.Lean = -14f * bell;
                    p.HipL = -25f * bell;
                    p.KneeL = 40f * bell;
                    p.ShoulderR = -50f;
                    break;
                case 2: // spin: full turn
                    p.BodyYaw = 360f * Ease.InOutSine(Mathf.Clamp01(c.Frame / 20f));
                    p.Crouch = 0.35f;
                    p.ArmOutL = 50f;
                    p.ArmOutR = 30f;
                    break;
                default: // hesitation: rise up, then drop
                    p.Crouch = Mathf.Lerp(0.05f, 0.4f, Mathf.Clamp01((c.Frame - 6f) / 6f));
                    p.Lean = Mathf.Lerp(-4f, 18f, Mathf.Clamp01((c.Frame - 6f) / 6f));
                    p.HeadPitch = -15f;
                    break;
            }
        }

        private static void Gather(ref Pose p, in PoseContext c, bool finish)
        {
            float toPeak = Mathf.Clamp01(c.Frame / Mathf.Max(1, c.Peak));
            float preJump = Mathf.Clamp01(c.Frame / Mathf.Max(1, c.Takeoff));
            bool hook = (c.Flags & ActionFlag.Hook) != 0;
            if (c.Grounded)
            {
                p.Crouch = Mathf.Lerp(0.2f, finish ? 0.35f : 0.5f, preJump);
            }
            else
            {
                p.Crouch = 0f;
                p.KneeL = finish ? 80f : 20f;
                p.HipL = finish ? -70f : -8f;
                p.KneeR = finish ? 10f : 25f;
            }
            if (hook)
            {
                p.ArmOutR = Mathf.Lerp(20f, 165f, toPeak);
                p.ShoulderR = -20f;
                p.ElbowR = -10f;
                p.ShoulderL = -60f;
                p.ArmOutL = 40f;
                p.ElbowL = -60f;
                p.Twist = -20f;
                return;
            }
            if (finish && c.Kind == ShotKind.Dunk)
            {
                p.ShoulderL = Mathf.Lerp(-40f, -170f, toPeak);
                p.ShoulderR = Mathf.Lerp(-40f, -175f, toPeak);
                p.ElbowL = -20f;
                p.ElbowR = -15f;
                p.Lean = Mathf.Lerp(15f, -10f, toPeak);
                return;
            }
            if (finish)
            {
                p.ShoulderR = Mathf.Lerp(-50f, -168f, toPeak);
                p.ElbowR = Mathf.Lerp(-70f, -10f, toPeak);
                p.ShoulderL = -60f;
                p.ElbowL = -50f;
                p.Lean = 10f;
                return;
            }
            // Jump shot: ball rises from the hip to the set point above the forehead.
            p.ShoulderR = Mathf.Lerp(-40f, -155f, toPeak);
            p.ElbowR = Mathf.Lerp(-90f, -70f, toPeak);
            p.ShoulderL = Mathf.Lerp(-40f, -140f, toPeak);
            p.ElbowL = Mathf.Lerp(-90f, -80f, toPeak);
            p.ArmOutL = 10f;
            p.Lean = Mathf.Lerp(8f, -4f, toPeak);
            p.HeadPitch = -15f;
        }

        private static void Recover(ref Pose p, in PoseContext c)
        {
            float t = Mathf.Clamp01(c.Frame / 22f);
            // Follow-through: shooting arm extended, wrist "in the cookie jar".
            p.ShoulderR = Mathf.Lerp(-170f, -60f, t * t);
            p.ElbowR = Mathf.Lerp(-5f, -40f, t);
            p.ShoulderL = Mathf.Lerp(-120f, -40f, t);
            p.ElbowL = -40f;
            p.Lean = -4f;
            if (c.Grounded && c.Frame > 4f) p.Squash = Mathf.Clamp01(1f - (c.Frame - 4f) / 8f) * 0.5f;
        }

        private static void PostUp(ref Pose p, in PoseContext c)
        {
            p.Crouch = 0.5f;
            p.Lean = 28f;
            p.ShoulderL = -20f;
            p.ArmOutL = 65f;
            p.ElbowL = -85f;
            p.ShoulderR = -20f + 12f * Mathf.Sin(c.Tick / 12f * Mathf.PI * 2f);
            p.ElbowR = -50f;
            float bump = Mathf.Clamp01(1f - Mathf.Repeat(c.Tick, 22f) / 6f);
            p.Twist = -15f * bump;
        }

        private static void Trick(ref Pose p, in PoseContext c)
        {
            float f = c.Frame;
            switch (c.Behavior)
            {
                case TrickBehavior.LaunchStep:
                    p.Lean = 24f;
                    p.Crouch = 0.3f;
                    p.ShoulderL = -60f;
                    p.ShoulderR = -70f;
                    break;
                case TrickBehavior.MonsoonStep:
                case TrickBehavior.Drizzle:
                {
                    float bell = Mathf.Sin(Mathf.Clamp01(f / 12f) * Mathf.PI);
                    p.Crouch = 0.35f + bell * 0.15f;
                    p.Lean = c.Behavior == TrickBehavior.MonsoonStep ? -18f * bell : 10f;
                    p.HipL = -35f * bell;
                    p.KneeL = 50f * bell;
                    p.ShoulderR = -60f;
                    break;
                }
                case TrickBehavior.FrameSkip:
                    p.Crouch = 0.42f;
                    p.BodyYaw = 30f * Mathf.Sin(f / 4f * Mathf.PI);
                    p.ArmOutR = 30f * Mathf.Cos(f / 4f * Mathf.PI);
                    p.Lean = 20f;
                    break;
                case TrickBehavior.LagSpike:
                {
                    // Stutter-freeze: hold a mid-crossover pose with a glitch jitter.
                    float jitter = (Mathf.Repeat(f, 3f) < 1f) ? 6f : 0f;
                    p.Crouch = 0.4f;
                    p.BodyYaw = 12f + jitter;
                    p.ArmOutR = 5f;
                    p.Lean = 15f;
                    break;
                }
                case TrickBehavior.DropStep:
                    p.BodyYaw = 180f * Ease.OutCubic(Mathf.Clamp01((f - 6f) / 10f));
                    p.Crouch = 0.45f;
                    p.Lean = 25f;
                    p.ArmOutL = 70f;
                    break;
                case TrickBehavior.ChaseDown:
                {
                    float swing = Mathf.Sin(c.X * 3.5f);
                    p.Lean = 28f;
                    p.ShoulderL = -10f - swing * 55f;
                    p.ShoulderR = -10f + swing * 55f;
                    p.ElbowL = -80f;
                    p.ElbowR = -80f;
                    break;
                }
                case TrickBehavior.Undercurrent:
                    Swipe(ref p, c, 1.4f);
                    p.Crouch = 0.6f;
                    p.Lean = 40f;
                    break;
                case TrickBehavior.Pickpocket:
                    p.Lean = 38f;
                    p.Crouch = 0.35f;
                    p.ShoulderR = -85f;
                    p.ElbowR = -10f;
                    p.ShoulderL = 20f;
                    break;
                case TrickBehavior.RimLock:
                    p.Crouch = 0.35f;
                    p.ShoulderL = -165f;
                    p.ShoulderR = -165f;
                    p.ArmOutL = 20f;
                    p.ArmOutR = 20f;
                    p.ElbowL = -5f;
                    p.ElbowR = -5f;
                    break;
            }
        }

        // ------------------------------------------------------------------ defense

        private static void Wall(ref Pose p, float amount)
        {
            Pose planted = p;
            planted.Crouch = 0.55f;
            planted.Lean = 6f;
            planted.ShoulderL = -75f;
            planted.ShoulderR = -75f;
            planted.ArmOutL = 18f;
            planted.ArmOutR = 18f;
            planted.ElbowL = -45f;
            planted.ElbowR = -45f;
            planted.HipL = -12f;
            planted.HipR = 12f;
            p = Pose.Lerp(p, planted, amount);
        }

        private static void Contest(ref Pose p, in PoseContext c)
        {
            if (c.Grounded && c.Frame <= c.Takeoff)
            {
                p.Crouch = 0.5f;
                p.ShoulderL = -120f;
                p.ShoulderR = -120f;
                return;
            }
            p.Crouch = 0f;
            p.ShoulderL = -175f;
            p.ShoulderR = -150f;
            p.ArmOutL = 8f;
            p.ArmOutR = 22f;
            p.ElbowL = -5f;
            p.ElbowR = -10f;
            p.KneeL = 25f;
            p.KneeR = 35f;
            p.Lean = 6f;
            if (c.Grounded) p.Squash = 0.4f;
        }

        private static void Swipe(ref Pose p, in PoseContext c, float reach)
        {
            float t = Mathf.Clamp01(c.Frame / 8f);
            float lunge = Mathf.Sin(t * Mathf.PI);
            p.Lean = 15f + 25f * lunge * reach;
            p.ShoulderR = Mathf.Lerp(-45f, -95f, lunge);
            p.ElbowR = Mathf.Lerp(-40f, -5f, lunge);
            p.ArmOutR = 5f;
            p.HipR = -30f * lunge;
            p.KneeR = 35f * lunge;
        }

        private static void OffBalance(ref Pose p, in PoseContext c)
        {
            float w = Mathf.Sin(c.Frame / 5f * Mathf.PI);
            p.Lean = -18f + 8f * w;
            p.BodyYaw = 15f * w;
            p.ShoulderL = -90f + 40f * w;
            p.ShoulderR = -60f - 40f * w;
            p.ArmOutL = 60f;
            p.ArmOutR = 60f;
            p.Crouch = 0.1f;
        }

        private static void Stumble(ref Pose p, in PoseContext c)
        {
            float t = Mathf.Clamp01(c.Frame / 18f);
            float rise = Mathf.Clamp01((c.Frame - (c.StumbleFrames - 16f)) / 16f);
            float down = Ease.OutCubic(t) * (1f - rise);
            p.Crouch = Mathf.Lerp(0.2f, 1f, down);
            p.RootY = -0.12f * c.Height * down;
            p.Lean = -30f * down;
            p.BodyYaw = 70f * Ease.OutCubic(t);
            p.HipL = -70f * down;
            p.HipR = -40f * down;
            p.ShoulderL = 30f * down;
            p.ShoulderR = 20f * down;
            p.ArmOutL = 50f * down;
            p.ArmOutR = 50f * down;
        }

        private static void Knockdown(ref Pose p, in PoseContext c)
        {
            float fall = Ease.OutCubic(Mathf.Clamp01(c.Frame / 12f));
            float up = Mathf.Clamp01((c.Frame - 30f) / 14f);
            float amount = fall * (1f - up);
            p.RootRoll = 82f * amount;
            p.ShoulderL = -120f * amount;
            p.ShoulderR = -100f * amount;
            p.HipL = -20f * amount;
            p.KneeL = 30f * amount;
            p.Crouch = 0.2f * (1f - amount);
        }

        private static void Celebrate(ref Pose p, in PoseContext c)
        {
            float b = Mathf.Abs(Mathf.Sin(c.Frame / 14f * Mathf.PI));
            p.ShoulderL = -170f;
            p.ShoulderR = -165f;
            p.ArmOutL = 25f;
            p.ArmOutR = 25f;
            p.ElbowL = -10f;
            p.ElbowR = -10f;
            p.RootY = b * 0.06f * c.Height;
            p.Crouch = 0f;
            p.HeadPitch = -20f;
        }

        // ------------------------------------------------------------------ finishers (cinematic)

        private static void Finisher(ref Pose p, in PoseContext c)
        {
            float t = c.FinisherT;
            if (!c.IsFinisherAttacker)
            {
                // The defender watches, then braces for the Denial ring.
                p.Crouch = 0.4f;
                p.ShoulderL = -60f;
                p.ShoulderR = -60f;
                p.ArmOutL = 40f;
                p.ArmOutR = 40f;
                return;
            }
            float jump = Mathf.Sin(Mathf.Clamp01((t - 0.55f) / 0.4f) * Mathf.PI);
            switch (c.FinisherId)
            {
                case "orbit":
                    p.RootY = jump * 0.55f * c.Height;
                    p.BodyYaw = 360f * Mathf.Clamp01((t - 0.6f) / 0.3f);
                    p.ShoulderR = -90f - 360f * Mathf.Clamp01((t - 0.62f) / 0.3f);
                    p.ShoulderL = -120f;
                    p.KneeL = 80f * jump;
                    p.HipL = -70f * jump;
                    break;
                case "cloudburst":
                {
                    float rise = Mathf.Sin(Mathf.Clamp01((t - 0.3f) / 0.45f) * Mathf.PI);
                    p.RootY = rise * 0.35f * c.Height;
                    p.ShoulderR = Mathf.Lerp(-40f, -175f, Mathf.Clamp01((t - 0.25f) / 0.3f));
                    p.ShoulderL = Mathf.Lerp(-40f, -150f, Mathf.Clamp01((t - 0.25f) / 0.3f));
                    p.ElbowR = -10f;
                    p.Lean = -8f;
                    break;
                }
                case "overclock":
                {
                    float wiggle = Mathf.Sin(t * 60f);
                    p.BodyYaw = t < 0.75f ? 35f * wiggle : 0f;
                    p.ArmOutR = t < 0.75f ? 35f * Mathf.Cos(t * 60f) : 0f;
                    p.Crouch = t < 0.75f ? 0.45f : 0.1f;
                    p.RootY = t > 0.78f ? Mathf.Sin(Mathf.Clamp01((t - 0.78f) / 0.2f) * Mathf.PI) * 0.4f * c.Height : 0f;
                    p.ShoulderR = t > 0.78f ? -165f : p.ShoulderR;
                    break;
                }
                default: // tectonic: three seismic stomps, then the hammer dunk
                {
                    float stomp = t < 0.6f ? Mathf.Abs(Mathf.Sin(t / 0.6f * 3f * Mathf.PI)) : 0f;
                    p.Crouch = 0.25f + stomp * 0.4f;
                    p.HipR = -60f * stomp;
                    p.KneeR = 70f * stomp;
                    p.RootY = jump * 0.45f * c.Height;
                    float hammer = Mathf.Clamp01((t - 0.75f) / 0.15f);
                    p.ShoulderL = Mathf.Lerp(-175f, -70f, hammer);
                    p.ShoulderR = Mathf.Lerp(-175f, -70f, hammer);
                    break;
                }
            }
        }
    }
}
