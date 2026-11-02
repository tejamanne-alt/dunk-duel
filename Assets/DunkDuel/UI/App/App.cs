using System;
using System.IO;
using DunkDuel.AI;
using DunkDuel.Net;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using UnityEngine;
using UnityEngine.UI;

namespace DunkDuel.UI
{
    /// <summary>
    /// Creates the app root in any scene (so Play works from an empty scene and the Boot scene stays a single
    /// marker object, built by ProjectSetup through Editor APIs rather than hand-written YAML).
    /// </summary>
    public static class AppBootstrap
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Boot()
        {
            if (UnityEngine.Object.FindAnyObjectByType<App>() != null) return;
            var go = new GameObject("DunkDuelApp");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<App>();
        }
    }

    /// <summary>Root of the running game: loads data, owns services, navigation and the current match.</summary>
    public sealed class App : MonoBehaviour
    {
        public static App Instance { get; private set; }

        public SimTables Tables { get; private set; }
        public CpuTiers Tiers { get; private set; }
        public PresentationData Data { get; private set; }
        public MaterialLibrary Materials { get; private set; }
        public AudioDirector Audio { get; private set; }
        public QualityManager Quality { get; private set; }
        public GameSettings Settings { get; private set; }
        public Progression Progress { get; private set; }
        public LadderProgress Ladder { get; } = new LadderProgress();
        public HumanInput Input { get; } = new HumanInput();
        public UIRoot UI { get; private set; }
        public Navigator Nav { get; private set; }
        public MatchFlow Flow { get; private set; }
        public PresentationContext PresentationContext { get; private set; }
        public IRewardedAdProvider Ads = new NullRewardedAdProvider();
        public INetClock NetClock { get; } = new StopwatchClock();
        public NetLobby ActiveLobby { get; private set; }
        public int LastFighter = -1;

        private Replay _lastReplay;
        private UIText _toast;
        private float _toastTime;
        private Camera _menuCamera;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            Screen.orientation = ScreenOrientation.AutoRotation;
            Screen.autorotateToPortrait = false;
            Screen.autorotateToPortraitUpsideDown = false;
            Screen.autorotateToLandscapeLeft = true;
            Screen.autorotateToLandscapeRight = true;
            Screen.sleepTimeout = SleepTimeout.NeverSleep;

            Settings = GameSettings.Load();
            Loc.Load(Settings.Language);
            Theme.Apply(Settings.Colorblind, Settings.LargeText);
            UI = UIRoot.Create(transform);
            Nav = new Navigator(UI.ScreenLayer);
            BuildToast();

            try
            {
                var source = new ResourcesDataSource();
                Tables = SimTables.Load(source);
                Tiers = CpuTiers.Load(Tables, source);
                Data = PresentationData.Load();
            }
            catch (Exception e)
            {
                Debug.LogError("Dunk Duel: data failed to load: " + e);
                ShowFatal(e.Message);
                return;
            }

            var qGo = new GameObject("Quality");
            qGo.transform.SetParent(transform, false);
            Quality = qGo.AddComponent<QualityManager>();
            Quality.ThermalProbe = Haptics.ThermalState;
            Quality.Mode = Settings.Quality;
            Quality.Apply(Settings.Quality);
            Materials = new MaterialLibrary();
            Audio = AudioDirector.Create(transform);
            Haptics.Prepare();
            Progress = Progression.Load();
            Input.Bindings.LoadOverrides(Settings.Bindings);
            Settings.Apply(this);
            PresentationContext = new PresentationContext
            {
                Tables = Tables,
                Data = Data,
                Materials = Materials,
                Audio = Audio,
                Quality = Quality,
            };
            Flow = new MatchFlow(this);
            Flow.Ended += OnMatchEnded;
            EnsureMenuCamera();
            Nav.Push(new MainMenuScreen());
        }

        private void EnsureMenuCamera()
        {
            // A plain camera behind the menus when no match is running (screen-space overlay UI needs no camera).
            var go = new GameObject("MenuCamera");
            go.transform.SetParent(transform, false);
            _menuCamera = go.AddComponent<Camera>();
            _menuCamera.clearFlags = CameraClearFlags.SolidColor;
            _menuCamera.backgroundColor = Theme.Bg;
            _menuCamera.cullingMask = 0;
            _menuCamera.depth = -10f;
        }

        private void ShowFatal(string message)
        {
            Image bg = UIKit.Panel(UI.PopupLayer, "Fatal", Theme.Bg, false);
            UIKit.Stretch(bg.rectTransform);
            UIText t = UIKit.Label(bg.transform, "Dunk Duel could not start:\n" + message, 40, TextAlign.Center, Theme.Danger, true);
            UIKit.Stretch(t.Rect, 80, 80, 80, 80);
        }

        private void BuildToast()
        {
            _toast = UIKit.Label(UI.PopupLayer, "", 34, TextAlign.Center, Theme.Text, true);
            UIKit.Place(_toast.Rect, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 60f), new Vector2(1600f, 90f));
        }

        public void Toast(string message)
        {
            _toast.Text = message;
            _toastTime = 3.5f;
        }

        private void Update()
        {
            float dt = Time.unscaledDeltaTime;
            bool back = Input.Bindings.BackPressed();
            Nav?.Tick(dt, back);
            Flow?.Tick(dt);
            if (_menuCamera != null) _menuCamera.enabled = Flow == null || !Flow.Active;
            if (ActiveLobby != null && (Flow == null || Flow.Session == null || Flow.Session.IsOver))
            {
                ActiveLobby.Update();
                if (ActiveLobby.State == LobbyState.InMatch && ActiveLobby.Session != null && (Flow.Session != ActiveLobby.Session))
                {
                    // Rematch agreed: both phones start the new match.
                    Nav.ResetTo(new MainMenuScreen());
                    Flow.Start(new MatchRequest { Kind = MatchKind.Duel, Lobby = ActiveLobby });
                }
            }
            if (_toastTime > 0f)
            {
                _toastTime -= dt;
                if (_toastTime <= 0f) _toast.Text = "";
            }
        }

        // ------------------------------------------------------------------ match lifecycle

        public void StartMatch(MatchRequest r)
        {
            Flow.Start(r);
        }

        public void StartDuel(NetLobby lobby)
        {
            ActiveLobby = lobby;
            Flow.Start(new MatchRequest { Kind = MatchKind.Duel, Lobby = lobby });
        }

        public void RequestDuelRematch()
        {
            if (ActiveLobby == null) return;
            int fighter = LastFighter >= 0 ? LastFighter : 0;
            ActiveLobby.RequestRematch(fighter);
            Toast(Loc.T("lobby.rematchWaiting", "Waiting for the other player to accept the rematch..."));
        }

        private void OnMatchEnded(MatchFlow flow)
        {
            GameState final = flow.Session.State;
            if (flow.Session is LocalSession ls) _lastReplay = ls.Recorder.Replay;
            else if (flow.Session is RollbackSession rs) _lastReplay = rs.Recorder.Replay;
            if (_lastReplay != null) _lastReplay.FinalHash = final.Hash();
            int cred = 0;
            MatchRequest r = flow.Request;
            if (r.Kind == MatchKind.Quick || r.Kind == MatchKind.Ladder || r.Kind == MatchKind.Duel)
            {
                int local = flow.LocalPlayer;
                FighterState me = final.Get(local);
                bool won = final.MatchWinner == local;
                cred = Progress.AwardMatch(me.CharIndex, won, me);
                Progress.Save();
                if (r.Kind == MatchKind.Ladder && won) Ladder.Advance(Tables.Fighters[me.CharIndex].Def.Id);
            }
            Nav.Push(new ResultsScreen(flow, final, cred));
        }

        /// <summary>Leaves the current match (and duel link) and returns to the main menu.</summary>
        public void QuitMatch()
        {
            Flow.Stop();
            if (ActiveLobby != null)
            {
                ITransport t = ActiveLobby.Transport;
                ActiveLobby.Leave();
                ActiveLobby.Dispose();
                t.Dispose();
                ActiveLobby = null;
            }
            Nav.ResetTo(new MainMenuScreen());
        }

        /// <summary>Saves the last finished match as a replay; returns the path or null.</summary>
        public string SaveLastReplay()
        {
            if (_lastReplay == null) return null;
            try
            {
                string dir = ReplaysScreen.Folder;
                Directory.CreateDirectory(dir);
                MatchSetup s = _lastReplay.Setup;
                string name = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + Tables.Fighters[s.Fighter0].Def.Id + "_vs_" + Tables.Fighters[s.Fighter1].Def.Id + ".ddreplay";
                string path = Path.Combine(dir, name);
                File.WriteAllBytes(path, _lastReplay.ToBytes());
                return path;
            }
            catch (Exception e)
            {
                Debug.LogWarning("Dunk Duel: replay save failed: " + e.Message);
                return null;
            }
        }

        private void OnApplicationPause(bool paused)
        {
            if (paused)
            {
                Progress?.Save();
                Settings?.Save();
            }
        }
    }
}
