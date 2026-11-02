using System;
using System.Collections.Generic;
using DunkDuel.Sim.Data;

namespace DunkDuel.Sim.Session
{
    /// <summary>Anything that produces one player's input per tick: touch UI, a controller, the CPU, a replay.</summary>
    public interface IInputSource
    {
        /// <summary>Input for the tick that will advance <paramref name="state"/>. <paramref name="player"/> is 0 or 1.</summary>
        FrameInput NextInput(in GameState state, int player);
    }

    /// <summary>A running match as presentation sees it: local (CPU, training) or rollback netplay.</summary>
    public interface IMatchSession
    {
        GameState State { get; }
        SimTables Tables { get; }
        EventLedger Ledger { get; }
        bool IsOver { get; }
        /// <summary>Advances one 60 Hz tick (netplay may advance zero or two to stay in sync).</summary>
        void Tick();
    }

    /// <summary>Fixed input for tests and idle players.</summary>
    public sealed class ConstantInput : IInputSource
    {
        public FrameInput Value;
        public ConstantInput(FrameInput value) { Value = value; }
        public FrameInput NextInput(in GameState state, int player) => Value;
    }

    /// <summary>Records and plays back inputs for a single player (training dummy, replays).</summary>
    public sealed class ScriptedInput : IInputSource
    {
        private readonly List<FrameInput> _frames = new List<FrameInput>();
        private int _cursor;
        public bool Loop;

        public int Count => _frames.Count;
        public void Add(FrameInput input, int repeat = 1)
        {
            for (int i = 0; i < repeat; i++) _frames.Add(input);
        }

        public void Clear()
        {
            _frames.Clear();
            _cursor = 0;
        }

        public void Rewind() => _cursor = 0;

        public FrameInput NextInput(in GameState state, int player)
        {
            if (_frames.Count == 0) return FrameInput.None;
            if (_cursor >= _frames.Count)
            {
                if (!Loop) return FrameInput.None;
                _cursor = 0;
            }
            return _frames[_cursor++];
        }
    }

    /// <summary>Single-device match: two input sources (human, CPU, scripted) stepping the sim directly.</summary>
    public sealed class LocalSession : IMatchSession
    {
        private readonly Simulation _sim;
        private readonly IInputSource _p0;
        private readonly IInputSource _p1;
        private GameState _state;

        public LocalSession(SimTables tables, in MatchSetup setup, IInputSource p0, IInputSource p1)
        {
            _sim = new Simulation(tables);
            _state = _sim.CreateInitialState(setup);
            _p0 = p0;
            _p1 = p1;
            Setup = setup;
            Ledger = new EventLedger();
            Recorder = new ReplayRecorder(SimBuildInfo.Version(tables), setup);
        }

        public MatchSetup Setup { get; }
        public GameState State => _state;
        public SimTables Tables => _sim.Tables;
        public EventLedger Ledger { get; }
        public ReplayRecorder Recorder { get; }
        public bool IsOver => _state.Phase == Phase.MatchEnd;
        public FrameInput LastInput0 { get; private set; }
        public FrameInput LastInput1 { get; private set; }

        public void Tick()
        {
            FrameInput i0 = _p0.NextInput(_state, 0);
            FrameInput i1 = _p1.NextInput(_state, 1);
            LastInput0 = i0;
            LastInput1 = i1;
            Recorder.Record(i0, i1);
            StepResult r = _sim.Step(_state, i0, i1);
            _state = r.State;
            Ledger.Confirm(_state.Tick, r.Events);
        }

        /// <summary>Training: jump back to a saved state (record/replay setups).</summary>
        public void Restore(in GameState state) => _state = state;
    }

    /// <summary>
    /// Rollback-aware event ledger keyed by (frame, type, actor, sequence). Sessions report each tick's events;
    /// after a resimulation they report the corrected events and the ledger tells presentation which effects
    /// to fade out (orphaned) and which to spawn late (missing), with their age in ticks.
    /// </summary>
    public sealed class EventLedger
    {
        public struct Entry
        {
            public SimEvent Event;
            public int Tick;
        }

        /// <summary>Raised for a new event; second argument is how many ticks late it is being shown.</summary>
        public event Action<SimEvent, int> Spawned;
        /// <summary>Raised for an event that a rollback proved never happened.</summary>
        public event Action<SimEvent> Orphaned;

        private readonly List<Entry> _live = new List<Entry>(256);
        private int _currentTick;
        public int KeepTicks = 120;

        public int Count => _live.Count;
        public Entry this[int index] => _live[index];

        /// <summary>Events produced while simulating <paramref name="tick"/> for the first time (or confirmed).</summary>
        public void Confirm(int tick, SimEventBuffer events)
        {
            if (tick > _currentTick) _currentTick = tick;
            for (int i = 0; i < events.Count; i++) Add(tick, events[i]);
            Trim();
        }

        /// <summary>
        /// Starts a resimulation from <paramref name="fromTick"/>: entries at or after it become provisional until
        /// <see cref="Resimulated"/> re-reports them.
        /// </summary>
        public RollbackScope BeginRollback(int fromTick) => new RollbackScope(this, fromTick);

        public sealed class RollbackScope
        {
            private readonly EventLedger _ledger;
            private readonly int _from;
            private readonly List<Entry> _old = new List<Entry>();
            private readonly List<Entry> _new = new List<Entry>();

            internal RollbackScope(EventLedger ledger, int from)
            {
                _ledger = ledger;
                _from = from;
                for (int i = ledger._live.Count - 1; i >= 0; i--)
                {
                    if (ledger._live[i].Tick >= from)
                    {
                        _old.Add(ledger._live[i]);
                        ledger._live.RemoveAt(i);
                    }
                }
            }

            public void Resimulated(int tick, SimEventBuffer events)
            {
                for (int i = 0; i < events.Count; i++) _new.Add(new Entry { Event = events[i], Tick = tick });
            }

            public void End()
            {
                for (int i = 0; i < _old.Count; i++)
                {
                    if (!Contains(_new, _old[i].Event.Key, _old[i].Event.Type)) _ledger.Orphaned?.Invoke(_old[i].Event);
                }
                for (int i = 0; i < _new.Count; i++)
                {
                    Entry e = _new[i];
                    _ledger._live.Add(e);
                    if (!Contains(_old, e.Event.Key, e.Event.Type))
                    {
                        _ledger.Spawned?.Invoke(e.Event, _ledger._currentTick - e.Tick);
                    }
                }
            }

            private static bool Contains(List<Entry> list, long key, SimEventType type)
            {
                for (int i = 0; i < list.Count; i++) if (list[i].Event.Key == key && list[i].Event.Type == type) return true;
                return false;
            }
        }

        private void Add(int tick, in SimEvent e)
        {
            _live.Add(new Entry { Event = e, Tick = tick });
            Spawned?.Invoke(e, 0);
        }

        private void Trim()
        {
            int cutoff = _currentTick - KeepTicks;
            int remove = 0;
            while (remove < _live.Count && _live[remove].Tick < cutoff) remove++;
            if (remove > 0) _live.RemoveRange(0, remove);
        }

        public void Clear() => _live.Clear();
    }
}
