using System;
using DunkDuel.AI;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>Screen with a title bar and a back button.</summary>
    public abstract class TitledScreen : UIScreen
    {
        protected RectTransform Body;

        protected void Frame(RectTransform root, string title)
        {
            Image bg = UIKit.Panel(root, "Bg", Theme.Bg, false);
            UIKit.Stretch(bg.rectTransform);
            UIText t = UIKit.Label(root, title, 64, TextAlign.Left, Theme.Accent, true);
            UIKit.Place(t.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(160f, -30f), new Vector2(1500f, 90f));
            Button back = UIKit.Button(root, "<", () => App.Nav.Pop(), 110, 90, Theme.PanelLight, 48);
            UIKit.Place((RectTransform)back.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(30f, -30f), new Vector2(110f, 90f));
            Body = UIKit.Node(root, "Body");
            UIKit.Stretch(Body, 40, 140, 40, 30);
        }
    }

    public sealed class MainMenuScreen : UIScreen
    {
        private int _devTaps;

        protected override void Build(RectTransform root)
        {
            Image bg = UIKit.Panel(root, "Bg", new Color(0.04f, 0.045f, 0.06f, 0.92f), false);
            UIKit.Stretch(bg.rectTransform);
            UIText title = UIKit.Label(root, "DUNK DUEL", 150, TextAlign.Left, Theme.Accent, true);
            UIKit.Place(title.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -90f), new Vector2(1100f, 200f));
            Button devHotspot = UIKit.Button(root, "", () =>
            {
                if (++_devTaps >= 5)
                {
                    _devTaps = 0;
                    App.Nav.Push(new DevMenuScreen());
                }
            }, 300, 160, new Color(0f, 0f, 0f, 0f), 10);
            UIKit.Place((RectTransform)devHotspot.transform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -110f), new Vector2(300f, 160f));
            UIText sub = UIKit.Label(root, Loc.T("menu.tagline", "1v1 streetball. Every possession is a duel."), 40, TextAlign.Left, Theme.Dim);
            UIKit.Place(sub.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(116f, -290f), new Vector2(1100f, 60f));

            RectTransform col = UIKit.VStack(root, "Menu", 18, 0, TextAnchor.UpperLeft);
            UIKit.Place(col, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -380f), new Vector2(700f, 700f));
            UIKit.Button(col, Loc.T("menu.duel", "Bluetooth Duel"), () => App.Nav.Push(new LobbyScreen()), 620, 92, Theme.Accent * 0.75f + Color.black * 0.25f);
            UIKit.Button(col, Loc.T("menu.quick", "Quick Match"), () => App.Nav.Push(new FighterSelectScreen(new Selection { Kind = MatchKind.Quick })), 620, 92);
            UIKit.Button(col, Loc.T("menu.crown", "Road to The Crown"), () => App.Nav.Push(new FighterSelectScreen(new Selection { Kind = MatchKind.Ladder })), 620, 92);
            UIKit.Button(col, Loc.T("menu.school", "Court School"), () => App.Nav.Push(new CourtSchoolScreen()), 620, 92);
            UIKit.Button(col, Loc.T("menu.training", "Training"), () => App.Nav.Push(new FighterSelectScreen(new Selection { Kind = MatchKind.Training })), 620, 92);
            UIKit.Button(col, Loc.T("menu.replays", "Replays"), () => App.Nav.Push(new ReplaysScreen()), 620, 92);
            UIKit.Button(col, Loc.T("menu.settings", "Settings"), () => App.Nav.Push(new SettingsScreen()), 620, 92);

            UIText cred = UIKit.Label(root, Loc.F("menu.cred", App.Progress.Cred), 34, TextAlign.Right, Theme.Accent, true);
            UIKit.Place(cred.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 30f), new Vector2(600f, 60f));
            if (App.Progress.WasTampered)
            {
                UIText warn = UIKit.Label(root, Loc.T("menu.tampered", "Progress file failed its integrity check and was reset."), 26, TextAlign.Right, Theme.Danger);
                UIKit.Place(warn.Rect, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 90f), new Vector2(900f, 40f));
            }
        }

        public override bool OnBack() => true; // the main menu is the root
    }

    /// <summary>What has been picked so far in a selection flow.</summary>
    public sealed class Selection
    {
        public MatchKind Kind;
        public int Fighter0 = -1;
        public int Fighter1 = -1;
        public int Court = -1;
        public CpuTier Tier = CpuTier.Pro;
        public bool Rival;
        public int RoundsToWin = 2;
    }

    public sealed class FighterSelectScreen : TitledScreen
    {
        private readonly Selection _sel;
        private readonly bool _pickingOpponent;

        public FighterSelectScreen(Selection sel, bool opponent = false)
        {
            _sel = sel;
            _pickingOpponent = opponent;
        }

        protected override void Build(RectTransform root)
        {
            Frame(root, _pickingOpponent ? Loc.T("select.opponent", "Choose your opponent") : Loc.T("select.fighter", "Choose your fighter"));
            SimTables t = App.Tables;
            RectTransform row = UIKit.HStack(Body, "Fighters", 24, 0, TextAnchor.UpperCenter);
            UIKit.Stretch(row);
            for (int i = 0; i < t.FighterCount; i++)
            {
                int idx = i;
                FighterStats st = t.Fighters[i];
                FighterVisual vis = App.Data.Visual(st.Def.Id);
                Image card = UIKit.Panel(row, "Card_" + st.Def.Id, Theme.Panel);
                card.rectTransform.sizeDelta = new Vector2(430f, 860f);
                UIKit.Layout(card, 430, 860);
                Image stripe = UIKit.Panel(card.transform, "Stripe", vis.Primary);
                UIKit.Place(stripe.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -10f), new Vector2(410f, 16f));
                UIText name = UIKit.Label(card.transform, st.Def.Nickname.ToUpperInvariant(), 52, TextAlign.Top, vis.Primary, true);
                UIKit.Place(name.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -40f), new Vector2(410f, 70f));
                UIText full = UIKit.Label(card.transform, st.Def.Name + "  ·  " + st.Def.Height.ToString(2) + " m", 26, TextAlign.Top, Theme.Dim);
                UIKit.Place(full.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -110f), new Vector2(410f, 40f));
                AttributeBlock a = st.Def.Attributes;
                string[] labels = { "SPD", "HND", "RNG", "FIN", "DEF", "STR" };
                int[] vals = { a.Speed, a.Handles, a.Range, a.Finishing, a.Defense, a.Strength };
                for (int k = 0; k < labels.Length; k++)
                {
                    UIText l = UIKit.Label(card.transform, labels[k], 24, TextAlign.Left, Theme.Dim, true);
                    UIKit.Place(l.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -170f - k * 44f), new Vector2(80f, 36f));
                    Image bar = UIKit.Bar(card.transform, "Attr" + k, new Color(1f, 1f, 1f, 0.1f), vals[k] == 9 ? Theme.Accent : vis.Primary, out Image fill);
                    UIKit.Place(bar.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(110f, -172f - k * 44f), new Vector2(280f, 30f));
                    fill.fillAmount = vals[k] / 10f;
                }
                string tricks = Loc.T("select.mastery", "Mastery") + ": " + st.Def.Mastery + "\n"
                    + "> " + st.Def.TrickForward.Name + "\n< " + st.Def.TrickBack.Name + "\nD " + st.Def.TrickDefense.Name + "\n"
                    + Loc.T("select.finisher", "Finisher") + ": " + st.Def.Finisher.Name;
                UIText trickText = UIKit.Label(card.transform, tricks, 26, TextAlign.TopLeft, Theme.Text);
                UIKit.Place(trickText.Rect, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(26f, -450f), new Vector2(380f, 260f));
                Button pick = UIKit.Button(card.transform, Loc.T("select.pick", "Pick"), () => Pick(idx), 380, 90, vis.Primary * 0.7f + Color.black * 0.3f);
                UIKit.Place((RectTransform)pick.transform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 24f), new Vector2(380f, 90f));
            }
        }

        private void Pick(int idx)
        {
            if (!_pickingOpponent)
            {
                _sel.Fighter0 = idx;
                App.LastFighter = idx;
                if (_sel.Kind == MatchKind.Ladder)
                {
                    App.Nav.Push(new RoadToCrownScreen(idx));
                    return;
                }
                App.Nav.Push(new FighterSelectScreen(_sel, true));
                return;
            }
            _sel.Fighter1 = idx;
            App.Nav.Push(new CourtSelectScreen(_sel));
        }
    }

    public sealed class CourtSelectScreen : TitledScreen
    {
        private readonly Selection _sel;

        public CourtSelectScreen(Selection sel)
        {
            _sel = sel;
        }

        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("select.court", "Choose a court"));
            RectTransform grid = UIKit.Grid(Body, "Courts", new Vector2(590f, 270f), new Vector2(20f, 20f), 3);
            UIKit.Stretch(grid);
            CourtDef[] courts = App.Data.Courts;
            for (int i = 0; i < courts.Length; i++)
            {
                int idx = i;
                CourtDef c = courts[i];
                Image card = UIKit.Panel(grid, "Court_" + c.Id, Color.Lerp(c.SkyBottom, Theme.Panel, 0.55f));
                Button b = card.gameObject.AddComponent<Button>();
                b.targetGraphic = card;
                b.onClick.AddListener(() => Pick(idx));
                UIText name = UIKit.Label(card.transform, c.Name, 44, TextAlign.TopLeft, Theme.Text, true);
                UIKit.Stretch(name.Rect, 24, 18, 24, 170);
                UIText tier = UIKit.Label(card.transform, c.Tier.ToString().ToUpperInvariant(), 24, TextAlign.TopRight, Theme.Accent, true);
                UIKit.Stretch(tier.Rect, 24, 26, 24, 170);
                UIText blurb = UIKit.Label(card.transform, c.Blurb, 22, TextAlign.TopLeft, Theme.Dim);
                UIKit.Stretch(blurb.Rect, 24, 80, 24, 14);
            }
        }

        private void Pick(int idx)
        {
            _sel.Court = idx;
            if (_sel.Kind == MatchKind.Training)
            {
                App.StartMatch(new MatchRequest { Kind = MatchKind.Training, Fighter0 = _sel.Fighter0, Fighter1 = _sel.Fighter1, Court = idx, RoundsToWin = 99, FirstOffense = 0 });
                return;
            }
            App.Nav.Push(new TierSelectScreen(_sel));
        }
    }

    public sealed class TierSelectScreen : TitledScreen
    {
        private readonly Selection _sel;

        public TierSelectScreen(Selection sel)
        {
            _sel = sel;
        }

        protected override void Build(RectTransform root)
        {
            Frame(root, Loc.T("select.tier", "CPU difficulty"));
            RectTransform col = UIKit.VStack(Body, "Tiers", 16, 0);
            UIKit.Stretch(col);
            string[] names = { "Rookie", "Varsity", "Pro", "All-Star", "Legend" };
            string[] notes =
            {
                Loc.T("tier.rookie", "Reacts in half a second, rarely spends Hype."),
                Loc.T("tier.varsity", "Fair execution, spends when full."),
                Loc.T("tier.pro", "Clean execution, learns your habits."),
                Loc.T("tier.allstar", "Sharp reads, saves Hype for Denials."),
                Loc.T("tier.legend", "Elite reactions and near-perfect execution."),
            };
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                Button b = UIKit.Button(col, names[i] + "   -   " + notes[i], () => Go((CpuTier)idx), 1500, 100, i == (int)_sel.Tier ? Theme.PanelLight * 1.3f : Theme.PanelLight, 34);
                _ = b;
            }
            RectTransform options = UIKit.HStack(col, "Options", 20, 0);
            UIKit.Layout(options, 1500, 110);
            options.sizeDelta = new Vector2(1500, 110);
            UIKit.Switch(options, Loc.T("select.rival", "Rival mode"), () => _sel.Rival, v => _sel.Rival = v, 600, 100);
            UIKit.Switch(options, Loc.T("select.bestof5", "Best of 5"), () => _sel.RoundsToWin == 3, v => _sel.RoundsToWin = v ? 3 : 2, 600, 100);
        }

        private void Go(CpuTier tier)
        {
            _sel.Tier = tier;
            App.StartMatch(new MatchRequest
            {
                Kind = MatchKind.Quick,
                Fighter0 = _sel.Fighter0,
                Fighter1 = _sel.Fighter1,
                Court = _sel.Court,
                Tier = tier,
                Rival = _sel.Rival,
                RoundsToWin = _sel.RoundsToWin,
            });
        }
    }

    /// <summary>Winner, match stats, Court Cred, replay save, rematch.</summary>
    public sealed class ResultsScreen : TitledScreen
    {
        private readonly MatchFlow _flow;
        private readonly GameState _final;
        private int _cred;
        private UIText _credText;

        public ResultsScreen(MatchFlow flow, GameState final, int cred)
        {
            _flow = flow;
            _final = final;
            _cred = cred;
        }

        protected override void Build(RectTransform root)
        {
            SimTables t = App.Tables;
            int winner = _final.MatchWinner;
            string winnerName = winner < 2 ? t.Fighters[_final.Get(winner).CharIndex].Def.Nickname.ToUpperInvariant() : "";
            bool youWon = winner == _flow.LocalPlayer;
            Frame(root, _flow.Request.Kind == MatchKind.Replay ? Loc.T("results.replay", "Replay finished") : (youWon ? Loc.T("results.win", "YOU WIN") : Loc.F("results.lose", winnerName)));

            for (int i = 0; i < 2; i++)
            {
                FighterState f = _final.Get(i);
                FighterStats st = t.Fighters[f.CharIndex];
                Image panel = UIKit.Panel(Body, "Stats" + i, Theme.Panel);
                UIKit.Place(panel.rectTransform, new Vector2(i == 0 ? 0f : 1f, 1f), new Vector2(i == 0 ? 0f : 1f, 1f), Vector2.zero, new Vector2(880f, 640f));
                string text = "<b>" + st.Def.Nickname.ToUpperInvariant() + "</b>   " + Loc.F("results.rounds", f.RoundsWon) + "\n\n"
                    + Loc.F("results.fg", f.FgMade, f.FgAtt) + "\n" + Loc.F("results.deep", f.DeepMade, f.DeepAtt) + "\n"
                    + Loc.F("results.dunks", f.Dunks) + "   " + Loc.F("results.posters", f.Posters) + "\n"
                    + Loc.F("results.steals", f.Steals) + "   " + Loc.F("results.blocks", f.Blocks) + "   " + Loc.F("results.charges", f.Charges) + "\n"
                    + Loc.F("results.ankles", f.AnkleBreaks) + "   " + Loc.F("results.bites", f.BitesDrawn) + "\n"
                    + Loc.F("results.finishers", f.Finishers, f.FinisherAtt) + "   " + Loc.F("results.denials", f.Denials) + "\n"
                    + Loc.F("results.turnovers", f.Turnovers);
                UIText l = UIKit.Label(panel.transform, text, 34, TextAlign.TopLeft, Theme.Text);
                UIKit.Stretch(l.Rect, 30, 24, 30, 24);
            }

            RectTransform bar = UIKit.HStack(Body, "Actions", 20, 0);
            UIKit.Place(bar, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), Vector2.zero, new Vector2(1800f, 120f));
            if (_flow.Request.Kind != MatchKind.Replay)
            {
                _credText = UIKit.Label(bar, Loc.F("results.cred", _cred), 36, TextAlign.Center, Theme.Accent, true);
                UIKit.Layout(_credText.Rect, 340, 110);
                _credText.Rect.sizeDelta = new Vector2(340, 110);
                if (App.Ads.IsReady && _cred > 0)
                {
                    Button ad = null;
                    ad = UIKit.Button(bar, Loc.T("results.doubleCred", "Double Cred"), () => App.Ads.Show(ok =>
                    {
                        if (!ok) return;
                        App.Progress.Cred += _cred;
                        _cred *= 2;
                        _credText.Text = Loc.F("results.cred", _cred);
                        App.Progress.Save();
                        ad.interactable = false;
                    }), 300, 100);
                }
            }
            UIKit.Button(bar, Loc.T("results.saveReplay", "Save replay"), SaveReplay, 300, 100);
            if (_flow.Request.Kind == MatchKind.Ladder && youWon)
                UIKit.Button(bar, Loc.T("results.continue", "Continue"), () =>
                {
                    int fighter = _final.Get(_flow.LocalPlayer).CharIndex;
                    App.QuitMatch();
                    App.Nav.Push(new RoadToCrownScreen(fighter));
                }, 300, 100);
            if (_flow.Request.Kind == MatchKind.Duel)
                UIKit.Button(bar, Loc.T("results.rematch", "Rematch"), () =>
                {
                    App.Nav.Pop();
                    App.RequestDuelRematch();
                }, 300, 100, Theme.Accent * 0.7f + Color.black * 0.3f);
            else if (_flow.Request.Kind != MatchKind.Replay)
                UIKit.Button(bar, Loc.T("results.rematch", "Rematch"), () =>
                {
                    App.Nav.Pop();
                    _flow.Restart();
                }, 300, 100, Theme.Accent * 0.7f + Color.black * 0.3f);
            UIKit.Button(bar, Loc.T("results.menu", "Main menu"), () => App.QuitMatch(), 300, 100);
        }

        private void SaveReplay()
        {
            string path = App.SaveLastReplay();
            if (_credText != null) _credText.Text = path != null ? Loc.T("results.saved", "Replay saved") : Loc.T("results.saveFailed", "Could not save");
        }

        public override bool OnBack()
        {
            App.QuitMatch();
            return true;
        }
    }
}
