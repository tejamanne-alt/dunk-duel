using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    public enum TextAlign : byte { Left, Center, Right, TopLeft, Top, TopRight, BottomLeft, Bottom, BottomRight }

    /// <summary>Fonts for code-built UI: TextMeshPro when a TMP font asset is available, legacy Text otherwise.</summary>
    public static class UIFonts
    {
        public static TMP_FontAsset Tmp;
        public static Font Legacy;
        public static bool UseTmp;
        private static bool _ready;

        public static void Init()
        {
            if (_ready) return;
            _ready = true;
            Legacy = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            try
            {
                Tmp = TMP_Settings.defaultFontAsset;
            }
            catch (Exception)
            {
                Tmp = null;
            }
            if (Tmp == null && Legacy != null)
            {
                try
                {
                    Tmp = TMP_FontAsset.CreateFontAsset(Legacy);
                }
                catch (Exception)
                {
                    Tmp = null;
                }
            }
            UseTmp = Tmp != null;
        }
    }

    /// <summary>One text element, backed by TextMeshProUGUI or legacy UI.Text.</summary>
    public sealed class UIText
    {
        public RectTransform Rect;
        private TextMeshProUGUI _tmp;
        private Text _legacy;
        private float _baseSize;

        public GameObject GameObject => Rect.gameObject;

        /// <summary>Wraps an existing text element created by <see cref="Create"/>.</summary>
        public static UIText Wrap(RectTransform rt)
        {
            var t = new UIText { Rect = rt };
            t._tmp = rt.GetComponent<TextMeshProUGUI>();
            if (t._tmp == null) t._legacy = rt.GetComponent<Text>();
            if (t._tmp == null && t._legacy == null) return null;
            t._baseSize = t._tmp != null ? t._tmp.fontSize / Theme.TextScale : t._legacy.fontSize / Theme.TextScale;
            return t;
        }

        public static UIText Create(Transform parent, string text, float size, TextAlign align, Color color, bool bold)
        {
            UIFonts.Init();
            var go = new GameObject("Text", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var t = new UIText { Rect = (RectTransform)go.transform, _baseSize = size };
            if (UIFonts.UseTmp)
            {
                t._tmp = go.AddComponent<TextMeshProUGUI>();
                t._tmp.font = UIFonts.Tmp;
                t._tmp.raycastTarget = false;
                t._tmp.overflowMode = TextOverflowModes.Overflow;
                t._tmp.fontStyle = bold ? FontStyles.Bold : FontStyles.Normal;
            }
            else
            {
                t._legacy = go.AddComponent<Text>();
                t._legacy.font = UIFonts.Legacy;
                t._legacy.raycastTarget = false;
                t._legacy.horizontalOverflow = HorizontalWrapMode.Wrap;
                t._legacy.verticalOverflow = VerticalWrapMode.Overflow;
                t._legacy.fontStyle = bold ? FontStyle.Bold : FontStyle.Normal;
            }
            t.Size = size;
            t.Align = align;
            t.Color = color;
            t.Text = text;
            return t;
        }

        public string Text
        {
            get => _tmp != null ? _tmp.text : _legacy.text;
            set
            {
                if (_tmp != null) _tmp.text = value;
                else _legacy.text = value;
            }
        }

        public Color Color
        {
            get => _tmp != null ? _tmp.color : _legacy.color;
            set
            {
                if (_tmp != null) _tmp.color = value;
                else _legacy.color = value;
            }
        }

        public float Size
        {
            get => _baseSize;
            set
            {
                _baseSize = value;
                float s = value * Theme.TextScale;
                if (_tmp != null) _tmp.fontSize = s;
                else _legacy.fontSize = Mathf.RoundToInt(s);
            }
        }

        public TextAlign Align
        {
            set
            {
                if (_tmp != null) _tmp.alignment = ToTmp(value);
                else _legacy.alignment = ToAnchor(value);
            }
        }

        public void SetActive(bool on) => Rect.gameObject.SetActive(on);

        private static TextAlignmentOptions ToTmp(TextAlign a)
        {
            switch (a)
            {
                case TextAlign.Left: return TextAlignmentOptions.Left;
                case TextAlign.Right: return TextAlignmentOptions.Right;
                case TextAlign.TopLeft: return TextAlignmentOptions.TopLeft;
                case TextAlign.Top: return TextAlignmentOptions.Top;
                case TextAlign.TopRight: return TextAlignmentOptions.TopRight;
                case TextAlign.BottomLeft: return TextAlignmentOptions.BottomLeft;
                case TextAlign.Bottom: return TextAlignmentOptions.Bottom;
                case TextAlign.BottomRight: return TextAlignmentOptions.BottomRight;
                default: return TextAlignmentOptions.Center;
            }
        }

        private static TextAnchor ToAnchor(TextAlign a)
        {
            switch (a)
            {
                case TextAlign.Left: return TextAnchor.MiddleLeft;
                case TextAlign.Right: return TextAnchor.MiddleRight;
                case TextAlign.TopLeft: return TextAnchor.UpperLeft;
                case TextAlign.Top: return TextAnchor.UpperCenter;
                case TextAlign.TopRight: return TextAnchor.UpperRight;
                case TextAlign.BottomLeft: return TextAnchor.LowerLeft;
                case TextAlign.Bottom: return TextAnchor.LowerCenter;
                case TextAlign.BottomRight: return TextAnchor.LowerRight;
                default: return TextAnchor.MiddleCenter;
            }
        }
    }

    /// <summary>UI palette and scales. Accessibility options swap to a colorblind-safe set and enlarge text.</summary>
    public static class Theme
    {
        public static float TextScale = 1f;
        public static bool Colorblind;

        public static Color Bg = new Color(0.055f, 0.06f, 0.075f, 1f);
        public static Color Panel = new Color(0.11f, 0.12f, 0.15f, 0.96f);
        public static Color PanelLight = new Color(0.18f, 0.19f, 0.23f, 0.96f);
        public static Color Scrim = new Color(0f, 0f, 0f, 0.6f);
        public static Color Text = new Color(0.96f, 0.95f, 0.92f, 1f);
        public static Color Dim = new Color(0.62f, 0.64f, 0.68f, 1f);
        public static Color Accent = new Color(1f, 0.82f, 0.36f, 1f);
        public static Color Good = new Color(0.36f, 0.85f, 0.52f, 1f);
        public static Color Danger = new Color(0.95f, 0.36f, 0.3f, 1f);
        public static Color Hype = new Color(1f, 0.78f, 0.25f, 1f);
        public static Color Balance = new Color(0.5f, 0.85f, 0.75f, 1f);
        public static Color P1 = new Color(0.95f, 0.6f, 0.25f, 1f);
        public static Color P2 = new Color(0.33f, 0.66f, 0.96f, 1f);

        public static void Apply(bool colorblind, bool largeText)
        {
            Colorblind = colorblind;
            TextScale = largeText ? 1.22f : 1f;
            if (colorblind)
            {
                // Okabe-Ito: distinguishable for the common color-vision deficiencies.
                P1 = new Color(0.902f, 0.624f, 0f, 1f);
                P2 = new Color(0f, 0.447f, 0.698f, 1f);
                Hype = new Color(0.941f, 0.894f, 0.259f, 1f);
                Balance = new Color(0f, 0.62f, 0.451f, 1f);
                Danger = new Color(0.835f, 0.369f, 0f, 1f);
                Good = new Color(0.337f, 0.706f, 0.914f, 1f);
            }
            else
            {
                P1 = new Color(0.95f, 0.6f, 0.25f, 1f);
                P2 = new Color(0.33f, 0.66f, 0.96f, 1f);
                Hype = new Color(1f, 0.78f, 0.25f, 1f);
                Balance = new Color(0.5f, 0.85f, 0.75f, 1f);
                Danger = new Color(0.95f, 0.36f, 0.3f, 1f);
                Good = new Color(0.36f, 0.85f, 0.52f, 1f);
            }
        }

        public static Color PlayerColor(int player, Color identity) => Colorblind ? (player == 0 ? P1 : P2) : identity;
    }

    /// <summary>Generated sprites (rounded panels, circles, rings) so the UI needs no imported textures.</summary>
    public static class Sprites
    {
        private static Sprite _rounded;
        private static Sprite _circle;
        private static Sprite _ring;

        public static Sprite Rounded => _rounded != null ? _rounded : (_rounded = MakeRounded(64, 18));
        public static Sprite Circle => _circle != null ? _circle : (_circle = MakeCircle(128, 0f));
        public static Sprite Ring => _ring != null ? _ring : (_ring = MakeCircle(128, 0.78f));

        private static Sprite MakeRounded(int size, int radius)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Rounded" };
            var px = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = Mathf.Max(0f, Mathf.Max(radius - x - 0.5f, x + 0.5f - (size - radius)));
                    float dy = Mathf.Max(0f, Mathf.Max(radius - y - 0.5f, y + 0.5f - (size - radius)));
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float a = Mathf.Clamp01(radius - d + 0.5f);
                    px[y * size + x] = new Color(1f, 1f, 1f, a);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, new Vector4(radius, radius, radius, radius));
        }

        private static Sprite MakeCircle(int size, float inner)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "Circle" };
            var px = new Color32[size * size];
            float r = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x + 0.5f - r) * (x + 0.5f - r) + (y + 0.5f - r) * (y + 0.5f - r));
                    float outer = Mathf.Clamp01(r - d);
                    float hole = inner > 0f ? Mathf.Clamp01(d - r * inner) : 1f;
                    px[y * size + x] = new Color(1f, 1f, 1f, outer * hole);
                }
            }
            tex.SetPixels32(px);
            tex.Apply(false);
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f);
        }
    }

    /// <summary>Builders for code-made uGUI. Sizes are in reference pixels (1920 x 1080, landscape).</summary>
    public static class UIKit
    {
        public static RectTransform Node(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            return (RectTransform)go.transform;
        }

        /// <summary>Stretches past the safe area to the physical screen edges (notch, home indicator), for backgrounds.</summary>
        public static RectTransform Bleed(RectTransform rt) => Stretch(rt, -BleedMargin, -BleedMargin, -BleedMargin, -BleedMargin);

        private const float BleedMargin = 600f; // canvas units; larger than any device's safe-area inset

        public static RectTransform Stretch(RectTransform rt, float left = 0, float top = 0, float right = 0, float bottom = 0)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
            return rt;
        }

        /// <summary>Anchors at <paramref name="anchor"/> (0..1) with a pivot, offset and fixed size.</summary>
        public static RectTransform Place(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
        {
            rt.anchorMin = anchor;
            rt.anchorMax = anchor;
            rt.pivot = pivot;
            rt.anchoredPosition = pos;
            rt.sizeDelta = size;
            return rt;
        }

        public static Image Panel(Transform parent, string name, Color color, bool rounded = true)
        {
            RectTransform rt = Node(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.color = color;
            if (rounded)
            {
                img.sprite = Sprites.Rounded;
                img.type = Image.Type.Sliced;
            }
            return img;
        }

        public static Image Circle(Transform parent, string name, Color color, bool ring = false)
        {
            RectTransform rt = Node(parent, name);
            var img = rt.gameObject.AddComponent<Image>();
            img.sprite = ring ? Sprites.Ring : Sprites.Circle;
            img.color = color;
            img.preserveAspect = true;
            return img;
        }

        public static UIText Label(Transform parent, string text, float size, TextAlign align = TextAlign.Center, Color? color = null, bool bold = false)
        {
            return UIText.Create(parent, text, size, align, color ?? Theme.Text, bold);
        }

        public static Button Button(Transform parent, string label, Action onClick, float width = 520, float height = 110, Color? color = null, float fontSize = 40)
        {
            Image bg = Panel(parent, "Button_" + label, color ?? Theme.PanelLight);
            var btn = bg.gameObject.AddComponent<Button>();
            btn.targetGraphic = bg;
            ColorBlock cb = btn.colors;
            cb.highlightedColor = new Color(1f, 1f, 1f, 1f);
            cb.pressedColor = new Color(0.75f, 0.75f, 0.75f, 1f);
            cb.disabledColor = new Color(0.5f, 0.5f, 0.5f, 0.5f);
            btn.colors = cb;
            if (onClick != null) btn.onClick.AddListener(() => onClick());
            UIText t = Label(bg.transform, label, fontSize, TextAlign.Center, Theme.Text, true);
            Stretch(t.Rect, 12, 4, 12, 4);
            Layout(bg, width, height);
            bg.rectTransform.sizeDelta = new Vector2(width, height);
            return btn;
        }

        public static RectTransform VStack(Transform parent, string name, float spacing, int padding, TextAnchor align = TextAnchor.UpperCenter)
        {
            RectTransform rt = Node(parent, name);
            var v = rt.gameObject.AddComponent<VerticalLayoutGroup>();
            v.spacing = spacing;
            v.padding = new RectOffset(padding, padding, padding, padding);
            v.childAlignment = align;
            v.childControlWidth = false;
            v.childControlHeight = false;
            v.childForceExpandWidth = false;
            v.childForceExpandHeight = false;
            return rt;
        }

        public static RectTransform HStack(Transform parent, string name, float spacing, int padding, TextAnchor align = TextAnchor.MiddleCenter)
        {
            RectTransform rt = Node(parent, name);
            var h = rt.gameObject.AddComponent<HorizontalLayoutGroup>();
            h.spacing = spacing;
            h.padding = new RectOffset(padding, padding, padding, padding);
            h.childAlignment = align;
            h.childControlWidth = false;
            h.childControlHeight = false;
            h.childForceExpandWidth = false;
            h.childForceExpandHeight = false;
            return rt;
        }

        public static RectTransform Grid(Transform parent, string name, Vector2 cell, Vector2 spacing, int columns)
        {
            RectTransform rt = Node(parent, name);
            var g = rt.gameObject.AddComponent<GridLayoutGroup>();
            g.cellSize = cell;
            g.spacing = spacing;
            g.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            g.constraintCount = columns;
            g.childAlignment = TextAnchor.UpperCenter;
            return rt;
        }

        public static LayoutElement Layout(Component c, float width = -1, float height = -1)
        {
            LayoutElement le = c.GetComponent<LayoutElement>();
            if (le == null) le = c.gameObject.AddComponent<LayoutElement>();
            if (width >= 0) le.preferredWidth = width;
            if (height >= 0) le.preferredHeight = height;
            return le;
        }

        /// <summary>Horizontal fill bar; returns the background, outputs the fill image (Image.Type.Filled).</summary>
        public static Image Bar(Transform parent, string name, Color bg, Color fill, out Image fillImage, bool fromRight = false)
        {
            Image back = Panel(parent, name, bg);
            RectTransform f = Node(back.transform, "Fill");
            Stretch(f, 4, 4, 4, 4);
            fillImage = f.gameObject.AddComponent<Image>();
            fillImage.color = fill;
            fillImage.type = Image.Type.Filled;
            fillImage.fillMethod = Image.FillMethod.Horizontal;
            fillImage.fillOrigin = fromRight ? 1 : 0;
            fillImage.fillAmount = 0f;
            return back;
        }

        /// <summary>Scrolling vertical list; children go into <paramref name="content"/>.</summary>
        public static ScrollRect Scroll(Transform parent, string name, out RectTransform content)
        {
            Image bg = Panel(parent, name, new Color(0f, 0f, 0f, 0.25f));
            bg.gameObject.AddComponent<RectMask2D>();
            var scroll = bg.gameObject.AddComponent<ScrollRect>();
            content = VStack(bg.transform, "Content", 14, 12);
            content.anchorMin = new Vector2(0f, 1f);
            content.anchorMax = new Vector2(1f, 1f);
            content.pivot = new Vector2(0.5f, 1f);
            content.anchoredPosition = Vector2.zero;
            content.sizeDelta = new Vector2(0f, 0f);
            var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
            fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            VerticalLayoutGroup v = content.GetComponent<VerticalLayoutGroup>();
            v.childControlWidth = true;
            v.childForceExpandWidth = true;
            v.childControlHeight = false;
            scroll.content = content;
            scroll.horizontal = false;
            scroll.vertical = true;
            scroll.movementType = ScrollRect.MovementType.Clamped;
            scroll.viewport = bg.rectTransform;
            return scroll;
        }

        /// <summary>A two-state switch drawn as a button whose label shows the state.</summary>
        public static Button Switch(Transform parent, string label, Func<bool> get, Action<bool> set, float width = 760, float height = 96)
        {
            UIText text = null;
            Button b = Button(parent, label, null, width, height, Theme.PanelLight, 34);
            text = FindLabel(b);
            void Refresh() => text.Text = label + ":  " + (get() ? "ON" : "OFF");
            b.onClick.AddListener(() =>
            {
                set(!get());
                Refresh();
            });
            Refresh();
            return b;
        }

        /// <summary>A minus/plus stepper with a value label (touch-friendly slider).</summary>
        public static RectTransform Stepper(Transform parent, string label, Func<string> value, Action<int> step, float width = 760)
        {
            RectTransform row = HStack(parent, "Stepper_" + label, 16, 0);
            Layout(row, width, 96);
            row.sizeDelta = new Vector2(width, 96);
            UIText title = Label(row, label, 32, TextAlign.Left);
            Layout(title.Rect, width - 420, 96);
            title.Rect.sizeDelta = new Vector2(width - 420, 96);
            UIText val = null;
            Button minus = Button(row, "-", () =>
            {
                step(-1);
                val.Text = value();
            }, 96, 96);
            val = Label(row, value(), 32, TextAlign.Center, Theme.Accent, true);
            Layout(val.Rect, 180, 96);
            val.Rect.sizeDelta = new Vector2(180, 96);
            Button plus = Button(row, "+", () =>
            {
                step(1);
                val.Text = value();
            }, 96, 96);
            _ = minus;
            _ = plus;
            return row;
        }

        /// <summary>The text element created by <see cref="Button"/>.</summary>
        public static UIText FindLabel(Button b)
        {
            UIFonts.Init();
            Transform t = b.transform.childCount > 0 ? b.transform.GetChild(0) : null;
            if (t == null) return null;
            return UIText.Wrap((RectTransform)t);
        }
    }
}
