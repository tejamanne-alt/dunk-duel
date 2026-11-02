using System.Collections.Generic;
using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.Rendering;

namespace DunkDuel.Presentation
{
    /// <summary>
    /// Builds a graybox court at the sim's dimensions as a film set with three depth layers: the play plane in
    /// full detail (floor, lines, hoop), a low-poly mid-ground (bleachers, fences, buildings), and painted-style
    /// backdrop cards with parallax. Everything comes from code: no scene or prefab YAML.
    /// </summary>
    public static class CourtBuilder
    {
        public const float CourtHalfDepth = 7.5f;   // half court is 15 m wide in Z
        public const float FarZ = 8.2f;             // far sideline area where the crowd starts

        public static CourtView Build(Transform parent, CourtDef def, Tuning t, MaterialLibrary mats, QualityProfile q)
        {
            var rootGo = new GameObject("Court_" + def.Id);
            rootGo.transform.SetParent(parent, false);
            CourtView view = rootGo.AddComponent<CourtView>();
            view.Def = def;
            Transform root = rootGo.transform;

            float rimX = t.Court.RimX.ToFloat(), rimY = t.Court.RimY.ToFloat(), rimR = t.Court.RimRadius.ToFloat();
            float baseline = t.Court.BaselineX.ToFloat(), arc = t.Court.ArcX.ToFloat(), check = t.Court.CheckX.ToFloat();
            float left = t.Court.LeftBoundX.ToFloat(), ft = t.Court.FreeThrowX.ToFloat();
            float boardX = t.Court.BackboardX.ToFloat(), boardBottom = t.Court.BackboardBottom.ToFloat(), boardTop = t.Court.BackboardTop.ToFloat();

            Atmosphere(view, def);
            BuildFloor(view, root, def, mats, left, baseline);
            BuildLines(view, root, def, mats, rimX, baseline, arc, check, left, ft);
            view.Hoop = HoopView.Build(root, mats, def, rimX, rimY, rimR, boardX, boardBottom, boardTop, baseline);
            BuildMidGround(view, root, def, mats, q, left, baseline);
            BuildBackdrop(view, root, def, mats);
            BuildCrowd(view, root, def, mats, q, left, baseline);
            BuildWeather(view, root, def, mats, q);
            view.BuildSetPiece(mats, q, left, baseline, rimX, rimY);
            return view;
        }

        private static void Atmosphere(CourtView view, CourtDef def)
        {
            RenderSettings.ambientMode = AmbientMode.Flat;
            RenderSettings.ambientLight = def.Ambient;
            RenderSettings.fog = def.FogDensity > 0f;
            RenderSettings.fogMode = FogMode.ExponentialSquared;
            RenderSettings.fogColor = def.Fog;
            RenderSettings.fogDensity = def.FogDensity;

            var keyGo = new GameObject("KeyLight");
            keyGo.transform.SetParent(view.transform, false);
            keyGo.transform.rotation = Quaternion.Euler(def.KeyEuler);
            var key = keyGo.AddComponent<Light>();
            key.type = LightType.Directional;
            key.color = def.KeyLight;
            key.intensity = def.KeyIntensity;
            key.shadows = LightShadows.Soft;
            key.shadowStrength = 0.6f;
            view.KeyLight = key;
            view.BaseKeyIntensity = def.KeyIntensity;
        }

        internal static MeshRenderer Place(Transform parent, string name, Mesh mesh, Material mat, Vector3 pos, bool shadows = false)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = mat;
            mr.shadowCastingMode = shadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
            mr.receiveShadows = true;
            return mr;
        }

        private static void BuildFloor(CourtView view, Transform root, CourtDef def, MaterialLibrary mats, float left, float baseline)
        {
            // Apron: big ground plane beyond the court.
            Place(root, "Apron", MeshFactory.FloorRect(left - 14f, baseline + 14f, -16f, 40f, -0.01f), mats.Lit(def.Apron, Mathf.Min(0.9f, def.Smoothness * 0.8f)), Vector3.zero);
            // Court surface. The Glass Court floor gets its own material instance so it can glow.
            Material floor = def.SetPiece == "motif" ? mats.LitInstance(def.Floor, def.Smoothness) : mats.Lit(def.Floor, def.Smoothness);
            view.FloorMaterial = floor;
            view.Floor = Place(root, "Floor", MeshFactory.FloorRect(left, baseline, -CourtHalfDepth, CourtHalfDepth, 0f), floor, Vector3.zero);
        }

        private static void BuildLines(CourtView view, Transform root, CourtDef def, MaterialLibrary mats, float rimX, float baseline, float arc, float check,
            float left, float ft)
        {
            var linesRoot = new GameObject("Lines").transform;
            linesRoot.SetParent(root, false);
            view.LinesRoot = linesRoot;
            Material line = def.SetPiece == "motif" ? mats.Emissive(def.Lines, 1.5f) : mats.Lit(def.Lines, 0.4f);
            Material paint = mats.Lit(def.Paint, def.Smoothness);
            const float w = 0.05f;
            const float y = 0.004f;
            float arcRadius = rimX - arc;          // 6.75 m
            float corner = 6.6f;                   // straight corner lines
            float cornerX = rimX - Mathf.Sqrt(Mathf.Max(0f, arcRadius * arcRadius - corner * corner));

            // Paint (key): baseline to the free-throw line, 4.9 m wide.
            Place(root, "Paint", MeshFactory.FloorRect(ft, baseline, -2.45f, 2.45f, 0.002f), paint, Vector3.zero);

            var pts = new List<Vector2>();
            // Three-point arc across the court depth, with corner lines to the baseline.
            pts.Add(new Vector2(baseline, -corner));
            pts.Add(new Vector2(cornerX, -corner));
            float a0 = Mathf.Atan2(-corner, cornerX - rimX) * Mathf.Rad2Deg;
            float a1 = Mathf.Atan2(corner, cornerX - rimX) * Mathf.Rad2Deg;
            if (a0 < 0f) a0 += 360f;
            if (a1 < 0f) a1 += 360f;
            foreach (Vector2 p in MeshFactory.Arc(new Vector2(rimX, 0f), arcRadius, a0, a1 > a0 ? a1 - 360f : a1, 48)) pts.Add(p);
            pts.Add(new Vector2(cornerX, corner));
            pts.Add(new Vector2(baseline, corner));
            Place(linesRoot, "ThreePoint", MeshFactory.FloorStrip(pts, w, y), line, Vector3.zero);

            // Key outline and free-throw circle.
            Place(linesRoot, "KeyOutline", MeshFactory.FloorStrip(new List<Vector2>
            {
                new Vector2(baseline, -2.45f), new Vector2(ft, -2.45f), new Vector2(ft, 2.45f), new Vector2(baseline, 2.45f),
            }, w, y), line, Vector3.zero);
            Place(linesRoot, "FreeThrowCircle", MeshFactory.FloorStrip(MeshFactory.Arc(new Vector2(ft, 0f), 1.8f, 0f, 360f, 40), w, y, true), line, Vector3.zero);
            // Restricted-area arc.
            Place(linesRoot, "Restricted", MeshFactory.FloorStrip(MeshFactory.Arc(new Vector2(rimX, 0f), 1.25f, 90f, 270f, 24), w, y), line, Vector3.zero);
            // Baseline, sidelines, half-court line (left bound) and its circle.
            Place(linesRoot, "Baseline", MeshFactory.FloorStrip(new List<Vector2> { new Vector2(baseline, -CourtHalfDepth), new Vector2(baseline, CourtHalfDepth) }, w * 1.4f, y), line, Vector3.zero);
            Place(linesRoot, "HalfCourt", MeshFactory.FloorStrip(new List<Vector2> { new Vector2(left, -CourtHalfDepth), new Vector2(left, CourtHalfDepth) }, w * 1.4f, y), line, Vector3.zero);
            Place(linesRoot, "SidelineFar", MeshFactory.FloorStrip(new List<Vector2> { new Vector2(left, CourtHalfDepth), new Vector2(baseline, CourtHalfDepth) }, w * 1.4f, y), line, Vector3.zero);
            Place(linesRoot, "SidelineNear", MeshFactory.FloorStrip(new List<Vector2> { new Vector2(left, -CourtHalfDepth), new Vector2(baseline, -CourtHalfDepth) }, w * 1.4f, y), line, Vector3.zero);
            Place(linesRoot, "CenterCircle", MeshFactory.FloorStrip(MeshFactory.Arc(new Vector2(left, 0f), 1.8f, -90f, 90f, 24), w, y), line, Vector3.zero);
            // Check spot at the top.
            Place(linesRoot, "CheckSpot", MeshFactory.FloorDisc(0.16f, 16, y + 0.001f), line, new Vector3(check, 0f, 0f));
        }

        private static void BuildMidGround(CourtView view, Transform root, CourtDef def, MaterialLibrary mats, QualityProfile q, float left, float baseline)
        {
            Transform mid = new GameObject("MidGround").transform;
            mid.SetParent(root, false);
            Material wall = mats.Lit(def.Wall, 0.1f);
            Material block = mats.Lit(def.MidGround, 0.1f);
            Material accent = mats.Emissive(def.Accent, 1.4f);
            Material accent2 = mats.Lit(def.Accent2, 0.3f);
            float cx = (left + baseline) * 0.5f;
            float span = baseline - left + 20f;

            switch (def.CrowdStyle)
            {
                case "bleachers":
                case "wooden":
                case "arena":
                case "glass":
                {
                    // Indoor: back wall, bleacher blocks, banners, ceiling lights.
                    Place(mid, "BackWall", MeshFactory.Box(new Vector3(span + 20f, 18f, 0.5f)), wall, new Vector3(cx, 9f, 24f));
                    int rows = def.CrowdStyle == "arena" ? 12 : (def.CrowdStyle == "glass" ? 8 : 9);
                    Place(mid, "Bleachers", MeshFactory.Bleachers(span, rows), block, new Vector3(cx, 0f, FarZ + 0.6f));
                    for (int i = 0; i < 6; i++)
                    {
                        Material banner = i % 2 == 0 ? accent2 : mats.Lit(def.Accent, 0.2f);
                        Place(mid, "Banner", MeshFactory.Box(new Vector3(1.6f, 3.2f, 0.05f)), banner, new Vector3(left - 4f + i * (span / 6f), 12.5f, 23.5f));
                    }
                    for (int i = 0; i < 5; i++)
                    {
                        MeshRenderer lamp = Place(mid, "CeilingLight", MeshFactory.Box(new Vector3(2.4f, 0.25f, 1.2f)), accent, new Vector3(left + i * (span / 5f), 15f, 4f + (i % 2) * 6f));
                        view.LightBanks.Add(lamp);
                    }
                    if (def.CrowdStyle == "wooden")
                    {
                        for (int i = 0; i < 7; i++) Place(mid, "Truss", MeshFactory.Box(new Vector3(0.35f, 0.35f, 30f)), mats.Lit(new Color(0.18f, 0.18f, 0.2f), 0.5f, 0.6f), new Vector3(left - 6f + i * 4.5f, 16f, 10f));
                        Place(mid, "Scoreboard", MeshFactory.Box(new Vector3(5f, 2.2f, 0.4f)), mats.Lit(new Color(0.12f, 0.1f, 0.09f), 0.3f), new Vector3(cx, 11f, 23f));
                    }
                    if (def.CrowdStyle == "arena")
                    {
                        view.Jumbotron = Place(mid, "Jumbotron", MeshFactory.Box(new Vector3(7f, 3.6f, 0.4f)), mats.LitInstance(new Color(0.05f, 0.06f, 0.08f), 0.6f), new Vector3(cx, 13.5f, 22f));
                    }
                    break;
                }
                case "fence":
                {
                    // Elevated tracks above, chain-link fence, brick walls.
                    Place(mid, "Fence", MeshFactory.Box(new Vector3(span, 3.4f, 0.04f)), mats.TransparentInstance(new Color(0.75f, 0.75f, 0.72f, 0.35f)), new Vector3(cx, 1.7f, FarZ));
                    for (int i = 0; i < 12; i++) Place(mid, "FencePost", MeshFactory.Cylinder(0.05f, 3.5f, 6), mats.Lit(new Color(0.5f, 0.5f, 0.5f), 0.5f, 0.7f), new Vector3(left - 8f + i * (span / 11f), 0f, FarZ));
                    Place(mid, "TrackDeck", MeshFactory.Box(new Vector3(span + 30f, 1.2f, 4f)), block, new Vector3(cx, 9.5f, 14f));
                    for (int i = 0; i < 6; i++) Place(mid, "TrackPillar", MeshFactory.Box(new Vector3(0.9f, 9f, 0.9f)), wall, new Vector3(left - 10f + i * 8f, 4.5f, 14f));
                    Place(mid, "BrickWall", MeshFactory.Box(new Vector3(span + 20f, 7f, 0.6f)), wall, new Vector3(cx, 3.5f, 19f));
                    Place(mid, "DJTable", MeshFactory.Box(new Vector3(1.8f, 0.8f, 0.7f)), accent2, new Vector3(left + 3f, 0.4f, FarZ + 1.4f));
                    break;
                }
                case "umbrellas":
                {
                    // Market lane: shop fronts, tarps, bulb strings, a heritage arch around the hoop.
                    for (int i = 0; i < 8; i++)
                    {
                        float x = left - 8f + i * (span / 7f);
                        Place(mid, "Shop", MeshFactory.Box(new Vector3(3.6f, 6f + (i % 3), 3f)), wall, new Vector3(x, 3f, 16f));
                        Place(mid, "Tarp", MeshFactory.Box(new Vector3(3.4f, 0.06f, 2.2f)), i % 2 == 0 ? accent2 : mats.Lit(new Color(0.55f, 0.45f, 0.3f), 0.2f), new Vector3(x, 3.3f, 14.4f));
                        Place(mid, "Neon", MeshFactory.Box(new Vector3(1.4f, 0.4f, 0.05f)), mats.Emissive(i % 2 == 0 ? def.Accent : new Color(0.95f, 0.35f, 0.3f), 2f), new Vector3(x, 4.6f, 14.45f));
                    }
                    for (int i = 0; i < 30; i++)
                    {
                        float x = left - 6f + i * (span / 29f);
                        float sag = Mathf.Sin(i / 29f * Mathf.PI * 3f) * 0.5f;
                        Place(mid, "Bulb", MeshFactory.Sphere(0.07f, 6, 4), accent, new Vector3(x, 5.6f - Mathf.Abs(sag), FarZ + 2f));
                    }
                    Place(mid, "ArchL", MeshFactory.Box(new Vector3(0.9f, 6.5f, 1.2f)), mats.Lit(new Color(0.55f, 0.47f, 0.4f), 0.3f), new Vector3(baseline + 0.6f, 3.25f, 2.2f));
                    Place(mid, "ArchR", MeshFactory.Box(new Vector3(0.9f, 6.5f, 1.2f)), mats.Lit(new Color(0.55f, 0.47f, 0.4f), 0.3f), new Vector3(baseline + 0.6f, 3.25f, -2.2f));
                    Place(mid, "ArchTop", MeshFactory.Box(new Vector3(1f, 1f, 5.6f)), mats.Lit(new Color(0.55f, 0.47f, 0.4f), 0.3f), new Vector3(baseline + 0.6f, 6.8f, 0f));
                    break;
                }
                case "sidewalk":
                {
                    // Boardwalk: seawall, food trucks, palms, skate bowl.
                    Place(mid, "Seawall", MeshFactory.Box(new Vector3(span + 30f, 1.4f, 1.2f)), wall, new Vector3(cx, 0.7f, 15f));
                    for (int i = 0; i < 3; i++) Place(mid, "FoodTruck", MeshFactory.Box(new Vector3(4.5f, 2.6f, 2.2f)), i == 1 ? accent2 : mats.Lit(new Color(0.85f, 0.83f, 0.78f), 0.4f), new Vector3(left - 2f + i * 7f, 1.3f, 12f));
                    for (int i = 0; i < 5; i++)
                    {
                        float x = left - 6f + i * 5.5f;
                        Place(mid, "PalmTrunk", MeshFactory.Cylinder(0.18f, 6f, 8, true, 0.12f), mats.Lit(new Color(0.45f, 0.35f, 0.25f), 0.2f), new Vector3(x, 0f, 13.5f));
                        Place(mid, "PalmCrown", MeshFactory.Sphere(1.2f, 8, 5), mats.Lit(new Color(0.35f, 0.5f, 0.3f), 0.2f), new Vector3(x, 6.2f, 13.5f));
                    }
                    Place(mid, "SkateBowl", MeshFactory.Cylinder(3.5f, 0.6f, 20, true), mats.Lit(new Color(0.78f, 0.76f, 0.72f), 0.3f), new Vector3(baseline + 6f, 0f, 11f));
                    break;
                }
                case "grass":
                {
                    Place(mid, "Lawn", MeshFactory.FloorRect(left - 20f, baseline + 20f, FarZ, 30f, 0.005f), mats.Lit(new Color(0.42f, 0.45f, 0.26f), 0.1f), Vector3.zero);
                    Place(mid, "Fence", MeshFactory.Box(new Vector3(span, 2.4f, 0.04f)), mats.TransparentInstance(new Color(0.7f, 0.7f, 0.68f, 0.3f)), new Vector3(cx, 1.2f, FarZ));
                    Place(mid, "ClockTower", MeshFactory.Box(new Vector3(3f, 16f, 3f)), mats.Lit(new Color(0.6f, 0.45f, 0.35f), 0.2f), new Vector3(left + 2f, 8f, 28f));
                    Place(mid, "ClockFace", MeshFactory.Cylinder(0.9f, 0.1f, 20), mats.Emissive(new Color(0.95f, 0.9f, 0.75f), 0.8f), new Vector3(left + 2f, 13.5f, 26.4f)).transform.localRotation = Quaternion.Euler(90f, 0f, 0f);
                    for (int i = 0; i < 7; i++)
                    {
                        float x = left - 10f + i * 6f;
                        Place(mid, "Maple", MeshFactory.Sphere(2.2f, 8, 6), mats.Lit(i % 2 == 0 ? def.Accent : new Color(0.75f, 0.45f, 0.2f), 0.15f), new Vector3(x, 5f, 20f + (i % 3)));
                        Place(mid, "MapleTrunk", MeshFactory.Cylinder(0.25f, 3.5f, 8), mats.Lit(new Color(0.3f, 0.22f, 0.16f), 0.1f), new Vector3(x, 0f, 20f + (i % 3)));
                    }
                    break;
                }
                default: // sparse gym (Lights Out)
                {
                    Place(mid, "BackWall", MeshFactory.Box(new Vector3(span + 20f, 14f, 0.5f)), wall, new Vector3(cx, 7f, 16f));
                    for (int i = 0; i < 6; i++)
                    {
                        MeshRenderer window = Place(mid, "Window", MeshFactory.Box(new Vector3(2.2f, 3f, 0.05f)), mats.LitInstance(def.Accent, 0.9f), new Vector3(left - 4f + i * 4.5f, 10f, 15.7f));
                        view.Windows.Add(window);
                    }
                    for (int i = 0; i < 6; i++)
                    {
                        MeshRenderer lamp = Place(mid, "LightBank", MeshFactory.Box(new Vector3(2.2f, 0.25f, 1.2f)), mats.Emissive(new Color(0.9f, 0.92f, 0.95f), i % 2 == 0 ? 1.4f : 0.1f), new Vector3(left + i * 2.5f, 12f, 3f + (i % 2) * 5f));
                        view.LightBanks.Add(lamp);
                    }
                    Place(mid, "StackedChairs", MeshFactory.Box(new Vector3(1.2f, 1.6f, 0.6f)), mats.Lit(new Color(0.35f, 0.33f, 0.32f), 0.3f, 0.5f), new Vector3(left + 2f, 0.8f, FarZ + 1f));
                    Place(mid, "JanitorCart", MeshFactory.Box(new Vector3(1.1f, 1f, 0.6f)), mats.Lit(new Color(0.6f, 0.55f, 0.2f), 0.3f), new Vector3(baseline - 1.5f, 0.5f, FarZ + 0.8f));
                    break;
                }
            }
        }

        private static void BuildBackdrop(CourtView view, Transform root, CourtDef def, MaterialLibrary mats)
        {
            // Painted backdrop cards at several depths; CourtView adds extra parallax as the camera tracks.
            Transform back = new GameObject("Backdrop").transform;
            back.SetParent(root, false);
            view.BackdropRoot = back;
            Material sky = mats.VertexColor(Color.white);
            MeshRenderer skyCard = Place(back, "Sky", MeshFactory.GradientQuad(220f, 70f, def.SkyBottom, def.SkyTop), sky, new Vector3(-4f, 22f, 60f));
            view.Parallax.Add(new CourtView.ParallaxCard { Card = skyCard.transform, Factor = 0.85f, Base = skyCard.transform.localPosition });
            Color far = Color.Lerp(def.SkyBottom, def.MidGround, 0.55f);
            Color near = Color.Lerp(def.SkyBottom, def.MidGround, 0.8f);
            for (int layer = 0; layer < 2; layer++)
            {
                Color c = layer == 0 ? far : near;
                float z = layer == 0 ? 46f : 34f;
                Transform cardRoot = new GameObject("Skyline" + layer).transform;
                cardRoot.SetParent(back, false);
                cardRoot.localPosition = new Vector3(0f, 0f, z);
                for (int i = 0; i < 18; i++)
                {
                    float w = 3f + VisualNoise.Hash01(i, layer) * 6f;
                    float h = (def.Outdoor ? 6f : 3f) + VisualNoise.Hash01(i + 40, layer) * (def.Outdoor ? 16f : 6f);
                    Place(cardRoot, "Silhouette", MeshFactory.Quad(w, h), mats.Unlit(c), new Vector3(-60f + i * 7.5f, h * 0.5f, 0f));
                }
                view.Parallax.Add(new CourtView.ParallaxCard { Card = cardRoot, Factor = layer == 0 ? 0.6f : 0.35f, Base = cardRoot.localPosition });
            }
            if (def.Id == "concrete_coast")
            {
                MeshRenderer sea = Place(back, "Sea", MeshFactory.FloorRect(-120f, 120f, 16f, 90f, 0.3f), mats.Lit(def.Accent, 0.85f), Vector3.zero);
                view.Sea = sea;
            }
        }

        private static void BuildCrowd(CourtView view, Transform root, CourtDef def, MaterialLibrary mats, QualityProfile q, float left, float baseline)
        {
            int target = Mathf.RoundToInt(def.CrowdDensity * 520f * q.CrowdScale);
            if (target <= 0) return;
            Color[] tones =
            {
                Color.Lerp(def.Accent2, Color.gray, 0.3f), Color.Lerp(def.Wall, Color.white, 0.3f), new Color(0.2f, 0.2f, 0.22f),
                Color.Lerp(def.Accent, def.Wall, 0.5f), new Color(0.75f, 0.7f, 0.62f),
            };
            var palette = new Material[tones.Length];
            for (int i = 0; i < tones.Length; i++) palette[i] = mats.LitInstance(tones[i], 0.15f);
            CrowdRenderer crowd = CrowdRenderer.Create(root, MeshFactory.Spectator(), palette);
            view.Crowd = crowd;
            float x0 = left - 9f, x1 = baseline + 7f;
            int seed = 0;
            switch (def.CrowdStyle)
            {
                case "bleachers":
                case "wooden":
                case "arena":
                case "glass":
                {
                    int rows = def.CrowdStyle == "arena" ? 12 : 9;
                    int perRow = Mathf.Max(4, target / rows);
                    for (int r = 0; r < rows; r++)
                    {
                        for (int i = 0; i < perRow; i++)
                        {
                            float x = Mathf.Lerp(x0, x1, (i + 0.5f * (r % 2)) / perRow) + VisualNoise.Signed(seed, 1) * 0.15f;
                            crowd.Add(new Vector3(x, (r + 1) * 0.45f, FarZ + 0.6f + r * 0.8f + 0.4f), 1f, r == 0 ? 1 : 0, seed++);
                        }
                    }
                    if (def.CrowdStyle == "arena")
                    {
                        // Courtside photographers on the near side, crouched low.
                        for (int i = 0; i < 8; i++) crowd.Add(new Vector3(left + 1f + i * 1.4f, -0.5f, -CourtHalfDepth - 1.2f), 0.75f, 0, seed++);
                    }
                    break;
                }
                case "fence":
                    for (int i = 0; i < target; i++) crowd.Add(new Vector3(Mathf.Lerp(x0, x1, i / (float)target), 0f, FarZ + 0.35f + (i % 3) * 0.45f), 1f, 2, seed++);
                    break;
                case "umbrellas":
                    crowd.EnableUmbrellas(MeshFactory.Cylinder(0.55f, 0.08f, 10, true, 0.05f), mats.LitInstance(Color.Lerp(def.Accent2, Color.black, 0.2f), 0.6f));
                    for (int i = 0; i < target; i++) crowd.Add(new Vector3(Mathf.Lerp(x0, x1, VisualNoise.Hash01(i, 5)), 0f, FarZ + 0.5f + VisualNoise.Hash01(i, 9) * 4f), 1f, 1, seed++);
                    break;
                case "grass":
                    for (int i = 0; i < target; i++) crowd.Add(new Vector3(Mathf.Lerp(x0, x1, VisualNoise.Hash01(i, 5)), 0f, FarZ + 1f + VisualNoise.Hash01(i, 9) * 6f), 0.62f, 2, seed++);
                    break;
                case "sidewalk":
                    for (int i = 0; i < target; i++) crowd.Add(new Vector3(Mathf.Lerp(x0, x1, VisualNoise.Hash01(i, 5)), 0f, FarZ + 0.5f + VisualNoise.Hash01(i, 9) * 3f), 1f, 1, seed++);
                    break;
                default:
                    crowd.Add(new Vector3(baseline - 2.2f, 0f, FarZ + 1f), 1f, 0, seed++); // the janitor
                    break;
            }
            crowd.Seal();
        }

        private static void BuildWeather(CourtView view, Transform root, CourtDef def, MaterialLibrary mats, QualityProfile q)
        {
            switch (def.Weather)
            {
                case "rain":
                    view.Rain = Emitter(root, "Rain", new Vector3(-4f, 13f, 3f), new Vector3(34f, 22f, 0.1f), mats.TransparentInstance(new Color(0.8f, 0.85f, 0.95f, 0.45f)),
                        900f * q.EffectsScale, 1.1f, 14f, 0.025f, true);
                    break;
                case "leaves":
                    view.Leaves = Emitter(root, "Leaves", new Vector3(-4f, 9f, 3f), new Vector3(34f, 16f, 0.1f), mats.TransparentInstance(def.Accent),
                        14f * q.EffectsScale, 6f, 1.2f, 0.12f, false);
                    break;
                case "dust":
                case "haze":
                    view.Dust = Emitter(root, "Dust", new Vector3(-4f, 6f, 3f), new Vector3(30f, 14f, 8f), mats.TransparentInstance(new Color(1f, 0.92f, 0.8f, 0.25f)),
                        30f * q.EffectsScale, 8f, 0.15f, 0.05f, false);
                    break;
            }
        }

        internal static ParticleSystem Emitter(Transform parent, string name, Vector3 pos, Vector3 box, Material mat, float rate, float life, float speed, float size, bool stretch)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = pos;
            go.transform.localRotation = Quaternion.Euler(90f, 0f, 0f); // emit downward
            var ps = go.AddComponent<ParticleSystem>();
            ParticleSystem.MainModule main = ps.main;
            main.loop = true;
            main.startLifetime = life;
            main.startSpeed = speed;
            main.startSize = size;
            main.maxParticles = Mathf.Max(64, Mathf.RoundToInt(rate * life * 1.3f));
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.gravityModifier = stretch ? 0.4f : 0.02f;
            ParticleSystem.EmissionModule em = ps.emission;
            em.rateOverTime = rate;
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = box;
            var r = go.GetComponent<ParticleSystemRenderer>();
            r.material = mat;
            r.renderMode = stretch ? ParticleSystemRenderMode.Stretch : ParticleSystemRenderMode.Billboard;
            r.lengthScale = 3f;
            r.velocityScale = 0.03f;
            r.shadowCastingMode = ShadowCastingMode.Off;
            r.receiveShadows = false;
            ps.Play();
            return ps;
        }
    }
}
