using DunkDuel.Sim;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// The ball. Its sim position is authoritative in flight and when loose; while held, presentation draws the
    /// dribble bounce and gather from the holder's hands (the sim only knows the ball is held).
    /// </summary>
    public sealed class BallView : MonoBehaviour
    {
        private Transform _mesh;
        private TrailRenderer _trail;
        private float _radius;
        private Vector3 _last;
        private float _shotStartZ;
        private bool _wasFlight;
        public Color TrailColor = Color.white;

        public static BallView Create(Transform parent, float radius, MaterialLibrary mats)
        {
            var go = new GameObject("Ball");
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<BallView>();
            view._radius = radius;
            var meshGo = new GameObject("BallMesh");
            meshGo.transform.SetParent(go.transform, false);
            meshGo.AddComponent<MeshFilter>().sharedMesh = MeshFactory.Sphere(radius, 18, 12);
            var mr = meshGo.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mats.Textured(TextureFactory.BallSkin(new Color(0.82f, 0.38f, 0.14f), new Color(0.12f, 0.08f, 0.06f)), Color.white, 0.35f);
            view._mesh = meshGo.transform;
            view._trail = go.AddComponent<TrailRenderer>();
            view._trail.material = mats.TransparentInstance(new Color(1f, 1f, 1f, 0.5f), true);
            view._trail.time = 0.18f;
            view._trail.startWidth = radius * 1.6f;
            view._trail.endWidth = 0f;
            view._trail.emitting = false;
            view._trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return view;
        }

        public void SetSkin(Material skin)
        {
            if (skin != null) _mesh.GetComponent<MeshRenderer>().sharedMaterial = skin;
        }

        public void Apply(in GameState prev, in GameState cur, float alpha, FighterView[] fighters, float dt)
        {
            BallState b = cur.Ball;
            Vector3 pos;
            bool held = b.Mode == BallMode.Held || cur.Phase == Phase.Finisher;
            if (held)
            {
                int holder = cur.Phase == Phase.Finisher ? cur.Fin.Attacker : b.Holder;
                pos = fighters[holder].BallHold;
                _shotStartZ = fighters[holder].Depth;
            }
            else
            {
                BallState a = prev.Ball;
                bool continuous = a.Mode == b.Mode || (a.Mode == BallMode.Flight && b.Mode != BallMode.Held);
                float x = continuous ? FixExtensions.Lerp(a.X, b.X, alpha) : b.X.ToFloat();
                float y = continuous ? FixExtensions.Lerp(a.Y, b.Y, alpha) : b.Y.ToFloat();
                float z;
                if (b.Mode == BallMode.Flight && cur.Shot.StageFrames > 0)
                {
                    float t = cur.Shot.Stage == ShotStage.ToTarget ? Mathf.Clamp01((cur.Shot.Frame + alpha) / cur.Shot.StageFrames) : 1f;
                    z = Mathf.Lerp(_shotStartZ, 0f, t);
                }
                else
                {
                    z = Mathf.MoveTowards(transform.position.z, 0f, dt * 0.8f);
                }
                pos = new Vector3(x, y, z);
            }
            transform.position = pos;
            Vector3 delta = pos - _last;
            _last = pos;
            // Roll/spin from motion: backspin in flight, forward roll otherwise.
            float spin = delta.magnitude / Mathf.Max(0.01f, _radius) * Mathf.Rad2Deg;
            bool flight = b.Mode == BallMode.Flight;
            _mesh.Rotate(Vector3.forward, flight ? spin : -spin * Mathf.Sign(delta.x == 0f ? 1f : delta.x), Space.World);
            if (flight != _wasFlight)
            {
                _trail.emitting = flight;
                if (flight)
                {
                    _trail.startColor = TrailColor;
                    _trail.endColor = new Color(TrailColor.r, TrailColor.g, TrailColor.b, 0f);
                }
                _wasFlight = flight;
            }
        }
    }
}
