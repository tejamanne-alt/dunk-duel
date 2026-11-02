using System.Collections.Generic;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.Presentation
{
    public enum CourtTier : byte { Street = 0, School = 1, Pro = 2 }

    /// <summary>One court's look and life (courts.json). Courts never change gameplay: geometry comes from the sim.</summary>
    public sealed class CourtDef
    {
        public int Index;
        public string Id;
        public string Name;
        public CourtTier Tier;
        public string Blurb;
        public string FinisherMoment;
        public bool Outdoor;
        public bool Night;
        public string Weather;      // none, rain, storm, leaves, haze, dust
        public string SetPiece;     // downpour, train, wave, surge, blackout, leafstorm, shatter, motif, pyro
        public string CrowdStyle;   // umbrellas, fence, sidewalk, bleachers, sparse, grass, wooden, glass, arena
        public float CrowdDensity;
        public string Music;        // style id for the synthesized stems
        public float Tempo;
        public string Reverb;       // AudioReverbPreset name
        public Color Floor;
        public Color Lines;
        public Color Paint;
        public Color Apron;
        public Color Wall;
        public Color MidGround;
        public Color SkyTop;
        public Color SkyBottom;
        public Color Accent;
        public Color Accent2;
        public Color Ambient;
        public Color Fog;
        public float FogDensity;
        public Color KeyLight;
        public float KeyIntensity;
        public Vector3 KeyEuler;
        public float Smoothness;
    }

    public sealed class CameraRecipe
    {
        public string Id;
        public string Kind;      // shake, zoom, tilt, whip, cut, follow, none
        public float Duration;
        public float Amplitude;
        public float Frequency;
        public float Zoom;       // fraction of distance removed at the peak
        public float Tilt;       // degrees of roll at the peak
    }

    public sealed class CameraConfig
    {
        public float Fov = 30f;
        public float MinDistance = 9f;
        public float MaxDistance = 17f;
        public float Height = 2.4f;
        public float LookHeight = 1.9f;
        public float PaddingX = 2.2f;
        public float PaddingY = 1.2f;
        public float Smooth = 0.22f;
        public float Pitch = 4f;
        public float FinisherDistance = 6.5f;
        public float FinisherHeight = 1.8f;
        public float ReducedMotionScale = 0.25f;
        public readonly Dictionary<string, CameraRecipe> Recipes = new Dictionary<string, CameraRecipe>();
    }

    /// <summary>Fighter look (visuals.json): identity colors, proportions and effect motif.</summary>
    public sealed class FighterVisual
    {
        public string Id;
        public Color Primary;
        public Color Secondary;
        public Color Trim;
        public Color Skin;
        public Color Hair;
        public Color HairTip;
        public Color Shoes;
        public Color Rim;
        public Color Motif;
        public string MotifKind;  // solar, storm, glitch, seismic
        public string HairStyle;  // twists, braid, bob, short
        public float Shoulders = 1.12f;
        public float Hands = 1.15f;
        public float Calves = 1.12f;
        public float Bulk = 1f;
        public string[] Outfits;  // cosmetic primary color variants (hex), index 0 = default
    }

    /// <summary>Everything presentation reads from Resources/DunkDuelPresentation.</summary>
    public sealed class PresentationData
    {
        public const string Root = "DunkDuelPresentation/";

        public CourtDef[] Courts;
        public CameraConfig Camera;
        public FighterVisual[] Visuals;

        public CourtDef Court(int index)
        {
            if (Courts == null || Courts.Length == 0) return null;
            if (index < 0 || index >= Courts.Length) index = 0;
            return Courts[index];
        }

        public int CourtIndex(string id)
        {
            for (int i = 0; i < Courts.Length; i++) if (Courts[i].Id == id) return i;
            return 0;
        }

        public FighterVisual Visual(string fighterId)
        {
            for (int i = 0; i < Visuals.Length; i++) if (Visuals[i].Id == fighterId) return Visuals[i];
            return Visuals.Length > 0 ? Visuals[0] : new FighterVisual { Id = fighterId, Primary = Color.gray, Secondary = Color.white };
        }

        public static PresentationData Load()
        {
            var d = new PresentationData
            {
                Courts = LoadCourts(PJson.Load(Root + "courts")),
                Camera = LoadCamera(PJson.Load(Root + "camera")),
                Visuals = LoadVisuals(PJson.Load(Root + "visuals")),
            };
            if (d.Courts.Length == 0) throw new PresentationException("courts.json is missing or empty");
            return d;
        }

        private static CourtDef[] LoadCourts(JsonValue root)
        {
            var list = new List<CourtDef>();
            foreach (JsonValue c in PJson.Arr(root, "courts"))
            {
                string tier = PJson.Str(c, "tier", "street");
                list.Add(new CourtDef
                {
                    Index = list.Count,
                    Id = PJson.Str(c, "id"),
                    Name = PJson.Str(c, "name"),
                    Tier = tier == "pro" ? CourtTier.Pro : (tier == "school" ? CourtTier.School : CourtTier.Street),
                    Blurb = PJson.Str(c, "blurb"),
                    FinisherMoment = PJson.Str(c, "finisherMoment"),
                    Outdoor = PJson.Bool(c, "outdoor"),
                    Night = PJson.Bool(c, "night"),
                    Weather = PJson.Str(c, "weather", "none"),
                    SetPiece = PJson.Str(c, "setPiece", "none"),
                    CrowdStyle = PJson.Str(c, "crowdStyle", "bleachers"),
                    CrowdDensity = PJson.Num(c, "crowdDensity", 0.5f),
                    Music = PJson.Str(c, "music", "boombap"),
                    Tempo = PJson.Num(c, "tempo", 92f),
                    Reverb = PJson.Str(c, "reverb", "Room"),
                    Floor = PJson.Col(c, "floor", Color.gray),
                    Lines = PJson.Col(c, "lines", Color.white),
                    Paint = PJson.Col(c, "paint", Color.gray),
                    Apron = PJson.Col(c, "apron", Color.gray),
                    Wall = PJson.Col(c, "wall", Color.gray),
                    MidGround = PJson.Col(c, "midGround", Color.gray),
                    SkyTop = PJson.Col(c, "skyTop", Color.black),
                    SkyBottom = PJson.Col(c, "skyBottom", Color.gray),
                    Accent = PJson.Col(c, "accent", Color.white),
                    Accent2 = PJson.Col(c, "accent2", Color.white),
                    Ambient = PJson.Col(c, "ambient", Color.gray),
                    Fog = PJson.Col(c, "fog", Color.gray),
                    FogDensity = PJson.Num(c, "fogDensity", 0.01f),
                    KeyLight = PJson.Col(c, "keyLight", Color.white),
                    KeyIntensity = PJson.Num(c, "keyIntensity", 1f),
                    KeyEuler = PJson.Vec(c, "keyEuler", new Vector3(50f, -30f, 0f)),
                    Smoothness = PJson.Num(c, "smoothness", 0.3f),
                });
            }
            return list.ToArray();
        }

        private static CameraConfig LoadCamera(JsonValue root)
        {
            var cfg = new CameraConfig();
            if (root == null) return cfg;
            JsonValue f = root.Get("framing");
            cfg.Fov = PJson.Num(f, "fov", cfg.Fov);
            cfg.MinDistance = PJson.Num(f, "minDistance", cfg.MinDistance);
            cfg.MaxDistance = PJson.Num(f, "maxDistance", cfg.MaxDistance);
            cfg.Height = PJson.Num(f, "height", cfg.Height);
            cfg.LookHeight = PJson.Num(f, "lookHeight", cfg.LookHeight);
            cfg.PaddingX = PJson.Num(f, "paddingX", cfg.PaddingX);
            cfg.PaddingY = PJson.Num(f, "paddingY", cfg.PaddingY);
            cfg.Smooth = PJson.Num(f, "smooth", cfg.Smooth);
            cfg.Pitch = PJson.Num(f, "pitch", cfg.Pitch);
            cfg.FinisherDistance = PJson.Num(f, "finisherDistance", cfg.FinisherDistance);
            cfg.FinisherHeight = PJson.Num(f, "finisherHeight", cfg.FinisherHeight);
            cfg.ReducedMotionScale = PJson.Num(f, "reducedMotionScale", cfg.ReducedMotionScale);
            foreach (JsonValue r in PJson.Arr(root, "recipes"))
            {
                var recipe = new CameraRecipe
                {
                    Id = PJson.Str(r, "id"),
                    Kind = PJson.Str(r, "kind", "none"),
                    Duration = PJson.Num(r, "duration", 0.2f),
                    Amplitude = PJson.Num(r, "amplitude", 0f),
                    Frequency = PJson.Num(r, "frequency", 25f),
                    Zoom = PJson.Num(r, "zoom", 0f),
                    Tilt = PJson.Num(r, "tilt", 0f),
                };
                if (!string.IsNullOrEmpty(recipe.Id)) cfg.Recipes[recipe.Id] = recipe;
            }
            return cfg;
        }

        private static FighterVisual[] LoadVisuals(JsonValue root)
        {
            var list = new List<FighterVisual>();
            foreach (JsonValue v in PJson.Arr(root, "fighters"))
            {
                var outfits = new List<string>();
                foreach (JsonValue o in PJson.Arr(v, "outfits")) if (o.Kind == JsonKind.String) outfits.Add(o.Text);
                list.Add(new FighterVisual
                {
                    Id = PJson.Str(v, "id"),
                    Primary = PJson.Col(v, "primary", Color.gray),
                    Secondary = PJson.Col(v, "secondary", Color.white),
                    Trim = PJson.Col(v, "trim", Color.white),
                    Skin = PJson.Col(v, "skin", new Color(0.55f, 0.38f, 0.26f)),
                    Hair = PJson.Col(v, "hair", Color.black),
                    HairTip = PJson.Col(v, "hairTip", Color.black),
                    Shoes = PJson.Col(v, "shoes", Color.white),
                    Rim = PJson.Col(v, "rim", Color.white),
                    Motif = PJson.Col(v, "motif", Color.white),
                    MotifKind = PJson.Str(v, "motifKind", "solar"),
                    HairStyle = PJson.Str(v, "hairStyle", "short"),
                    Shoulders = PJson.Num(v, "shoulders", 1.12f),
                    Hands = PJson.Num(v, "hands", 1.15f),
                    Calves = PJson.Num(v, "calves", 1.12f),
                    Bulk = PJson.Num(v, "bulk", 1f),
                    Outfits = outfits.ToArray(),
                });
            }
            return list.ToArray();
        }
    }
}
