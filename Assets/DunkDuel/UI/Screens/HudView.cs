using System.Collections.Generic;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>
    /// Match HUD: score bars filling toward the round target, 3-segment Hype, Balance, game and shot clocks,
    /// round pips, callouts, CLEAR indicator, Heat Check glow, Rhythm stacks, combo counter and the Denial ring.
    /// </summary>
    public sealed class HudView
    {
        private sealed class Side
        {
            public Image ScoreFill;
            public UIText Score;
            public UIText Name;
            public Image[] Hype = new Image[3];
            public Image BalanceFill;
            public Image[] Pips = new Image[3];
            public Image Glow;
            public UIText Tags;
            public float ShownScore;
            public Color Color;
        }

        private readonly Side[] _sides = { new Side(), new Side() };
        private readonly SimTables _tables;
        private readonly int _local;
        private UIText _clock;
        private UIText _shotClock;
        private UIText _clear;
        private UIText _callout;
        private UIText _calloutSub;
        private float _calloutTime;
        private long _calloutKey;
        private Image _denyRing;
        private Image _denyTarget;
        private UIText _youTag;
        private UIText _netStats;
        public RectTransform Root { get; }

        private struct PendingCallout
        {
            public CalloutInfo Info;
        }

        private readonly Queue<PendingCallout> _queue = new Queue<PendingCallout>();

        public HudView(RectTransform parent, SimTables tables, int localPlayer, Color c0, Color c1, string name0, string name1)
        {
            _tables = tables;
            _local = localPlayer;
            Root = UIKit.Node(parent, "HUD");
            UIKit.Stretch(Root);
            BuildSide(0, c0, name0);
            BuildSide(1, c1, name1);

            Image clockBg = UIKit.Panel(Root, "Clock", new Color(0f, 0f, 0f, 0.55f));
            UIKit.Place(clockBg.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -14f), new Vector2(230f, 130f));
            _clock = UIKit.Label(clockBg.transform, "1:30", 56, TextAlign.Top, Theme.Text, true);
            UIKit.Stretch(_clock.Rect, 0, 6, 0, 50);
            _shotClock = UIKit.Label(clockBg.transform, "8", 38, TextAlign.Bottom, Theme.Accent, true);
            UIKit.Stretch(_shotClock.Rect, 0, 70, 0, 6);

            _clear = UIKit.Label(Root, Loc.T("hud.clear", "CLEAR IT"), 40, TextAlign.Center, Theme.Danger, true);
            UIKit.Place(_clear.Rect, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -160f), new Vector2(500f, 60f));

            _callout = UIKit.Label(Root, "", 110, TextAlign.Center, Theme.Accent, true);
            UIKit.Place(_callout.Rect, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1600f, 160f));
            _calloutSub = UIKit.Label(Root, "", 40, TextAlign.Center, Theme.Text, false);
            UIKit.Place(_calloutSub.Rect, new Vector2(0.5f, 0.62f), new Vector2(0.5f, 0.5f), new Vector2(0f, -95f), new Vector2(1400f, 60f));

            _denyTarget = UIKit.Circle(Root, "DenyTarget", new Color(1f, 1f, 1f, 0.8f), true);
            UIKit.Place(_denyTarget.rectTransform, new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 220f));
            _denyRing = UIKit.Circle(Root, "DenyRing", new Color(1f, 0.3f, 0.35f, 0.9f), true);
            UIKit.Place(_denyRing.rectTransform, new Vector2(0.5f, 0.4f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(220f, 220f));
            _denyRing.raycastTarget = false;
            _denyTarget.raycastTarget = false;

            _youTag = UIKit.Label(Root, Loc.T("hud.you", "YOU"), 30, TextAlign.Center, Theme.Accent, true);
            UIKit.Place(_youTag.Rect, new Vector2(_local == 0 ? 0f : 1f, 1f), new Vector2(_local == 0 ? 0f : 1f, 1f), new Vector2(_local == 0 ? 30f : -30f, -186f), new Vector2(120f, 40f));

            _netStats = UIKit.Label(Root, "", 24, TextAlign.TopLeft, Theme.Dim, false);
            UIKit.Place(_netStats.Rect, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(20f, 20f), new Vector2(700f, 140f));
        }

        private void BuildSide(int i, Color c, string name)
        {
            Side s = _sides[i];
            s.Color = c;
            float ax = i == 0 ? 0f : 1f;
            float dir = i == 0 ? 1f : -1f;
            Image panel = UIKit.Panel(Root, "Side" + i, new Color(0f, 0f, 0f, 0.5f));
            UIKit.Place(panel.rectTransform, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * 20f, -14f), new Vector2(700f, 168f));
            s.Glow = UIKit.Panel(panel.transform, "HeatGlow", new Color(1f, 0.4f, 0.15f, 0f));
            UIKit.Stretch(s.Glow.rectTransform, -8, -8, -8, -8);
            s.Glow.raycastTarget = false;

            s.Name = UIKit.Label(panel.transform, name, 34, i == 0 ? TextAlign.TopLeft : TextAlign.TopRight, Theme.Text, true);
            UIKit.Stretch(s.Name.Rect, 22, 10, 22, 110);
            s.Tags = UIKit.Label(panel.transform, "", 26, i == 0 ? TextAlign.TopRight : TextAlign.TopLeft, Theme.Accent, true);
            UIKit.Stretch(s.Tags.Rect, 22, 12, 22, 110);

            Image bar = UIKit.Bar(panel.transform, "ScoreBar", new Color(1f, 1f, 1f, 0.1f), c, out s.ScoreFill, i == 1);
            UIKit.Place(bar.rectTransform, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * 22f, -58f), new Vector2(540f, 44f));
            s.Score = UIKit.Label(panel.transform, "0", 64, TextAlign.Center, Theme.Text, true);
            UIKit.Place(s.Score.Rect, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * 572f, -44f), new Vector2(110f, 80f));

            for (int k = 0; k < 3; k++)
            {
                Image seg = UIKit.Panel(panel.transform, "Hype" + k, new Color(1f, 1f, 1f, 0.12f));
                UIKit.Place(seg.rectTransform, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * (22f + k * 122f), -112f), new Vector2(114f, 22f));
                s.Hype[k] = seg;
            }
            Image bal = UIKit.Bar(panel.transform, "Balance", new Color(1f, 1f, 1f, 0.1f), Theme.Balance, out s.BalanceFill, i == 1);
            UIKit.Place(bal.rectTransform, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * 22f, -142f), new Vector2(360f, 16f));
            for (int k = 0; k < 3; k++)
            {
                Image pip = UIKit.Circle(panel.transform, "Pip" + k, new Color(1f, 1f, 1f, 0.2f));
                UIKit.Place(pip.rectTransform, new Vector2(ax, 1f), new Vector2(ax, 1f), new Vector2(dir * (420f + k * 40f), -136f), new Vector2(28f, 28f));
                s.Pips[k] = pip;
            }
        }

        public void Enqueue(CalloutInfo c)
        {
            _queue.Enqueue(new PendingCallout { Info = c });
        }

        public void Cancel(long key)
        {
            if (_calloutKey == key && key != 0)
            {
                _calloutTime = 0f;
                _callout.Text = "";
                _calloutSub.Text = "";
            }
        }

        public void SetNetStats(string text) => _netStats.Text = text;

        public void Update(in GameState s, float dt)
        {
            int target = _tables.T.Match.RoundTargetPoints;
            for (int i = 0; i < 2; i++)
            {
                FighterState f = s.Get(i);
                Side side = _sides[i];
                side.ShownScore = Mathf.MoveTowards(side.ShownScore, f.Score, dt * 6f);
                side.ScoreFill.fillAmount = Mathf.Clamp01(side.ShownScore / target);
                side.Score.Text = f.Score.ToString();
                int bar = _tables.T.Hype.Bar;
                for (int k = 0; k < 3; k++)
                {
                    float fill = Mathf.Clamp01((f.Hype - k * bar) / (float)bar);
                    side.Hype[k].color = fill >= 1f ? Theme.Hype : Color.Lerp(new Color(1f, 1f, 1f, 0.12f), new Color(Theme.Hype.r, Theme.Hype.g, Theme.Hype.b, 0.55f), fill);
                }
                side.BalanceFill.fillAmount = f.Balance.ToFloat() / _tables.T.Balance.Max.ToFloat();
                side.BalanceFill.color = f.Balance.ToFloat() < 30f ? Theme.Danger : Theme.Balance;
                for (int k = 0; k < 3; k++)
                {
                    bool shown = k < s.RoundsToWin;
                    side.Pips[k].enabled = shown;
                    side.Pips[k].color = k < f.RoundsWon ? side.Color : new Color(1f, 1f, 1f, 0.2f);
                }
                bool heat = (f.Status & StatusFlag.HeatCheck) != 0;
                side.Glow.color = heat ? new Color(1f, 0.4f, 0.15f, 0.35f + 0.25f * Mathf.Sin(Time.time * 6f)) : new Color(1f, 0.4f, 0.15f, 0f);
                string tags = "";
                FighterStats st = _tables.Fighters[f.CharIndex];
                if (st.Def.Mastery == Mastery.Rhythm && f.Rhythm > 0) tags += new string('|', f.Rhythm) + " RHYTHM  ";
                if (f.ComboLinks > 1 && (f.Action == ActionId.Shake || f.ComboTimer > 0)) tags += "x" + f.ComboLinks + " ";
                if (heat) tags += "HEAT ";
                side.Tags.Text = tags;
            }

            int secs = Mathf.CeilToInt(s.RoundClock / 60f);
            _clock.Text = s.SuddenDeath ? Loc.T("hud.sudden", "OT") : (secs / 60) + ":" + (secs % 60).ToString("00");
            bool showShot = s.Ball.Mode == BallMode.Held && s.Phase == Phase.Live && s.Mode != MatchMode.Training;
            _shotClock.Text = showShot ? Mathf.CeilToInt(s.ShotClock / 60f).ToString() : "";
            _shotClock.Color = s.ShotClock < 120 ? Theme.Danger : Theme.Accent;
            _clear.SetActive(s.NeedsClear && s.Ball.Mode == BallMode.Held && s.Ball.Holder == _local);

            // Denial ring: closes on the target at the denial window.
            bool ring = s.Phase == Phase.Finisher && s.Fin.Attacker != _local && s.Fin.HasRing && !s.Fin.Attempted;
            _denyRing.enabled = ring;
            _denyTarget.enabled = ring;
            if (ring)
            {
                float framesTo = s.Fin.DenyStart + s.Fin.DenyFrames * 0.5f - s.Fin.Frame;
                float scale = 1f + Mathf.Max(-0.4f, framesTo / 30f);
                _denyRing.rectTransform.localScale = new Vector3(scale, scale, 1f);
                bool inside = s.Fin.Frame >= s.Fin.DenyStart && s.Fin.Frame < s.Fin.DenyStart + s.Fin.DenyFrames;
                _denyTarget.color = inside ? Theme.Good : new Color(1f, 1f, 1f, 0.6f);
            }

            UpdateCallouts(dt);
        }

        private void UpdateCallouts(float dt)
        {
            if (_calloutTime > 0f)
            {
                _calloutTime -= dt;
                float t = Mathf.Clamp01(1f - _calloutTime / 1.1f);
                float pop = t < 0.15f ? Mathf.Lerp(1.6f, 1f, t / 0.15f) : 1f;
                _callout.Rect.localScale = new Vector3(pop, pop, 1f);
                Color c = _callout.Color;
                c.a = _calloutTime < 0.3f ? _calloutTime / 0.3f : 1f;
                _callout.Color = c;
                if (_calloutTime <= 0f)
                {
                    _callout.Text = "";
                    _calloutSub.Text = "";
                }
            }
            if (_queue.Count > 0 && (_calloutTime <= 0.55f))
            {
                CalloutInfo c = _queue.Dequeue().Info;
                _calloutKey = c.Key;
                _calloutTime = Mathf.Max(0.2f, 1.1f - c.Age);
                _callout.Text = Loc.T(c.Id, c.Text);
                Color col = c.Actor == 0 ? _sides[0].Color : (c.Actor == 1 ? _sides[1].Color : Theme.Accent);
                if (c.Style == CalloutStyle.Big || c.Style == CalloutStyle.Finisher) col = Theme.Accent;
                _callout.Color = col;
                _callout.Size = c.Style == CalloutStyle.Info ? 64 : (c.Style == CalloutStyle.Finisher ? 130 : 110);
                _calloutSub.Text = c.Style == CalloutStyle.Finisher ? Loc.T("hud.finisherSub", "SIGNATURE FINISHER") : "";
            }
        }
    }
}
