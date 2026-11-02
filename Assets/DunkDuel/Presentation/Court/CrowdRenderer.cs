using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Hundreds of spectators as GPU-instanced meshes (Graphics.RenderMeshInstanced), a few color batches,
    /// animated on the CPU per Stage Heat state: calm sway, hyped bounce, frenzy jumping. Set pieces can push
    /// groups (surge to the sideline, climb the fence, umbrellas up). Final art swaps the mesh for a
    /// vertex-animation-texture crowd baked in Blender (Tools/Blender/dd_vat_bake.py) without changing this API.
    /// </summary>
    public sealed class CrowdRenderer : MonoBehaviour
    {
        private const int MaxPerCall = 1023;

        private sealed class Batch
        {
            public Material Material;
            public readonly List<Vector3> Base = new List<Vector3>();
            public readonly List<float> Phase = new List<float>();
            public readonly List<float> Scale = new List<float>();
            public readonly List<int> Group = new List<int>();
            public Matrix4x4[] Matrices = new Matrix4x4[0];
        }

        private readonly List<Batch> _batches = new List<Batch>();
        private Mesh _mesh;
        private Mesh _umbrella;
        private Material _umbrellaMat;
        private bool _umbrellas;
        private Matrix4x4[] _umbrellaMatrices = new Matrix4x4[0];
        private Bounds _bounds;
        private StageHeat.State _state;
        private float _heat;
        private float _surge;
        private float _climb;
        private float _umbrellaLift;
        private float _timeOffset;
        public int Count { get; private set; }
        public bool Visible = true;

        public static CrowdRenderer Create(Transform parent, Mesh spectator, Material[] palette)
        {
            var go = new GameObject("Crowd");
            go.transform.SetParent(parent, false);
            var c = go.AddComponent<CrowdRenderer>();
            c._mesh = spectator;
            for (int i = 0; i < palette.Length; i++)
            {
                palette[i].enableInstancing = true;
                c._batches.Add(new Batch { Material = palette[i] });
            }
            c._bounds = new Bounds(Vector3.zero, new Vector3(200f, 60f, 200f));
            return c;
        }

        public void EnableUmbrellas(Mesh umbrella, Material mat)
        {
            _umbrella = umbrella;
            _umbrellaMat = mat;
            _umbrellaMat.enableInstancing = true;
            _umbrellas = true;
        }

        /// <summary>Adds a spectator. <paramref name="group"/>: 0 stands/sits normally, 1 front row (surges), 2 fence row (climbs).</summary>
        public void Add(Vector3 position, float scale, int group, int seed)
        {
            Batch b = _batches[Mathf.Abs(seed) % _batches.Count];
            b.Base.Add(position);
            b.Phase.Add(VisualNoise.Hash01(seed, 3) * Mathf.PI * 2f);
            b.Scale.Add(scale * (0.9f + VisualNoise.Hash01(seed, 7) * 0.2f));
            b.Group.Add(group);
            Count++;
        }

        public void Seal()
        {
            int total = 0;
            for (int i = 0; i < _batches.Count; i++)
            {
                _batches[i].Matrices = new Matrix4x4[_batches[i].Base.Count];
                total += _batches[i].Base.Count;
            }
            if (_umbrellas) _umbrellaMatrices = new Matrix4x4[total];
        }

        public void SetHeat(StageHeat.State state, float heat)
        {
            _state = state;
            _heat = heat;
        }

        public void Surge(float seconds) => _surge = Mathf.Max(_surge, seconds);
        public void Climb(float seconds) => _climb = Mathf.Max(_climb, seconds);
        public void UmbrellasUp(float seconds) => _umbrellaLift = Mathf.Max(_umbrellaLift, seconds);

        private void LateUpdate()
        {
            if (!Visible || _mesh == null) return;
            float dt = Time.deltaTime;
            _timeOffset += dt;
            _surge = Mathf.Max(0f, _surge - dt);
            _climb = Mathf.Max(0f, _climb - dt);
            _umbrellaLift = Mathf.Max(0f, _umbrellaLift - dt);
            float t = _timeOffset;
            float freq = _state == StageHeat.State.Frenzy ? 2.6f : (_state == StageHeat.State.Hyped ? 1.8f : 0.6f);
            float amp = _state == StageHeat.State.Frenzy ? 0.22f : (_state == StageHeat.State.Hyped ? 0.09f : 0.025f);
            float surgeZ = _surge > 0f ? -1.6f * Mathf.Clamp01(_surge) : 0f;
            float climbY = _climb > 0f ? 0.9f * Mathf.Clamp01(_climb) : 0f;
            int u = 0;
            for (int bi = 0; bi < _batches.Count; bi++)
            {
                Batch b = _batches[bi];
                int n = b.Base.Count;
                for (int i = 0; i < n; i++)
                {
                    float ph = b.Phase[i];
                    float bob = Mathf.Max(0f, Mathf.Sin(t * freq * Mathf.PI * 2f + ph)) * amp;
                    float sway = Mathf.Sin(t * 0.7f + ph) * (3f + _heat * 5f);
                    Vector3 p = b.Base[i] + new Vector3(0f, bob, 0f);
                    if (b.Group[i] == 1) p.z += surgeZ;
                    if (b.Group[i] == 2) p.y += climbY;
                    float s = b.Scale[i];
                    b.Matrices[i] = Matrix4x4.TRS(p, Quaternion.Euler(0f, 180f + sway, sway * 0.4f), new Vector3(s, s, s));
                    if (_umbrellas && u < _umbrellaMatrices.Length)
                    {
                        float lift = _umbrellaLift > 0f ? 1.2f * Mathf.Clamp01(_umbrellaLift) : 0f;
                        _umbrellaMatrices[u++] = Matrix4x4.TRS(p + new Vector3(0f, 1.95f * s + lift, 0f), Quaternion.Euler(sway, 0f, sway), new Vector3(s, s, s));
                    }
                }
                Draw(_mesh, b.Material, b.Matrices, n);
            }
            if (_umbrellas) Draw(_umbrella, _umbrellaMat, _umbrellaMatrices, u);
        }

        private void Draw(Mesh mesh, Material mat, Matrix4x4[] matrices, int count)
        {
            var rp = new RenderParams(mat)
            {
                worldBounds = _bounds,
                shadowCastingMode = ShadowCastingMode.Off,
                receiveShadows = false,
            };
            for (int start = 0; start < count; start += MaxPerCall)
            {
                int n = Mathf.Min(MaxPerCall, count - start);
                Graphics.RenderMeshInstanced(rp, mesh, 0, matrices, n, start);
            }
        }
    }
}
