using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;
using NUnit.Framework;

namespace DunkDuel.Tests
{
    /// <summary>
    /// Determinism gate: a 10,000-frame recorded replay hashes identically everywhere (this test in EditMode and
    /// headless; the in-game Determinism check on iPhone and Android), plus rollback and serialization round-trips.
    /// </summary>
    public class DeterminismTests
    {
        private static Replay RecordCpuMatch(int ticks, ulong seed)
        {
            SimTables t = TestData.Tables;
            var setup = MatchSetup.Versus(TestData.Fighter("kofi"), TestData.Fighter("juno"), seed);
            var session = new LocalSession(t, setup,
                new CpuPlayer(t, TestData.Tiers, CpuTier.Legend, 0, seed + 1),
                new CpuPlayer(t, TestData.Tiers, CpuTier.Pro, 1, seed + 2));
            for (int i = 0; i < ticks && !session.IsOver; i++) session.Tick();
            Replay r = session.Recorder.Replay;
            r.FinalHash = session.State.Hash();
            return r;
        }

        [Test]
        public void TenThousandFrameReplayReplaysBitIdentically()
        {
            Replay r = RecordCpuMatch(10000, 77);
            Assert.GreaterOrEqual(r.Ticks, 2000);
            ReplayRunner.Result a = ReplayRunner.Run(TestData.Tables, r);
            ReplayRunner.Result b = ReplayRunner.Run(TestData.Tables, Replay.FromBytes(r.ToBytes()));
            Assert.AreEqual(r.FinalHash, a.FinalHash, "replayed inputs reproduce the recorded state");
            Assert.AreEqual(a.FinalHash, b.FinalHash);
            Assert.AreEqual(a.ChainHash, b.ChainHash);
        }

        [Test]
        public void GoldenReplayMatchesCommittedHash()
        {
            string dir = Path.Combine(TestData.RepoRoot, "Assets", "DunkDuel", "Tests", "EditMode", "Data");
            string replayPath = Path.Combine(dir, "golden_10000.ddreplay");
            string jsonPath = Path.Combine(dir, "golden_10000.json");
            if (!File.Exists(replayPath) || !File.Exists(jsonPath)) Assert.Inconclusive("golden replay missing: dotnet run --project Tools/SimTool -- golden");
            Replay r = Replay.FromBytes(File.ReadAllBytes(replayPath));
            string json = File.ReadAllText(jsonPath);
            string version = Regex.Match(json, "\"simVersion\": \"([0-9A-F]+)\"").Groups[1].Value;
            string final = Regex.Match(json, "\"finalHash\": \"([0-9A-F]+)\"").Groups[1].Value;
            string current = SimBuildInfo.Version(TestData.Tables).ToString("X16");
            if (version != current) Assert.Inconclusive("sim version changed (" + version + " -> " + current + "): regenerate with Tools/SimTool golden");
            Assert.AreEqual(10000, r.Ticks);
            ReplayRunner.Result res = ReplayRunner.Run(TestData.Tables, r);
            Assert.AreEqual(final, res.FinalHash.ToString("X16"), "10,000-frame replay must hash identically on every platform");
        }

        [Test]
        public void SnapshotRestoreResimulatesIdentically()
        {
            Replay r = RecordCpuMatch(10000, 99);
            var sim = new Simulation(TestData.Tables);
            GameState s = sim.CreateInitialState(r.Setup);
            for (int t = 0; t < r.Ticks; t++)
            {
                if (t % 7 == 0 && t + 8 < r.Ticks)
                {
                    // Roll back 8 frames, as the netcode does, and resimulate.
                    GameState saved = s.Clone();
                    GameState ahead = s;
                    for (int k = 0; k < 8; k++) ahead = sim.Step(ahead, r.P0(t + k), r.P1(t + k)).State;
                    GameState again = saved;
                    for (int k = 0; k < 8; k++) again = sim.Step(again, r.P0(t + k), r.P1(t + k)).State;
                    Assert.AreEqual(ahead.Hash(), again.Hash(), "resimulation diverged at tick " + t);
                }
                s = sim.Step(s, r.P0(t), r.P1(t)).State;
            }
            Assert.AreEqual(r.FinalHash, s.Hash());
        }

        [Test]
        public void StateSerializationRoundTripsAndFitsTwoKilobytes()
        {
            Assert.LessOrEqual(StateCodec.SerializedSize(), 2048, "GameState snapshot must be at most 2 KB");
            Replay r = RecordCpuMatch(6000, 5);
            var sim = new Simulation(TestData.Tables);
            GameState s = sim.CreateInitialState(r.Setup);
            var buf = new byte[StateCodec.MaxBytes];
            for (int t = 0; t < r.Ticks; t++)
            {
                s = sim.Step(s, r.P0(t), r.P1(t)).State;
                if (t % 250 == 0)
                {
                    int n = StateCodec.Write(ref s, buf);
                    GameState back = StateCodec.Read(buf, n);
                    Assert.AreEqual(s.Hash(), back.Hash());
                }
            }
        }

        [Test]
        public void HashCoversEveryField()
        {
            var sim = new Simulation(TestData.Tables);
            GameState a = sim.CreateInitialState(MatchSetup.Versus(0, 1, 3));
            GameState b = a;
            b.F1.FgAtt++;
            Assert.AreNotEqual(a.Hash(), b.Hash());
            b = a;
            b.Ball.NoGrab++;
            Assert.AreNotEqual(a.Hash(), b.Hash());
            b = a;
            b.Fin.EndX = Fix.One;
            Assert.AreNotEqual(a.Hash(), b.Hash());
        }

        [Test]
        public void SeparateSimulationsAgree()
        {
            Replay r = RecordCpuMatch(3000, 1234);
            var s1 = new Simulation(TestData.Tables);
            var s2 = new Simulation(SimTables.Load(TestData.Source));
            GameState a = s1.CreateInitialState(r.Setup), b = s2.CreateInitialState(r.Setup);
            for (int t = 0; t < r.Ticks; t++)
            {
                a = s1.Step(a, r.P0(t), r.P1(t)).State;
                b = s2.Step(b, r.P0(t), r.P1(t)).State;
            }
            Assert.AreEqual(a.Hash(), b.Hash());
        }
    }

    /// <summary>Static gate: DunkDuel.Sim must not reference UnityEngine, float, double, System.Random or DateTime.</summary>
    public class StaticGateTests
    {
        private static readonly string[] Forbidden =
        {
            @"\bUnityEngine\b",
            @"\bfloat\b",
            @"\bdouble\b",
            @"\bdecimal\b",
            @"\bSystem\.Random\b",
            @"\bnew\s+Random\s*\(",
            @"\bDateTime\b",
            @"\bDateTimeOffset\b",
            @"\bStopwatch\b",
            @"\bDictionary\s*<",
            @"\bHashSet\s*<",
            @"\bMathF?\.(Sin|Cos|Tan|Sqrt|Pow|Exp|Log|Atan2?)\b",
        };

        private static string StripCommentsAndStrings(string code)
        {
            code = Regex.Replace(code, @"//[^\n]*", "");
            code = Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline);
            code = Regex.Replace(code, "@\"(?:[^\"]|\"\")*\"", "\"\"");
            code = Regex.Replace(code, "\"(?:\\\\.|[^\"\\\\])*\"", "\"\"");
            return code;
        }

        [Test]
        public void SimSourcesUseNoForbiddenTypes()
        {
            string simDir = Path.Combine(TestData.RepoRoot, "Assets", "DunkDuel", "Sim");
            var failures = new List<string>();
            foreach (string path in Directory.GetFiles(simDir, "*.cs", SearchOption.AllDirectories))
            {
                string code = StripCommentsAndStrings(File.ReadAllText(path));
                foreach (string pattern in Forbidden)
                {
                    foreach (Match m in Regex.Matches(code, pattern))
                    {
                        failures.Add(Path.GetFileName(path) + ": " + m.Value);
                    }
                }
            }
            Assert.IsEmpty(failures, string.Join("\n", failures));
        }

        [Test]
        public void SimAsmdefHasNoEngineReferences()
        {
            string asmdef = File.ReadAllText(Path.Combine(TestData.RepoRoot, "Assets", "DunkDuel", "Sim", "DunkDuel.Sim.asmdef"));
            StringAssert.Contains("\"noEngineReferences\": true", asmdef);
            StringAssert.Contains("\"references\": []", asmdef);
        }

        [Test]
        public void SimCodeHashIsCurrent()
        {
            string simDir = Path.Combine(TestData.RepoRoot, "Assets", "DunkDuel", "Sim");
            var files = new List<KeyValuePair<string, string>>();
            foreach (string path in Directory.GetFiles(simDir, "*.cs", SearchOption.AllDirectories))
            {
                string rel = path.Substring(simDir.Length + 1).Replace('\\', '/');
                files.Add(new KeyValuePair<string, string>(rel, File.ReadAllText(path)));
            }
            ulong hash = SimBuildInfo.ComputeCodeHash(files);
            Assert.AreEqual(hash, SimBuildInfo.CodeHash, "SimBuildInfo.g.cs is stale: dotnet run --project Tools/SimTool -- hash");
        }
    }
}
