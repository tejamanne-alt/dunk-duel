using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DunkDuel.AI;
using DunkDuel.Presentation;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using UnityEngine;

namespace DunkDuel.UI
{
    /// <summary>Localization-ready strings: Resources/DunkDuelPresentation/strings_&lt;lang&gt;.json (flat key to text).</summary>
    public static class Loc
    {
        private static readonly Dictionary<string, string> _strings = new Dictionary<string, string>();
        public static string Language { get; private set; } = "en";

        public static void Load(string language)
        {
            Language = language;
            _strings.Clear();
            JsonValue root = PJson.Load(PresentationData.Root + "strings_" + language) ?? PJson.Load(PresentationData.Root + "strings_en");
            if (root == null || root.Members == null) return;
            foreach (KeyValuePair<string, JsonValue> kv in root.Members)
            {
                if (kv.Value.Kind == JsonKind.String) _strings[kv.Key] = kv.Value.Text;
            }
        }

        public static string T(string key, string fallback = null)
        {
            if (key != null && _strings.TryGetValue(key, out string s)) return s;
            return fallback ?? key;
        }

        public static string F(string key, params object[] args)
        {
            string fmt = T(key);
            try
            {
                return string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args);
            }
            catch (FormatException)
            {
                return fmt;
            }
        }
    }

    /// <summary>Player settings persisted in PlayerPrefs.</summary>
    public sealed class GameSettings
    {
        public bool SimpleMode;
        public bool Haptics = true;
        public int HapticStrength = 3;     // 0..4
        public int MasterVolume = 8;       // 0..10
        public int MusicVolume = 6;
        public int SfxVolume = 8;
        public QualityMode Quality = QualityMode.Auto;
        public bool ReducedMotion;
        public bool ReducedFlash;
        public bool Colorblind;
        public bool LargeText;
        public bool InputDisplay = true;
        public bool LeftHanded;
        public int BestOf = 3;
        public string PlayerName = "Player";
        public string Language = "en";
        public string Bindings = "";

        private const string Prefix = "dd.settings.";

        public static GameSettings Load()
        {
            var s = new GameSettings
            {
                SimpleMode = PlayerPrefs.GetInt(Prefix + "simple", 0) == 1,
                Haptics = PlayerPrefs.GetInt(Prefix + "haptics", 1) == 1,
                HapticStrength = PlayerPrefs.GetInt(Prefix + "hapticStrength", 3),
                MasterVolume = PlayerPrefs.GetInt(Prefix + "master", 8),
                MusicVolume = PlayerPrefs.GetInt(Prefix + "music", 6),
                SfxVolume = PlayerPrefs.GetInt(Prefix + "sfx", 8),
                Quality = (QualityMode)PlayerPrefs.GetInt(Prefix + "quality", 0),
                ReducedMotion = PlayerPrefs.GetInt(Prefix + "reducedMotion", 0) == 1,
                ReducedFlash = PlayerPrefs.GetInt(Prefix + "reducedFlash", 0) == 1,
                Colorblind = PlayerPrefs.GetInt(Prefix + "colorblind", 0) == 1,
                LargeText = PlayerPrefs.GetInt(Prefix + "largeText", 0) == 1,
                InputDisplay = PlayerPrefs.GetInt(Prefix + "inputDisplay", 1) == 1,
                LeftHanded = PlayerPrefs.GetInt(Prefix + "leftHanded", 0) == 1,
                BestOf = PlayerPrefs.GetInt(Prefix + "bestOf", 3),
                PlayerName = PlayerPrefs.GetString(Prefix + "name", "Player"),
                Language = PlayerPrefs.GetString(Prefix + "language", "en"),
                Bindings = PlayerPrefs.GetString(Prefix + "bindings", ""),
            };
            return s;
        }

        public void Save()
        {
            PlayerPrefs.SetInt(Prefix + "simple", SimpleMode ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "haptics", Haptics ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "hapticStrength", HapticStrength);
            PlayerPrefs.SetInt(Prefix + "master", MasterVolume);
            PlayerPrefs.SetInt(Prefix + "music", MusicVolume);
            PlayerPrefs.SetInt(Prefix + "sfx", SfxVolume);
            PlayerPrefs.SetInt(Prefix + "quality", (int)Quality);
            PlayerPrefs.SetInt(Prefix + "reducedMotion", ReducedMotion ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "reducedFlash", ReducedFlash ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "colorblind", Colorblind ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "largeText", LargeText ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "inputDisplay", InputDisplay ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "leftHanded", LeftHanded ? 1 : 0);
            PlayerPrefs.SetInt(Prefix + "bestOf", BestOf);
            PlayerPrefs.SetString(Prefix + "name", PlayerName ?? "Player");
            PlayerPrefs.SetString(Prefix + "language", Language ?? "en");
            PlayerPrefs.SetString(Prefix + "bindings", Bindings ?? "");
            PlayerPrefs.Save();
        }

        /// <summary>Pushes settings into the services that consume them.</summary>
        public void Apply(App app)
        {
            Presentation.Haptics.Enabled = Haptics;
            Presentation.Haptics.Intensity = HapticStrength / 4f;
            if (app.Audio != null)
            {
                app.Audio.MasterVolume = MasterVolume / 10f;
                app.Audio.MusicVolume = MusicVolume / 10f;
                app.Audio.SfxVolume = SfxVolume / 10f;
            }
            if (app.Quality != null && app.Quality.Mode != Quality) app.Quality.Apply(Quality);
            Theme.Apply(Colorblind, LargeText);
            if (Loc.Language != Language) Loc.Load(Language);
        }
    }

    /// <summary>Optional rewarded ads hook. No SDK ships: an ad provider must be approved before integration.</summary>
    public interface IRewardedAdProvider
    {
        bool IsReady { get; }
        void Show(Action<bool> onComplete);
    }

    public sealed class NullRewardedAdProvider : IRewardedAdProvider
    {
        public bool IsReady => false;
        public void Show(Action<bool> onComplete) => onComplete?.Invoke(false);
    }

    /// <summary>
    /// Cosmetic-only progression: Court Cred, fighter mastery levels, unlocked outfits / ball skins / effect tints.
    /// Saved as JSON with an integrity hash; a tampered file resets to defaults. Never affects gameplay.
    /// </summary>
    public sealed class Progression
    {
        public const int MaxLevel = 20;
        public int Cred;
        public readonly int[] MasteryXp = new int[8];
        public readonly int[] Outfit = new int[8];
        public int BallSkin;
        public readonly List<string> Unlocked = new List<string>();
        public bool WasTampered { get; private set; }

        private static string PathOf => System.IO.Path.Combine(Application.persistentDataPath, "progress.json");
        private const string Salt = "dunkduel-cosmetics-v1";

        public static int LevelFor(int xp) => Mathf.Min(MaxLevel, 1 + xp / 400);

        public bool IsUnlocked(string id) => Unlocked.Contains(id);

        /// <summary>Awards a match. Returns the Cred earned (before any rewarded-ad doubling).</summary>
        public int AwardMatch(int fighterIndex, bool won, in FighterState stats)
        {
            int cred = (won ? 100 : 40) + stats.Steals * 5 + stats.Blocks * 5 + stats.Dunks * 3 + stats.AnkleBreaks * 10
                + stats.Posters * 15 + stats.Finishers * 20 + stats.Denials * 15;
            Cred += cred;
            if (fighterIndex >= 0 && fighterIndex < MasteryXp.Length)
            {
                int before = LevelFor(MasteryXp[fighterIndex]);
                MasteryXp[fighterIndex] += cred + (won ? 60 : 20);
                int after = LevelFor(MasteryXp[fighterIndex]);
                for (int lv = before + 1; lv <= after; lv++) UnlockForLevel(fighterIndex, lv);
            }
            return cred;
        }

        private void UnlockForLevel(int fighter, int level)
        {
            if (level == 3) Unlock("outfit." + fighter + ".1");
            if (level == 6) Unlock("tint." + fighter + ".1");
            if (level == 9) Unlock("outfit." + fighter + ".2");
            if (level == 12) Unlock("ball.street");
            if (level == 15) Unlock("intro." + fighter + ".1");
            if (level == 20) Unlock("pose." + fighter + ".1");
        }

        public void Unlock(string id)
        {
            if (!Unlocked.Contains(id)) Unlocked.Add(id);
        }

        public bool Spend(int cred)
        {
            if (Cred < cred) return false;
            Cred -= cred;
            return true;
        }

        public void Save()
        {
            string payload = Serialize();
            string json = "{\"payload\":" + Quote(payload) + ",\"hash\":\"" + Hash(payload).ToString("X16") + "\"}";
            try
            {
                File.WriteAllText(PathOf, json);
            }
            catch (IOException e)
            {
                Debug.LogWarning("Dunk Duel: could not save progress: " + e.Message);
            }
        }

        public static Progression Load()
        {
            var p = new Progression();
            try
            {
                if (!File.Exists(PathOf)) return p;
                JsonValue root = Json.Parse(File.ReadAllText(PathOf), "progress.json");
                string payload = PJson.Str(root, "payload");
                string hash = PJson.Str(root, "hash");
                if (Hash(payload).ToString("X16") != hash)
                {
                    p.WasTampered = true;
                    return p;
                }
                p.Deserialize(payload);
            }
            catch (Exception e)
            {
                Debug.LogWarning("Dunk Duel: progress unreadable, starting fresh: " + e.Message);
            }
            return p;
        }

        private string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append("cred=").Append(Cred).Append(';');
            sb.Append("ball=").Append(BallSkin).Append(';');
            for (int i = 0; i < MasteryXp.Length; i++) sb.Append("xp").Append(i).Append('=').Append(MasteryXp[i]).Append(';');
            for (int i = 0; i < Outfit.Length; i++) sb.Append("outfit").Append(i).Append('=').Append(Outfit[i]).Append(';');
            sb.Append("unlocked=").Append(string.Join(",", Unlocked.ToArray())).Append(';');
            return sb.ToString();
        }

        private void Deserialize(string payload)
        {
            foreach (string part in payload.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                string k = part.Substring(0, eq), v = part.Substring(eq + 1);
                int.TryParse(v, out int n);
                if (k == "cred") Cred = n;
                else if (k == "ball") BallSkin = n;
                else if (k.StartsWith("xp", StringComparison.Ordinal) && int.TryParse(k.Substring(2), out int xi) && xi >= 0 && xi < MasteryXp.Length) MasteryXp[xi] = n;
                else if (k.StartsWith("outfit", StringComparison.Ordinal) && int.TryParse(k.Substring(6), out int oi) && oi >= 0 && oi < Outfit.Length) Outfit[oi] = n;
                else if (k == "unlocked" && v.Length > 0) Unlocked.AddRange(v.Split(','));
            }
        }

        private static ulong Hash(string payload) => TableBinder.HashText(Salt + payload);

        private static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    /// <summary>Road to The Crown: a six-match ladder from the streets to the finals, saved per fighter.</summary>
    public sealed class LadderProgress
    {
        public struct Stage
        {
            public string Court;
            public CpuTier Tier;
        }

        public static readonly Stage[] Stages =
        {
            new Stage { Court = "monsoon_bazaar", Tier = CpuTier.Rookie },
            new Stage { Court = "the_tracks", Tier = CpuTier.Varsity },
            new Stage { Court = "rivalry_night", Tier = CpuTier.Pro },
            new Stage { Court = "campus_quad", Tier = CpuTier.Pro },
            new Stage { Court = "old_barn", Tier = CpuTier.AllStar },
            new Stage { Court = "the_crown", Tier = CpuTier.Legend },
        };

        private const string Prefix = "dd.ladder.";

        public int StageFor(string fighterId) => PlayerPrefs.GetInt(Prefix + fighterId, 0);

        public bool Completed(string fighterId) => PlayerPrefs.GetInt(Prefix + fighterId + ".done", 0) == 1;

        public void Advance(string fighterId)
        {
            int next = StageFor(fighterId) + 1;
            if (next >= Stages.Length)
            {
                PlayerPrefs.SetInt(Prefix + fighterId + ".done", 1);
                next = 0;
            }
            PlayerPrefs.SetInt(Prefix + fighterId, next);
            PlayerPrefs.Save();
        }

        public void Reset(string fighterId)
        {
            PlayerPrefs.SetInt(Prefix + fighterId, 0);
            PlayerPrefs.Save();
        }

        /// <summary>Opponent for a stage: every other fighter in turn, with a mirror match only if the roster is tiny.</summary>
        public static int Opponent(SimTables tables, int fighter, int stage)
        {
            int n = tables.FighterCount;
            if (n <= 1) return fighter;
            int k = stage % (n - 1);
            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                if (i == fighter) continue;
                if (idx == k) return i;
                idx++;
            }
            return (fighter + 1) % n;
        }
    }

    /// <summary>Rival mode: nudges the CPU's tier between rounds toward a 50% win rate for the player.</summary>
    public sealed class RivalController
    {
        private readonly CpuTiers _tiers;
        private readonly CpuPlayer _cpu;
        public Fix Skill { get; private set; }
        public int HumanRounds { get; private set; }
        public int CpuRounds { get; private set; }

        public RivalController(CpuTiers tiers, CpuPlayer cpu, CpuTier start)
        {
            _tiers = tiers;
            _cpu = cpu;
            Skill = Fix.FromInt((int)start);
            cpu.SetTier(tiers.Blend(Skill));
        }

        public void RoundEnded(bool humanWon)
        {
            RivalParams r = _tiers.Table.Rival;
            if (humanWon) HumanRounds++;
            else CpuRounds++;
            // Toward 50%: step up after a human round win, down after a loss, larger steps when lopsided.
            Fix step = r.StepPerRound * (1 + Math.Min(2, Math.Abs(HumanRounds - CpuRounds)));
            Skill = Fix.Clamp(humanWon ? Skill + step : Skill - step, r.MinSkill, r.MaxSkill);
            _cpu.SetTier(_tiers.Blend(Skill));
        }

        public string Describe()
        {
            int lo = Skill.FloorToInt();
            string[] names = { "Rookie", "Varsity", "Pro", "All-Star", "Legend" };
            return "Rival " + names[Mathf.Clamp(lo, 0, 4)] + (Skill.FloorToInt() < 4 && (Skill - lo) > Fix.Zero ? "+" : "");
        }
    }
}
