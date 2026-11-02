using System;
using System.Collections.Generic;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using UnityEngine;
#if DD_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace DunkDuel.Presentation
{
    /// <summary>Shared presentation services handed to every match.</summary>
    public sealed class PresentationContext
    {
        public SimTables Tables;
        public PresentationData Data;
        public MaterialLibrary Materials;
        public AudioDirector Audio;
        public QualityManager Quality;
        public bool ReducedMotion;
        public bool ReducedFlash;
        public readonly int[] Outfits = new int[2];
        public Material BallSkin;
    }

    /// <summary>
    /// Builds and binds a whole match on screen for an <see cref="IMatchSession"/>: court, fighters, ball,
    /// camera, effects, audio and the impact director. It reads sim state and events and never writes to the sim.
    /// </summary>
    public sealed class MatchPresenter : MonoBehaviour
    {
        public IMatchSession Session { get; private set; }
        public int LocalPlayer { get; private set; }
        public CourtDef CourtDef { get; private set; }
        public MatchDriver Driver { get; private set; }
        public GameCamera Camera { get; private set; }
        public CourtView Court { get; private set; }
        public FighterView[] Fighters { get; private set; }
        public BallView Ball { get; private set; }
        public EffectPool Effects { get; private set; }
        public ImpactDirector Impacts { get; private set; }
        public StageHeat Heat { get; } = new StageHeat();
        public HitboxDebugView Hitboxes { get; private set; }
        public PresentationContext Context { get; private set; }

        public event Action<CalloutInfo> Callout;
        public event Action<long> CalloutCancelled;

        private readonly List<Vector3> _framePoints = new List<Vector3>(8);
        private Vector3 _rim;

        public static MatchPresenter Create(PresentationContext ctx, IMatchSession session, int courtIndex, int localPlayer)
        {
            var go = new GameObject("Match");
            var p = go.AddComponent<MatchPresenter>();
            p.Build(ctx, session, courtIndex, localPlayer);
            return p;
        }

        private void Build(PresentationContext ctx, IMatchSession session, int courtIndex, int localPlayer)
        {
            Context = ctx;
            Session = session;
            LocalPlayer = localPlayer;
            SimTables tables = session.Tables;
            CourtDef = ctx.Data.Court(courtIndex);
            QualityProfile q = ctx.Quality != null ? ctx.Quality.Profile : QualityProfile.For(QualityTier.High);

            Court = CourtBuilder.Build(transform, CourtDef, tables.T, ctx.Materials, q);
            Court.ReducedFlash = ctx.ReducedFlash;
            Camera = GameCamera.Create(transform, ctx.Data.Camera, CourtDef.SkyTop);
            Camera.ReducedMotion = ctx.ReducedMotion;
            Effects = EffectPool.Create(transform, ctx.Materials, q.EffectsScale);
            Effects.ReducedFlash = ctx.ReducedFlash;
            Court.Bind(Effects, ctx.Audio);

            GameState s = session.State;
            float ballR = tables.T.Court.BallRadius.ToFloat();
            Fighters = new FighterView[2];
            for (int i = 0; i < 2; i++)
            {
                FighterStats st = tables.Fighters[s.Get(i).CharIndex];
                FighterVisual vis = ctx.Data.Visual(st.Def.Id);
                Fighters[i] = FighterView.Create(transform, st, vis, ctx.Materials, q, i, ctx.Outfits[i], ballR);
                Fighters[i].Bounce += OnBounce;
            }
            Ball = BallView.Create(transform, ballR, ctx.Materials);
            Ball.SetSkin(ctx.BallSkin);
            Hitboxes = HitboxDebugView.Create(transform, tables, ctx.Materials);
            _rim = new Vector3(tables.T.Court.RimX.ToFloat(), tables.T.Court.RimY.ToFloat(), 0f);

            Impacts = new ImpactDirector(tables, Effects, Camera, ctx.Audio, Heat, Court, Fighters, localPlayer);
            Impacts.Callout += c => Callout?.Invoke(c);
            Impacts.CalloutCancelled += k => CalloutCancelled?.Invoke(k);
            Impacts.Attach(session.Ledger);

            Driver = gameObject.AddComponent<MatchDriver>();
            Driver.Begin(session);
            ctx.Audio?.EnterCourt(CourtDef, Camera.GetComponent<AudioListener>());
            SetupPostFx(q);
            Camera.HardCut();
        }

        private void SetupPostFx(QualityProfile q)
        {
#if DD_URP
            if (!q.PostFx) return;
            var volume = gameObject.AddComponent<Volume>();
            volume.isGlobal = true;
            volume.priority = 1f;
            var profile = ScriptableObject.CreateInstance<VolumeProfile>();
            Bloom bloom = profile.Add<Bloom>(true);
            bloom.intensity.Override(0.55f);
            bloom.threshold.Override(1.05f);
            Vignette vignette = profile.Add<Vignette>(true);
            vignette.intensity.Override(0.22f);
            volume.sharedProfile = profile;
            Camera.Cam.GetUniversalAdditionalCameraData().renderPostProcessing = true;
#endif
        }

        private void OnBounce(FighterView f)
        {
            Context.Audio?.DribbleBounce(Mathf.Abs(f.LastContext.VX));
        }

        public void SetHitboxes(bool on) => Hitboxes.SetVisible(on);

        private void LateUpdate()
        {
            if (Session == null) return;
            float dt = Time.deltaTime;
            GameState prev = Driver.Previous;
            GameState cur = Driver.Current;
            float alpha = Driver.Alpha;
            SimTables tables = Session.Tables;
            Impacts.Flush(cur);

            int roundFrames = tables.T.Match.RoundFrames;
            float roundProgress = roundFrames > 0 ? 1f - cur.RoundClock / (float)roundFrames : 0f;
            float sweat = Mathf.Clamp01((cur.Round + roundProgress) / 2.5f);
            for (int i = 0; i < Fighters.Length; i++) Fighters[i].Apply(prev, cur, alpha, tables, sweat, dt);

            int shooter = cur.Shot.Active ? cur.Shot.Shooter : cur.Ball.LastTouch;
            Ball.TrailColor = Fighters[shooter < 2 ? shooter : 0].Visual.Motif;
            Ball.Apply(prev, cur, alpha, Fighters, dt);

            Heat.Update(cur, roundFrames, dt);
            Context.Audio?.SetHeat(Heat.Value);
            if (cur.F0.Score != cur.F1.Score) Court.SetLeader(Fighters[cur.F0.Score > cur.F1.Score ? 0 : 1].Primary);
            else Court.SetLeader(Color.clear);

            // Camera: finisher orbit, buzzer ball-follow, otherwise frame fighters + ball + rim.
            if (cur.Phase == Phase.Finisher)
            {
                FighterView a = Fighters[cur.Fin.Attacker];
                Camera.SetFinisher(true, a.Feet, cur.Fin.Frames > 0 ? (cur.Fin.Frame + alpha) / cur.Fin.Frames : 0f, a.Stats.Def.Finisher.Id);
            }
            else
            {
                Camera.SetFinisher(false, Vector3.zero, 0f, null);
            }
            Camera.SetFollow(cur.Shot.Active && cur.Shot.Buzzer, Ball.transform.position);
            _framePoints.Clear();
            for (int i = 0; i < Fighters.Length; i++)
            {
                _framePoints.Add(Fighters[i].Feet);
                _framePoints.Add(Fighters[i].Feet + Vector3.up * Fighters[i].Rig.Height);
            }
            _framePoints.Add(Ball.transform.position);
            _framePoints.Add(_rim);
            Camera.Tick(_framePoints, dt);
            Court.Tick(Camera.transform.position, Heat, dt);
            Hitboxes.Apply(cur, Fighters);
        }

        public void Shutdown()
        {
            Impacts?.Detach();
            Context?.Audio?.LeaveCourt();
            Driver?.End();
            Session = null;
        }

        private void OnDestroy()
        {
            Shutdown();
        }
    }
}
