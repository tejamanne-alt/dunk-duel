using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Tools
{
    /// <summary>Aggregated results of many CPU-vs-CPU matches.</summary>
    public sealed class LabStats
    {
        public long Matches;
        public long Ticks;
        public long[] Wins = new long[2];
        public long Points;
        public long FgAtt, FgMade, DeepAtt, DeepMade, Dunks;
        public long Steals, Blocks, Charges, Ankles, Posters, Bites, Turnovers;
        public long FinAtt, FinLand, Denials;

        public void Add(in GameState s)
        {
            Interlocked.Increment(ref Matches);
            Interlocked.Add(ref Ticks, s.Tick);
            if (s.MatchWinner < 2) Interlocked.Increment(ref Wins[s.MatchWinner]);
            for (int i = 0; i < 2; i++) AddFighter(s.Get(i));
        }

        /// <summary>One fighter's view of a match: counts the match, its own stats and its own win in Wins[0].</summary>
        public void AddSide(in GameState s, int side)
        {
            Interlocked.Increment(ref Matches);
            Interlocked.Add(ref Ticks, s.Tick);
            if (s.MatchWinner == side) Interlocked.Increment(ref Wins[0]);
            AddFighter(s.Get(side));
        }

        private void AddFighter(in FighterState f)
        {
            {
                Interlocked.Add(ref FgAtt, f.FgAtt);
                Interlocked.Add(ref FgMade, f.FgMade);
                Interlocked.Add(ref DeepAtt, f.DeepAtt);
                Interlocked.Add(ref DeepMade, f.DeepMade);
                Interlocked.Add(ref Dunks, f.Dunks);
                Interlocked.Add(ref Steals, f.Steals);
                Interlocked.Add(ref Blocks, f.Blocks);
                Interlocked.Add(ref Charges, f.Charges);
                Interlocked.Add(ref Ankles, f.AnkleBreaks);
                Interlocked.Add(ref Posters, f.Posters);
                Interlocked.Add(ref Bites, f.BitesDrawn);
                Interlocked.Add(ref Turnovers, f.Turnovers);
                Interlocked.Add(ref FinAtt, f.FinisherAtt);
                Interlocked.Add(ref FinLand, f.Finishers);
                Interlocked.Add(ref Denials, f.Denials);
            }
        }
    }

    public static class Program
    {
        private sealed class Options
        {
            public int Matches = 200;
            public CpuTier Tier = CpuTier.Legend;
            public ulong Seed = 20261102;
            public int Threads = Environment.ProcessorCount;
            public bool Ladder = true;
            public bool Pairings = true;
            public string Json;
            public double Low = 45.0;
            public double High = 55.0;
            public int LadderMatches = 200;
        }

        public static int Main(string[] args)
        {
            var o = Parse(args);
            string repo = FindRepoRoot();
            int tuneIdx = Array.IndexOf(args, "--tune");
            if (tuneIdx >= 0)
            {
                int sweeps = tuneIdx + 1 < args.Length && int.TryParse(args[tuneIdx + 1], out int sw0) ? sw0 : 200;
                return Tuner.Run(repo, o.Matches, sweeps, Array.IndexOf(args, "--apply") >= 0, o.Threads);
            }
            var data = new FolderDataSource(Path.Combine(repo, "Assets", "DunkDuel", "Data", "Resources", "DunkDuelData"));
            SimTables tables = SimTables.Load(data);
            CpuTiers tiers = CpuTiers.Load(tables, data);
            var sw = Stopwatch.StartNew();
            var report = new StringBuilder();
            bool pass = true;
            int n = tables.FighterCount;

            if (o.Pairings)
            {
                Console.WriteLine($"Balance Lab: {o.Matches} matches per pairing and side, tier {o.Tier}, {o.Threads} threads");
                var rates = new double[n, n];
                var all = new LabStats();
                var perFighter = new LabStats[n];
                for (int i = 0; i < n; i++) perFighter[i] = new LabStats();
                for (int a = 0; a < n; a++)
                {
                    for (int b = a + 1; b < n; b++)
                    {
                        long winsA = 0;
                        var st = new LabStats();
                        RunMany(tables, tiers, a, b, o.Tier, o.Tier, o.Matches, o.Seed + (ulong)(a * 131 + b * 17), o.Threads, st, perFighter, ref winsA);
                        double rate = 100.0 * winsA / Math.Max(1, st.Matches);
                        rates[a, b] = rate;
                        rates[b, a] = 100 - rate;
                        Add(all, st);
                        bool ok = rate >= o.Low && rate <= o.High;
                        pass &= ok;
                        string line = $"{Id(tables, a),-6} vs {Id(tables, b),-6}: {rate,5:F1}%  {(ok ? "ok" : "FAIL")}  ({st.Matches} matches, {st.Ticks / (60.0 * Math.Max(1, st.Matches)):F0}s avg)";
                        Console.WriteLine(line);
                        report.AppendLine(line);
                    }
                }
                Console.WriteLine();
                Console.WriteLine("Win matrix (row beats column %):");
                Console.Write("        ");
                for (int b = 0; b < n; b++) Console.Write($"{Id(tables, b),8}");
                Console.WriteLine();
                for (int a = 0; a < n; a++)
                {
                    Console.Write($"{Id(tables, a),-8}");
                    for (int b = 0; b < n; b++) Console.Write(a == b ? "       -" : $"{rates[a, b],8:F1}");
                    Console.WriteLine();
                }
                Console.WriteLine();
                Console.WriteLine("Per fighter (as either side):");
                for (int i = 0; i < n; i++) PrintFighter(tables, i, perFighter[i]);
                Console.WriteLine();
                PrintTotals(all);
                if (o.Json != null) File.WriteAllText(o.Json, ToJson(tables, rates, all, pass));
            }

            if (o.Ladder)
            {
                Console.WriteLine();
                Console.WriteLine($"Tier ladder ({o.LadderMatches} matches per step, rotating fighters):");
                double prev = 0;
                bool rising = true;
                for (int t = 1; t <= 4; t++)
                {
                    long winsHigh = 0, total = 0;
                    for (int a = 0; a < n; a++)
                    {
                        var st = new LabStats();
                        long w = 0;
                        // Higher tier plays fighter a against a mirror at the lower tier.
                        RunMany(tables, tiers, a, a, (CpuTier)t, (CpuTier)(t - 1), o.LadderMatches / n, o.Seed + 999UL * (ulong)t + (ulong)a, o.Threads, st, null, ref w);
                        winsHigh += w;
                        total += st.Matches;
                    }
                    double rate = 100.0 * winsHigh / Math.Max(1, total);
                    Console.WriteLine($"  {(CpuTier)t,-8} beats {(CpuTier)(t - 1),-8}: {rate,5:F1}%");
                    if (rate <= 50.0) rising = false;
                    prev = rate;
                }
                _ = prev;
                Console.WriteLine(rising ? "  win rates rise with each tier: ok" : "  win rates rise with each tier: FAIL");
                pass &= rising;
            }

            Console.WriteLine();
            Console.WriteLine($"Balance Lab {(pass ? "PASS" : "FAIL")} in {sw.Elapsed.TotalSeconds:F1}s");
            return pass ? 0 : 1;
        }

        /// <summary>Plays <paramref name="perSide"/> matches with A as player 0 and as many with A as player 1.</summary>
        private static void RunMany(SimTables tables, CpuTiers tiers, int a, int b, CpuTier tierA, CpuTier tierB, int perSide, ulong seed,
            int threads, LabStats st, LabStats[] perFighter, ref long winsA)
        {
            long wins = 0;
            int total = perSide * 2;
            Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
            {
                bool swap = (k & 1) == 1;
                ulong ms = seed * 6364136223846793005UL + (ulong)k * 1442695040888963407UL;
                int f0 = swap ? b : a, f1 = swap ? a : b;
                CpuTier t0 = swap ? tierB : tierA, t1 = swap ? tierA : tierB;
                var setup = MatchSetup.Versus(f0, f1, ms);
                setup.FirstOffense = (k >> 1) & 1;
                var p0 = new CpuPlayer(tables, tiers, t0, 0, ms ^ 0x1111UL);
                var p1 = new CpuPlayer(tables, tiers, t1, 1, ms ^ 0x2222UL);
                var session = new LocalSession(tables, setup, p0, p1);
                int guard = 60 * 60 * 30;
                while (!session.IsOver && guard-- > 0) session.Tick();
                GameState s = session.State;
                st.Add(s);
                int aSide = swap ? 1 : 0;
                if (s.MatchWinner == aSide) Interlocked.Increment(ref wins);
                if (perFighter != null)
                {
                    perFighter[f0].AddSide(s, 0);
                    perFighter[f1].AddSide(s, 1);
                }
            });
            winsA += wins;
        }

        private static void Add(LabStats into, LabStats from)
        {
            into.Matches += from.Matches;
            into.Ticks += from.Ticks;
            into.FgAtt += from.FgAtt;
            into.FgMade += from.FgMade;
            into.DeepAtt += from.DeepAtt;
            into.DeepMade += from.DeepMade;
            into.Dunks += from.Dunks;
            into.Steals += from.Steals;
            into.Blocks += from.Blocks;
            into.Charges += from.Charges;
            into.Ankles += from.Ankles;
            into.Posters += from.Posters;
            into.Bites += from.Bites;
            into.Turnovers += from.Turnovers;
            into.FinAtt += from.FinAtt;
            into.FinLand += from.FinLand;
            into.Denials += from.Denials;
        }

        private static void PrintFighter(SimTables tables, int i, LabStats s)
        {
            double m = Math.Max(1, s.Matches);
            Console.WriteLine($"  {Id(tables, i),-6} FG {Pct(s.FgMade, s.FgAtt),5:F1}% ({s.FgAtt / m:F1}/m)  3P {Pct(s.DeepMade, s.DeepAtt),5:F1}% ({s.DeepAtt / m:F1}/m)  dunks {s.Dunks / m:F1}  stl {s.Steals / m:F2} blk {s.Blocks / m:F2} chg {s.Charges / m:F2} ankles {s.Ankles / m:F2} posters {s.Posters / m:F2} bites {s.Bites / m:F2} TO {s.Turnovers / m:F2}  fin {s.FinLand}/{s.FinAtt}");
        }

        private static void PrintTotals(LabStats s)
        {
            double m = Math.Max(1, s.Matches);
            Console.WriteLine($"All: {s.Matches} matches, avg {s.Ticks / (60.0 * m):F0}s, FG {Pct(s.FgMade, s.FgAtt):F1}%, 3P {Pct(s.DeepMade, s.DeepAtt):F1}% on {s.DeepAtt / m:F1}/match, steals {s.Steals / m:F2}, blocks {s.Blocks / m:F2}, charges {s.Charges / m:F2}, ankle breaks {s.Ankles / m:F2}, posterizers {s.Posters / m:F2}, bites {s.Bites / m:F2}/match");
            Console.WriteLine($"Finishers: {s.FinAtt} attempts, {s.FinLand} landed ({Pct(s.FinLand, s.FinAtt):F1}% land rate, target 75-80%), {s.Denials} denials");
        }

        private static double Pct(long a, long b) => b == 0 ? 0 : 100.0 * a / b;

        private static string Id(SimTables t, int i) => t.Fighters[i].Def.Id;

        private static string ToJson(SimTables t, double[,] rates, LabStats all, bool pass)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"pass\": ").Append(pass ? "true" : "false").Append(",\n  \"pairings\": [\n");
            int n = t.FighterCount;
            bool first = true;
            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append($"    {{ \"a\": \"{Id(t, a)}\", \"b\": \"{Id(t, b)}\", \"aWinPct\": {rates[a, b]:F2} }}");
                }
            }
            sb.Append("\n  ],\n");
            sb.Append($"  \"finisherLandPct\": {Pct(all.FinLand, all.FinAtt):F2},\n  \"avgMatchSeconds\": {all.Ticks / (60.0 * Math.Max(1, all.Matches)):F1}\n}}\n");
            return sb.ToString();
        }

        private static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--matches": o.Matches = int.Parse(args[++i]); break;
                    case "--ladder-matches": o.LadderMatches = int.Parse(args[++i]); break;
                    case "--tier": o.Tier = (CpuTier)Enum.Parse(typeof(CpuTier), args[++i], true); break;
                    case "--seed": o.Seed = ulong.Parse(args[++i]); break;
                    case "--threads": o.Threads = int.Parse(args[++i]); break;
                    case "--no-ladder": o.Ladder = false; break;
                    case "--ladder-only": o.Pairings = false; break;
                    case "--json": o.Json = args[++i]; break;
                }
            }
            return o;
        }

        private static string FindRepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            for (int i = 0; i < 12 && dir != null; i++)
            {
                if (Directory.Exists(Path.Combine(dir, "Assets", "DunkDuel", "Sim"))) return dir;
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }
            return Directory.GetCurrentDirectory();
        }
    }
}
