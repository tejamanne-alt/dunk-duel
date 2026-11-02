using UnityEngine;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Runtime-synthesized placeholder audio (no imported sounds): ball, rim, net, buzzer, crowd, shoes and
    /// simple layered music stems per court style. Replaced by authored audio during content production.
    /// </summary>
    public static class SynthClips
    {
        public const int Rate = 22050;

        private static float Noise(ref uint s)
        {
            unchecked
            {
                s ^= s << 13;
                s ^= s >> 17;
                s ^= s << 5;
                return (s & 0xFFFFFF) / 8388608f - 1f;
            }
        }

        private static AudioClip Make(string name, float[] data, int channels = 1)
        {
            var clip = AudioClip.Create(name, data.Length / channels, channels, Rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        private static float[] Buffer(float seconds) => new float[Mathf.CeilToInt(seconds * Rate)];

        public static AudioClip Dribble()
        {
            float[] d = Buffer(0.12f);
            uint s = 1;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Exp(-t * 45f);
                float body = Mathf.Sin(2f * Mathf.PI * (95f - t * 200f) * t);
                float slap = Noise(ref s) * Mathf.Exp(-t * 220f) * 0.5f;
                d[i] = (body * 0.8f + slap) * env;
            }
            return Make("dribble", d);
        }

        public static AudioClip Squeak(float pitch = 1f)
        {
            float[] d = Buffer(0.1f);
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float f = (2100f + 1600f * t / 0.1f) * pitch;
                float env = Mathf.Sin(Mathf.PI * t / 0.1f);
                d[i] = Mathf.Sin(2f * Mathf.PI * f * t) * env * 0.35f * (0.7f + 0.3f * Mathf.Sin(2f * Mathf.PI * 60f * t));
            }
            return Make("squeak", d);
        }

        public static AudioClip Swish()
        {
            float[] d = Buffer(0.38f);
            uint s = 7;
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Sin(Mathf.PI * Mathf.Clamp01(t / 0.38f)) * Mathf.Exp(-t * 4f);
                lp += (Noise(ref s) - lp) * 0.35f;
                d[i] = lp * env * 0.9f;
            }
            return Make("swish", d);
        }

        public static AudioClip NetSnap()
        {
            float[] d = Buffer(0.14f);
            uint s = 11;
            float prev = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float n = Noise(ref s);
                float hp = n - prev;
                prev = n;
                d[i] = hp * Mathf.Exp(-t * 35f) * 0.8f;
            }
            return Make("netsnap", d);
        }

        public static AudioClip Rim()
        {
            float[] d = Buffer(0.7f);
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                d[i] = (Mathf.Sin(2f * Mathf.PI * 523f * t) * Mathf.Exp(-t * 6f) * 0.5f
                        + Mathf.Sin(2f * Mathf.PI * 1318f * t) * Mathf.Exp(-t * 9f) * 0.3f
                        + Mathf.Sin(2f * Mathf.PI * 2210f * t) * Mathf.Exp(-t * 14f) * 0.2f) * 0.8f;
            }
            return Make("rim", d);
        }

        public static AudioClip Backboard()
        {
            float[] d = Buffer(0.35f);
            uint s = 17;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                d[i] = (Mathf.Sin(2f * Mathf.PI * 82f * t) * Mathf.Exp(-t * 18f) + Noise(ref s) * Mathf.Exp(-t * 30f) * 0.4f) * 0.8f;
            }
            return Make("backboard", d);
        }

        public static AudioClip Buzzer()
        {
            float[] d = Buffer(0.9f);
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float sq = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * (400f + 4f * Mathf.Sin(t * 30f)) * t));
                float env = Mathf.Clamp01(t * 60f) * Mathf.Clamp01((0.9f - t) * 20f);
                d[i] = sq * env * 0.25f;
            }
            return Make("buzzer", d);
        }

        public static AudioClip Whoosh()
        {
            float[] d = Buffer(0.22f);
            uint s = 23;
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float k = 0.05f + 0.5f * Mathf.Sin(Mathf.PI * t / 0.22f);
                lp += (Noise(ref s) - lp) * k;
                d[i] = lp * Mathf.Sin(Mathf.PI * t / 0.22f) * 0.7f;
            }
            return Make("whoosh", d);
        }

        public static AudioClip Thud()
        {
            float[] d = Buffer(0.3f);
            uint s = 29;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                d[i] = (Mathf.Sin(2f * Mathf.PI * (60f - 40f * t) * t) + Noise(ref s) * 0.25f) * Mathf.Exp(-t * 14f) * 0.9f;
            }
            return Make("thud", d);
        }

        public static AudioClip Cheer(float seconds = 1.8f, uint seed = 31)
        {
            float[] d = Buffer(seconds);
            uint s = seed;
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float env = Mathf.Clamp01(t * 4f) * Mathf.Clamp01((seconds - t) * 1.2f);
                lp += (Noise(ref s) - lp) * 0.25f;
                lp2 += (lp - lp2) * 0.4f;
                float shimmer = 0.75f + 0.25f * Mathf.Sin(2f * Mathf.PI * 7f * t + Mathf.Sin(t * 3f));
                d[i] = lp2 * env * shimmer * 1.6f;
            }
            return Make("cheer", d);
        }

        public static AudioClip Groan()
        {
            float[] d = Buffer(1.0f);
            uint s = 37;
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                lp += (Noise(ref s) - lp) * 0.12f;
                float tone = Mathf.Sin(2f * Mathf.PI * (240f - 110f * t) * t) * 0.3f;
                d[i] = (lp * 1.5f + tone) * Mathf.Sin(Mathf.PI * t) * 0.7f;
            }
            return Make("groan", d);
        }

        /// <summary>Seamless crowd bed loop (filtered noise with slow swells).</summary>
        public static AudioClip CrowdBed()
        {
            float[] d = Buffer(4f);
            uint s = 41;
            float lp = 0f, lp2 = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                lp += (Noise(ref s) - lp) * 0.2f;
                lp2 += (lp - lp2) * 0.3f;
                float swell = 0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * t / 4f);
                d[i] = lp2 * swell * 1.4f;
            }
            // Cross-fade the loop seam.
            int fade = Rate / 10;
            for (int i = 0; i < fade; i++)
            {
                float w = i / (float)fade;
                d[i] = d[i] * w + d[d.Length - fade + i] * (1f - w);
            }
            return Make("crowdbed", d);
        }

        public static AudioClip Rumble(float seconds, float clack)
        {
            float[] d = Buffer(seconds);
            uint s = 43;
            float lp = 0f;
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                lp += (Noise(ref s) - lp) * 0.04f;
                float env = Mathf.Sin(Mathf.PI * t / seconds);
                float c = clack > 0f && Mathf.Repeat(t * clack, 1f) < 0.03f ? Noise(ref s) * 0.6f : 0f;
                d[i] = (lp * 3f + c) * env * 0.8f;
            }
            return Make("rumble", d);
        }

        /// <summary>Short chord stinger (finishers, set pieces). <paramref name="style"/> picks the timbre.</summary>
        public static AudioClip Stinger(string style)
        {
            float[] d = Buffer(1.3f);
            float[] notes = { 261.6f, 329.6f, 392f, 523.3f };
            for (int i = 0; i < d.Length; i++)
            {
                float t = i / (float)Rate;
                float v = 0f;
                for (int n = 0; n < notes.Length; n++) v += Voice(style, notes[n], t);
                float env = Mathf.Clamp01(t * 40f) * Mathf.Exp(-t * 2.2f);
                d[i] = v * env * 0.18f;
            }
            return Make("stinger_" + style, d);
        }

        private static float Voice(string style, float f, float t)
        {
            float ph = 2f * Mathf.PI * f * t;
            switch (style)
            {
                case "organ": return Mathf.Sin(ph) + 0.5f * Mathf.Sin(ph * 2f) + 0.25f * Mathf.Sin(ph * 4f);
                case "brass": return Saw(f * t) * (0.6f + 0.4f * Mathf.Sin(t * 30f));
                case "electro": return Mathf.Sign(Mathf.Sin(ph)) * 0.6f;
                default: return Mathf.Sin(ph) + 0.3f * Saw(f * t);
            }
        }

        private static float Saw(float x) => 2f * (x - Mathf.Floor(x + 0.5f));

        /// <summary>
        /// Four-bar music stems for a court style at a tempo: 0 = drums, 1 = bass, 2 = pad/lead. Layered by Stage Heat.
        /// </summary>
        public static AudioClip[] MusicStems(string style, float bpm)
        {
            float beat = 60f / Mathf.Max(60f, bpm);
            float bars = 4f;
            float seconds = beat * 4f * bars;
            var drums = Buffer(seconds);
            var bass = Buffer(seconds);
            var pad = Buffer(seconds);
            uint s = 53;
            float[] roots = style == "dhol" || style == "storm" ? new[] { 110f, 110f, 98f, 123.5f } : new[] { 110f, 87.3f, 98f, 82.4f };
            for (int i = 0; i < drums.Length; i++)
            {
                float t = i / (float)Rate;
                float b = t / beat;                  // beats elapsed
                float inBeat = (b - Mathf.Floor(b)) * beat; // seconds into the beat
                int beatIndex = Mathf.FloorToInt(b) % 4;
                float half = (b * 2f - Mathf.Floor(b * 2f)) * beat * 0.5f;
                float kick = 0f, snare = 0f, hat = 0f;
                switch (style)
                {
                    case "dhol":
                        kick = (beatIndex == 0 || beatIndex == 2 || (beatIndex == 3 && inBeat > beat * 0.5f)) ? Mathf.Sin(2f * Mathf.PI * 70f * inBeat) * Mathf.Exp(-inBeat * 14f) : 0f;
                        snare = Mathf.Sin(2f * Mathf.PI * 240f * half) * Mathf.Exp(-half * 30f) * 0.5f;
                        break;
                    case "electro":
                    case "anthem":
                        kick = Mathf.Sin(2f * Mathf.PI * (55f + 80f * Mathf.Exp(-inBeat * 30f)) * inBeat) * Mathf.Exp(-inBeat * 9f);
                        hat = Noise(ref s) * Mathf.Exp(-half * 60f) * 0.25f;
                        snare = beatIndex % 2 == 1 ? Noise(ref s) * Mathf.Exp(-inBeat * 20f) * 0.45f : 0f;
                        break;
                    default: // boom-bap, brass, organ, surf, marching, storm
                        kick = (beatIndex == 0 || (beatIndex == 2 && inBeat < beat * 0.6f)) ? Mathf.Sin(2f * Mathf.PI * 60f * inBeat) * Mathf.Exp(-inBeat * 12f) : 0f;
                        snare = beatIndex % 2 == 1 ? Noise(ref s) * Mathf.Exp(-inBeat * 18f) * 0.5f : 0f;
                        hat = Noise(ref s) * Mathf.Exp(-half * 80f) * 0.15f;
                        break;
                }
                drums[i] = (kick + snare + hat) * 0.55f;

                int bar = Mathf.FloorToInt(b / 4f) % 4;
                float root = roots[bar];
                float bassEnv = Mathf.Exp(-inBeat * 5f);
                bass[i] = Mathf.Sin(2f * Mathf.PI * root * t) * bassEnv * 0.35f;
                float chord = Voice(style == "organ" || style == "brass" || style == "electro" ? style : "pad", root * 2f, t)
                    + Voice(style == "organ" || style == "brass" || style == "electro" ? style : "pad", root * 2.5f, t)
                    + Voice(style == "organ" || style == "brass" || style == "electro" ? style : "pad", root * 3f, t);
                pad[i] = chord * 0.05f * (0.8f + 0.2f * Mathf.Sin(2f * Mathf.PI * t / (beat * 8f)));
            }
            return new[] { Make("stem_drums_" + style, drums), Make("stem_bass_" + style, bass), Make("stem_pad_" + style, pad) };
        }
    }
}
