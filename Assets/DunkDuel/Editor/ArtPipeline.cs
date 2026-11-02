using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;
using UnityEngine.Rendering;
#if DD_ADDRESSABLES
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
#endif

namespace DunkDuel.Editor
{
    // =====================================================================================================
    // Conventions: a C# mirror of Tools/Blender/dd_conventions.py (the single source of truth). Keep in sync.
    // =====================================================================================================

    public enum ArtKind { None, Fighter, Court, SharedStage, Crowd, Unknown }

    public enum ArtSeverity { Error, Warning, Info }

    /// <summary>A parsed CH_&lt;Fighter&gt;_&lt;Part&gt;[_LODn] or ST_&lt;Court&gt;_&lt;Prop&gt;[_LODn] name.</summary>
    public struct ArtName
    {
        public string Raw;
        public string Family;   // "CH" or "ST"
        public string Owner;    // fighter or court token
        public string Part;
        public int Lod;         // -1 when the name has no LOD suffix
        public string Base => Family + "_" + Owner + "_" + Part;
    }

    public static class ArtConventions
    {
        public const string ArtRoot = "Assets/DunkDuel/Art";
        public const string FightersRoot = ArtRoot + "/Fighters";
        public const string CourtsRoot = ArtRoot + "/Courts";
        public const string SharedStageDir = CourtsRoot + "/shared";
        public const string CrowdDir = ArtRoot + "/Crowd";
        public const string SharedToken = "Shared";
        public const int MaxLod = 2;
        public const int TextureMax = 2048;
        public const string GrayboxSuffix = "_Graybox";
        /// <summary>An importer whose userData contains this keeps hand-made settings (the pipeline only validates).</summary>
        public const string ManualTag = "dd:manual";

        public static readonly string[] FighterIds = { "kofi", "meera", "juno", "matas" };
        public static readonly string[] FighterTokens = { "Kofi", "Meera", "Juno", "Matas" };
        public static readonly string[] CourtIds = { "monsoon_bazaar", "the_tracks", "concrete_coast", "rivalry_night", "lights_out", "campus_quad", "old_barn", "glass_court", "the_crown" };
        public static readonly string[] CourtTokens = { "MonsoonBazaar", "TheTracks", "ConcreteCoast", "RivalryNight", "LightsOut", "CampusQuad", "OldBarn", "GlassCourt", "TheCrown" };
        /// <summary>Courts in the base install (the design's three-court, 200 MB budget); the rest download on demand.</summary>
        public static readonly string[] BaseInstallCourts = { "monsoon_bazaar", "the_tracks", "concrete_coast" };

        public const string LayerPlay = "PlayPlane";
        public const string LayerMid = "MidGround";
        public const string LayerBackdrop = "Backdrop";
        public const string LayerCrowd = "Crowd";
        public static readonly string[] Layers = { LayerPlay, LayerMid, LayerBackdrop, LayerCrowd };

        public static readonly int[] FighterMaxTris = { 60000, 30000, 12000 };
        public const int FighterMinTrisLod0 = 40000;
        public const int CourtTotalMaxTris = 180000;
        public const int CrowdInstanceMaxTris = 1500;
        public const float LodStepMaxFraction = 0.75f;
        public const int MaxDeformBones = 90;

        public static readonly string[] RequiredSlots = { "BaseColor", "Normal", "ORM" };
        public static readonly string[] TextureExtensions = { ".png", ".tga", ".exr", ".tif", ".tiff" };

        public static readonly string[] RequiredBones =
        {
            "Root", "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head",
            "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
            "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes",
        };

        private static readonly Regex NameRe = new Regex(@"^(?<family>CH|ST)_(?<owner>[A-Z][A-Za-z0-9]*)_(?<part>[A-Z][A-Za-z0-9]*(?:_[A-Z][A-Za-z0-9]*)*)$");
        private static readonly Regex LodRe = new Regex(@"^(?<base>.+)_LOD(?<lod>[0-9]+)$");
        private static readonly Regex LowerLodRe = new Regex(@"_lod\d+$", RegexOptions.IgnoreCase);
        private static readonly Regex DupRe = new Regex(@"\.\d{3}$");
        private static readonly Regex TexRe = new Regex(@"^(?<set>.+)_(?<slot>BaseColor|Normal|ORM|Emission)$");

        public static int LayerPropMax(string layer)
        {
            switch (layer)
            {
                case LayerMid: return 6000;
                case LayerBackdrop: return 200;
                case LayerCrowd: return 10000;
                default: return 20000;
            }
        }

        public static int LayerMax(string layer)
        {
            switch (layer)
            {
                case LayerMid: return 40000;
                case LayerBackdrop: return 2000;
                case LayerCrowd: return 30000;
                default: return 120000;
            }
        }

        /// <summary>Parses an asset name; returns false with a human-readable reason when it breaks the convention.</summary>
        public static bool TryParse(string name, out ArtName result, out string error)
        {
            result = default;
            error = null;
            if (DupRe.IsMatch(name))
            {
                error = "'" + name + "': Blender duplicate suffix (.001); rename the object";
                return false;
            }
            int lod = -1;
            string core = name;
            Match m = LodRe.Match(name);
            if (m.Success)
            {
                lod = int.Parse(m.Groups["lod"].Value);
                core = m.Groups["base"].Value;
                if (lod > MaxLod)
                {
                    error = "'" + name + "': LOD" + lod + " is beyond _LOD" + MaxLod;
                    return false;
                }
            }
            else if (LowerLodRe.IsMatch(name))
            {
                error = "'" + name + "': LOD suffix must be upper case, e.g. _LOD1";
                return false;
            }
            Match nm = NameRe.Match(core);
            if (!nm.Success)
            {
                error = "'" + name + "': expected CH_<Fighter>_<Part> or ST_<Court>_<Prop> (CamelCase tokens)";
                return false;
            }
            string family = nm.Groups["family"].Value;
            string owner = nm.Groups["owner"].Value;
            if (family == "CH" && Array.IndexOf(FighterTokens, owner) < 0)
            {
                error = "'" + name + "': unknown fighter '" + owner + "' (known: " + string.Join(", ", FighterTokens) + ")";
                return false;
            }
            if (family == "ST" && owner != SharedToken && Array.IndexOf(CourtTokens, owner) < 0)
            {
                error = "'" + name + "': unknown court '" + owner + "' (known: " + string.Join(", ", CourtTokens) + ", " + SharedToken + ")";
                return false;
            }
            result = new ArtName { Raw = name, Family = family, Owner = owner, Part = nm.Groups["part"].Value, Lod = lod };
            return true;
        }

        public static bool TryParseTexture(string stem, out string set, out string slot)
        {
            Match m = TexRe.Match(stem);
            set = m.Success ? m.Groups["set"].Value : null;
            slot = m.Success ? m.Groups["slot"].Value : null;
            return m.Success;
        }

        public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

        public static bool IsArt(string assetPath) => assetPath != null && assetPath.Replace('\\', '/').StartsWith(ArtRoot + "/", StringComparison.Ordinal);

        /// <summary>Which art folder a path is in, and the fighter or court id that owns it.</summary>
        public static ArtKind Locate(string assetPath, out string ownerId)
        {
            ownerId = null;
            if (!IsArt(assetPath)) return ArtKind.None;
            string p = assetPath.Replace('\\', '/');
            if (p.StartsWith(CrowdDir + "/", StringComparison.Ordinal)) return ArtKind.Crowd;
            if (p.StartsWith(SharedStageDir + "/", StringComparison.Ordinal)) return ArtKind.SharedStage;
            if (p.StartsWith(FightersRoot + "/", StringComparison.Ordinal))
            {
                ownerId = FirstSegment(p.Substring(FightersRoot.Length + 1));
                return Array.IndexOf(FighterIds, ownerId) >= 0 ? ArtKind.Fighter : ArtKind.Unknown;
            }
            if (p.StartsWith(CourtsRoot + "/", StringComparison.Ordinal))
            {
                ownerId = FirstSegment(p.Substring(CourtsRoot.Length + 1));
                return Array.IndexOf(CourtIds, ownerId) >= 0 ? ArtKind.Court : ArtKind.Unknown;
            }
            return ArtKind.Unknown;
        }

        private static string FirstSegment(string rest)
        {
            int slash = rest.IndexOf('/');
            return slash < 0 ? rest : rest.Substring(0, slash);
        }

        /// <summary>The name token an asset in this folder must use (Kofi, TheTracks, Shared), or null.</summary>
        public static string ExpectedToken(ArtKind kind, string ownerId)
        {
            switch (kind)
            {
                case ArtKind.Fighter: return FighterTokens[Array.IndexOf(FighterIds, ownerId)];
                case ArtKind.Court: return CourtTokens[Array.IndexOf(CourtIds, ownerId)];
                case ArtKind.SharedStage:
                case ArtKind.Crowd: return SharedToken;
                default: return null;
            }
        }

        public static bool IsManual(AssetImporter importer) => importer != null && !string.IsNullOrEmpty(importer.userData) && importer.userData.Contains(ManualTag);

        /// <summary>VAT textures from Tools/Blender/dd_vat_bake.py (&lt;name&gt;_pos.exr / _nrm.exr).</summary>
        public static bool IsVatTexture(string assetPath)
        {
            string stem = Path.GetFileNameWithoutExtension(assetPath);
            return assetPath.EndsWith(".exr", StringComparison.OrdinalIgnoreCase) && (stem.EndsWith("_pos", StringComparison.Ordinal) || stem.EndsWith("_nrm", StringComparison.Ordinal));
        }
    }

    // =====================================================================================================
    // Court layout manifests written by dd_export.py (ST_<Court>_layout.json next to the court's FBX files).
    // =====================================================================================================

    [Serializable]
    public sealed class CourtLayout
    {
        public string court;
        public string token;
        public CourtLayoutProp[] props;

        public static CourtLayout Load(string path)
        {
            if (!File.Exists(path)) return null;
            try
            {
                return JsonUtility.FromJson<CourtLayout>(File.ReadAllText(path));
            }
            catch (Exception)
            {
                return null;
            }
        }

        public static string PathFor(string courtId)
        {
            int i = Array.IndexOf(ArtConventions.CourtIds, courtId);
            return i < 0 ? null : ArtConventions.CourtsRoot + "/" + courtId + "/ST_" + ArtConventions.CourtTokens[i] + "_layout.json";
        }

        /// <summary>The depth layer of the prop exported to this FBX, or null when the manifest does not list it.</summary>
        public static string LayerOf(string fbxAssetPath)
        {
            ArtKind kind = ArtConventions.Locate(fbxAssetPath, out string courtId);
            if (kind == ArtKind.Crowd) return ArtConventions.LayerCrowd;
            if (kind != ArtKind.Court) return null;
            CourtLayout layout = Load(PathFor(courtId));
            if (layout?.props == null) return null;
            string want = fbxAssetPath.Replace('\\', '/');
            string stem = Path.GetFileNameWithoutExtension(want);
            foreach (CourtLayoutProp p in layout.props)
            {
                string fbx = (p.fbx ?? "").Replace('\\', '/');
                if (fbx == want || p.name == stem) return string.IsNullOrEmpty(p.layer) ? null : p.layer;
            }
            return null;
        }
    }

    [Serializable]
    public sealed class CourtLayoutProp
    {
        public string name;
        public string fbx;
        public string layer;
        public float[] positionUnity;
        public CourtLayoutTris tris;
    }

    [Serializable]
    public sealed class CourtLayoutTris
    {
        public int LOD0;
        public int LOD1;
        public int LOD2;
    }

    // =====================================================================================================
    // Import settings by folder.
    // =====================================================================================================

    /// <summary>
    /// Applies Dunk Duel import settings to everything under Assets/DunkDuel/Art, keyed by folder:
    /// Fighters/&lt;id&gt;/ (humanoid rigs, exact frame-locked clips), Courts/&lt;id&gt;/ and Courts/shared/ (static props,
    /// LOD groups, lightmap UVs by depth layer), Crowd/ (VAT meshes and textures), texture sets (2K, ASTC) and
    /// URP Lit materials built from &lt;Material&gt;_BaseColor/_Normal/_ORM/_Emission. Add "dd:manual" to an
    /// importer's user data to keep hand-made settings for that one asset.
    /// </summary>
    public sealed class ArtImportPostprocessor : AssetPostprocessor
    {
        // Bump when the rules below change so Unity reimports affected assets.
        public override uint GetVersion() => 3;

        // Run after the render pipeline's own material-description preprocessors.
        public override int GetPostprocessOrder() => 100;

        // ------------------------------------------------------------------ models

        private void OnPreprocessModel()
        {
            if (!ArtConventions.IsArt(assetPath) || ArtConventions.IsManual(assetImporter)) return;
            var mi = assetImporter as ModelImporter;
            if (mi == null) return;
            ArtKind kind = ArtConventions.Locate(assetPath, out _);

            mi.globalScale = 1f;
            mi.useFileScale = true;
            mi.importCameras = false;
            mi.importLights = false;
            mi.isReadable = false;
            mi.addCollider = false;
            mi.materialImportMode = ModelImporterMaterialImportMode.ImportViaMaterialDescription;
            mi.materialLocation = ModelImporterMaterialLocation.InPrefab;
            mi.importNormals = ModelImporterNormals.Import;
            mi.importTangents = ModelImporterTangents.CalculateMikk;

            if (kind == ArtKind.Fighter)
            {
                // Blender exports characters with the standard -Z forward; Unity bakes the axis conversion.
                mi.bakeAxisConversion = true;
                mi.meshCompression = ModelImporterMeshCompression.Off;
                mi.importBlendShapes = true;
                mi.animationType = ModelImporterAnimationType.Human;
                mi.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                mi.optimizeGameObjects = false; // ball sockets and light anchors stay addressable by name
                mi.importAnimation = true;
                mi.animationCompression = ModelImporterAnimationCompression.Off; // exact per-frame sampling
                mi.resampleCurves = false;
                mi.generateSecondaryUV = false;
            }
            else
            {
                // Stage props bake the Blender-to-Unity axis mapping in the exporter (identity rotations here).
                mi.meshCompression = ModelImporterMeshCompression.Low;
                mi.importBlendShapes = kind == ArtKind.Crowd;
                mi.animationType = ModelImporterAnimationType.None;
                mi.importAnimation = false;
                string layer = kind == ArtKind.Crowd ? ArtConventions.LayerCrowd : CourtLayout.LayerOf(assetPath);
                // Crowd meshes carry the VAT_ID map in uv2; lightmap UVs would overwrite it.
                bool crowd = kind == ArtKind.Crowd || layer == ArtConventions.LayerCrowd || Path.GetFileName(assetPath).StartsWith("ST_Shared_Crowd", StringComparison.Ordinal);
                mi.generateSecondaryUV = !crowd && (layer == ArtConventions.LayerPlay || layer == ArtConventions.LayerMid);
            }
        }

        /// <summary>Clips play in place (the sim owns position); every Blender action is one take.</summary>
        private void OnPreprocessAnimation()
        {
            if (!ArtConventions.IsArt(assetPath) || ArtConventions.IsManual(assetImporter)) return;
            var mi = assetImporter as ModelImporter;
            if (mi == null || ArtConventions.Locate(assetPath, out _) != ArtKind.Fighter) return;
            ModelImporterClipAnimation[] clips = mi.defaultClipAnimations;
            if (clips == null || clips.Length == 0) return;
            foreach (ModelImporterClipAnimation c in clips)
            {
                c.lockRootRotation = true;
                c.lockRootHeightY = true;
                c.lockRootPositionXZ = true;
                c.keepOriginalOrientation = true;
                c.keepOriginalPositionY = true;
                c.keepOriginalPositionXZ = true;
                string n = c.name ?? "";
                c.loopTime = n.Contains("Idle") || n.Contains("Loop") || n.Contains("Dribble") || n.Contains("Run");
            }
            mi.clipAnimations = clips;
        }

        private void OnPostprocessModel(GameObject root)
        {
            if (!ArtConventions.IsArt(assetPath)) return;
            ArtKind kind = ArtConventions.Locate(assetPath, out _);
            string layer = kind == ArtKind.Fighter ? null : CourtLayout.LayerOf(assetPath);

            // LOD groups come from _LODn siblings; tune transitions for the fixed side camera.
            foreach (LODGroup group in root.GetComponentsInChildren<LODGroup>(true))
            {
                LOD[] lods = group.GetLODs();
                float[] heights = kind == ArtKind.Fighter ? new[] { 0.22f, 0.09f, 0.01f } : new[] { 0.12f, 0.04f, 0.004f };
                if (lods.Length > heights.Length) continue; // not one of ours: leave hand-tuned groups alone
                for (int i = 0; i < lods.Length; i++) lods[i].screenRelativeTransitionHeight = heights[i];
                group.SetLODs(lods);
                group.RecalculateBounds();
            }

            // Painted backdrop cards never cast shadows; the crowd only receives them on High (set at runtime).
            if (layer == ArtConventions.LayerBackdrop || kind == ArtKind.Crowd || layer == ArtConventions.LayerCrowd)
            {
                foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true)) r.shadowCastingMode = ShadowCastingMode.Off;
            }
        }

        // ------------------------------------------------------------------ materials

        private void OnPreprocessMaterialDescription(MaterialDescription description, Material material, AnimationClip[] materialAnimation)
        {
            if (!ArtConventions.IsArt(assetPath) || ArtConventions.IsManual(assetImporter)) return;
            string layer = ArtConventions.Locate(assetPath, out _) == ArtKind.Fighter ? null : CourtLayout.LayerOf(assetPath);
            ArtMaterials.Configure(material, description, assetPath, layer, context);
        }

        // ------------------------------------------------------------------ textures

        private void OnPreprocessTexture()
        {
            if (!ArtConventions.IsArt(assetPath) || ArtConventions.IsManual(assetImporter)) return;
            var ti = assetImporter as TextureImporter;
            if (ti == null) return;

            if (ArtConventions.IsVatTexture(assetPath))
            {
                // Vertex animation data: exact texels, no color conversion, no compression, no rescaling.
                ti.textureType = TextureImporterType.Default;
                ti.sRGBTexture = false;
                ti.mipmapEnabled = false;
                ti.filterMode = FilterMode.Point;
                ti.wrapMode = TextureWrapMode.Clamp;
                ti.npotScale = TextureImporterNPOTScale.None;
                ti.textureCompression = TextureImporterCompression.Uncompressed;
                ti.alphaSource = TextureImporterAlphaSource.FromInput;
                ti.maxTextureSize = 8192;
                foreach (string platform in new[] { "Android", "iPhone" })
                {
                    TextureImporterPlatformSettings ps = ti.GetPlatformTextureSettings(platform);
                    ps.overridden = true;
                    ps.maxTextureSize = 8192;
                    ps.format = TextureImporterFormat.RGBAHalf;
                    ti.SetPlatformTextureSettings(ps);
                }
                return;
            }

            string stem = Path.GetFileNameWithoutExtension(assetPath);
            ArtConventions.TryParseTexture(stem, out _, out string slot);
            ti.maxTextureSize = ArtConventions.TextureMax;
            ti.mipmapEnabled = true;
            ti.npotScale = TextureImporterNPOTScale.ToNearest;
            ti.wrapMode = TextureWrapMode.Repeat;
            ti.filterMode = FilterMode.Trilinear;
            ti.anisoLevel = 2;
            ti.isReadable = false;
            TextureImporterFormat mobile = TextureImporterFormat.ASTC_6x6;
            switch (slot)
            {
                case "Normal":
                    ti.textureType = TextureImporterType.NormalMap; // Blender writes OpenGL (Y+) normals, as Unity expects
                    ti.sRGBTexture = false;
                    mobile = TextureImporterFormat.ASTC_4x4;
                    break;
                case "ORM":
                    ti.textureType = TextureImporterType.Default;
                    ti.sRGBTexture = false;
                    ti.alphaSource = TextureImporterAlphaSource.FromInput;
                    // R = AO, G = roughness, B = metallic in Blender; URP Lit reads metallic from R, occlusion
                    // from G and smoothness from A, so the importer swizzles one texture to serve both maps.
                    ArtMaterials.OrmSwizzled = TextureSwizzle.TrySet(ti, "B", "R", "Zero", "OneMinusG");
                    break;
                case "BaseColor":
                case "Emission":
                default:
                    ti.textureType = TextureImporterType.Default;
                    ti.sRGBTexture = true;
                    break;
            }
            ti.textureCompression = TextureImporterCompression.Compressed;
            foreach (string platform in new[] { "Android", "iPhone" })
            {
                TextureImporterPlatformSettings ps = ti.GetPlatformTextureSettings(platform);
                ps.overridden = true;
                ps.maxTextureSize = ArtConventions.TextureMax;
                ps.format = mobile;
                ps.compressionQuality = 50;
                ti.SetPlatformTextureSettings(ps);
            }
        }

        // ------------------------------------------------------------------ after import

        private static void OnPostprocessAllAssets(string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            var changed = new List<string>();
            foreach (string p in importedAssets) if (ArtConventions.IsArt(p)) changed.Add(p);
            foreach (string p in movedAssets) if (ArtConventions.IsArt(p)) changed.Add(p);
            if (changed.Count == 0) return;

            // A layout manifest decides layers (lightmap UVs, shadows, unlit backdrop cards) for the props it lists.
            foreach (string p in changed)
            {
                if (!p.EndsWith("_layout.json", StringComparison.Ordinal)) continue;
                CourtLayout layout = CourtLayout.Load(p);
                if (layout?.props == null) continue;
                foreach (CourtLayoutProp prop in layout.props)
                {
                    string fbx = (prop.fbx ?? "").Replace('\\', '/');
                    if (fbx.Length > 0 && File.Exists(fbx) && !changed.Contains(fbx)) AssetDatabase.ImportAsset(fbx);
                }
            }
#if DD_ADDRESSABLES
            ArtAddressables.Assign(changed);
#endif
            ArtValidationWindow.MarkStale();
        }
    }

    /// <summary>TextureImporter channel swizzles, set by reflection so a renamed API degrades to a warning, not a broken Editor assembly.</summary>
    public static class TextureSwizzle
    {
        public static bool TrySet(TextureImporter ti, string r, string g, string b, string a)
        {
            try
            {
                Type t = typeof(TextureImporter);
                PropertyInfo pr = t.GetProperty("swizzleR"), pg = t.GetProperty("swizzleG"), pb = t.GetProperty("swizzleB"), pa = t.GetProperty("swizzleA");
                if (pr == null || pg == null || pb == null || pa == null || !pr.PropertyType.IsEnum) return false;
                Type e = pr.PropertyType;
                pr.SetValue(ti, Enum.Parse(e, r));
                pg.SetValue(ti, Enum.Parse(e, g));
                pb.SetValue(ti, Enum.Parse(e, b));
                pa.SetValue(ti, Enum.Parse(e, a));
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }

    // =====================================================================================================
    // Materials from texture sets.
    // =====================================================================================================

    public static class ArtMaterials
    {
        /// <summary>False when this Editor has no texture swizzle support: ORM maps are then left off materials.</summary>
        public static bool OrmSwizzled = true;

        public static void Configure(Material material, MaterialDescription description, string modelPath, string layer, AssetImportContext context)
        {
            string name = description != null && !string.IsNullOrEmpty(description.materialName) ? description.materialName : material.name;
            bool urp = GraphicsSettings.defaultRenderPipeline != null;
            bool unlit = layer == ArtConventions.LayerBackdrop;
            Shader shader = Shader.Find(urp ? (unlit ? "Universal Render Pipeline/Unlit" : "Universal Render Pipeline/Lit") : (unlit ? "Unlit/Texture" : "Standard"));
            if (shader != null) material.shader = shader;
            material.enableInstancing = true;

            Color baseColor = Color.white;
            if (description != null && description.TryGetProperty("DiffuseColor", out Vector4 diffuse)) baseColor = new Color(diffuse.x, diffuse.y, diffuse.z, 1f);
            SetColor(material, urp ? "_BaseColor" : "_Color", baseColor);
            if (name.EndsWith(ArtConventions.GrayboxSuffix, StringComparison.Ordinal)) return;

            string dir = Path.GetDirectoryName(modelPath).Replace('\\', '/');
            Texture2D baseMap = Find(dir, name, "BaseColor", context);
            Texture2D normal = Find(dir, name, "Normal", context);
            Texture2D orm = Find(dir, name, "ORM", context);
            Texture2D emission = Find(dir, name, "Emission", context);
            if (baseMap != null)
            {
                SetColor(material, urp ? "_BaseColor" : "_Color", Color.white);
                SetTexture(material, urp ? "_BaseMap" : "_MainTex", baseMap);
                if (urp) SetTexture(material, "_MainTex", baseMap);
            }
            if (unlit) return;
            if (normal != null)
            {
                SetTexture(material, "_BumpMap", normal);
                material.EnableKeyword("_NORMALMAP");
            }
            if (orm != null && OrmSwizzled)
            {
                SetTexture(material, "_MetallicGlossMap", orm);
                SetTexture(material, "_OcclusionMap", orm);
                SetFloat(material, "_Metallic", 1f);
                SetFloat(material, urp ? "_Smoothness" : "_GlossMapScale", 1f);
                SetFloat(material, "_OcclusionStrength", 1f);
                material.EnableKeyword(urp ? "_METALLICSPECGLOSSMAP" : "_METALLICGLOSSMAP");
                material.EnableKeyword("_OCCLUSIONMAP");
            }
            if (emission != null)
            {
                SetTexture(material, "_EmissionMap", emission);
                SetColor(material, "_EmissionColor", Color.white);
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
        }

        /// <summary>&lt;dir&gt;/Textures/&lt;Material&gt;_&lt;Slot&gt;.* (where dd_export copies them), or next to the model.</summary>
        public static string FindPath(string dir, string material, string slot)
        {
            foreach (string folder in new[] { dir + "/Textures", dir })
            {
                foreach (string ext in ArtConventions.TextureExtensions)
                {
                    string p = folder + "/" + material + "_" + slot + ext;
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        private static Texture2D Find(string dir, string material, string slot, AssetImportContext context)
        {
            string p = FindPath(dir, material, slot);
            if (p == null) return null;
            // Declaring the dependency imports the texture first and reimports the model when it changes.
            context?.DependsOnArtifact(p);
            return AssetDatabase.LoadAssetAtPath<Texture2D>(p);
        }

        private static void SetTexture(Material m, string prop, Texture t)
        {
            if (m.HasProperty(prop)) m.SetTexture(prop, t);
        }

        private static void SetColor(Material m, string prop, Color c)
        {
            if (m.HasProperty(prop)) m.SetColor(prop, c);
        }

        private static void SetFloat(Material m, string prop, float v)
        {
            if (m.HasProperty(prop)) m.SetFloat(prop, v);
        }
    }

#if DD_ADDRESSABLES
    // =====================================================================================================
    // Addressables groups by folder.
    // =====================================================================================================

    /// <summary>
    /// One group per fighter and per court plus shared stage and crowd groups. Fighters, shared assets and the
    /// three base courts build locally (base install); the other six courts use the remote profile paths so
    /// they download on demand. Addresses are file names (unique under the naming convention).
    /// </summary>
    public static class ArtAddressables
    {
        public const string GroupPrefix = "DD ";

        public static void Assign(IList<string> paths)
        {
            AddressableAssetSettings settings = AddressableAssetSettingsDefaultObject.GetSettings(true);
            if (settings == null) return;
            bool any = false;
            foreach (string path in paths)
            {
                if (!IsAddressable(path)) continue;
                ArtKind kind = ArtConventions.Locate(path, out string owner);
                string groupName;
                bool remote = false;
                var labels = new List<string>();
                switch (kind)
                {
                    case ArtKind.Fighter:
                        groupName = GroupPrefix + "Fighter " + owner;
                        labels.Add("fighter");
                        labels.Add("fighter:" + owner);
                        break;
                    case ArtKind.Court:
                        groupName = GroupPrefix + "Court " + owner;
                        remote = Array.IndexOf(ArtConventions.BaseInstallCourts, owner) < 0;
                        labels.Add("court");
                        labels.Add("court:" + owner);
                        break;
                    case ArtKind.SharedStage:
                        groupName = GroupPrefix + "Shared Stage";
                        labels.Add("stage-shared");
                        break;
                    case ArtKind.Crowd:
                        groupName = GroupPrefix + "Crowd";
                        labels.Add("crowd");
                        break;
                    default:
                        continue;
                }
                if (!remote) labels.Add("base");
                AddressableAssetGroup group = FindOrCreateGroup(settings, groupName, remote);
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (string.IsNullOrEmpty(guid)) continue;
                AddressableAssetEntry entry = settings.CreateOrMoveEntry(guid, group, false, false);
                if (entry == null) continue;
                entry.address = Path.GetFileNameWithoutExtension(path);
                foreach (string l in labels) entry.SetLabel(l, true, true, false);
                any = true;
            }
            if (any)
            {
                settings.SetDirty(AddressableAssetSettings.ModificationEvent.BatchModification, null, true, true);
                AssetDatabase.SaveAssets();
            }
        }

        /// <summary>Models, prefabs, layout manifests and VAT data are entries; textures and materials come along as dependencies.</summary>
        public static bool IsAddressable(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".fbx" || ext == ".prefab") return true;
            if (ext == ".json") return path.EndsWith("_layout.json", StringComparison.Ordinal) || path.EndsWith(".vat.json", StringComparison.Ordinal);
            return ext == ".exr" && ArtConventions.IsVatTexture(path);
        }

        private static AddressableAssetGroup FindOrCreateGroup(AddressableAssetSettings settings, string name, bool remote)
        {
            AddressableAssetGroup group = settings.FindGroup(name);
            if (group == null)
            {
                group = settings.CreateGroup(name, false, false, true, null, typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            }
            var bundled = group.GetSchema<BundledAssetGroupSchema>();
            if (bundled != null)
            {
                bundled.BuildPath.SetVariableByName(settings, remote ? AddressableAssetSettings.kRemoteBuildPath : AddressableAssetSettings.kLocalBuildPath);
                bundled.LoadPath.SetVariableByName(settings, remote ? AddressableAssetSettings.kRemoteLoadPath : AddressableAssetSettings.kLocalLoadPath);
                bundled.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            }
            var update = group.GetSchema<ContentUpdateGroupSchema>();
            if (update != null) update.StaticContent = !remote;
            return group;
        }
    }
#endif

    // =====================================================================================================
    // Validation.
    // =====================================================================================================

    [Serializable]
    public sealed class ArtIssue
    {
        public ArtSeverity Severity;
        public string Path;
        public string Message;

        public ArtIssue(ArtSeverity severity, string path, string message)
        {
            Severity = severity;
            Path = path;
            Message = message;
        }
    }

    /// <summary>
    /// Checks everything under Assets/DunkDuel/Art against the conventions: names, LOD chains, triangle budgets,
    /// fighter rigs, texture sets and sizes, court layouts and crowd meshes.
    /// Batch: Unity -batchmode -quit -projectPath . -executeMethod DunkDuel.Editor.ArtValidation.RunBatch [-ddArtReport path.json]
    /// </summary>
    public static class ArtValidation
    {
        [Serializable]
        private sealed class Report
        {
            public int errors;
            public int warnings;
            public List<ArtIssue> issues = new List<ArtIssue>();
        }

        public static List<ArtIssue> Run()
        {
            var issues = new List<ArtIssue>();
            if (!AssetDatabase.IsValidFolder(ArtConventions.ArtRoot)) return issues;
            var textureSets = new Dictionary<string, HashSet<string>>();
            foreach (string guid in AssetDatabase.FindAssets("", new[] { ArtConventions.ArtRoot }))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (AssetDatabase.IsValidFolder(path)) continue;
                ArtKind kind = ArtConventions.Locate(path, out string owner);
                if (kind == ArtKind.Unknown)
                {
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, "Unknown art folder: use Fighters/<fighter id>/, Courts/<court id>/, Courts/shared/ or Crowd/"));
                    continue;
                }
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".fbx") CheckModel(path, kind, owner, issues);
                else if (Array.IndexOf(ArtConventions.TextureExtensions, ext) >= 0) CheckTexture(path, textureSets, issues);
                else if (path.EndsWith("_layout.json", StringComparison.Ordinal)) CheckLayout(path, kind, owner, issues);
            }
            foreach (KeyValuePair<string, HashSet<string>> set in textureSets)
            {
                var missing = new List<string>();
                foreach (string slot in ArtConventions.RequiredSlots) if (!set.Value.Contains(slot)) missing.Add(slot);
                if (missing.Count > 0) issues.Add(new ArtIssue(ArtSeverity.Error, set.Key, "Texture set missing " + string.Join(", ", missing)));
            }
            issues.Sort((a, b) => a.Severity != b.Severity ? a.Severity.CompareTo(b.Severity) : string.CompareOrdinal(a.Path, b.Path));
            return issues;
        }

        public static string ToJson(List<ArtIssue> issues)
        {
            var r = new Report { issues = issues };
            foreach (ArtIssue i in issues)
            {
                if (i.Severity == ArtSeverity.Error) r.errors++;
                else if (i.Severity == ArtSeverity.Warning) r.warnings++;
            }
            return JsonUtility.ToJson(r, true);
        }

        public static void RunBatch()
        {
            List<ArtIssue> issues = Run();
            string report = "Library/DunkDuel/ArtValidation.json";
            string[] args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "-ddArtReport") report = args[i + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(report)));
            File.WriteAllText(report, ToJson(issues));
            int errors = 0;
            foreach (ArtIssue i in issues)
            {
                if (i.Severity == ArtSeverity.Error) errors++;
                string line = i.Severity + " " + i.Path + ": " + i.Message;
                if (i.Severity == ArtSeverity.Error) Debug.LogError(line);
                else Debug.Log(line);
            }
            Debug.Log("Dunk Duel art validation: " + issues.Count + " issue(s), " + errors + " error(s). Report: " + report);
            if (Application.isBatchMode) EditorApplication.Exit(errors > 0 ? 1 : 0);
        }

        // ------------------------------------------------------------------ models

        private static void CheckModel(string path, ArtKind kind, string owner, List<ArtIssue> issues)
        {
            string stem = Path.GetFileNameWithoutExtension(path);
            string token = ArtConventions.ExpectedToken(kind, owner);
            var root = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            if (root == null)
            {
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Model failed to import"));
                return;
            }
            if (kind == ArtKind.Fighter)
            {
                if (stem != "CH_" + token) issues.Add(new ArtIssue(ArtSeverity.Error, path, "Fighter file should be CH_" + token + ".fbx"));
                CheckFighter(path, root, token, issues);
            }
            else
            {
                if (!ArtConventions.TryParse(stem, out ArtName fileName, out string err)) issues.Add(new ArtIssue(ArtSeverity.Error, path, "File name " + err));
                else if (fileName.Family != "ST" || fileName.Owner != token) issues.Add(new ArtIssue(ArtSeverity.Error, path, "Stage file in this folder should start with ST_" + token + "_"));
                CheckProp(path, root, kind, token, stem, issues);
            }
        }

        private static int Triangles(Mesh mesh)
        {
            if (mesh == null) return 0;
            long indices = 0;
            for (int s = 0; s < mesh.subMeshCount; s++)
            {
                if (mesh.GetTopology(s) == MeshTopology.Triangles) indices += mesh.GetIndexCount(s);
            }
            return (int)(indices / 3);
        }

        private static IEnumerable<KeyValuePair<Renderer, Mesh>> Meshes(GameObject root)
        {
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                Mesh m = null;
                if (r is SkinnedMeshRenderer smr) m = smr.sharedMesh;
                else
                {
                    MeshFilter mf = r.GetComponent<MeshFilter>();
                    if (mf != null) m = mf.sharedMesh;
                }
                if (m != null) yield return new KeyValuePair<Renderer, Mesh>(r, m);
            }
        }

        private static void CheckFighter(string path, GameObject root, string token, List<ArtIssue> issues)
        {
            var trisByLod = new int[ArtConventions.MaxLod + 1];
            var deformBones = new HashSet<Transform>();
            foreach (KeyValuePair<Renderer, Mesh> rm in Meshes(root))
            {
                string n = rm.Key.gameObject.name;
                if (!ArtConventions.TryParse(n, out ArtName an, out string err))
                {
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, err));
                    continue;
                }
                if (an.Family != "CH" || an.Owner != token)
                {
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, "'" + n + "' does not belong in CH_" + token + ".fbx"));
                    continue;
                }
                trisByLod[Mathf.Max(0, an.Lod)] += Triangles(rm.Value);
                if (rm.Key is SkinnedMeshRenderer smr && smr.bones != null) foreach (Transform b in smr.bones) if (b != null) deformBones.Add(b);
            }
            for (int lod = 0; lod <= ArtConventions.MaxLod; lod++)
            {
                if (trisByLod[lod] == 0) continue;
                if (trisByLod[lod] > ArtConventions.FighterMaxTris[lod])
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, "LOD" + lod + ": " + trisByLod[lod].ToString("N0") + " triangles exceeds " + ArtConventions.FighterMaxTris[lod].ToString("N0")));
            }
            if (trisByLod[0] > 0 && trisByLod[0] < ArtConventions.FighterMinTrisLod0)
                issues.Add(new ArtIssue(ArtSeverity.Warning, path, "LOD0: " + trisByLod[0].ToString("N0") + " triangles is under the 40,000 target"));
            if (trisByLod[0] == 0) issues.Add(new ArtIssue(ArtSeverity.Error, path, "No CH_" + token + "_* meshes at LOD0"));
            CheckLodChain(path, "CH_" + token, trisByLod, issues);
            if (deformBones.Count > ArtConventions.MaxDeformBones)
                issues.Add(new ArtIssue(ArtSeverity.Warning, path, deformBones.Count + " deform bones (over " + ArtConventions.MaxDeformBones + " costs GPU skinning on mid-tier phones)"));

            var names = new HashSet<string>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) names.Add(t.name);
            var missing = new List<string>();
            foreach (string b in ArtConventions.RequiredBones) if (!names.Contains(b)) missing.Add(b);
            if (missing.Count > 0) issues.Add(new ArtIssue(ArtSeverity.Error, path, "Missing bones: " + string.Join(", ", missing)));

            Avatar avatar = null;
            var clips = new List<AnimationClip>();
            foreach (UnityEngine.Object o in AssetDatabase.LoadAllAssetsAtPath(path))
            {
                if (o is Avatar a) avatar = a;
                else if (o is AnimationClip c && !c.name.StartsWith("__preview__", StringComparison.Ordinal)) clips.Add(c);
            }
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (mi != null && mi.animationType == ModelImporterAnimationType.Human && (avatar == null || !avatar.isValid || !avatar.isHuman))
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Humanoid avatar is invalid; check the bone mapping (Rig tab > Configure)"));
            foreach (AnimationClip c in clips)
            {
                if (Mathf.Abs(c.frameRate - 60f) > 0.01f)
                    issues.Add(new ArtIssue(ArtSeverity.Warning, path, "Clip '" + c.name + "' is " + c.frameRate + " fps; frame-locked playback expects 60"));
            }
        }

        private static void CheckProp(string path, GameObject root, ArtKind kind, string token, string stem, List<ArtIssue> issues)
        {
            string layer = kind == ArtKind.Crowd ? ArtConventions.LayerCrowd : CourtLayout.LayerOf(path);
            if (kind == ArtKind.Court && layer == null)
                issues.Add(new ArtIssue(ArtSeverity.Warning, path, "Not listed in the court's layout manifest; treated as PlayPlane"));
            string effectiveLayer = layer ?? ArtConventions.LayerPlay;
            var trisByLod = new int[ArtConventions.MaxLod + 1];
            bool crowd = kind == ArtKind.Crowd || effectiveLayer == ArtConventions.LayerCrowd || stem.StartsWith("ST_Shared_Crowd", StringComparison.Ordinal);
            foreach (KeyValuePair<Renderer, Mesh> rm in Meshes(root))
            {
                string n = rm.Key.gameObject.name;
                if (!ArtConventions.TryParse(n, out ArtName an, out string err))
                {
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, err));
                    continue;
                }
                if (an.Base != stem) issues.Add(new ArtIssue(ArtSeverity.Warning, path, "'" + n + "' does not match the file's prop group " + stem));
                trisByLod[Mathf.Max(0, an.Lod)] += Triangles(rm.Value);
                if (crowd && !rm.Value.HasVertexAttribute(VertexAttribute.TexCoord1))
                    issues.Add(new ArtIssue(ArtSeverity.Warning, path, "'" + n + "' has no uv2 (VAT_ID); bake it with dd_vat_bake.py"));
            }
            int max = crowd ? ArtConventions.CrowdInstanceMaxTris : ArtConventions.LayerPropMax(effectiveLayer);
            if (trisByLod[0] > max)
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "LOD0: " + trisByLod[0].ToString("N0") + " triangles exceeds the " + (crowd ? "crowd instance" : effectiveLayer + " prop") + " budget " + max.ToString("N0")));
            CheckLodChain(path, stem, trisByLod, issues);
            var mi = AssetImporter.GetAtPath(path) as ModelImporter;
            if (crowd && mi != null && mi.generateSecondaryUV)
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Generate Lightmap UVs is on for a crowd mesh; it overwrites the VAT_ID uv2"));
        }

        private static void CheckLodChain(string path, string key, int[] trisByLod, List<ArtIssue> issues)
        {
            int last = -1;
            for (int lod = 0; lod < trisByLod.Length; lod++)
            {
                if (trisByLod[lod] == 0) continue;
                if (last < 0 && lod != 0) issues.Add(new ArtIssue(ArtSeverity.Warning, path, key + ": LOD chain starts at LOD" + lod + ", expected _LOD0"));
                if (last >= 0 && lod != last + 1) issues.Add(new ArtIssue(ArtSeverity.Warning, path, key + ": LOD chain skips from LOD" + last + " to LOD" + lod));
                if (last >= 0 && trisByLod[lod] > trisByLod[last] * ArtConventions.LodStepMaxFraction)
                    issues.Add(new ArtIssue(ArtSeverity.Warning, path, key + ": LOD" + lod + " has " + trisByLod[lod].ToString("N0") + " triangles, more than 75% of LOD" + last));
                last = lod;
            }
        }

        // ------------------------------------------------------------------ textures

        private static void CheckTexture(string path, Dictionary<string, HashSet<string>> sets, List<ArtIssue> issues)
        {
            var ti = AssetImporter.GetAtPath(path) as TextureImporter;
            if (ArtConventions.IsVatTexture(path))
            {
                if (ti != null && (ti.sRGBTexture || ti.mipmapEnabled || ti.textureCompression != TextureImporterCompression.Uncompressed))
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, "VAT texture must be linear, without mipmaps or compression"));
                return;
            }
            string stem = Path.GetFileNameWithoutExtension(path);
            if (!ArtConventions.TryParseTexture(stem, out string set, out string slot))
            {
                issues.Add(new ArtIssue(ArtSeverity.Warning, path, "Texture name has no _BaseColor/_Normal/_ORM/_Emission suffix"));
                return;
            }
            string key = Path.GetDirectoryName(path).Replace('\\', '/') + "/" + set;
            if (!sets.TryGetValue(key, out HashSet<string> slots)) sets[key] = slots = new HashSet<string>();
            slots.Add(slot);
            if (TextureSize.TryGet(path, ti, out int w, out int h))
            {
                if (w > ArtConventions.TextureMax || h > ArtConventions.TextureMax)
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, w + "x" + h + " exceeds 2048x2048"));
                if (!ArtConventions.IsPowerOfTwo(w) || !ArtConventions.IsPowerOfTwo(h))
                    issues.Add(new ArtIssue(ArtSeverity.Error, path, w + "x" + h + " is not a power of two"));
            }
            if (ti == null) return;
            bool wantSrgb = slot == "BaseColor" || slot == "Emission";
            if (ti.sRGBTexture != wantSrgb && slot != "Normal")
                issues.Add(new ArtIssue(ArtSeverity.Error, path, slot + " should be imported " + (wantSrgb ? "as sRGB" : "linear (sRGB off)")));
            if (slot == "Normal" && ti.textureType != TextureImporterType.NormalMap)
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Normal map is not imported as a Normal Map"));
            if (slot == "ORM" && !ArtMaterials.OrmSwizzled)
                issues.Add(new ArtIssue(ArtSeverity.Warning, path, "This Editor has no texture swizzle; ORM is not applied to URP Lit materials"));
        }

        // ------------------------------------------------------------------ layouts

        private static void CheckLayout(string path, ArtKind kind, string owner, List<ArtIssue> issues)
        {
            CourtLayout layout = CourtLayout.Load(path);
            if (layout == null)
            {
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Layout manifest is not valid JSON"));
                return;
            }
            if (kind != ArtKind.Court || layout.court != owner)
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Layout for '" + layout.court + "' is in the wrong folder"));
            var perLayer = new Dictionary<string, int>();
            int total = 0;
            if (layout.props != null)
            {
                foreach (CourtLayoutProp p in layout.props)
                {
                    string layer = string.IsNullOrEmpty(p.layer) ? ArtConventions.LayerPlay : p.layer;
                    if (Array.IndexOf(ArtConventions.Layers, layer) < 0) issues.Add(new ArtIssue(ArtSeverity.Error, path, p.name + ": unknown layer '" + layer + "'"));
                    string fbx = (p.fbx ?? "").Replace('\\', '/');
                    if (fbx.Length == 0 || !File.Exists(fbx)) issues.Add(new ArtIssue(ArtSeverity.Error, path, p.name + ": FBX not found (" + fbx + ")"));
                    int tris = p.tris != null ? p.tris.LOD0 : 0;
                    perLayer.TryGetValue(layer, out int sum);
                    perLayer[layer] = sum + tris;
                    total += tris;
                }
            }
            foreach (KeyValuePair<string, int> kv in perLayer)
            {
                int max = ArtConventions.LayerMax(kv.Key);
                if (kv.Value > max) issues.Add(new ArtIssue(ArtSeverity.Error, path, kv.Key + ": " + kv.Value.ToString("N0") + " triangles exceeds the layer budget " + max.ToString("N0")));
            }
            if (total > ArtConventions.CourtTotalMaxTris)
                issues.Add(new ArtIssue(ArtSeverity.Error, path, "Court total " + total.ToString("N0") + " triangles exceeds " + ArtConventions.CourtTotalMaxTris.ToString("N0")));
            issues.Add(new ArtIssue(ArtSeverity.Info, path, (layout.props?.Length ?? 0) + " props, " + total.ToString("N0") + " triangles at LOD0"));
        }
    }

    /// <summary>Source image size before import scaling (the importer API, with PNG/TGA header fallbacks).</summary>
    public static class TextureSize
    {
        public static bool TryGet(string path, TextureImporter ti, out int width, out int height)
        {
            width = height = 0;
            if (ti != null)
            {
                try
                {
                    MethodInfo m = typeof(TextureImporter).GetMethod("GetSourceTextureWidthAndHeight", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (m != null)
                    {
                        var args = new object[] { 0, 0 };
                        m.Invoke(ti, args);
                        width = (int)args[0];
                        height = (int)args[1];
                        if (width > 0 && height > 0) return true;
                    }
                }
                catch (Exception)
                {
                    // fall through to the header readers
                }
            }
            try
            {
                using (FileStream fs = File.OpenRead(path))
                {
                    var head = new byte[24];
                    if (fs.Read(head, 0, head.Length) < 18) return false;
                    if (head[0] == 0x89 && head[1] == 'P' && head[2] == 'N' && head[3] == 'G')
                    {
                        width = (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
                        height = (head[20] << 24) | (head[21] << 16) | (head[22] << 8) | head[23];
                        return true;
                    }
                    if (path.EndsWith(".tga", StringComparison.OrdinalIgnoreCase))
                    {
                        width = head[12] | (head[13] << 8);
                        height = head[14] | (head[15] << 8);
                        return true;
                    }
                }
            }
            catch (IOException)
            {
            }
            return false;
        }
    }

    // =====================================================================================================
    // Report window.
    // =====================================================================================================

    public sealed class ArtValidationWindow : EditorWindow
    {
        private static bool _stale = true;
        private List<ArtIssue> _issues = new List<ArtIssue>();
        private Vector2 _scroll;
        private bool _showErrors = true, _showWarnings = true, _showInfo;
        private string _filter = "";
        private DateTime _scannedAt;

        [MenuItem("DunkDuel/Art/Validation Report", false, 30)]
        public static void Open()
        {
            var w = GetWindow<ArtValidationWindow>("Art Validation");
            w.minSize = new Vector2(520f, 300f);
            w.Scan();
        }

        [MenuItem("DunkDuel/Art/Reimport All Art", false, 31)]
        public static void ReimportAll()
        {
            if (!AssetDatabase.IsValidFolder(ArtConventions.ArtRoot)) return;
            AssetDatabase.ImportAsset(ArtConventions.ArtRoot, ImportAssetOptions.ImportRecursive | ImportAssetOptions.ForceUpdate);
        }

        public static void MarkStale()
        {
            _stale = true;
        }

        private void Scan()
        {
            _issues = ArtValidation.Run();
            _scannedAt = DateTime.Now;
            _stale = false;
            Repaint();
        }

        private void OnGUI()
        {
            int errors = 0, warnings = 0, infos = 0;
            foreach (ArtIssue i in _issues)
            {
                if (i.Severity == ArtSeverity.Error) errors++;
                else if (i.Severity == ArtSeverity.Warning) warnings++;
                else infos++;
            }

            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            if (GUILayout.Button(_stale ? "Rescan (assets changed)" : "Rescan", EditorStyles.toolbarButton, GUILayout.Width(150f))) Scan();
            _showErrors = GUILayout.Toggle(_showErrors, " Errors " + errors, EditorStyles.toolbarButton);
            _showWarnings = GUILayout.Toggle(_showWarnings, " Warnings " + warnings, EditorStyles.toolbarButton);
            _showInfo = GUILayout.Toggle(_showInfo, " Info " + infos, EditorStyles.toolbarButton);
            GUILayout.FlexibleSpace();
            _filter = GUILayout.TextField(_filter, EditorStyles.toolbarTextField, GUILayout.Width(160f));
            if (GUILayout.Button("Export JSON", EditorStyles.toolbarButton))
            {
                string p = EditorUtility.SaveFilePanel("Art validation report", "", "ArtValidation.json", "json");
                if (!string.IsNullOrEmpty(p)) File.WriteAllText(p, ArtValidation.ToJson(_issues));
            }
            EditorGUILayout.EndHorizontal();

            if (!AssetDatabase.IsValidFolder(ArtConventions.ArtRoot))
            {
                EditorGUILayout.HelpBox("No art yet. Export from Blender with Tools/Blender/dd_export.py; files land in " + ArtConventions.ArtRoot + "/Fighters/<id>/ and /Courts/<id>/. The game runs on graybox meshes until then.", MessageType.Info);
                return;
            }
            if (_issues.Count == 0)
            {
                EditorGUILayout.HelpBox("All art passes the conventions (scanned " + _scannedAt.ToShortTimeString() + ").", MessageType.Info);
                return;
            }

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (ArtIssue i in _issues)
            {
                if (i.Severity == ArtSeverity.Error && !_showErrors) continue;
                if (i.Severity == ArtSeverity.Warning && !_showWarnings) continue;
                if (i.Severity == ArtSeverity.Info && !_showInfo) continue;
                if (_filter.Length > 0 && i.Path.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0 && i.Message.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                string icon = i.Severity == ArtSeverity.Error ? "console.erroricon.sml" : (i.Severity == ArtSeverity.Warning ? "console.warnicon.sml" : "console.infoicon.sml");
                EditorGUILayout.BeginHorizontal();
                GUILayout.Label(EditorGUIUtility.IconContent(icon), GUILayout.Width(20f), GUILayout.Height(18f));
                EditorGUILayout.BeginVertical();
                GUILayout.Label(i.Message, EditorStyles.wordWrappedLabel);
                if (GUILayout.Button(i.Path, EditorStyles.miniLabel))
                {
                    UnityEngine.Object o = AssetDatabase.LoadMainAssetAtPath(i.Path);
                    if (o != null)
                    {
                        EditorGUIUtility.PingObject(o);
                        Selection.activeObject = o;
                    }
                }
                EditorGUILayout.EndVertical();
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
    }
}
