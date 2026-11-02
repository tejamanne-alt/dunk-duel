using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Tools
{
    /// <summary>
    /// Automated balance search (pattern search with shrinking steps) over per-fighter data knobs and CPU style
    /// weights. Every evaluation replays identical match seeds (common random numbers), so a change in the loss
    /// comes from the data. Loss = sum over pairings of (win% - 50)^2. --apply writes the best values back.
    /// </summary>
    public static class Tuner
    {
        private sealed class Knob
        {
            public string File;      // data file key, e.g. "fighters/kofi" or "ai"
            public string Fighter;
            public string Key;
            public bool IsInt;
            public decimal Value;
            public decimal Step;
            public decimal MinStep;
            public decimal Min;
            public decimal Max;

            public string Format() => IsInt ? ((int)Value).ToString() : "\"" + Value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + "\"";
            public string Name => Fighter + "." + Key;
        }

        public static int Run(string repo, int matchesPerSide, int maxEvals, bool apply, int threads)
        {
            string dataDir = Path.Combine(repo, "Assets", "DunkDuel", "Data", "Resources", "DunkDuelData");
            var files = new Dictionary<string, string>();
            foreach (string name in new[] { "tuning", "moves", "impacts", "ai" }) files[name] = File.ReadAllText(Path.Combine(dataDir, name + ".json"));
            string[] fighters = { "kofi", "meera", "juno", "matas" };
            foreach (string f in fighters) files["fighters/" + f] = File.ReadAllText(Path.Combine(dataDir, "fighters", f + ".json"));

            var knobs = new List<Knob>();
            foreach (string f in fighters)
            {
                // Design guard: the two guards stay layup-only (shorter jump and reach caps).
                bool guard = f == "juno" || f == "meera";
                knobs.Add(FighterKnob(files, f, "jumperGatherFrames", true, 3, 1, 20, 42));
                knobs.Add(FighterKnob(files, f, "finishGatherFrames", true, 3, 1, 10, 30));
                knobs.Add(FighterKnob(files, f, "jumpRiseFrames", true, 2, 1, 10, guard ? 15 : 19));
                knobs.Add(FighterKnob(files, f, "reachFactor", false, 0.03m, 0.01m, 1.22m, guard ? 1.34m : 1.40m));
                knobs.Add(FighterKnob(files, f, "pushboxHalfWidth", false, 0.03m, 0.01m, 0.20m, 0.40m));
                knobs.Add(AiKnob(files, f, "shootWeight", 0.3m, 0.05m, 0.3m, 2.2m));
                knobs.Add(AiKnob(files, f, "driveWeight", 0.3m, 0.05m, 0.3m, 2.2m));
            }

            int evals = 0;
            double best = Evaluate(files, fighters, knobs, matchesPerSide, threads, out double[] rates);
            evals++;
            Console.WriteLine($"start loss {best:F1}  {Rates(fighters, rates)}");
            ulong order = 12345;
            while (evals < maxEvals)
            {
                bool improved = false;
                // Shuffled knob order each pass (deterministic).
                var idx = new List<int>();
                for (int i = 0; i < knobs.Count; i++) idx.Add(i);
                for (int i = idx.Count - 1; i > 0; i--)
                {
                    int j = Rng.Range(ref order, 0, i + 1);
                    (idx[i], idx[j]) = (idx[j], idx[i]);
                }
                foreach (int i in idx)
                {
                    if (evals >= maxEvals) break;
                    Knob k = knobs[i];
                    decimal original = k.Value;
                    foreach (int dir in new[] { 1, -1 })
                    {
                        decimal v = original + dir * k.Step;
                        if (v < k.Min || v > k.Max) continue;
                        k.Value = v;
                        double loss = Evaluate(files, fighters, knobs, matchesPerSide, threads, out double[] r);
                        evals++;
                        if (loss < best - 2.0)
                        {
                            best = loss;
                            rates = r;
                            original = v;
                            improved = true;
                            Console.WriteLine($"  [{evals}] {k.Name} -> {k.Format()}  loss {loss:F1}  {Rates(fighters, r)}");
                            Console.Out.Flush();
                            break;
                        }
                    }
                    k.Value = original;
                }
                if (!improved)
                {
                    bool shrunk = false;
                    foreach (Knob k in knobs)
                    {
                        if (k.Step > k.MinStep)
                        {
                            k.Step = Math.Max(k.MinStep, k.Step / 2);
                            if (k.IsInt) k.Step = Math.Max(1, Math.Round(k.Step));
                            shrunk = true;
                        }
                    }
                    if (!shrunk) break;
                    Console.WriteLine("  (steps halved)");
                }
            }

            Console.WriteLine($"best loss {best:F1}  {Rates(fighters, rates)}  after {evals} evaluations");
            foreach (Knob k in knobs) Console.WriteLine($"  {k.Name} = {k.Format()}");
            if (apply)
            {
                foreach (string key in new List<string>(files.Keys))
                {
                    string text = Patched(files, key, knobs);
                    if (text == files[key]) continue;
                    string path = Path.Combine(dataDir, key.Replace('/', Path.DirectorySeparatorChar) + ".json");
                    File.WriteAllText(path, text);
                }
                Console.WriteLine("applied");
            }
            return 0;
        }

        private static Knob FighterKnob(Dictionary<string, string> files, string fighter, string key, bool isInt, decimal step, decimal minStep, decimal min, decimal max)
        {
            string file = "fighters/" + fighter;
            Match m = Regex.Match(files[file], "\"" + key + "\": \"?([0-9.\\-]+)\"?");
            if (!m.Success) throw new InvalidOperationException(fighter + " has no " + key);
            return new Knob { File = file, Fighter = fighter, Key = key, IsInt = isInt, Value = Parse(m.Groups[1].Value), Step = step, MinStep = minStep, Min = min, Max = max };
        }

        private static Knob AiKnob(Dictionary<string, string> files, string fighter, string key, decimal step, decimal minStep, decimal min, decimal max)
        {
            Match m = Regex.Match(files["ai"], "\"fighter\": \"" + fighter + "\"[^}]*?\"" + key + "\": \"([0-9.]+)\"");
            if (!m.Success) throw new InvalidOperationException("ai.json has no " + key + " for " + fighter);
            return new Knob { File = "ai", Fighter = fighter, Key = key, IsInt = false, Value = Parse(m.Groups[1].Value), Step = step, MinStep = minStep, Min = min, Max = max };
        }

        private static decimal Parse(string s) => decimal.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

        private static string Patched(Dictionary<string, string> files, string key, List<Knob> knobs)
        {
            string text = files[key];
            foreach (Knob k in knobs)
            {
                if (k.File != key) continue;
                if (key == "ai")
                {
                    text = Regex.Replace(text, "(\"fighter\": \"" + k.Fighter + "\"[^}]*?\"" + k.Key + "\": )\"[0-9.]+\"", "${1}" + k.Format());
                }
                else
                {
                    text = Regex.Replace(text, "\"" + k.Key + "\": \"?[0-9.\\-]+\"?", "\"" + k.Key + "\": " + k.Format());
                }
            }
            return text;
        }

        private static double Evaluate(Dictionary<string, string> files, string[] fighters, List<Knob> knobs, int perSide, int threads, out double[] rates)
        {
            var src = new MemoryDataSource();
            foreach (string key in files.Keys) src.Add(key, Patched(files, key, knobs));
            SimTables tables = SimTables.Load(src);
            CpuTiers tiers = CpuTiers.Load(tables, src);
            int n = fighters.Length;
            var list = new List<double>();
            double loss = 0;
            for (int a = 0; a < n; a++)
            {
                for (int b = a + 1; b < n; b++)
                {
                    long wins = 0;
                    int total = perSide * 2;
                    int fa = a, fb = b;
                    Parallel.For(0, total, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
                    {
                        bool swap = (k & 1) == 1;
                        ulong ms = 0xBA1A0CEUL * (ulong)(fa * 7 + fb * 3 + 1) + (ulong)k * 1442695040888963407UL;
                        var setup = MatchSetup.Versus(swap ? fb : fa, swap ? fa : fb, ms);
                        setup.FirstOffense = (k >> 1) & 1;
                        var session = new LocalSession(tables, setup,
                            new CpuPlayer(tables, tiers, CpuTier.Legend, 0, ms ^ 0x1111UL),
                            new CpuPlayer(tables, tiers, CpuTier.Legend, 1, ms ^ 0x2222UL));
                        int guard = 60 * 60 * 30;
                        while (!session.IsOver && guard-- > 0) session.Tick();
                        if (session.State.MatchWinner == (swap ? 1 : 0)) Interlocked.Increment(ref wins);
                    });
                    double rate = 100.0 * wins / total;
                    list.Add(rate);
                    loss += (rate - 50) * (rate - 50);
                }
            }
            rates = list.ToArray();
            return loss;
        }

        private static string Rates(string[] f, double[] r)
        {
            var sb = new StringBuilder();
            int i = 0;
            for (int a = 0; a < f.Length; a++)
                for (int b = a + 1; b < f.Length; b++)
                    sb.Append(f[a].Substring(0, 2)).Append('-').Append(f[b].Substring(0, 2)).Append(' ').Append(r[i++].ToString("F0")).Append("  ");
            return sb.ToString();
        }
    }
}
