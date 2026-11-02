using System;
using System.Collections.Generic;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Tools
{
    /// <summary>Per-matchup diagnostics: how each side scores, how possessions end, shot quality by zone.</summary>
    public static class Matchup
    {
        private sealed class Side
        {
            public int Wins;
            public long Shots, Makes, Points;
            public readonly long[] ZoneShots = new long[3];
            public readonly long[] ZoneMakes = new long[3];
            public long SumQ, SumP; // raw Fix sums
            public readonly long[] Grades = new long[4];
            public readonly Dictionary<string, int> DefAtRelease = new Dictionary<string, int>();
            public long Possessions;
            public readonly Dictionary<string, int> Endings = new Dictionary<string, int>();
            public long Blocks, Steals, Charges, Ankles, Bites, Finishers, Denied;
        }

        public static int Run(SimTables tables, IDataSource data, string[] args)
        {
            string a = args.Length > 1 ? args[1] : "meera";
            string b = args.Length > 2 ? args[2] : "juno";
            int n = args.Length > 3 ? int.Parse(args[3]) : 60;
            CpuTier tier = args.Length > 4 ? (CpuTier)Enum.Parse(typeof(CpuTier), args[4], true) : CpuTier.Legend;
            CpuTiers tiers = CpuTiers.Load(tables, data);
            int fa = tables.FighterIndex(a), fb = tables.FighterIndex(b);
            var sides = new[] { new Side(), new Side() }; // index 0 = fighter a, 1 = fighter b
            long ticks = 0;

            for (int k = 0; k < n; k++)
            {
                bool swap = (k & 1) == 1;
                var setup = MatchSetup.Versus(swap ? fb : fa, swap ? fa : fb, (ulong)(7000 + k));
                setup.FirstOffense = (k >> 1) & 1;
                var session = new LocalSession(tables, setup,
                    new CpuPlayer(tables, tiers, tier, 0, (ulong)k * 31 + 1), new CpuPlayer(tables, tiers, tier, 1, (ulong)k * 37 + 2));
                int SideOf(int player) => (player == 0) != swap ? 0 : 1;
                session.Ledger.Spawned += (e, age) =>
                {
                    GameState st = session.State;
                    switch (e.Type)
                    {
                        case SimEventType.ShotRelease:
                            {
                                Side s = sides[SideOf(e.Actor)];
                                s.Shots++;
                                s.ZoneShots[e.B]++;
                                s.Grades[e.A]++;
                                s.SumQ += st.Shot.Contest.Raw;
                                s.SumP += st.Shot.MakeChance.Raw;
                                FighterState d = st.Get(1 - e.Actor);
                                string key = d.Action + (d.Y > tables.T.Physics.AirborneHeight ? "(air)" : "") + " " + Bucket(Fix.Abs(d.X - e.X));
                                s.DefAtRelease.TryGetValue(key, out int c);
                                s.DefAtRelease[key] = c + 1;
                                break;
                            }
                        case SimEventType.ShotMade:
                            {
                                Side s = sides[SideOf(e.Actor)];
                                s.Makes++;
                                s.Points += e.A;
                                if (st.Shot.Zone <= ShotZone.Deep) s.ZoneMakes[(int)st.Shot.Zone]++;
                                End(s, "made " + e.A);
                                break;
                            }
                        case SimEventType.FinisherScore: sides[SideOf(e.Actor)].Finishers++; sides[SideOf(e.Actor)].Points += 3; End(sides[SideOf(e.Actor)], "finisher"); break;
                        case SimEventType.Denied: sides[SideOf(e.Actor)].Denied++; End(sides[1 - SideOf(e.Actor)], "denied"); break;
                        case SimEventType.Block: sides[SideOf(e.Actor)].Blocks++; break;
                        case SimEventType.Steal: sides[SideOf(e.Actor)].Steals++; End(sides[1 - SideOf(e.Actor)], "stolen"); break;
                        case SimEventType.Charge: sides[SideOf(e.Actor)].Charges++; End(sides[1 - SideOf(e.Actor)], "charge"); break;
                        case SimEventType.AnkleBreak: sides[SideOf(e.A)].Ankles++; break;
                        case SimEventType.Bite: sides[SideOf(e.B)].Bites++; break;
                        case SimEventType.Rebound: if (e.A == 0) End(sides[1 - SideOf(e.Actor)], "def rebound"); break;
                        case SimEventType.ShotClockViolation: End(sides[SideOf(e.Actor)], "shot clock"); break;
                    }
                };
                int guard = 60 * 60 * 30;
                while (!session.IsOver && guard-- > 0) session.Tick();
                ticks += session.State.Tick;
                sides[SideOf(session.State.MatchWinner)].Wins++;
            }

            Console.WriteLine($"{a} vs {b}, {n} matches at {tier}, avg {ticks / 60.0 / n:F0}s");
            for (int i = 0; i < 2; i++)
            {
                Side s = sides[i];
                string name = i == 0 ? a : b;
                double pos = Math.Max(1, s.Possessions);
                Console.WriteLine($"\n{name}: wins {s.Wins}, points/match {s.Points / (double)n:F1}, PPP {s.Points / pos:F2}, possessions/match {s.Possessions / (double)n:F1}");
                Console.WriteLine($"  shots {s.Shots / (double)n:F1}/match, FG {Pct(s.Makes, s.Shots):F1}%, avg Q {Avg(s.SumQ, s.Shots):F2}, avg P {Avg(s.SumP, s.Shots):F2}");
                string[] zones = { "rim", "mid", "deep" };
                for (int z = 0; z < 3; z++) Console.WriteLine($"  {zones[z],-4} {s.ZoneShots[z] / (double)n:F1}/match  {Pct(s.ZoneMakes[z], s.ZoneShots[z]):F1}%");
                Console.WriteLine($"  grades P/G/L/E: {s.Grades[0]} {s.Grades[1]} {s.Grades[2]} {s.Grades[3]}");
                Console.WriteLine($"  defense dealt: blocks {s.Blocks / (double)n:F2} steals {s.Steals / (double)n:F2} charges {s.Charges / (double)n:F2}; offense: ankles {s.Ankles / (double)n:F2} bites {s.Bites / (double)n:F2} finishers {s.Finishers} (denied by them {s.Denied})");
                Console.Write("  endings:");
                foreach (var kv in s.Endings) Console.Write($" {kv.Key}={kv.Value / (double)n:F1}");
                Console.WriteLine();
                Console.WriteLine("  defender at release (top):");
                var list = new List<KeyValuePair<string, int>>(s.DefAtRelease);
                list.Sort((x, y) => y.Value.CompareTo(x.Value));
                for (int j = 0; j < Math.Min(8, list.Count); j++) Console.WriteLine($"    {list[j].Value / (double)n,5:F1}/match  {list[j].Key}");
            }
            return 0;
        }

        private static void End(Side s, string how)
        {
            s.Possessions++;
            s.Endings.TryGetValue(how, out int c);
            s.Endings[how] = c + 1;
        }

        private static string Bucket(Fix d)
        {
            if (d < Fix.One) return "<1m";
            if (d < Fix.Two) return "1-2m";
            if (d < Fix.FromInt(3)) return "2-3m";
            return "3m+";
        }

        private static double Pct(long a, long b) => b == 0 ? 0 : 100.0 * a / b;
        private static double Avg(long sumRaw, long n) => n == 0 ? 0 : sumRaw / (double)Fix.OneRaw / n;
    }
}
