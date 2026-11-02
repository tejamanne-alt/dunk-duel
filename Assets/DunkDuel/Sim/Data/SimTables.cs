using System;

namespace DunkDuel.Sim.Data
{
    /// <summary>Per-fighter numbers derived from the attribute budget at bake time.</summary>
    public sealed class FighterStats
    {
        public int Index;
        public CharacterDefinition Def;
        public Fix Height;
        public Fix Reach;          // standing reach
        public Fix PushW;          // pushbox half width
        public Fix JumpV0;         // takeoff vertical speed (m/frame) of a full jump
        public Fix JumpHeight;     // apex height of a full jump
        public Fix WalkSpeed;
        public Fix DriveSpeed;
        public Fix SlideSpeed;
        public Fix HandlesMult;
        public Fix Resist;
        public Fix ContestMult;
        public Fix BlockPower;
        public Fix SwipeReachBonus;
        public Fix BaseRim;
        public Fix BaseMid;
        public Fix BaseDeep;
        public bool CanDunk;
        public int MaxComboLinks;
        public int PerfectFrames;
        public MoveDefinition[] Tricks; // indexed by TrickSlot

        public Fix MaxHand => Reach + JumpHeight;
    }

    /// <summary>
    /// Immutable, baked tables for one build. Created once from JSON; shared by the sim, AI and presentation.
    /// <see cref="DataHash"/> feeds the sim version used by the netcode handshake.
    /// </summary>
    public sealed class SimTables
    {
        public static readonly string[] CoreFiles = { "tuning", "moves", "impacts" };

        public Tuning T;
        public CommonMovesTable Moves;
        public MoveDefinition[] Shakes;     // 0 crossover, 1 stepback, 2 spin, 3 hesitation
        public ImpactDef[] ImpactByEvent;   // indexed by SimEventType, null = no impact
        public FighterStats[] Fighters;
        public ulong DataHash;

        public int FighterCount => Fighters.Length;

        public int FighterIndex(string id)
        {
            for (int i = 0; i < Fighters.Length; i++) if (Fighters[i].Def.Id == id) return i;
            return -1;
        }

        public static SimTables Load(IDataSource source)
        {
            ulong hash = 14695981039346656037UL;
            string Read(string name)
            {
                string text = source.Read(name);
                if (text == null) throw new DataException("missing data table '" + name + "'");
                hash = TableBinder.HashText(name, hash);
                hash = TableBinder.HashText(text, hash);
                return text;
            }

            JsonValue tuningJson = Json.Parse(Read("tuning"), "tuning.json");
            int tickRate = tuningJson.Require("tickRate").AsInt();
            if (tickRate <= 0) throw new DataException("tickRate must be positive");

            var tables = new SimTables();
            tables.T = TableBinder.Bind<Tuning>(tuningJson, tickRate);
            tables.Moves = TableBinder.Bind<CommonMovesTable>(Json.Parse(Read("moves"), "moves.json"), tickRate);
            ImpactsTable impacts = TableBinder.Bind<ImpactsTable>(Json.Parse(Read("impacts"), "impacts.json"), tickRate);

            tables.Shakes = new[] { tables.Moves.Crossover, tables.Moves.Stepback, tables.Moves.Spin, tables.Moves.Hesitation };
            tables.ImpactByEvent = new ImpactDef[(int)SimEventType.Count];
            for (int i = 0; i < impacts.Impacts.Length; i++)
            {
                ImpactDef d = impacts.Impacts[i];
                if (tables.ImpactByEvent[(int)d.Event] != null) throw new DataException("impacts.json: duplicate impact for " + d.Event);
                if (d.SlowFrames > 0 && d.SlowDiv < 2) throw new DataException("impacts.json: slowDiv must be >= 2 for " + d.Event);
                tables.ImpactByEvent[(int)d.Event] = d;
            }

            string[] ids = tables.T.Fighters;
            if (ids == null || ids.Length < 1 || ids.Length > 200) throw new DataException("tuning.json: fighters list is empty");
            tables.Fighters = new FighterStats[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                CharacterDefinition def = TableBinder.Bind<CharacterDefinition>(Json.Parse(Read("fighters/" + ids[i]), ids[i] + ".json"), tickRate);
                if (def.Id != ids[i]) throw new DataException(ids[i] + ".json: id '" + def.Id + "' does not match its file");
                tables.Fighters[i] = Derive(tables.T, def, i);
            }

            Validate(tables);
            tables.DataHash = hash;
            return tables;
        }

        private static FighterStats Derive(Tuning t, CharacterDefinition d, int index)
        {
            AttributeBlock a = d.Attributes;
            var s = new FighterStats { Index = index, Def = d };
            s.Height = d.Height;
            s.Reach = d.Height * d.ReachFactor;
            s.PushW = d.PushboxHalfWidth;
            // Discrete jump: v0 = -g * R so the apex is reached after R frames; apex = -g * R * (R - 1) / 2.
            Fix g = t.Physics.FighterGravity;
            int r = d.JumpRiseFrames;
            s.JumpV0 = -g * r;
            s.JumpHeight = -g * (r * (r - 1)) / 2;
            s.WalkSpeed = t.Movement.WalkBase + t.Movement.WalkPerSpeed * (a.Speed - 5);
            s.DriveSpeed = t.Movement.DriveBase + t.Movement.DrivePerSpeed * (a.Speed - 5);
            s.SlideSpeed = t.Movement.SlideBase + t.Movement.SlidePerSpeed * (a.Speed - 5);
            s.HandlesMult = t.Balance.HandlesMultBase + t.Balance.HandlesMultPerPoint * (a.Handles - 5);
            s.Resist = Fix.Clamp(t.Balance.ResistPerStrength * (a.Strength - 5) + t.Balance.ResistPerDefense * (a.Defense - 5) + d.ResistBonus,
                t.Balance.ResistMin, t.Balance.ResistMax);
            s.ContestMult = Fix.One + t.Contest.DefensePerPoint * (a.Defense - 5);
            s.BlockPower = Fix.One + t.Contest.BlockPowerPerDefense * (a.Defense - 5);
            s.SwipeReachBonus = t.Read.SwipeReachPerDefense * (a.Defense - 5);
            s.BaseRim = t.Shot.BaseRim + t.Shot.PerPoint * (a.Finishing - 5);
            s.BaseMid = t.Shot.BaseMid + t.Shot.PerPoint * (a.Range - 5);
            s.BaseDeep = t.Shot.BaseDeep + t.Shot.PerPoint * (a.Range - 5);
            s.CanDunk = s.Reach + s.JumpHeight >= t.Court.RimY + t.Shot.DunkClearance;
            s.MaxComboLinks = d.ComboLinks;
            s.PerfectFrames = d.PerfectFrames;
            s.Tricks = new[] { d.TrickForward, d.TrickBack, d.TrickDefense };
            return s;
        }

        private static void Validate(SimTables tables)
        {
            Tuning t = tables.T;
            for (int i = 0; i < tables.Fighters.Length; i++)
            {
                CharacterDefinition d = tables.Fighters[i].Def;
                AttributeBlock a = d.Attributes;
                int[] vals = { a.Speed, a.Handles, a.Range, a.Finishing, a.Defense, a.Strength };
                int nines = 0;
                for (int k = 0; k < vals.Length; k++)
                {
                    if (vals[k] < 1 || vals[k] > 10) throw new DataException(d.Id + ": attributes must be 1-10");
                    if (vals[k] == 9) nines++;
                }
                if (a.Total != 36) throw new DataException(d.Id + ": attribute budget is " + a.Total + ", must be 36");
                if (nines != 1) throw new DataException(d.Id + ": exactly one attribute must be the mastery 9");
                if (d.ComboLinks < 1 || d.ComboLinks > t.Balance.ComboMultipliers.Length)
                    throw new DataException(d.Id + ": comboLinks exceeds comboMultipliers");
                if (d.TrickForward.Kind != MoveKind.OffenseTrick || d.TrickBack.Kind != MoveKind.OffenseTrick || d.TrickDefense.Kind != MoveKind.DefenseTrick)
                    throw new DataException(d.Id + ": trick kinds must be offense, offense, defense");
                ValidateMove(d.TrickForward, true);
                ValidateMove(d.TrickBack, true);
                ValidateMove(d.TrickDefense, true);
                FinisherDefinition f = d.Finisher;
                if (f.Frames < 120 || f.Frames > 180) throw new DataException(d.Id + ": finisher must last 120-180 frames");
                if (f.DenyFrames < 4 || f.DenyFrames > 6) throw new DataException(d.Id + ": denial window must be 4-6 frames");
                if (f.DenyStart < 1 || f.DenyStart + f.DenyFrames >= f.Frames) throw new DataException(d.Id + ": denial window outside finisher");
                if (f.ZoneMin >= f.ZoneMax) throw new DataException(d.Id + ": finisher zone is empty");
            }
            for (int i = 0; i < tables.Shakes.Length; i++) ValidateMove(tables.Shakes[i], false);
            ValidateMove(tables.Moves.Swipe, false);
            if (t.Shot.PerfectFrames > t.Shot.GoodFrames) throw new DataException("tuning: perfect window wider than good window");
            if (t.Hype.Max != 3 * t.Hype.Bar) throw new DataException("tuning: hype must be three bars");
        }

        private static void ValidateMove(MoveDefinition m, bool trick)
        {
            if (m.Startup < 0 || m.Active < 0 || m.Recovery < 0 || m.Total <= 0) throw new DataException(m.Id + ": bad frame data");
            if (m.BaitEnd < m.BaitStart || m.ExposedEnd < m.ExposedStart || m.CancelEnd < m.CancelStart || m.MoveEnd < m.MoveStart)
                throw new DataException(m.Id + ": window end before start");
            if (trick && m.Params == null) throw new DataException(m.Id + ": tricks need params");
        }

        /// <summary>Hype gain after Simple-mode scaling.</summary>
        public int ScaleHype(in FighterState f, int amount)
        {
            if ((f.Status & StatusFlag.SimpleMode) == 0 || amount <= 0) return amount;
            return (T.Hype.SimpleModeScale * amount).FloorToInt();
        }
    }

    /// <summary>Reads tables from a folder on disk (headless tools and tests).</summary>
    public sealed class FolderDataSource : IDataSource
    {
        private readonly string _root;

        public FolderDataSource(string root) { _root = root; }

        public string Read(string name)
        {
            string path = System.IO.Path.Combine(_root, name.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".json");
            return System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : null;
        }

        /// <summary>Walks up from <paramref name="start"/> to find Assets/DunkDuel/Data/Resources/DunkDuelData.</summary>
        public static string FindDataRoot(string start)
        {
            string dir = start;
            for (int i = 0; i < 12 && !string.IsNullOrEmpty(dir); i++)
            {
                string candidate = System.IO.Path.Combine(dir, "Assets", "DunkDuel", "Data", "Resources", "DunkDuelData");
                if (System.IO.Directory.Exists(candidate)) return candidate;
                dir = System.IO.Path.GetDirectoryName(dir);
            }
            throw new InvalidOperationException("Could not find Assets/DunkDuel/Data/Resources/DunkDuelData above " + start);
        }
    }
}
