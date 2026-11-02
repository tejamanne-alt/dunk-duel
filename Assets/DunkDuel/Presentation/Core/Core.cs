using System;
using System.Collections.Generic;
using System.Globalization;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>Fixed-point to float conversion. Presentation only: floats never flow back into the sim.</summary>
    public static class FixExtensions
    {
        private const double Scale = 1.0 / 4294967296.0;

        public static float ToFloat(this Fix f) => (float)(f.Raw * Scale);

        public static float Lerp(Fix a, Fix b, float t) => Mathf.LerpUnclamped(a.ToFloat(), b.ToFloat(), t);
    }

    /// <summary>
    /// Sim plane to Unity world: sim X (meters, +X toward the hoop) is world X (screen right), sim Y is world Y,
    /// and world Z is court depth away from the fixed side camera (the play plane is z = 0).
    /// </summary>
    public static class World
    {
        public static Vector3 FromSim(Fix x, Fix y, float z) => new Vector3(x.ToFloat(), y.ToFloat(), z);

        public static Vector3 FromSim(float x, float y, float z) => new Vector3(x, y, z);
    }

    /// <summary>Reads sim tables from Unity Resources (TextAssets under Resources/DunkDuelData).</summary>
    public sealed class ResourcesDataSource : IDataSource
    {
        private readonly string _root;

        public ResourcesDataSource(string root = "DunkDuelData")
        {
            _root = root;
        }

        public string Read(string name)
        {
            TextAsset asset = Resources.Load<TextAsset>(_root + "/" + name);
            return asset != null ? asset.text : null;
        }
    }

    /// <summary>Small helpers over the sim's integer-only JSON DOM for presentation data (floats allowed here).</summary>
    public static class PJson
    {
        public static JsonValue Load(string resourcePath)
        {
            TextAsset asset = Resources.Load<TextAsset>(resourcePath);
            if (asset == null) return null;
            try
            {
                return Json.Parse(asset.text, resourcePath);
            }
            catch (DataException e)
            {
                Debug.LogError("Dunk Duel: bad presentation data " + resourcePath + ": " + e.Message);
                return null;
            }
        }

        public static string Str(JsonValue o, string key, string fallback = "")
        {
            JsonValue v = o?.Get(key);
            return v != null && v.Kind == JsonKind.String ? v.Text : fallback;
        }

        public static float Num(JsonValue o, string key, float fallback = 0f)
        {
            JsonValue v = o?.Get(key);
            if (v == null || (v.Kind != JsonKind.Number && v.Kind != JsonKind.String)) return fallback;
            return float.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : fallback;
        }

        public static int Int(JsonValue o, string key, int fallback = 0)
        {
            JsonValue v = o?.Get(key);
            if (v == null) return fallback;
            return JsonValue.TryParseInt(v.Text, out int i) ? i : (int)Num(o, key, fallback);
        }

        public static bool Bool(JsonValue o, string key, bool fallback = false)
        {
            JsonValue v = o?.Get(key);
            return v != null && v.Kind == JsonKind.Bool ? v.BoolValue : fallback;
        }

        public static Color Col(JsonValue o, string key, Color fallback)
        {
            string s = Str(o, key, null);
            return string.IsNullOrEmpty(s) ? fallback : ParseColor(s, fallback);
        }

        public static Vector3 Vec(JsonValue o, string key, Vector3 fallback)
        {
            JsonValue v = o?.Get(key);
            if (v == null || !v.IsArray || v.Items.Count < 3) return fallback;
            return new Vector3(F(v.Items[0]), F(v.Items[1]), F(v.Items[2]));
        }

        public static List<JsonValue> Arr(JsonValue o, string key)
        {
            JsonValue v = o?.Get(key);
            return v != null && v.IsArray ? v.Items : new List<JsonValue>();
        }

        public static float F(JsonValue v)
        {
            if (v == null) return 0f;
            return float.TryParse(v.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : 0f;
        }

        /// <summary>"#RRGGBB" or "#RRGGBBAA".</summary>
        public static Color ParseColor(string hex, Color fallback)
        {
            if (string.IsNullOrEmpty(hex)) return fallback;
            string h = hex[0] == '#' ? hex.Substring(1) : hex;
            if (h.Length != 6 && h.Length != 8) return fallback;
            if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return fallback;
            if (h.Length == 6) v = (v << 8) | 0xFF;
            return new Color(((v >> 24) & 0xFF) / 255f, ((v >> 16) & 0xFF) / 255f, ((v >> 8) & 0xFF) / 255f, (v & 0xFF) / 255f);
        }
    }

    /// <summary>Deterministic per-instance noise for visuals (no System.Random in presentation either, for repeatable replays).</summary>
    public static class VisualNoise
    {
        public static float Hash01(int a, int b = 0)
        {
            unchecked
            {
                uint h = (uint)a * 0x9E3779B1u ^ (uint)b * 0x85EBCA77u;
                h ^= h >> 15;
                h *= 0x2C1B3C6Du;
                h ^= h >> 12;
                h *= 0x297A2D39u;
                h ^= h >> 15;
                return (h & 0xFFFFFF) / 16777216f;
            }
        }

        public static float Signed(int a, int b = 0) => Hash01(a, b) * 2f - 1f;
    }

    /// <summary>Easing curves used by camera recipes and effects.</summary>
    public static class Ease
    {
        public static float OutCubic(float t)
        {
            t = Mathf.Clamp01(t) - 1f;
            return t * t * t + 1f;
        }

        public static float InOutSine(float t) => -(Mathf.Cos(Mathf.PI * Mathf.Clamp01(t)) - 1f) * 0.5f;

        /// <summary>0 -> 1 -> 0 bump with a fast attack and slow release.</summary>
        public static float Punch(float t, float attack = 0.2f)
        {
            t = Mathf.Clamp01(t);
            if (t < attack) return OutCubic(t / attack);
            return 1f - InOutSine((t - attack) / (1f - attack));
        }
    }

    /// <summary>Thrown by presentation setup when required data is missing; caught by the app and shown to the player.</summary>
    public sealed class PresentationException : Exception
    {
        public PresentationException(string message) : base(message) { }
    }
}
