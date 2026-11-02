using System.Collections.Generic;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// The fixed side-on gameplay camera: frames both fighters, the ball and the rim, and plays the impact
    /// recipes from camera.json (nudge, snap zoom, Dutch tilt, punch-ins, net whip, hard cut, ball follow).
    /// During a Signature Finisher it orbits the attacker for exactly the cinematic's sim duration, the only
    /// time it leaves the side view.
    /// </summary>
    public sealed class GameCamera : MonoBehaviour
    {
        private struct ActiveRecipe
        {
            public CameraRecipe Recipe;
            public float Age;
            public Vector3 Focus;
            public float Seed;
            public long Key;
        }

        public Camera Cam { get; private set; }
        public bool ReducedMotion;
        public float ShakeScale = 1f;

        private CameraConfig _cfg;
        private Vector3 _center;
        private float _distance;
        private Vector3 _centerVel;
        private float _distanceVel;
        private bool _snapNext = true;
        private readonly List<ActiveRecipe> _active = new List<ActiveRecipe>();
        private Vector3 _followTarget;
        private bool _following;

        // Finisher orbit.
        private bool _finisher;
        private Vector3 _finisherFocus;
        private float _finisherT;
        private string _finisherStyle;

        public static GameCamera Create(Transform parent, CameraConfig cfg, Color background)
        {
            var go = new GameObject("GameCamera");
            go.transform.SetParent(parent, false);
            var cam = go.AddComponent<Camera>();
            cam.fieldOfView = cfg.Fov;
            cam.nearClipPlane = 0.3f;
            cam.farClipPlane = 120f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = background;
            cam.allowHDR = true;
            cam.allowMSAA = true;
            go.AddComponent<AudioListener>();
            var gc = go.AddComponent<GameCamera>();
            gc.Cam = cam;
            gc._cfg = cfg;
            gc._distance = (cfg.MinDistance + cfg.MaxDistance) * 0.5f;
            return gc;
        }

        public void HardCut() => _snapNext = true;

        /// <summary>Starts an impact recipe; <paramref name="age"/> skips ahead for late (rolled-back) events.</summary>
        public void Play(string recipeId, Vector3 focus, float age = 0f, long key = 0)
        {
            if (string.IsNullOrEmpty(recipeId) || !_cfg.Recipes.TryGetValue(recipeId, out CameraRecipe r)) return;
            if (r.Kind == "none") return;
            if (r.Kind == "cut")
            {
                HardCut();
                return;
            }
            if (age >= r.Duration) return;
            _active.Add(new ActiveRecipe { Recipe = r, Age = age, Focus = focus, Seed = VisualNoise.Hash01(_active.Count, Time.frameCount) * 100f, Key = key });
        }

        /// <summary>Removes recipes started by an event a rollback proved never happened.</summary>
        public void Cancel(long key)
        {
            for (int i = _active.Count - 1; i >= 0; i--) if (_active[i].Key == key && key != 0) _active.RemoveAt(i);
        }

        public void SetFollow(bool on, Vector3 target)
        {
            _following = on;
            _followTarget = target;
        }

        public void SetFinisher(bool active, Vector3 focus, float t01, string style)
        {
            if (_finisher && !active) HardCut();
            _finisher = active;
            _finisherFocus = focus;
            _finisherT = t01;
            _finisherStyle = style;
        }

        /// <summary>Frames the given points (fighters, ball, rim) and applies recipes. Call once per rendered frame.</summary>
        public void Tick(IList<Vector3> points, float dt)
        {
            if (_finisher)
            {
                Orbit(dt);
                return;
            }
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, 0f), max = new Vector3(float.MinValue, float.MinValue, 0f);
            for (int i = 0; i < points.Count; i++)
            {
                min = Vector3.Min(min, points[i]);
                max = Vector3.Max(max, points[i]);
            }
            if (_following)
            {
                min = Vector3.Min(min, _followTarget);
                max = Vector3.Max(max, _followTarget);
            }
            float halfFovV = _cfg.Fov * 0.5f * Mathf.Deg2Rad;
            float aspect = Mathf.Max(1f, Cam.aspect);
            float halfFovH = Mathf.Atan(Mathf.Tan(halfFovV) * aspect);
            float width = (max.x - min.x) + _cfg.PaddingX * 2f;
            float height = (max.y - min.y) + _cfg.PaddingY * 2f;
            float dW = width * 0.5f / Mathf.Tan(halfFovH);
            float dH = height * 0.5f / Mathf.Tan(halfFovV);
            float targetDist = Mathf.Clamp(Mathf.Max(dW, dH), _cfg.MinDistance, _cfg.MaxDistance);
            var targetCenter = new Vector3((min.x + max.x) * 0.5f, _cfg.LookHeight + Mathf.Max(0f, (max.y - 3.2f) * 0.35f), 0f);

            if (_snapNext)
            {
                _center = targetCenter;
                _distance = targetDist;
                _centerVel = Vector3.zero;
                _distanceVel = 0f;
                _snapNext = false;
            }
            else
            {
                _center = Vector3.SmoothDamp(_center, targetCenter, ref _centerVel, _cfg.Smooth, Mathf.Infinity, dt);
                _distance = Mathf.SmoothDamp(_distance, targetDist, ref _distanceVel, _cfg.Smooth * 1.5f, Mathf.Infinity, dt);
            }

            // Recipes.
            Vector3 offset = Vector3.zero;
            float zoom = 0f, roll = 0f;
            Vector3 lookBias = Vector3.zero;
            float scale = (ReducedMotion ? _cfg.ReducedMotionScale : 1f) * ShakeScale;
            for (int i = _active.Count - 1; i >= 0; i--)
            {
                ActiveRecipe a = _active[i];
                a.Age += dt;
                CameraRecipe r = a.Recipe;
                float t = r.Duration > 0f ? a.Age / r.Duration : 1f;
                if (t >= 1f)
                {
                    _active.RemoveAt(i);
                    continue;
                }
                _active[i] = a;
                float env = Ease.Punch(t, 0.18f);
                switch (r.Kind)
                {
                    case "shake":
                        float decay = 1f - t;
                        offset += new Vector3(Mathf.Sin((a.Age * r.Frequency + a.Seed) * 6.28f), Mathf.Cos((a.Age * r.Frequency * 1.3f + a.Seed) * 6.28f), 0f) * r.Amplitude * decay * scale;
                        break;
                    case "zoom":
                        zoom += r.Zoom * env;
                        lookBias += (a.Focus - _center) * (0.35f * env);
                        offset += new Vector3(0f, Mathf.Sin(a.Age * 40f) * r.Amplitude * (1f - t) * scale, 0f);
                        break;
                    case "tilt":
                        roll += r.Tilt * env * scale;
                        zoom += r.Zoom * env;
                        break;
                    case "whip":
                        lookBias += (a.Focus - _center) * (0.6f * env);
                        zoom += r.Zoom * env;
                        break;
                    case "follow":
                        lookBias += (a.Focus - _center) * (0.25f * env);
                        zoom += r.Zoom * env;
                        break;
                }
            }

            float dist = _distance * (1f - Mathf.Clamp(zoom, 0f, 0.6f));
            Vector3 look = _center + lookBias;
            Vector3 pos = new Vector3(look.x, _cfg.Height + (look.y - _cfg.LookHeight) * 0.6f, -dist) + offset;
            transform.position = pos;
            Quaternion rot = Quaternion.LookRotation((look + Vector3.up * 0.0f) - pos, Vector3.up);
            transform.rotation = rot * Quaternion.Euler(_cfg.Pitch * 0.25f, 0f, roll);
        }

        private void Orbit(float dt)
        {
            // Style-specific sweeps; all end facing the side view again so the cut back is clean.
            float t = _finisherT;
            float angle;
            float lift = 0f;
            switch (_finisherStyle)
            {
                case "orbit": angle = Mathf.Lerp(-90f, 270f, Ease.InOutSine(t)); lift = Mathf.Sin(t * Mathf.PI) * 1.2f; break;
                case "cloudburst": angle = Mathf.Lerp(-90f, -40f, Ease.InOutSine(t)); lift = Ease.OutCubic(t) * 3.5f; break;
                case "overclock": angle = -90f + Mathf.Sin(t * Mathf.PI * 3f) * 50f; break;
                default: angle = Mathf.Lerp(-120f, -60f, t); lift = -0.4f + Mathf.Sin(t * Mathf.PI) * 0.6f; break;
            }
            float rad = angle * Mathf.Deg2Rad;
            Vector3 focus = _finisherFocus + Vector3.up * (_cfg.FinisherHeight + lift * 0.5f);
            Vector3 pos = focus + new Vector3(Mathf.Cos(rad), 0f, Mathf.Sin(rad)) * _cfg.FinisherDistance + Vector3.up * (0.4f + lift * 0.3f);
            transform.position = Vector3.Lerp(transform.position, pos, 1f - Mathf.Exp(-dt * 10f));
            transform.rotation = Quaternion.LookRotation(focus - transform.position, Vector3.up);
        }
    }
}
