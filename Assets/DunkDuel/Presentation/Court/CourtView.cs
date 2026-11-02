using System.Collections.Generic;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// A living court: parallax backdrop, weather, crowd heat, ambient life (the Tracks train every 40 s,
    /// storm lightning, gulls) and the court's one set-piece reaction to Signature Finishers.
    /// Presentation only: nothing here touches gameplay.
    /// </summary>
    public sealed class CourtView : MonoBehaviour
    {
        public struct ParallaxCard
        {
            public Transform Card;
            public float Factor;
            public Vector3 Base;
        }

        public CourtDef Def;
        public HoopView Hoop;
        public Light KeyLight;
        public float BaseKeyIntensity;
        public MeshRenderer Floor;
        public Material FloorMaterial;
        public Transform LinesRoot;
        public Transform BackdropRoot;
        public CrowdRenderer Crowd;
        public ParticleSystem Rain;
        public ParticleSystem Leaves;
        public ParticleSystem Dust;
        public MeshRenderer Jumbotron;
        public MeshRenderer Sea;
        public readonly List<ParallaxCard> Parallax = new List<ParallaxCard>();
        public readonly List<MeshRenderer> LightBanks = new List<MeshRenderer>();
        public readonly List<MeshRenderer> Windows = new List<MeshRenderer>();

        private EffectPool _fx;
        private AudioDirector _audio;
        private MaterialLibrary _mats;
        private Transform _train;
        private float _trainTimer = 12f;
        private float _trainX = -999f;
        private float _lightningTimer = 9f;
        private Light _flashLight;
        private float _flash;
        private Light[] _emergency;
        private float _blackout;
        private Light _spot;
        private float _spotTime;
        private Transform _gulls;
        private float _gullTimer;
        private float _linesDraw = 1f;
        private Color _leaderColor = Color.clear;
        private Color _floorBase;
        private float _motifTime;
        private string _motif;
        private QualityProfile _q;
        private float _leftX;
        private float _baselineX;
        private Vector3 _rim;
        private TextMesh _jumboText;

        public bool ReducedFlash;

        internal void BuildSetPiece(MaterialLibrary mats, QualityProfile q, float left, float baseline, float rimX, float rimY)
        {
            _mats = mats;
            _q = q;
            _leftX = left;
            _baselineX = baseline;
            _rim = new Vector3(rimX, rimY, 0f);
            _floorBase = Def.Floor;

            var flashGo = new GameObject("FlashLight");
            flashGo.transform.SetParent(transform, false);
            flashGo.transform.localPosition = new Vector3((left + baseline) * 0.5f, 14f, -4f);
            _flashLight = flashGo.AddComponent<Light>();
            _flashLight.type = LightType.Point;
            _flashLight.range = 60f;
            _flashLight.intensity = 0f;
            _flashLight.color = new Color(0.85f, 0.9f, 1f);
            _flashLight.shadows = LightShadows.None;

            switch (Def.SetPiece)
            {
                case "train":
                {
                    _train = new GameObject("Train").transform;
                    _train.SetParent(transform, false);
                    Material car = mats.Lit(new Color(0.55f, 0.56f, 0.58f), 0.6f, 0.6f);
                    Material windows = mats.Emissive(new Color(1f, 0.92f, 0.7f), 1.6f);
                    for (int i = 0; i < 5; i++)
                    {
                        CourtBuilder.Place(_train, "Car", MeshFactory.Box(new Vector3(9f, 2.6f, 2.8f)), car, new Vector3(i * 9.4f, 11.4f, 14f));
                        CourtBuilder.Place(_train, "Windows", MeshFactory.Box(new Vector3(8f, 0.7f, 2.85f)), windows, new Vector3(i * 9.4f, 11.8f, 14f));
                    }
                    _train.localPosition = new Vector3(-999f, 0f, 0f);
                    break;
                }
                case "blackout":
                {
                    _emergency = new Light[2];
                    for (int i = 0; i < 2; i++)
                    {
                        var go = new GameObject("Emergency" + i);
                        go.transform.SetParent(transform, false);
                        go.transform.localPosition = new Vector3(i == 0 ? left + 2f : baseline - 1f, 9f, 12f);
                        _emergency[i] = go.AddComponent<Light>();
                        _emergency[i].type = LightType.Point;
                        _emergency[i].color = Def.Accent2;
                        _emergency[i].range = 30f;
                        _emergency[i].intensity = 0f;
                    }
                    break;
                }
                case "pyro":
                {
                    var go = new GameObject("Spotlight");
                    go.transform.SetParent(transform, false);
                    _spot = go.AddComponent<Light>();
                    _spot.type = LightType.Spot;
                    _spot.spotAngle = 22f;
                    _spot.range = 40f;
                    _spot.intensity = 0f;
                    _spot.color = Def.Accent;
                    if (Jumbotron != null) BuildJumbotronText();
                    break;
                }
                case "wave":
                {
                    _gulls = new GameObject("Gulls").transform;
                    _gulls.SetParent(transform, false);
                    Material gull = mats.Lit(new Color(0.95f, 0.95f, 0.95f), 0.2f);
                    for (int i = 0; i < 6; i++) CourtBuilder.Place(_gulls, "Gull", MeshFactory.Box(new Vector3(0.5f, 0.06f, 0.18f)), gull, new Vector3(i * 1.7f, VisualNoise.Hash01(i) * 2f, VisualNoise.Hash01(i, 2) * 3f));
                    _gulls.localPosition = new Vector3(left, 9f, 20f);
                    break;
                }
            }
        }

        private void BuildJumbotronText()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null) return;
            var go = new GameObject("JumboText");
            go.transform.SetParent(Jumbotron.transform, false);
            go.transform.localPosition = new Vector3(0f, 0f, -0.25f);
            _jumboText = go.AddComponent<TextMesh>();
            _jumboText.font = font;
            _jumboText.text = "DUNK DUEL";
            _jumboText.anchor = TextAnchor.MiddleCenter;
            _jumboText.alignment = TextAlignment.Center;
            _jumboText.characterSize = 0.12f;
            _jumboText.fontSize = 48;
            _jumboText.color = Def.Accent;
            go.GetComponent<MeshRenderer>().sharedMaterial = font.material;
        }

        public void Bind(EffectPool fx, AudioDirector audio)
        {
            _fx = fx;
            _audio = audio;
        }

        /// <summary>Glass Court lines draw themselves at round start.</summary>
        public void RoundStart()
        {
            if (Def.SetPiece == "motif") _linesDraw = 0f;
        }

        /// <summary>The leading fighter's color, or clear when tied (Glass Court floor glow).</summary>
        public void SetLeader(Color c) => _leaderColor = c;

        public void ShowHighlight(string text)
        {
            if (_jumboText != null) _jumboText.text = text;
        }

        /// <summary>Glass Court footstep ripples.</summary>
        public void Footstep(Vector3 pos, Color c)
        {
            if (Def.SetPiece == "motif" && _fx != null) _fx.Ring(0, new Vector3(pos.x, 0.01f, pos.z), new Color(c.r, c.g, c.b, 0.6f), 0.9f, 0.5f);
        }

        /// <summary>The court's set-piece reaction to a Signature Finisher.</summary>
        public void FinisherMoment(string motifKind, Vector3 attacker, Color motifColor, float finisherSeconds)
        {
            if (!_q.SetPieces && Def.SetPiece != "motif") return;
            _audio?.SetPiece(Def.SetPiece);
            switch (Def.SetPiece)
            {
                case "downpour":
                    if (Rain != null)
                    {
                        ParticleSystem.EmissionModule em = Rain.emission;
                        em.rateOverTimeMultiplier = 4f;
                        _rainBoostUntil = Time.time + finisherSeconds;
                    }
                    Lightning(attacker, 1f);
                    Crowd?.UmbrellasUp(finisherSeconds);
                    break;
                case "train":
                    _trainX = _leftX - 60f;
                    _trainTimer = 40f;
                    _fx?.Burst(BurstKind.Sparks, new Vector3(attacker.x, 10.5f, 13f), 80, new Color(1f, 0.8f, 0.4f), 6f, 0.05f, 1.2f);
                    Crowd?.Climb(finisherSeconds);
                    break;
                case "wave":
                    _fx?.Burst(BurstKind.Droplets, new Vector3(attacker.x, 1.5f, 15f), 160, new Color(0.92f, 0.96f, 1f), 7f, 0.09f, 1.8f);
                    _gullTimer = 3f;
                    break;
                case "surge":
                    Crowd?.Surge(finisherSeconds);
                    break;
                case "blackout":
                    _blackout = finisherSeconds + 1.5f;
                    break;
                case "leafstorm":
                    _fx?.Burst(BurstKind.Leaves, attacker + new Vector3(-4f, 2f, 2f), 140, Def.Accent, 8f, 0.14f, 2.5f);
                    Crowd?.Climb(finisherSeconds);
                    break;
                case "shatter":
                    Hoop?.Shatter(finisherSeconds);
                    _fx?.Burst(BurstKind.Shards, _rim + new Vector3(0.4f, 0.4f, 0f), 120, new Color(0.85f, 0.95f, 1f, 0.9f), 5f, 0.08f, 1.6f);
                    Flash(0.6f);
                    break;
                case "motif":
                    _motif = motifKind;
                    _motifTime = finisherSeconds;
                    break;
                case "pyro":
                    _spotTime = finisherSeconds;
                    break;
            }
            if (Def.SetPiece != "motif") _motif = null;
            _motifColor = motifColor;
        }

        private float _rainBoostUntil;
        private Color _motifColor;

        /// <summary>Confetti on match point (The Crown) and other end-of-match flourishes.</summary>
        public void MatchPoint(Vector3 focus)
        {
            if (Def.SetPiece == "pyro") _fx?.Burst(BurstKind.Confetti, focus + new Vector3(0f, 9f, 2f), 260, Def.Accent2, 3f, 0.1f, 4f);
        }

        public void Lightning(Vector3 near, float strength)
        {
            Flash(strength);
            _fx?.Bolt(0, near + new Vector3(-3f, 6f, 18f), new Color(0.9f, 0.93f, 1f), 10f, 0.25f);
        }

        public void Flash(float strength)
        {
            _flash = Mathf.Max(_flash, (ReducedFlash ? 0.35f : 1f) * strength);
        }

        /// <summary>Per-frame update with the camera position for parallax.</summary>
        public void Tick(Vector3 cameraPos, StageHeat heat, float dt)
        {
            for (int i = 0; i < Parallax.Count; i++)
            {
                ParallaxCard p = Parallax[i];
                if (p.Card != null) p.Card.localPosition = p.Base + new Vector3(cameraPos.x * p.Factor, 0f, 0f);
            }
            if (Crowd != null) Crowd.SetHeat(heat.Level, heat.Value);

            // Lightning flash light.
            if (_flash > 0f)
            {
                _flash = Mathf.Max(0f, _flash - dt * 4f);
                _flashLight.intensity = _flash * 6f;
            }
            else if (_flashLight.intensity > 0f) _flashLight.intensity = 0f;

            if (Rain != null && _rainBoostUntil > 0f && Time.time > _rainBoostUntil)
            {
                ParticleSystem.EmissionModule em = Rain.emission;
                em.rateOverTimeMultiplier = 1f;
                _rainBoostUntil = 0f;
            }

            // Ambient storm lightning.
            if (Def.Weather == "rain" || Def.Weather == "storm")
            {
                _lightningTimer -= dt;
                if (_lightningTimer <= 0f)
                {
                    _lightningTimer = 9f + VisualNoise.Hash01(Time.frameCount) * 14f;
                    if (_q.SetPieces) Lightning(new Vector3(cameraPos.x, 0f, 0f), 0.45f);
                }
            }

            // The Tracks: a train every 40 seconds, fence shaking.
            if (_train != null)
            {
                _trainTimer -= dt;
                if (_trainTimer <= 0f && _trainX < _leftX - 50f)
                {
                    _trainX = _leftX - 60f;
                    _trainTimer = 40f;
                    _audio?.SetPiece("train");
                }
                if (_trainX > _leftX - 70f)
                {
                    _trainX += dt * 22f;
                    _train.localPosition = new Vector3(_trainX, 0f, 0f);
                    if (_trainX > _baselineX + 40f) _trainX = -999f;
                }
            }

            // Lights Out blackout: lights off, red emergency glow, banks clank back on one by one.
            if (_emergency != null)
            {
                if (_blackout > 0f)
                {
                    _blackout -= dt;
                    float back = Mathf.Clamp01(1f - _blackout / 1.5f);
                    KeyLight.intensity = BaseKeyIntensity * Mathf.Lerp(0.05f, 1f, back);
                    for (int i = 0; i < _emergency.Length; i++) _emergency[i].intensity = (1f - back) * (2.2f + Mathf.Sin(Time.time * 6f) * 0.5f);
                    int on = Mathf.FloorToInt(back * LightBanks.Count);
                    for (int i = 0; i < LightBanks.Count; i++) LightBanks[i].enabled = i < on;
                }
                else
                {
                    KeyLight.intensity = BaseKeyIntensity;
                    for (int i = 0; i < _emergency.Length; i++) _emergency[i].intensity = 0f;
                    for (int i = 0; i < LightBanks.Count; i++) LightBanks[i].enabled = true;
                }
            }

            // The Crown: lights drop to one spotlight on the finisher.
            if (_spot != null)
            {
                if (_spotTime > 0f)
                {
                    _spotTime -= dt;
                    KeyLight.intensity = Mathf.Lerp(KeyLight.intensity, BaseKeyIntensity * 0.15f, dt * 6f);
                    _spot.intensity = 9f;
                    _spot.transform.position = cameraPos + new Vector3(0f, 14f, 6f);
                    if (_spotTime < 0.8f && _fx != null && Mathf.Repeat(_spotTime, 0.25f) < dt)
                    {
                        for (int i = 0; i < 4; i++) _fx.Burst(BurstKind.Embers, new Vector3(_leftX + 2f + i * 3.2f, 0.2f, 9f), 40, new Color(1f, 0.6f, 0.2f), 9f, 0.12f, 1.2f);
                    }
                }
                else
                {
                    KeyLight.intensity = Mathf.Lerp(KeyLight.intensity, BaseKeyIntensity, dt * 3f);
                    _spot.intensity = 0f;
                }
            }

            // Concrete Coast gulls scatter.
            if (_gulls != null)
            {
                if (_gullTimer > 0f)
                {
                    _gullTimer -= dt;
                    _gulls.localPosition += new Vector3(dt * 6f, dt * 4f, dt * 2f);
                }
                else
                {
                    _gulls.localPosition = Vector3.Lerp(_gulls.localPosition, new Vector3(_leftX + Mathf.Sin(Time.time * 0.2f) * 6f, 9f, 20f), dt * 0.3f);
                }
            }

            // Glass Court: lines draw on, floor glows in the leader's color, finisher motif patterns.
            if (Def.SetPiece == "motif")
            {
                if (_linesDraw < 1f && LinesRoot != null)
                {
                    _linesDraw = Mathf.Min(1f, _linesDraw + dt * 1.2f);
                    LinesRoot.localScale = new Vector3(1f, 1f, Mathf.Max(0.001f, Ease.OutCubic(_linesDraw)));
                }
                Color glow = _leaderColor.a > 0f ? _leaderColor * 0.18f : Color.black;
                if (_motifTime > 0f)
                {
                    _motifTime -= dt;
                    glow = _motifColor * (0.35f + 0.25f * Mathf.Sin(Time.time * 10f));
                    MotifPattern(dt);
                }
                if (FloorMaterial != null) MaterialLibrary.SetEmission(FloorMaterial, glow);
            }
        }

        private float _motifEmit;

        private void MotifPattern(float dt)
        {
            if (_fx == null) return;
            _motifEmit -= dt;
            if (_motifEmit > 0f) return;
            _motifEmit = 0.12f;
            float x = Mathf.Lerp(_leftX, _baselineX, VisualNoise.Hash01(Time.frameCount, 1));
            float z = Mathf.Lerp(-6f, 6f, VisualNoise.Hash01(Time.frameCount, 2));
            switch (_motif)
            {
                case "solar": _fx.Ring(0, new Vector3(_rim.x - 4f, 0.02f, 0f), _motifColor, 12f, 0.9f); break; // sunburst
                case "storm": _fx.Ring(0, new Vector3(x, 0.02f, z), _motifColor, 2.5f, 0.8f); break;          // rain ripples
                case "glitch": _fx.Flash(0, new Vector3(x, 0.05f, z), _motifColor, 1.2f, 0.25f); break;        // glitch blocks
                default: _fx.Ring(0, new Vector3(x, 0.02f, 0f), _motifColor, 1.6f, 0.5f); break;              // fissures
            }
        }
    }

    /// <summary>Hoop graybox (pole, backboard, rim, net) with rim wobble, net snap and the Old Barn shatter.</summary>
    public sealed class HoopView : MonoBehaviour
    {
        private Transform _rim;
        private Transform _net;
        private MeshRenderer _glass;
        private float _snap;
        private float _wobble;
        private float _dunkHang;
        private float _shatter;

        public static HoopView Build(Transform parent, MaterialLibrary mats, CourtDef def, float rimX, float rimY, float rimR,
            float boardX, float boardBottom, float boardTop, float baseline)
        {
            var go = new GameObject("Hoop");
            go.transform.SetParent(parent, false);
            var hoop = go.AddComponent<HoopView>();
            Material steel = mats.Lit(new Color(0.32f, 0.33f, 0.35f), 0.6f, 0.7f);
            Material orange = mats.Lit(new Color(0.85f, 0.32f, 0.12f), 0.5f, 0.6f);
            Material glass = mats.TransparentInstance(new Color(0.85f, 0.92f, 1f, 0.28f));
            Material frame = mats.Lit(new Color(0.95f, 0.95f, 0.95f), 0.4f);

            float poleX = baseline + 1.1f;
            CourtBuilder.Place(go.transform, "Pole", MeshFactory.Cylinder(0.11f, boardTop + 0.3f, 10), steel, new Vector3(poleX, 0f, 0f), true);
            CourtBuilder.Place(go.transform, "Arm", MeshFactory.Box(new Vector3(poleX - boardX, 0.14f, 0.14f)), steel, new Vector3((poleX + boardX) * 0.5f + 0.03f, boardTop - 0.15f, 0f), true);
            float boardH = boardTop - boardBottom;
            hoop._glass = CourtBuilder.Place(go.transform, "Backboard", MeshFactory.Box(new Vector3(0.04f, boardH, 1.8f)), glass, new Vector3(boardX + 0.02f, boardBottom + boardH * 0.5f, 0f));
            // Frame and the shooter's square.
            CourtBuilder.Place(go.transform, "BoardFrame", MeshFactory.Box(new Vector3(0.05f, 0.05f, 1.8f)), frame, new Vector3(boardX + 0.01f, boardTop, 0f));
            CourtBuilder.Place(go.transform, "BoardFrameLow", MeshFactory.Box(new Vector3(0.05f, 0.05f, 1.8f)), frame, new Vector3(boardX + 0.01f, boardBottom, 0f));
            CourtBuilder.Place(go.transform, "Square", MeshFactory.Box(new Vector3(0.02f, 0.45f, 0.59f)), mats.TransparentInstance(new Color(1f, 1f, 1f, 0.55f)), new Vector3(boardX - 0.005f, rimY + 0.27f, 0f));

            var rimGo = new GameObject("RimPivot");
            rimGo.transform.SetParent(go.transform, false);
            rimGo.transform.localPosition = new Vector3(boardX, rimY, 0f);
            hoop._rim = rimGo.transform;
            CourtBuilder.Place(hoop._rim, "Rim", MeshFactory.Torus(rimR, 0.012f, 28, 8), orange, new Vector3(rimX - boardX, 0f, 0f), true);
            CourtBuilder.Place(hoop._rim, "Bracket", MeshFactory.Box(new Vector3(0.16f, 0.03f, 0.12f)), orange, new Vector3(-0.08f, 0f, 0f));
            var netGo = new GameObject("NetPivot");
            netGo.transform.SetParent(hoop._rim, false);
            netGo.transform.localPosition = new Vector3(rimX - boardX, 0f, 0f);
            hoop._net = netGo.transform;
            CourtBuilder.Place(hoop._net, "Net", MeshFactory.Net(rimR, rimR * 0.62f, 0.42f), mats.VertexColor(new Color(0.96f, 0.96f, 0.96f)), Vector3.zero);
            return hoop;
        }

        public void NetSnap(float strength) => _snap = Mathf.Max(_snap, strength);
        public void RimHit(float strength) => _wobble = Mathf.Max(_wobble, strength);
        public void DunkHang(float seconds) => _dunkHang = Mathf.Max(_dunkHang, seconds);

        public void Shatter(float seconds)
        {
            _shatter = seconds;
            if (_glass != null) _glass.enabled = false;
        }

        private void Update()
        {
            float dt = Time.deltaTime;
            if (_snap > 0f) _snap = Mathf.Max(0f, _snap - dt * 3f);
            if (_wobble > 0f) _wobble = Mathf.Max(0f, _wobble - dt * 2.5f);
            if (_dunkHang > 0f) _dunkHang = Mathf.Max(0f, _dunkHang - dt);
            float stretch = 1f + _snap * 0.45f * Mathf.Sin(_snap * 18f) + _snap * 0.25f;
            _net.localScale = new Vector3(1f - _snap * 0.15f, stretch, 1f - _snap * 0.15f);
            float bend = _dunkHang > 0f ? -9f * Mathf.Clamp01(_dunkHang * 3f) : 0f;
            _rim.localRotation = Quaternion.Euler(Mathf.Sin(Time.time * 45f) * _wobble * 4f, 0f, bend + Mathf.Sin(Time.time * 38f) * _wobble * 3f);
            if (_shatter > 0f)
            {
                _shatter -= dt;
                if (_shatter <= 0f && _glass != null) _glass.enabled = true;
            }
        }
    }
}
