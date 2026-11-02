using System.Collections.Generic;
using UnityEngine;

namespace DunkDuel.Presentation
{
    public enum BurstKind : byte { Sparks = 0, Droplets = 1, Shards = 2, Dust = 3, Confetti = 4, Leaves = 5, Pixels = 6, Embers = 7 }

    /// <summary>
    /// Pooled, code-animated graybox effects (rings, flashes, bolts) plus shared particle emitters for bursts.
    /// Every effect is keyed by the sim event that spawned it so a rollback can fade orphans out fast; late
    /// effects start part-way through using their age.
    /// </summary>
    public sealed class EffectPool : MonoBehaviour
    {
        private enum FxShape : byte { Ring = 0, Flash = 1, Bolt = 2, Glow = 3 }

        private sealed class Fx
        {
            public long Key;
            public FxShape Shape;
            public GameObject Go;
            public Material Mat;
            public float Age;
            public float Duration;
            public Vector3 Origin;
            public float Size;
            public Color Color;
            public bool Fading;
            public float FadeLeft;
        }

        private readonly List<Fx> _active = new List<Fx>();
        private readonly Stack<Fx>[] _free = { new Stack<Fx>(), new Stack<Fx>(), new Stack<Fx>(), new Stack<Fx>() };
        private ParticleSystem[] _emitters;
        private MaterialLibrary _mats;
        private Mesh _ring;
        private Mesh _quad;
        private Mesh _bolt;
        private Mesh _glow;
        public float Scale = 1f;
        public bool ReducedFlash;

        public static EffectPool Create(Transform parent, MaterialLibrary mats, float effectsScale)
        {
            var go = new GameObject("Effects");
            go.transform.SetParent(parent, false);
            var pool = go.AddComponent<EffectPool>();
            pool._mats = mats;
            pool.Scale = effectsScale;
            pool._ring = MeshFactory.Ring(0.85f, 1f, 40);
            pool._quad = MeshFactory.Quad(1f, 1f);
            pool._bolt = MeshFactory.Box(new Vector3(0.05f, 1f, 0.05f), new Vector3(0f, 0.5f, 0f));
            pool._glow = MeshFactory.Sphere(0.5f, 10, 6);
            pool.BuildEmitters();
            return pool;
        }

        private void BuildEmitters()
        {
            int kinds = 8;
            _emitters = new ParticleSystem[kinds];
            Texture2D dot = TextureFactory.SoftDot(32);
            for (int k = 0; k < kinds; k++)
            {
                var go = new GameObject("Burst_" + (BurstKind)k);
                go.transform.SetParent(transform, false);
                var ps = go.AddComponent<ParticleSystem>();
                ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
                ParticleSystem.MainModule main = ps.main;
                main.loop = false;
                main.playOnAwake = false;
                main.maxParticles = 600;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.startLifetime = 0.8f;
                main.startSpeed = 0f;
                main.startSize = 0.06f;
                main.gravityModifier = k == (int)BurstKind.Confetti || k == (int)BurstKind.Leaves ? 0.15f : (k == (int)BurstKind.Embers ? -0.05f : 0.9f);
                ParticleSystem.EmissionModule em = ps.emission;
                em.enabled = false;
                ParticleSystem.ShapeModule shape = ps.shape;
                shape.enabled = false;
                var r = go.GetComponent<ParticleSystemRenderer>();
                bool additive = k == (int)BurstKind.Sparks || k == (int)BurstKind.Embers || k == (int)BurstKind.Pixels;
                Material m = _mats.TransparentInstance(Color.white, additive);
                if (k != (int)BurstKind.Pixels && k != (int)BurstKind.Shards && k != (int)BurstKind.Confetti) m.mainTexture = dot;
                r.material = m;
                r.renderMode = k == (int)BurstKind.Sparks || k == (int)BurstKind.Droplets ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
                r.velocityScale = 0.04f;
                r.lengthScale = 1.5f;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
                ps.Play();
                _emitters[k] = ps;
            }
        }

        /// <summary>Emits a burst from a shared emitter. Bursts are short-lived and not rollback-cancelled.</summary>
        public void Burst(BurstKind kind, Vector3 pos, int count, Color color, float speed, float size = 0.06f, float life = 0.8f)
        {
            ParticleSystem ps = _emitters[(int)kind];
            int n = Mathf.Max(1, Mathf.RoundToInt(count * Scale));
            for (int i = 0; i < n; i++)
            {
                float a = VisualNoise.Hash01(i, Time.frameCount) * Mathf.PI * 2f;
                float b = VisualNoise.Hash01(i + 77, Time.frameCount) * Mathf.PI - Mathf.PI * 0.5f;
                Vector3 dir = new Vector3(Mathf.Cos(a) * Mathf.Cos(b), Mathf.Abs(Mathf.Sin(b)) + 0.3f, Mathf.Sin(a) * Mathf.Cos(b) * 0.5f).normalized;
                float sp = speed * (0.4f + VisualNoise.Hash01(i + 13, Time.frameCount));
                var ep = new ParticleSystem.EmitParams
                {
                    position = pos,
                    velocity = dir * sp,
                    startColor = color,
                    startSize = size * (0.6f + VisualNoise.Hash01(i + 5, Time.frameCount) * 0.8f),
                    startLifetime = life * (0.6f + VisualNoise.Hash01(i + 9, Time.frameCount) * 0.6f),
                };
                ps.Emit(ep, 1);
            }
        }

        public void Ring(long key, Vector3 pos, Color c, float radius, float duration, float age = 0f) =>
            Spawn(key, FxShape.Ring, pos, c, radius, duration, age);

        public void Flash(long key, Vector3 pos, Color c, float size, float duration, float age = 0f)
        {
            if (ReducedFlash) c.a *= 0.35f;
            Spawn(key, FxShape.Flash, pos, c, size, duration, age);
        }

        public void Glow(long key, Vector3 pos, Color c, float size, float duration, float age = 0f) =>
            Spawn(key, FxShape.Glow, pos, c, size, duration, age);

        /// <summary>A jagged lightning bolt from <paramref name="pos"/> upward (storm motif, court lightning).</summary>
        public void Bolt(long key, Vector3 pos, Color c, float length, float duration, float age = 0f)
        {
            if (ReducedFlash) c.a *= 0.4f;
            for (int i = 0; i < 4; i++)
            {
                Vector3 p = pos + new Vector3(VisualNoise.Signed(i, Time.frameCount) * 0.3f, i * length * 0.25f, 0f);
                Spawn(key, FxShape.Bolt, p, c, length * 0.3f, duration, age, (VisualNoise.Signed(i + 3, Time.frameCount)) * 35f);
            }
        }

        private void Spawn(long key, FxShape shape, Vector3 pos, Color c, float size, float duration, float age, float roll = 0f)
        {
            if (age >= duration) return;
            Fx fx = _free[(int)shape].Count > 0 ? _free[(int)shape].Pop() : Create(shape);
            fx.Key = key;
            fx.Age = age;
            fx.Duration = duration;
            fx.Origin = pos;
            fx.Size = size;
            fx.Color = c;
            fx.Fading = false;
            fx.Go.SetActive(true);
            fx.Go.transform.position = pos;
            fx.Go.transform.rotation = shape == FxShape.Bolt ? Quaternion.Euler(0f, 0f, roll) : Quaternion.identity;
            _active.Add(fx);
            Animate(fx);
        }

        private Fx Create(FxShape shape)
        {
            var go = new GameObject("Fx_" + shape);
            go.transform.SetParent(transform, false);
            var mf = go.AddComponent<MeshFilter>();
            mf.sharedMesh = shape == FxShape.Ring ? _ring : (shape == FxShape.Bolt ? _bolt : (shape == FxShape.Glow ? _glow : _quad));
            var mr = go.AddComponent<MeshRenderer>();
            Material mat = _mats.TransparentInstance(Color.white, shape != FxShape.Ring);
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            mr.receiveShadows = false;
            return new Fx { Shape = shape, Go = go, Mat = mat };
        }

        /// <summary>Rollback: an event never happened, fade its effects out fast.</summary>
        public void Cancel(long key)
        {
            for (int i = 0; i < _active.Count; i++)
            {
                if (_active[i].Key == key && !_active[i].Fading)
                {
                    _active[i].Fading = true;
                    _active[i].FadeLeft = 0.08f;
                }
            }
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                Fx fx = _active[i];
                fx.Age += dt;
                if (fx.Fading) fx.FadeLeft -= dt;
                if (fx.Age >= fx.Duration || (fx.Fading && fx.FadeLeft <= 0f))
                {
                    fx.Go.SetActive(false);
                    _active.RemoveAt(i);
                    _free[(int)fx.Shape].Push(fx);
                    continue;
                }
                Animate(fx);
            }
        }

        private void Animate(Fx fx)
        {
            float t = Mathf.Clamp01(fx.Age / fx.Duration);
            float fade = fx.Fading ? Mathf.Clamp01(fx.FadeLeft / 0.08f) : 1f;
            Color c = fx.Color;
            Transform tr = fx.Go.transform;
            switch (fx.Shape)
            {
                case FxShape.Ring:
                    float r = Mathf.Lerp(0.1f, fx.Size, Ease.OutCubic(t));
                    tr.localScale = new Vector3(r, 1f, r);
                    c.a *= (1f - t) * fade;
                    break;
                case FxShape.Flash:
                    float s = fx.Size * (0.6f + 0.4f * Ease.OutCubic(t));
                    tr.localScale = new Vector3(s, s, s);
                    c.a *= (1f - t) * (1f - t) * fade;
                    break;
                case FxShape.Glow:
                    float g = fx.Size * (0.8f + 0.4f * Mathf.Sin(t * Mathf.PI));
                    tr.localScale = new Vector3(g, g, g);
                    c.a *= Mathf.Sin(t * Mathf.PI) * fade;
                    break;
                case FxShape.Bolt:
                    tr.localScale = new Vector3(1f, fx.Size, 1f);
                    c.a *= (Mathf.Repeat(fx.Age * 30f, 1f) < 0.6f ? 1f : 0.3f) * (1f - t) * fade;
                    break;
            }
            MaterialLibrary.SetColor(fx.Mat, c);
        }
    }
}
