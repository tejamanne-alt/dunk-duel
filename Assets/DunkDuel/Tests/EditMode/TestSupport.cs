using System;
using System.Collections.Generic;
using System.IO;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>Shared fixtures: tables are loaded once from the real data files (Unity and headless runs alike).</summary>
    public static class TestData
    {
        private static SimTables _tables;
        private static CpuTiers _tiers;
        private static IDataSource _source;

        public static string RepoRoot
        {
            get
            {
                foreach (string start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                {
                    string dir = start;
                    for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
                    {
                        if (Directory.Exists(Path.Combine(dir, "Assets", "DunkDuel", "Sim"))) return dir;
                        dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
                    }
                }
                throw new InvalidOperationException("repo root not found");
            }
        }

        public static string DataRoot => Path.Combine(RepoRoot, "Assets", "DunkDuel", "Data", "Resources", "DunkDuelData");

        public static IDataSource Source => _source ?? (_source = new FolderDataSource(DataRoot));

        public static SimTables Tables => _tables ?? (_tables = SimTables.Load(Source));

        public static CpuTiers Tiers => _tiers ?? (_tiers = CpuTiers.Load(Tables, Source));

        public static int Fighter(string id)
        {
            int i = Tables.FighterIndex(id);
            Assert.GreaterOrEqual(i, 0, "unknown fighter " + id);
            return i;
        }

        public static MemoryDataSource CopyOfData()
        {
            var m = new MemoryDataSource();
            foreach (string name in new[] { "tuning", "moves", "impacts", "ai" }) m.Add(name, Source.Read(name));
            foreach (string id in Tables.T.Fighters) m.Add("fighters/" + id, Source.Read("fighters/" + id));
            return m;
        }
    }

    /// <summary>
    /// Builds exact situations (positions, actions, timers) on a live possession and steps the sim with scripted
    /// inputs, recording every event. Player 0 has the ball unless stated otherwise.
    /// </summary>
    public sealed class Scenario
    {
        public readonly SimTables Tables = TestData.Tables;
        public readonly Simulation Sim;
        public GameState S;
        public readonly List<SimEvent> Events = new List<SimEvent>();

        public Scenario(string f0 = "kofi", string f1 = "meera", ulong seed = 1)
        {
            Sim = new Simulation(Tables);
            S = Sim.CreateInitialState(MatchSetup.Versus(TestData.Fighter(f0), TestData.Fighter(f1), seed));
            // Skip the intro and the check: live ball for player 0.
            S.Phase = Phase.Live;
            S.PhaseFrame = 0;
        }

        public Tuning T => Tables.T;

        public ref FighterState P0 => ref S.F0;
        public ref FighterState P1 => ref S.F1;

        public FighterStats Stats(int i) => Tables.Fighters[S.Get(i).CharIndex];

        public static Fix F(string s) => Fix.Parse(s);

        /// <summary>Places the handler (p0) and the defender (p1) on the court, both idle and grounded.</summary>
        public Scenario Place(string handlerX, string defenderX)
        {
            Reset(ref S.F0, F(handlerX), 1);
            Reset(ref S.F1, F(defenderX), -1);
            S.Ball.Mode = BallMode.Held;
            S.Ball.Holder = 0;
            S.Offense = 0;
            S.NeedsClear = false;
            return this;
        }

        private static void Reset(ref FighterState f, Fix x, int facing)
        {
            f.X = x;
            f.Y = Fix.Zero;
            f.VX = Fix.Zero;
            f.VY = Fix.Zero;
            f.Facing = (sbyte)facing;
            f.Status |= StatusFlag.Grounded;
            f.Action = ActionId.Idle;
            f.ActionFrame = 0;
            f.ActionFlags = 0;
            f.Param = 0;
            f.PlantFrames = 0;
            f.Beaten = 0;
            f.Lag = 0;
            f.LastContactFrame = -100000;
            f.BumpTimer = -100000;
            f.AgeShoot = 255;
            f.AgeShake = 255;
            f.AgeTrick = 255;
            f.AgeFinisher = 255;
            f.Held = 0;
            f.PrevHeld = 0;
        }

        /// <summary>Steps <paramref name="ticks"/> times; inputs may depend on the tick index.</summary>
        public Scenario Run(int ticks, Func<int, FrameInput> p0 = null, Func<int, FrameInput> p1 = null)
        {
            for (int t = 0; t < ticks; t++)
            {
                FrameInput a = p0 != null ? p0(t) : FrameInput.None;
                FrameInput b = p1 != null ? p1(t) : FrameInput.None;
                StepResult r = Sim.Step(S, a, b);
                S = r.State;
                for (int i = 0; i < r.Events.Count; i++) Events.Add(r.Events[i]);
            }
            return this;
        }

        /// <summary>Steps until <paramref name="until"/> holds (or fails the test after <paramref name="max"/> ticks).</summary>
        public Scenario RunUntil(Func<Scenario, bool> until, int max, Func<int, FrameInput> p0 = null, Func<int, FrameInput> p1 = null)
        {
            for (int t = 0; t < max; t++)
            {
                if (until(this)) return this;
                Run(1, p0 != null ? (Func<int, FrameInput>)(_ => p0(t)) : null, p1 != null ? (Func<int, FrameInput>)(_ => p1(t)) : null);
            }
            Assert.IsTrue(until(this), "condition not reached in " + max + " ticks");
            return this;
        }

        public bool Saw(SimEventType type) => Events.Exists(e => e.Type == type);
        public int Count(SimEventType type) => Events.FindAll(e => e.Type == type).Count;
        public SimEvent First(SimEventType type)
        {
            int i = Events.FindIndex(e => e.Type == type);
            Assert.GreaterOrEqual(i, 0, "expected event " + type);
            return Events[i];
        }

        public static FrameInput In(Buttons b) => new FrameInput(b);
    }
}
