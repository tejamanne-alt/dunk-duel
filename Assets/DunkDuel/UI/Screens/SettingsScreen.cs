using DunkDuel.Presentation;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>Settings: Simple mode, controls, haptics, audio, quality and accessibility.</summary>
    public sealed class SettingsScreen : TitledScreen
    {
        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("settings.title", "Settings"));
            GameSettings s = App.Settings;
            ScrollRect scroll = UIKit.Scroll(Body, "Options", out RectTransform c);
            UIKit.Stretch(scroll.GetComponent<RectTransform>());

            Section(c, Loc.T("settings.play", "Play"));
            UIKit.Switch(c, Loc.T("settings.simple", "Simple mode (wider timing, auto defense, less Hype)"), () => s.SimpleMode, v => { s.SimpleMode = v; Save(); }, 1600);
            UIKit.Switch(c, Loc.T("settings.leftHanded", "Left-handed controls"), () => s.LeftHanded, v => { s.LeftHanded = v; Save(); }, 1600);
            UIKit.Switch(c, Loc.T("settings.inputDisplay", "Training input display"), () => s.InputDisplay, v => { s.InputDisplay = v; Save(); }, 1600);
            UIKit.Button(c, Loc.T("settings.remap", "Controller and keyboard bindings"), () => App.Nav.Push(new RemapScreen()), 1600, 96, Theme.PanelLight, 34);

            Section(c, Loc.T("settings.feel", "Feel"));
            UIKit.Switch(c, Loc.T("settings.haptics", "Haptics"), () => s.Haptics, v => { s.Haptics = v; Save(); }, 1600);
            UIKit.Stepper(c, Loc.T("settings.hapticStrength", "Haptic strength"), () => s.HapticStrength.ToString(), d => { s.HapticStrength = Mathf.Clamp(s.HapticStrength + d, 0, 4); Save(); Haptics.Play(HapticKind.Medium); }, 1600);
            UIKit.Stepper(c, Loc.T("settings.master", "Master volume"), () => s.MasterVolume.ToString(), d => { s.MasterVolume = Mathf.Clamp(s.MasterVolume + d, 0, 10); Save(); }, 1600);
            UIKit.Stepper(c, Loc.T("settings.music", "Music volume"), () => s.MusicVolume.ToString(), d => { s.MusicVolume = Mathf.Clamp(s.MusicVolume + d, 0, 10); Save(); }, 1600);
            UIKit.Stepper(c, Loc.T("settings.sfx", "Effects volume"), () => s.SfxVolume.ToString(), d => { s.SfxVolume = Mathf.Clamp(s.SfxVolume + d, 0, 10); Save(); }, 1600);

            Section(c, Loc.T("settings.graphics", "Graphics"));
            UIKit.Stepper(c, Loc.T("settings.quality", "Quality"), () => s.Quality.ToString(), d => { s.Quality = (QualityMode)(((int)s.Quality + d + 4) % 4); Save(); }, 1600);

            Section(c, Loc.T("settings.access", "Accessibility"));
            UIKit.Switch(c, Loc.T("settings.reducedMotion", "Reduced camera shake"), () => s.ReducedMotion, v => { s.ReducedMotion = v; Save(); }, 1600);
            UIKit.Switch(c, Loc.T("settings.reducedFlash", "Reduced flashes"), () => s.ReducedFlash, v => { s.ReducedFlash = v; Save(); }, 1600);
            UIKit.Switch(c, Loc.T("settings.colorblind", "Colorblind-safe palette"), () => s.Colorblind, v => { s.Colorblind = v; Save(); }, 1600);
            UIKit.Switch(c, Loc.T("settings.largeText", "Larger text (applies to new screens)"), () => s.LargeText, v => { s.LargeText = v; Save(); }, 1600);
        }

        private static void Section(RectTransform parent, string title)
        {
            UIText t = UIKit.Label(parent, title, 40, TextAlign.Left, Theme.Accent, true);
            UIKit.Layout(t.Rect, 1600, 80);
        }

        private void Save()
        {
            App.Settings.Save();
            App.Settings.Apply(App);
        }
    }

    /// <summary>Interactive rebinding for every action (Input System), saved with the settings.</summary>
    public sealed class RemapScreen : TitledScreen
    {
        private RectTransform _list;
        private UIText _status;

        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("remap.title", "Bindings"));
            _status = UIKit.Label(Body, Loc.T("remap.hint", "Tap a binding, then press the new button. Escape cancels."), 30, TextAlign.Left, Theme.Dim);
            UIKit.Place(_status.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), Vector2.zero, new Vector2(1600f, 60f));
            _list = UIKit.VStack(Body, "List", 12, 0, TextAnchor.UpperLeft);
            UIKit.Place(_list, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, -80f), new Vector2(1700f, 760f));
            Refresh();
            Button reset = UIKit.Button(Body, Loc.T("remap.reset", "Reset to defaults"), () =>
            {
                App.Input.Bindings.ResetOverrides();
                Persist();
                Refresh();
            }, 460, 96);
            UIKit.Place((RectTransform)reset.transform, new Vector2(1f, 0f), new Vector2(1f, 0f), Vector2.zero, new Vector2(460f, 96f));
        }

        private void Refresh()
        {
            for (int i = _list.childCount - 1; i >= 0; i--) Object.Destroy(_list.GetChild(i).gameObject);
            GameInputBindings b = App.Input.Bindings;
            for (int a = 0; a < GameInputBindings.ActionNames.Length; a++)
            {
                RectTransform row = UIKit.HStack(_list, "Row", 14, 0, TextAnchor.MiddleLeft);
                UIKit.Layout(row, 1700, 96);
                row.sizeDelta = new Vector2(1700, 96);
                UIText name = UIKit.Label(row, GameInputBindings.ActionNames[a].ToUpperInvariant(), 34, TextAlign.Left, Theme.Text, true);
                UIKit.Layout(name.Rect, 320, 96);
                name.Rect.sizeDelta = new Vector2(320, 96);
                for (int k = 0; k < b.BindingCount(a); k++)
                {
                    int action = a, binding = k;
                    UIKit.Button(row, b.Describe(a, k), () =>
                    {
                        _status.Text = Loc.T("remap.waiting", "Press a button...");
                        b.Rebind(action, binding, () =>
                        {
                            Persist();
                            _status.Text = Loc.T("remap.done", "Saved.");
                            Refresh();
                        });
                    }, 420, 90, Theme.PanelLight, 28);
                }
            }
        }

        private void Persist()
        {
            App.Settings.Bindings = App.Input.Bindings.SaveOverrides();
            App.Settings.Save();
        }
    }
}
