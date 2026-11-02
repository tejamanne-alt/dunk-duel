using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Creates and caches graybox materials. Prefers the template materials that ProjectSetup writes to
    /// Resources/DunkDuelPresentation/Materials (so the shaders are guaranteed to be in player builds), and falls
    /// back to finding URP or built-in shaders by name.
    /// </summary>
    public sealed class MaterialLibrary
    {
        private const string TemplateRoot = "DunkDuelPresentation/Materials/";

        private readonly Dictionary<string, Material> _cache = new Dictionary<string, Material>();
        private readonly Material _litTemplate;
        private readonly Material _unlitTemplate;
        private readonly Material _transparentTemplate;
        private readonly Material _additiveTemplate;
        private readonly Material _vertexColorTemplate;

        public bool IsUrp { get; }

        public MaterialLibrary()
        {
            IsUrp = GraphicsSettings.currentRenderPipeline != null;
            _litTemplate = LoadTemplate("Lit", IsUrp ? "Universal Render Pipeline/Lit" : "Standard", false, false);
            _unlitTemplate = LoadTemplate("Unlit", IsUrp ? "Universal Render Pipeline/Unlit" : "Unlit/Color", false, false);
            _transparentTemplate = LoadTemplate("Transparent", IsUrp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", true, false);
            _additiveTemplate = LoadTemplate("Additive", IsUrp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", true, true);
            _vertexColorTemplate = LoadTemplate("VertexColor", IsUrp ? "Universal Render Pipeline/Particles/Unlit" : "Sprites/Default", false, false);
        }

        private Material LoadTemplate(string name, string shaderName, bool transparent, bool additive)
        {
            Material m = Resources.Load<Material>(TemplateRoot + name);
            if (m != null) return m;
            Shader shader = Shader.Find(shaderName);
            if (shader == null) shader = Shader.Find(IsUrp ? "Universal Render Pipeline/Unlit" : "Unlit/Color");
            if (shader == null) shader = Shader.Find("Hidden/InternalErrorShader");
            m = new Material(shader) { name = "DD_" + name };
            if (transparent) MakeTransparent(m, additive);
            return m;
        }

        /// <summary>URP surface switch to transparent (alpha or additive); harmless on built-in shaders.</summary>
        public static void MakeTransparent(Material m, bool additive)
        {
            if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
            if (m.HasProperty("_Blend")) m.SetFloat("_Blend", additive ? 2f : 0f);
            m.SetOverrideTag("RenderType", "Transparent");
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", additive ? (float)BlendMode.One : (float)BlendMode.OneMinusSrcAlpha);
            if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 0f);
            m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
            m.renderQueue = (int)RenderQueue.Transparent;
        }

        public static void SetColor(Material m, Color c)
        {
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
            if (m.HasProperty("_Color")) m.SetColor("_Color", c);
        }

        public static void SetSmoothness(Material m, float s)
        {
            if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", s);
            if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", s);
        }

        public static void SetEmission(Material m, Color c)
        {
            if (!m.HasProperty("_EmissionColor")) return;
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", c);
        }

        private static string Key(string kind, Color c, float a, float b) =>
            kind + ColorUtility.ToHtmlStringRGBA(c) + "_" + Mathf.RoundToInt(a * 100f) + "_" + Mathf.RoundToInt(b * 100f);

        /// <summary>Shared lit material (cached by color/smoothness/metallic). Do not modify the returned instance.</summary>
        public Material Lit(Color c, float smoothness = 0.25f, float metallic = 0f)
        {
            string key = Key("lit", c, smoothness, metallic);
            if (_cache.TryGetValue(key, out Material m)) return m;
            m = new Material(_litTemplate) { name = "Lit_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            SetColor(m, c);
            SetSmoothness(m, smoothness);
            if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
            _cache[key] = m;
            return m;
        }

        /// <summary>A new, unshared lit material the caller may animate (sweat sheen, flashes).</summary>
        public Material LitInstance(Color c, float smoothness = 0.25f)
        {
            var m = new Material(_litTemplate) { name = "LitInst" };
            SetColor(m, c);
            SetSmoothness(m, smoothness);
            return m;
        }

        public Material Unlit(Color c)
        {
            string key = Key("unlit", c, 0, 0);
            if (_cache.TryGetValue(key, out Material m)) return m;
            m = new Material(_unlitTemplate) { name = "Unlit_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            SetColor(m, c);
            _cache[key] = m;
            return m;
        }

        public Material Emissive(Color c, float intensity)
        {
            string key = Key("emis", c, intensity, 0);
            if (_cache.TryGetValue(key, out Material m)) return m;
            m = new Material(_litTemplate) { name = "Emissive_" + ColorUtility.ToHtmlStringRGB(c), enableInstancing = true };
            SetColor(m, c);
            SetEmission(m, c * intensity);
            _cache[key] = m;
            return m;
        }

        /// <summary>Unshared transparent unlit material (effects fade their own instance).</summary>
        public Material TransparentInstance(Color c, bool additive = false)
        {
            var m = new Material(additive ? _additiveTemplate : _transparentTemplate) { name = additive ? "FxAdd" : "FxAlpha" };
            SetColor(m, c);
            return m;
        }

        /// <summary>Unlit material that multiplies vertex colors (gradient backdrop cards, net pattern).</summary>
        public Material VertexColor(Color tint)
        {
            string key = Key("vcol", tint, 0, 0);
            if (_cache.TryGetValue(key, out Material m)) return m;
            m = new Material(_vertexColorTemplate) { name = "VertexColor" };
            SetColor(m, tint);
            _cache[key] = m;
            return m;
        }

        public Material Textured(Texture2D tex, Color tint, float smoothness = 0.2f)
        {
            var m = new Material(_litTemplate) { name = "Tex_" + tex.name, mainTexture = tex };
            SetColor(m, tint);
            SetSmoothness(m, smoothness);
            return m;
        }
    }

    /// <summary>Tiny procedural textures (stripes, seams, noise) so the graybox needs no imported art.</summary>
    public static class TextureFactory
    {
        public static Texture2D Stripes(Color[] bands, int size = 64, bool horizontal = true)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "Stripes", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    int t = horizontal ? y : x;
                    px[y * size + x] = bands[(t * bands.Length / size) % bands.Length];
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>Ball leather with dark seams (two great circles and two curves).</summary>
        public static Texture2D BallSkin(Color leather, Color seam, int size = 128)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, true) { name = "BallSkin", wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                float v = y / (float)size;
                for (int x = 0; x < size; x++)
                {
                    float u = x / (float)size;
                    float grain = 0.92f + 0.08f * VisualNoise.Hash01(x, y);
                    bool line = Mathf.Abs(u - 0.25f) < 0.012f || Mathf.Abs(u - 0.75f) < 0.012f || Mathf.Abs(v - 0.5f) < 0.012f
                        || Mathf.Abs(u - (0.5f + 0.18f * Mathf.Sin(v * Mathf.PI * 2f))) < 0.01f;
                    px[y * size + x] = line ? seam : leather * grain;
                }
            }
            tex.SetPixels32(px);
            tex.Apply(true);
            return tex;
        }

        /// <summary>Soft round dot with alpha falloff (particles, lights, glows).</summary>
        public static Texture2D SoftDot(int size = 64)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "SoftDot", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            var px = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), new Vector2(r, r)) / r;
                    float a = Mathf.Clamp01(1f - d);
                    px[y * size + x] = new Color(1f, 1f, 1f, a * a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return tex;
        }
    }
}
