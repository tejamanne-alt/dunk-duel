using System;
using UnityEngine;
#if DD_URP
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
#endif

namespace DunkDuel.Presentation
{
    public enum QualityTier : byte { Low = 0, Mid = 1, High = 2 }

    public enum QualityMode : byte { Auto = 0, Low = 1, Mid = 2, High = 3 }

    /// <summary>What a tier allows. Read by court, crowd and effect builders.</summary>
    public struct QualityProfile
    {
        public QualityTier Tier;
        public float CrowdScale;
        public float EffectsScale;
        public bool FighterShadows;
        public bool FighterLights;
        public int Msaa;
        public float RenderScale;
        public float MinRenderScale;
        public bool SetPieces;
        public bool PostFx;

        public static QualityProfile For(QualityTier t)
        {
            switch (t)
            {
                case QualityTier.High:
                    return new QualityProfile { Tier = t, CrowdScale = 1f, EffectsScale = 1f, FighterShadows = true, FighterLights = true, Msaa = 4, RenderScale = 1f, MinRenderScale = 0.8f, SetPieces = true, PostFx = true };
                case QualityTier.Mid:
                    return new QualityProfile { Tier = t, CrowdScale = 0.6f, EffectsScale = 0.7f, FighterShadows = true, FighterLights = true, Msaa = 2, RenderScale = 0.9f, MinRenderScale = 0.7f, SetPieces = true, PostFx = false };
                default:
                    return new QualityProfile { Tier = t, CrowdScale = 0.3f, EffectsScale = 0.45f, FighterShadows = false, FighterLights = false, Msaa = 1, RenderScale = 0.8f, MinRenderScale = 0.6f, SetPieces = false, PostFx = false };
            }
        }
    }

    /// <summary>
    /// High / Mid / Low presets with a device heuristic, dynamic resolution (URP render scale) driven by frame
    /// time, and a thermal monitor that steps the tier down when the device heats up.
    /// </summary>
    public sealed class QualityManager : MonoBehaviour
    {
        public const float TargetFrameMs = 16.6f;

        public QualityMode Mode = QualityMode.Auto;
        public QualityProfile Profile { get; private set; }
        public float CurrentRenderScale { get; private set; } = 1f;
        public float SmoothedFrameMs { get; private set; } = TargetFrameMs;
        public int ThermalLevel { get; private set; }

        /// <summary>Raised when the tier changes so views can rebuild density-dependent content.</summary>
        public event Action<QualityProfile> Changed;

        /// <summary>Thermal probe provided by the haptics plugin (0 nominal .. 3 critical); null = unknown.</summary>
        public Func<int> ThermalProbe;

        private float _overBudgetSeconds;
        private float _underBudgetSeconds;
        private float _thermalTimer;
        private bool _thermalDowngraded;
#if DD_URP
        private float _originalRenderScale = -1f;
        private int _originalMsaa = -1;
#endif

        private void Awake()
        {
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            Apply(Mode);
        }

        public static QualityTier DetectTier()
        {
            int memMb = SystemInfo.systemMemorySize;
            int cores = SystemInfo.processorCount;
            int gpuMb = SystemInfo.graphicsMemorySize;
            if (!Application.isMobilePlatform) return QualityTier.High;
            if (memMb >= 6000 && cores >= 6) return QualityTier.High;
            if (memMb >= 3500 && cores >= 6 && gpuMb >= 1000) return QualityTier.Mid;
            if (memMb >= 3500) return QualityTier.Mid;
            return QualityTier.Low;
        }

        public void Apply(QualityMode mode)
        {
            Mode = mode;
            QualityTier tier = mode == QualityMode.Auto ? DetectTier() : (QualityTier)((int)mode - 1);
            if (_thermalDowngraded && tier > QualityTier.Low) tier = (QualityTier)((int)tier - 1);
            SetTier(tier);
        }

        private void SetTier(QualityTier tier)
        {
            Profile = QualityProfile.For(tier);
            int level = FindLevel(tier);
            if (level >= 0 && level != QualitySettings.GetQualityLevel()) QualitySettings.SetQualityLevel(level, true);
            CurrentRenderScale = Profile.RenderScale;
            ApplyPipeline();
            Changed?.Invoke(Profile);
        }

        private static int FindLevel(QualityTier tier)
        {
            string[] names = QualitySettings.names;
            string want = tier.ToString();
            for (int i = 0; i < names.Length; i++) if (string.Equals(names[i], want, StringComparison.OrdinalIgnoreCase)) return i;
            if (names.Length == 0) return -1;
            // Fall back to spreading tiers across whatever levels exist.
            return Mathf.Clamp(Mathf.RoundToInt((int)tier / 2f * (names.Length - 1)), 0, names.Length - 1);
        }

        private void ApplyPipeline()
        {
#if DD_URP
            if (GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                if (_originalRenderScale < 0f)
                {
                    _originalRenderScale = urp.renderScale;
                    _originalMsaa = urp.msaaSampleCount;
                }
                urp.renderScale = CurrentRenderScale;
                urp.msaaSampleCount = Profile.Msaa;
            }
#endif
        }

        private void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            SmoothedFrameMs = Mathf.Lerp(SmoothedFrameMs, ms, 0.05f);

            // Dynamic resolution: step the render scale down after sustained overruns, back up after headroom.
            if (SmoothedFrameMs > TargetFrameMs * 1.08f)
            {
                _overBudgetSeconds += Time.unscaledDeltaTime;
                _underBudgetSeconds = 0f;
            }
            else if (SmoothedFrameMs < TargetFrameMs * 0.8f)
            {
                _underBudgetSeconds += Time.unscaledDeltaTime;
                _overBudgetSeconds = 0f;
            }
            if (_overBudgetSeconds > 2f && CurrentRenderScale > Profile.MinRenderScale)
            {
                CurrentRenderScale = Mathf.Max(Profile.MinRenderScale, CurrentRenderScale - 0.05f);
                _overBudgetSeconds = 0f;
                ApplyPipeline();
            }
            else if (_underBudgetSeconds > 8f && CurrentRenderScale < Profile.RenderScale)
            {
                CurrentRenderScale = Mathf.Min(Profile.RenderScale, CurrentRenderScale + 0.05f);
                _underBudgetSeconds = 0f;
                ApplyPipeline();
            }

            // Thermal monitor (polled every 5 s).
            _thermalTimer += Time.unscaledDeltaTime;
            if (_thermalTimer >= 5f && ThermalProbe != null)
            {
                _thermalTimer = 0f;
                ThermalLevel = ThermalProbe();
                if (ThermalLevel >= 2 && !_thermalDowngraded && Profile.Tier > QualityTier.Low)
                {
                    _thermalDowngraded = true;
                    SetTier((QualityTier)((int)Profile.Tier - 1));
                }
                else if (ThermalLevel == 0 && _thermalDowngraded)
                {
                    _thermalDowngraded = false;
                    Apply(Mode);
                }
            }
        }

        private void OnDestroy()
        {
#if DD_URP && UNITY_EDITOR
            // Do not leave play-mode changes in the URP asset on disk.
            if (_originalRenderScale > 0f && GraphicsSettings.currentRenderPipeline is UniversalRenderPipelineAsset urp)
            {
                urp.renderScale = _originalRenderScale;
                urp.msaaSampleCount = _originalMsaa;
            }
#endif
        }
    }
}
