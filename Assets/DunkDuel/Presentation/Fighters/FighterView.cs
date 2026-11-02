using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// One fighter on screen: the mannequin rig, hero lights (key, rim, fill per fighter, separate from the
    /// stage), motif trails, sweat sheen and flashes. Reads sim state only.
    /// </summary>
    public sealed class FighterView : MonoBehaviour
    {
        public const float CameraTurn = 30f;
        public const float HandlerDepth = -0.28f;
        public const float DefenderDepth = 0.28f;

        public FighterRig Rig { get; private set; }
        public FighterStats Stats { get; private set; }
        public FighterVisual Visual { get; private set; }
        public int Index { get; private set; }
        public Color Primary { get; private set; }

        /// <summary>Where the ball sits this frame if this fighter holds it (dribble bounce included).</summary>
        public Vector3 BallHold { get; private set; }
        public bool Dribbling { get; private set; }
        public float Depth { get; private set; }
        public Vector3 Feet => transform.position;
        public Vector3 HeadPosition => Rig.Head.position;
        public PoseContext LastContext { get; private set; }

        private Light _key;
        private Light _rim;
        private Light _fill;
        private TrailRenderer _trailA;
        private TrailRenderer _trailB;
        private float _flash;
        private Color _flashColor;
        private bool _heat;
        private float _sweat = -1f;
        private float _ballRadius;
        private float _lastDribblePhase;

        /// <summary>Raised when the dribbled ball hits the floor (audio and haptics hook).</summary>
        public System.Action<FighterView> Bounce;

        public static FighterView Create(Transform parent, FighterStats stats, FighterVisual vis, MaterialLibrary mats,
            QualityProfile q, int index, int outfit, float ballRadius)
        {
            Color primary = vis.Primary;
            if (vis.Outfits != null && outfit > 0 && outfit < vis.Outfits.Length) primary = PJson.ParseColor(vis.Outfits[outfit], vis.Primary);
            FighterRig rig = FighterRig.Build(parent, stats, vis, mats, primary);
            var view = rig.Root.gameObject.AddComponent<FighterView>();
            view.Rig = rig;
            view.Stats = stats;
            view.Visual = vis;
            view.Index = index;
            view.Primary = primary;
            view._ballRadius = ballRadius;
            view.Depth = index == 0 ? HandlerDepth : DefenderDepth;
            view.BuildLights(q);
            view.BuildTrails(mats, q);
            rig.SetShadows(q.FighterShadows);
            return view;
        }

        private void BuildLights(QualityProfile q)
        {
            float h = Rig.Height;
            _rim = MakeLight("RimLight", new Vector3(0f, h * 1.25f, 1.6f), Visual.Rim, 2.6f, 4.5f);
            if (q.FighterLights)
            {
                _key = MakeLight("KeyLight", new Vector3(-0.6f, h * 1.5f, -2.4f), new Color(1f, 0.96f, 0.9f), 1.3f, 6f);
                _fill = MakeLight("FillLight", new Vector3(0.8f, h * 0.45f, -1.6f), Color.Lerp(Visual.Rim, Color.white, 0.6f), 0.45f, 3.5f);
            }
        }

        private Light MakeLight(string name, Vector3 local, Color c, float intensity, float range)
        {
            var go = new GameObject(name);
            go.transform.SetParent(transform, false);
            go.transform.localPosition = local;
            var l = go.AddComponent<Light>();
            l.type = LightType.Point;
            l.color = c;
            l.intensity = intensity;
            l.range = range;
            l.shadows = LightShadows.None;
            return l;
        }

        private void BuildTrails(MaterialLibrary mats, QualityProfile q)
        {
            if (q.EffectsScale < 0.5f) return;
            switch (Visual.MotifKind)
            {
                case "glitch": // light-up high-tops leave light trails
                    _trailA = MakeTrail(Rig.FootL, mats, Visual.Motif, 0.06f, 0.35f);
                    _trailB = MakeTrail(Rig.FootR, mats, Visual.Primary, 0.06f, 0.35f);
                    break;
                case "solar": // heat-shimmer contrails off the hands on dunks
                    _trailA = MakeTrail(Rig.HandR, mats, Visual.Motif, 0.09f, 0.3f);
                    _trailB = MakeTrail(Rig.HandL, mats, Visual.Motif, 0.07f, 0.25f);
                    break;
                case "storm": // rain ribbons off the shooting hand
                    _trailA = MakeTrail(Rig.HandR, mats, Visual.Motif, 0.05f, 0.45f);
                    break;
                case "seismic":
                    _trailA = MakeTrail(Rig.FootR, mats, Visual.Motif, 0.1f, 0.2f);
                    break;
            }
        }

        private static TrailRenderer MakeTrail(Transform at, MaterialLibrary mats, Color c, float width, float time)
        {
            var go = new GameObject("Trail");
            go.transform.SetParent(at, false);
            var tr = go.AddComponent<TrailRenderer>();
            tr.material = mats.TransparentInstance(new Color(c.r, c.g, c.b, 0.8f), true);
            tr.time = time;
            tr.startWidth = width;
            tr.endWidth = 0f;
            tr.minVertexDistance = 0.04f;
            tr.startColor = c;
            tr.endColor = new Color(c.r, c.g, c.b, 0f);
            tr.emitting = false;
            tr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return tr;
        }

        public void Flash(Color c, float seconds)
        {
            _flashColor = c;
            _flash = Mathf.Max(_flash, seconds);
        }

        public void SetHeat(bool on) => _heat = on;

        /// <summary>Applies the interpolated sim state of fighter <paramref name="index"/>.</summary>
        public void Apply(in GameState prev, in GameState cur, float alpha, SimTables tables, float sweat, float dt)
        {
            FighterState a = prev.Get(Index);
            FighterState b = cur.Get(Index);
            bool sameAction = a.Action == b.Action && b.ActionFrame >= a.ActionFrame;
            float x = FixExtensions.Lerp(a.X, b.X, alpha);
            float y = FixExtensions.Lerp(a.Y, b.Y, alpha);

            PoseRole role = PoseRole.Free;
            if (cur.Ball.Mode == BallMode.Held) role = cur.Ball.Holder == Index ? PoseRole.Handler : PoseRole.Defender;
            if (cur.Phase == Phase.Finisher) role = cur.Fin.Attacker == Index ? PoseRole.Handler : PoseRole.Defender;

            float targetDepth = role == PoseRole.Handler ? HandlerDepth : (role == PoseRole.Defender ? DefenderDepth : (Index == 0 ? -0.15f : 0.15f));
            Depth = Mathf.MoveTowards(Depth, targetDepth, dt * 1.5f);
            transform.position = new Vector3(x, y, Depth);

            var ctx = new PoseContext
            {
                Action = b.Action,
                Frame = sameAction ? Mathf.Lerp(a.ActionFrame, b.ActionFrame, alpha) : b.ActionFrame,
                MoveSlot = b.MoveSlot,
                Flags = b.ActionFlags,
                Behavior = b.Action == ActionId.Trick ? Stats.Tricks[b.MoveSlot].Behavior : TrickBehavior.None,
                Kind = b.PendingKind,
                Peak = b.Peak,
                Takeoff = b.Takeoff,
                Grounded = b.Grounded,
                Y = y,
                VX = b.VX.ToFloat() * 60f,
                Facing = b.Facing,
                Role = role,
                Protecting = (b.Status & StatusFlag.Protecting) != 0,
                Driving = (b.Status & StatusFlag.Driving) != 0,
                Tick = Mathf.Lerp(prev.Frame, cur.Frame, alpha),
                X = x,
                Height = Rig.Height,
                Phase = cur.Phase,
                IsFinisherAttacker = cur.Phase == Phase.Finisher && cur.Fin.Attacker == Index,
                FinisherT = cur.Fin.Frames > 0 ? Mathf.Clamp01(cur.Fin.Frame / (float)cur.Fin.Frames) : 0f,
                FinisherId = Stats.Def.Finisher.Id,
                StumbleFrames = tables.T.Balance.StumbleFrames,
                UnplantFrames = tables.T.Read.UnplantFrames,
            };
            LastContext = ctx;
            Pose pose = FighterPoser.Compute(ctx);
            int facing = b.Facing == 0 ? (Index == 0 ? 1 : -1) : b.Facing;
            Rig.Apply(pose, facing, CameraTurn);

            UpdateBallHold(ctx, cur);
            UpdateTrails(ctx);
            UpdateMaterials(sweat, dt);
        }

        private void UpdateBallHold(in PoseContext c, in GameState cur)
        {
            Vector3 right = Rig.HandR.position;
            Vector3 left = Rig.HandL.position;
            bool airborneShot = c.Action == ActionId.GatherJumper || c.Action == ActionId.GatherFinish || c.Action == ActionId.ShotRecover
                || c.Action == ActionId.Contest || !c.Grounded;
            bool cinematic = c.Action == ActionId.Finisher;
            if (airborneShot || cinematic || c.Action == ActionId.Trick && (c.Behavior == TrickBehavior.LaunchStep))
            {
                Dribbling = false;
                BallHold = (right + left) * 0.5f + Vector3.up * (_ballRadius * 0.9f);
                return;
            }
            // Dribble: ball bounces between the right hand and the floor, frame-locked to the sim tick.
            Dribbling = true;
            float phase = FighterPoser.DribblePhase(c);
            float h = Mathf.Abs(Mathf.Cos(phase * Mathf.PI));
            float handY = right.y - _ballRadius;
            Vector3 p = right + transform.right * 0.02f;
            p.y = Mathf.Lerp(_ballRadius, handY, h);
            BallHold = p;
            if (_lastDribblePhase < 0.5f && phase >= 0.5f) Bounce?.Invoke(this);
            _lastDribblePhase = phase;
        }

        private void UpdateTrails(in PoseContext c)
        {
            bool fast = Mathf.Abs(c.VX) > 3.5f;
            switch (Visual.MotifKind)
            {
                case "glitch":
                    SetEmit(_trailA, fast || !c.Grounded);
                    SetEmit(_trailB, fast || !c.Grounded);
                    break;
                case "solar":
                    bool dunk = c.Action == ActionId.GatherFinish && !c.Grounded || c.IsFinisherAttacker;
                    SetEmit(_trailA, dunk);
                    SetEmit(_trailB, dunk);
                    break;
                case "storm":
                    SetEmit(_trailA, c.Action == ActionId.ShotRecover || c.IsFinisherAttacker);
                    break;
                case "seismic":
                    SetEmit(_trailA, c.Action == ActionId.PostUp || c.Behavior == TrickBehavior.DropStep || c.IsFinisherAttacker);
                    break;
            }
        }

        private static void SetEmit(TrailRenderer t, bool on)
        {
            if (t != null && t.emitting != on) t.emitting = on;
        }

        private void UpdateMaterials(float sweat, float dt)
        {
            if (Mathf.Abs(sweat - _sweat) > 0.01f)
            {
                _sweat = sweat;
                MaterialLibrary.SetSmoothness(Rig.Skin, Mathf.Lerp(0.3f, 0.78f, sweat));
            }
            Color glow = Color.black;
            if (_heat) glow = Primary * (0.35f + 0.15f * Mathf.Sin(Time.time * 6f));
            if (_flash > 0f)
            {
                _flash -= dt;
                glow += _flashColor * Mathf.Clamp01(_flash * 6f);
            }
            MaterialLibrary.SetEmission(Rig.Jersey, glow);
        }

        private void OnDestroy()
        {
            if (Rig == null) return;
            Destroy(Rig.Jersey);
            Destroy(Rig.Skin);
            Destroy(Rig.Shorts);
            Destroy(Rig.Glow);
        }
    }
}
