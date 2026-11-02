using System.Text;
using DunkDuel.AI;
using DunkDuel.Net;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>In-match screen: HUD, touch controls, pause, the Training overlay and Court School instructions.</summary>
    public sealed class MatchScreen : UIScreen
    {
        private readonly MatchFlow _flow;
        private HudView _hud;
        private TouchControls _touch;
        private RectTransform _pause;
        private RectTransform _training;
        private UIText _trainingText;
        private UIText _inputText;
        private UIText _lessonText;
        private UIText _reconnect;
        private bool _paused;
        private CpuTier _dummyTier = CpuTier.Pro;
        private float _reconnectSeconds;

        public MatchScreen(MatchFlow flow)
        {
            _flow = flow;
        }

        public override string Name => "Match";

        protected override void Build(RectTransform root)
        {
            SimTables t = App.Tables;
            GameState s = _flow.Session.State;
            FighterView f0 = _flow.Presenter.Fighters[0], f1 = _flow.Presenter.Fighters[1];
            _hud = new HudView(root, t, _flow.LocalPlayer, Theme.PlayerColor(0, f0.Primary), Theme.PlayerColor(1, f1.Primary),
                t.Fighters[s.F0.CharIndex].Def.Nickname.ToUpperInvariant(), t.Fighters[s.F1.CharIndex].Def.Nickname.ToUpperInvariant());
            _flow.Presenter.Callout += _hud.Enqueue;
            _flow.Presenter.CalloutCancelled += _hud.Cancel;

            bool interactive = _flow.Request.Kind != MatchKind.Replay;
            if (interactive) _touch = TouchControls.Create(root, App.Input, App.Settings.LeftHanded);

            Button pause = UIKit.Button(root, "II", TogglePause, 110, 90, new Color(0f, 0f, 0f, 0.45f), 40);
            UIKit.Place((RectTransform)pause.transform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -150f), new Vector2(110f, 80f));

            _reconnect = UIKit.Label(root, "", 44, TextAlign.Center, Theme.Danger, true);
            UIKit.Place(_reconnect.Rect, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(1400f, 200f));

            if (_flow.Request.Kind == MatchKind.Training) BuildTraining(root);
            if (_flow.Request.Kind == MatchKind.School && _flow.Lesson != null)
            {
                Image panel = UIKit.Panel(root, "Lesson", new Color(0f, 0f, 0f, 0.6f));
                UIKit.Place(panel.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -250f), new Vector2(1100f, 170f));
                _lessonText = UIKit.Label(panel.transform, "", 32, TextAlign.Center, Theme.Text, false);
                UIKit.Stretch(_lessonText.Rect, 24, 12, 24, 12);
            }
            if (_flow.Request.Kind == MatchKind.Replay)
            {
                UIText tag = UIKit.Label(root, Loc.T("match.replay", "REPLAY"), 40, TextAlign.Center, Theme.Accent, true);
                UIKit.Place(tag.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 40f), new Vector2(400f, 60f));
            }
            BuildPause(root);
        }

        private void BuildTraining(RectTransform root)
        {
            _training = UIKit.Node(root, "Training");
            UIKit.Place(_training, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(20f, 60f), new Vector2(620f, 620f));
            Image bg = UIKit.Panel(_training, "Bg", new Color(0f, 0f, 0f, 0.55f));
            UIKit.Stretch(bg.rectTransform);
            _trainingText = UIKit.Label(_training, "", 24, TextAlign.TopLeft, Theme.Text, false);
            UIKit.Stretch(_trainingText.Rect, 20, 16, 20, 300);
            _inputText = UIKit.Label(_training, "", 22, TextAlign.TopLeft, Theme.Accent, false);
            UIKit.Stretch(_inputText.Rect, 20, 330, 20, 200);

            RectTransform buttons = UIKit.Grid(_training, "Buttons", new Vector2(190f, 64f), new Vector2(8f, 8f), 3);
            UIKit.Place(buttons, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 14f), new Vector2(600f, 210f));
            Small(buttons, Loc.T("training.hitboxes", "Hitboxes"), () => _flow.Presenter.SetHitboxes(!_flow.Presenter.Hitboxes.Visible));
            Small(buttons, Loc.T("training.reset", "Reset"), () => _flow.ResetPositions());
            Small(buttons, Loc.T("training.step", "Step"), () =>
            {
                _flow.Presenter.Driver.Paused = true;
                _flow.Presenter.Driver.FrameStepMode = true;
                _flow.Presenter.Driver.StepOnce();
            });
            Small(buttons, Loc.T("training.stand", "Dummy: stand"), () => _flow.SetDummy(DummyMode.Stand, _dummyTier));
            Small(buttons, Loc.T("training.cpu", "Dummy: CPU"), () =>
            {
                _dummyTier = (CpuTier)(((int)_dummyTier + 1) % 5);
                _flow.SetDummy(DummyMode.Cpu, _dummyTier);
            });
            Small(buttons, Loc.T("training.run", "Run"), () =>
            {
                _flow.Presenter.Driver.FrameStepMode = false;
                _flow.Presenter.Driver.Paused = false;
            });
            Small(buttons, Loc.T("training.record", "Record"), () => _flow.SetDummy(DummyMode.Record, _dummyTier));
            Small(buttons, Loc.T("training.play", "Playback"), () => _flow.SetDummy(DummyMode.Playback, _dummyTier));
            Small(buttons, Loc.T("training.hype", "Inf. Hype"), () =>
            {
                MatchRequest r = _flow.Request.Clone();
                r.InfiniteHype = !r.InfiniteHype;
                r.Seed = 0;
                App.Nav.Pop();
                _flow.Start(r);
            });
        }

        private static void Small(RectTransform parent, string label, System.Action onClick)
        {
            UIKit.Button(parent, label, onClick, 190, 64, Theme.PanelLight, 22);
        }

        private void BuildPause(RectTransform root)
        {
            _pause = UIKit.Node(root, "Pause");
            UIKit.Stretch(_pause);
            Image scrim = UIKit.Panel(_pause, "Scrim", Theme.Scrim, false);
            UIKit.Stretch(scrim.rectTransform);
            RectTransform col = UIKit.VStack(_pause, "Menu", 20, 0);
            UIKit.Place(col, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(600f, 700f));
            UIText title = UIKit.Label(col, Loc.T("pause.title", "PAUSED"), 72, TextAlign.Center, Theme.Accent, true);
            UIKit.Layout(title.Rect, 600, 110);
            title.Rect.sizeDelta = new Vector2(600, 110);
            UIKit.Button(col, Loc.T("pause.resume", "Resume"), TogglePause);
            if (_flow.Request.Kind != MatchKind.Duel) UIKit.Button(col, Loc.T("pause.restart", "Restart"), () =>
            {
                App.Nav.Pop();
                _flow.Restart();
            });
            UIKit.Button(col, Loc.T("pause.settings", "Settings"), () => App.Nav.Push(new SettingsScreen()));
            UIKit.Button(col, Loc.T("pause.quit", "Quit match"), () =>
            {
                if (_flow.Request.Kind == MatchKind.Duel) _flow.Request.Lobby.Leave();
                App.QuitMatch();
            }, 520, 110, new Color(0.45f, 0.15f, 0.15f, 0.95f));
            _pause.gameObject.SetActive(false);
        }

        private void TogglePause()
        {
            _paused = !_paused;
            _pause.gameObject.SetActive(_paused);
            // Netplay never pauses the shared sim: the overlay just covers the controls.
            if (_flow.Request.Kind != MatchKind.Duel)
            {
                _flow.Presenter.Driver.Paused = _paused;
                _flow.Presenter.Driver.FrameStepMode = false;
            }
            if (_touch != null)
            {
                _touch.ReleaseAll();
                _touch.gameObject.SetActive(!_paused);
            }
        }

        public override bool OnBack()
        {
            TogglePause();
            return true;
        }

        public override void OnHide()
        {
            if (_touch != null) _touch.ReleaseAll();
        }

        public override void Tick(float dt)
        {
            if (_flow.Presenter == null) return;
            GameState s = _flow.Presenter.Driver.Current;
            _hud.Update(s, dt);
            int local = _flow.LocalPlayer;
            FighterState me = s.Get(local);
            FinisherDefinition fd = App.Tables.Fighters[me.CharIndex].Def.Finisher;
            bool ready = me.Hype >= App.Tables.T.Hype.FinisherCost && me.X >= fd.ZoneMin && me.X <= fd.ZoneMax && s.Ball.Mode == BallMode.Held && s.Ball.Holder == local;
            bool deny = s.Phase == Phase.Finisher && s.Fin.Attacker != local && s.Fin.HasRing && !s.Fin.Attempted;
            bool inWindow = deny && s.Fin.Frame >= s.Fin.DenyStart && s.Fin.Frame < s.Fin.DenyStart + s.Fin.DenyFrames;
            if (_touch != null) _touch.SetFinisherState(ready, deny, inWindow);

            if (_training != null) UpdateTraining(s);
            if (_lessonText != null && _flow.Lesson != null)
            {
                LessonRunner l = _flow.Lesson;
                if (l.Passed && PlayerPrefs.GetInt("dd.school." + l.Def.Id, 0) == 0)
                {
                    PlayerPrefs.SetInt("dd.school." + l.Def.Id, 1);
                    PlayerPrefs.Save();
                    Haptics.Play(HapticKind.Success);
                }
                _lessonText.Text = "<b>" + l.Def.Title + "</b>\n" + l.Def.Body + "\n" + (l.Passed ? Loc.T("school.passed", "LESSON PASSED!") : Loc.F("school.progress", l.Progress, l.Def.Count));
            }
            if (_flow.Request.Kind == MatchKind.Duel) UpdateNet(dt);
        }

        private void UpdateNet(float dt)
        {
            NetLobby lobby = _flow.Request.Lobby;
            if (lobby.Session != null)
            {
                NetStats st = lobby.Session.Stats;
                _hud.SetNetStats(Loc.F("net.overlay", st.RttMicros / 1000, st.JitterMicros / 1000, st.LossPermille / 10f, st.InputDelay, st.RollbackP95, st.Desyncs) + "  " + new string('|', st.QualityBars + 1));
            }
            if (lobby.State == LobbyState.Paused)
            {
                _reconnectSeconds += dt;
                int left = Mathf.Max(0, 20 - Mathf.FloorToInt(_reconnectSeconds));
                _reconnect.Text = Loc.F("net.reconnecting", left);
            }
            else
            {
                _reconnectSeconds = 0f;
                _reconnect.Text = lobby.State == LobbyState.Failed ? Loc.T("net.noContest", "Connection lost. No contest.") : "";
            }
        }

        private void UpdateTraining(in GameState s)
        {
            SimTables t = App.Tables;
            var sb = new StringBuilder(256);
            for (int i = 0; i < 2; i++)
            {
                FighterState f = s.Get(i);
                FighterStats st = t.Fighters[f.CharIndex];
                sb.Append(i == 0 ? "P1 " : "P2 ").Append(st.Def.Nickname).Append(": ").Append(f.Action).Append(" f").Append(f.ActionFrame);
                MoveDefinition m = f.Action == ActionId.Shake ? t.Shakes[f.MoveSlot] : (f.Action == ActionId.Trick ? st.Tricks[f.MoveSlot] : null);
                if (m != null)
                {
                    sb.Append("  ").Append(m.Name).Append("  S").Append(m.Startup).Append(" A").Append(m.Active).Append(" R").Append(m.Recovery);
                    if (m.BaitEnd > m.BaitStart) sb.Append("  bait ").Append(m.BaitStart).Append('-').Append(m.BaitEnd);
                    if (m.ExposedEnd > m.ExposedStart) sb.Append("  exposed ").Append(m.ExposedStart).Append('-').Append(m.ExposedEnd);
                }
                if (f.Action == ActionId.GatherJumper || f.Action == ActionId.GatherFinish) sb.Append("  peak ").Append(f.Peak);
                if (f.Action == ActionId.Wall) sb.Append("  planted ").Append(f.PlantFrames);
                sb.Append("\n   Hype ").Append(f.Hype).Append("  Balance ").Append(f.Balance.ToString(0)).Append("  Streak ").Append(f.Streak).Append('\n');
            }
            if (s.Shot.Active) sb.Append("Shot: ").Append(s.Shot.Grade).Append(' ').Append(s.Shot.Zone).Append("  P=").Append(s.Shot.MakeChance.ToString(2)).Append(" Q=").Append(s.Shot.Contest.ToString(2)).Append('\n');
            sb.Append(Loc.T("training.dummy", "Dummy")).Append(": ").Append(_flow.Dummy).Append(_flow.Dummy == DummyMode.Cpu ? " (" + _dummyTier + ")" : "");
            _trainingText.Text = sb.ToString();

            if (App.Settings.InputDisplay)
            {
                var ib = new StringBuilder(128);
                HumanInput h = App.Input;
                for (int k = 0; k < 10; k++)
                {
                    int idx = (h.HistoryHead - 1 - k + h.History.Length) % h.History.Length;
                    ib.Append(Describe(h.History[idx])).Append('\n');
                }
                _inputText.Text = ib.ToString();
            }
        }

        private static string Describe(FrameInput i)
        {
            if (i.Bits == 0) return "-";
            string d = "";
            switch (i.Dir)
            {
                case StickDir.Toward: d = ">"; break;
                case StickDir.Away: d = "<"; break;
                case StickDir.Down: d = "v"; break;
                case StickDir.Up: d = "^"; break;
                case StickDir.DownToward: d = "v>"; break;
                case StickDir.DownAway: d = "<v"; break;
                case StickDir.UpToward: d = "^>"; break;
                case StickDir.UpAway: d = "<^"; break;
            }
            if (i.Has(Buttons.Shoot)) d += " SHOOT";
            if (i.Has(Buttons.Shake)) d += " SHAKE";
            if (i.Has(Buttons.Trick)) d += " TRICK";
            if (i.Has(Buttons.Turbo)) d += " TURBO";
            if (i.Has(Buttons.Finisher)) d += " FIN";
            if (i.Has(Buttons.Amp)) d += " AMP";
            return d;
        }
    }
}
