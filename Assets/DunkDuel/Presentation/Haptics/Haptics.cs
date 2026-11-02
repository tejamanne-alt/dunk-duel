using System.Runtime.InteropServices;
using UnityEngine;

namespace DunkDuel.Presentation
{
    public enum HapticKind : byte
    {
        Tick = 0,     // dribble on a big move, UI
        Light = 1,
        Medium = 2,   // steal, block
        Heavy = 3,    // charge, posterizer, ankle break
        Success = 4,  // denial, finisher landing
        Warning = 5,  // shot clock, low balance
    }

    /// <summary>
    /// Thin haptics facade: Core Haptics on iOS (Plugins/iOS/DunkDuelHaptics), the vibrator on Android
    /// (Plugins/Android/DunkDuelHaptics.kt), and a no-op everywhere else. Also exposes the OS thermal state
    /// for the quality manager.
    /// </summary>
    public static class Haptics
    {
        public static bool Enabled = true;
        public static float Intensity = 1f;

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")] private static extern void DDHaptics_Prepare();
        [DllImport("__Internal")] private static extern void DDHaptics_Play(int kind, float intensity);
        [DllImport("__Internal")] private static extern int DDHaptics_ThermalState();
#endif

#if UNITY_ANDROID && !UNITY_EDITOR
        private static AndroidJavaClass _android;
        private static AndroidJavaObject _activity;
#endif

        private static bool _prepared;

        public static void Prepare()
        {
            if (_prepared) return;
            _prepared = true;
#if UNITY_IOS && !UNITY_EDITOR
            DDHaptics_Prepare();
#elif UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                _android = new AndroidJavaClass("com.dunkduel.haptics.DunkDuelHaptics");
                using (var player = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
                {
                    _activity = player.GetStatic<AndroidJavaObject>("currentActivity");
                }
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("Dunk Duel haptics unavailable: " + e.Message);
                _android = null;
            }
#endif
        }

        public static void Play(HapticKind kind, float scale = 1f)
        {
            if (!Enabled || Intensity <= 0f) return;
            if (!_prepared) Prepare();
            float intensity = Mathf.Clamp01(Intensity * scale);
#if UNITY_IOS && !UNITY_EDITOR
            DDHaptics_Play((int)kind, intensity);
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (_android != null && _activity != null)
            {
                try
                {
                    _android.CallStatic("play", _activity, (int)kind, intensity);
                }
                catch (System.Exception)
                {
                    _android = null;
                }
            }
#endif
        }

        /// <summary>0 nominal, 1 fair, 2 serious, 3 critical; -1 when unknown.</summary>
        public static int ThermalState()
        {
#if UNITY_IOS && !UNITY_EDITOR
            return DDHaptics_ThermalState();
#elif UNITY_ANDROID && !UNITY_EDITOR
            if (_android == null || _activity == null) return -1;
            try
            {
                return _android.CallStatic<int>("thermalState", _activity);
            }
            catch (System.Exception)
            {
                return -1;
            }
#else
            return -1;
#endif
        }
    }
}
