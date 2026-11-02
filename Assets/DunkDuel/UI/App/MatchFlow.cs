using System;
using DunkDuel.AI;
using DunkDuel.Net;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Session;
using UnityEngine;

namespace DunkDuel.UI
{
    public enum MatchKind : byte { Quick = 0, Training = 1, Ladder = 2, School = 3, Replay = 4, Duel = 5 }

    public enum DummyMode : byte { Stand = 0, Cpu = 1, Record = 2, Playback = 3 }

    /// <summary>Everything needed to start a match.</summary>
    public sealed class MatchRequest
    {
        public MatchKind Kind;
        public int Fighter0;
        public int Fighter1;
        public int Court;
        public CpuTier Tier = CpuTier.Pro;
        public bool Rival;
        public int RoundsToWin = 2;
        public ulong Seed;
        public int FirstOffense = -1;
        public bool InfiniteHype;
        public int LadderStage = -1;
        public Replay Replay;
        public NetLobby Lobby;
        public LessonDef Lesson;

        public MatchRequest Clone() => (MatchRequest)MemberwiseClone();
    }

    /// <summary>An input source whose target can be swapped at runtime (training record/playback, lessons).</summary>
    public sealed class SwitchableInput : IInputSource
    {
        public IInputSource Target;
        public SwitchableInput(IInputSource target) { Target = target; }
        public FrameInput NextInput(in GameState state, int player) => Target != null ? Target.NextInput(state, player) : FrameInput.None;
    }

    /// <summary>Feeds a replay's recorded inputs back by tick.</summary>
    public sealed class ReplayInput : IInputSource
    {
        private readonly Replay _replay;
        private readonly int _player;
        public ReplayInput(Replay replay, int player)
        {
            _replay = replay;
            _player = player;
        }

        public FrameInput NextInput(in GameState state, int player)
        {
            int t = state.Tick;
            if (t < 0 || t >= _replay.Ticks) return FrameInput.None;
            return _player == 0 ? _replay.P0(t) : _replay.P1(t);
        }
    }

    /// <summary>Creates sessions and presenters for every mode, and tears them down. One match at a time.</summary>
    public sealed class MatchFlow
    {
        private readonly App _app;
        private bool _endHandled;
        private float _endDelay;

        public MatchRequest Request { get; private set; }
        public IMatchSession Session { get; private set; }
        public MatchPresenter Presenter { get; private set; }
        public CpuPlayer Cpu { get; private set; }
        public RivalController Rival { get; private set; }
        public SwitchableInput P0 { get; private set; }
        public SwitchableInput P1 { get; private set; }
        public ScriptedInput Recording { get; } = new ScriptedInput { Loop = true };
        public DummyMode Dummy { get; private set; }
        public LessonRunner Lesson { get; private set; }
        public GameState InitialState { get; private set; }
        public int LocalPlayer { get; private set; }
        public MatchScreen Screen { get; private set; }
        public bool Active => Presenter != null;

        public event Action<MatchFlow> Ended;

        public MatchFlow(App app)
        {
            _app = app;
        }

        public void Start(MatchRequest r)
        {
            Stop();
            Request = r;
            if (r.Seed == 0) r.Seed = (ulong)DateTime.UtcNow.Ticks ^ 0x5DEECE66DUL;
            int court = r.Court;
            LocalPlayer = 0;
            Cpu = null;
            Rival = null;
            Lesson = null;
            switch (r.Kind)
            {
                case MatchKind.Duel:
                    Session = r.Lobby.Session;
                    LocalPlayer = r.Lobby.IsHost ? 0 : 1;
                    court = r.Lobby.Config.Court;
                    break;
                case MatchKind.Replay:
                    Session = new LocalSession(_app.Tables, r.Replay.Setup, new ReplayInput(r.Replay, 0), new ReplayInput(r.Replay, 1));
                    court = r.Replay.Setup.CourtIndex;
                    break;
                default:
                {
                    var setup = new MatchSetup
                    {
                        Fighter0 = r.Fighter0,
                        Fighter1 = r.Fighter1,
                        Seed = r.Seed,
                        RoundsToWin = r.RoundsToWin,
                        Mode = r.Kind == MatchKind.Training || r.Kind == MatchKind.School ? MatchMode.Training : MatchMode.Versus,
                        Simple0 = _app.Settings.SimpleMode,
                        FirstOffense = r.FirstOffense >= 0 ? r.FirstOffense : (int)(r.Seed & 1),
                        InfiniteHype = r.InfiniteHype,
                        CourtIndex = court,
                    };
                    P0 = new SwitchableInput(_app.Input);
                    IInputSource opponent;
                    if (r.Kind == MatchKind.School && r.Lesson != null)
                    {
                        Lesson = new LessonRunner(r.Lesson, _app.Tables);
                        opponent = Lesson.Dummy;
                    }
                    else if (r.Kind == MatchKind.Training)
                    {
                        Cpu = new CpuPlayer(_app.Tables, _app.Tiers, r.Tier, 1, r.Seed * 31 + 7);
                        opponent = null;
                    }
                    else
                    {
                        Cpu = new CpuPlayer(_app.Tables, _app.Tiers, r.Tier, 1, r.Seed * 31 + 7);
                        if (r.Rival) Rival = new RivalController(_app.Tiers, Cpu, r.Tier);
                        opponent = Cpu;
                    }
                    P1 = new SwitchableInput(opponent);
                    Session = new LocalSession(_app.Tables, setup, P0, P1);
                    Dummy = r.Kind == MatchKind.Training ? DummyMode.Stand : DummyMode.Cpu;
                    break;
                }
            }
            InitialState = Session.State;
            _app.PresentationContext.Outfits[0] = _app.Progress.Outfit[Mathf.Clamp(Session.State.F0.CharIndex, 0, 7)];
            _app.PresentationContext.Outfits[1] = 0;
            _app.PresentationContext.ReducedMotion = _app.Settings.ReducedMotion;
            _app.PresentationContext.ReducedFlash = _app.Settings.ReducedFlash;
            Presenter = MatchPresenter.Create(_app.PresentationContext, Session, court, LocalPlayer);
            if (r.Kind == MatchKind.Duel) Presenter.Driver.BeforeTick += () => r.Lobby.Update();
            Session.Ledger.Spawned += OnEvent;
            _endHandled = false;
            _endDelay = 0f;
            Screen = _app.Nav.Push(new MatchScreen(this));
        }

        private void OnEvent(SimEvent e, int age)
        {
            if (e.Type == SimEventType.RoundEnd && Rival != null) Rival.RoundEnded(e.A == LocalPlayer);
            Lesson?.OnEvent(e, LocalPlayer);
        }

        /// <summary>Training: switch what the dummy (player 2) does.</summary>
        public void SetDummy(DummyMode mode, CpuTier tier)
        {
            if (Request == null || Request.Kind != MatchKind.Training) return;
            Dummy = mode;
            switch (mode)
            {
                case DummyMode.Stand:
                    P0.Target = _app.Input;
                    P1.Target = null;
                    break;
                case DummyMode.Cpu:
                    Cpu = new CpuPlayer(_app.Tables, _app.Tiers, tier, 1, (ulong)Time.frameCount + 99);
                    P0.Target = _app.Input;
                    P1.Target = Cpu;
                    break;
                case DummyMode.Record:
                    // The player drives the dummy; their inputs are recorded for playback.
                    Recording.Clear();
                    P0.Target = null;
                    P1.Target = new RecordingInput(_app.Input, Recording);
                    break;
                case DummyMode.Playback:
                    Recording.Rewind();
                    P0.Target = _app.Input;
                    P1.Target = Recording;
                    break;
            }
        }

        /// <summary>Training: back to the opening position (local sessions only).</summary>
        public void ResetPositions()
        {
            if (Session is LocalSession ls) ls.Restore(InitialState);
            if (Dummy == DummyMode.Playback) Recording.Rewind();
        }

        public void Restart()
        {
            if (Request == null || Request.Kind == MatchKind.Duel) return;
            MatchRequest r = Request.Clone();
            r.Seed = 0;
            Start(r);
        }

        public void Tick(float dt)
        {
            if (Session == null) return;
            if (Session.IsOver && !_endHandled)
            {
                _endDelay += dt;
                if (_endDelay >= 2.2f)
                {
                    _endHandled = true;
                    Ended?.Invoke(this);
                }
            }
        }

        public void Stop()
        {
            if (Session != null) Session.Ledger.Spawned -= OnEvent;
            if (Presenter != null)
            {
                Presenter.Shutdown();
                UnityEngine.Object.Destroy(Presenter.gameObject);
            }
            Presenter = null;
            if (Session is RollbackSession rs && (Request == null || Request.Kind != MatchKind.Duel)) rs.Dispose();
            Session = null;
            Screen = null;
        }
    }

    /// <summary>Passes the human's input through to the dummy while recording it.</summary>
    public sealed class RecordingInput : IInputSource
    {
        private readonly IInputSource _source;
        private readonly ScriptedInput _into;
        public RecordingInput(IInputSource source, ScriptedInput into)
        {
            _source = source;
            _into = into;
        }

        public FrameInput NextInput(in GameState state, int player)
        {
            FrameInput i = _source.NextInput(state, player);
            if (_into.Count < 60 * 30) _into.Add(i);
            return i;
        }
    }
}
