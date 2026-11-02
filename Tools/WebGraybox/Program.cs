using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text;
using DunkDuel.AI;
using DunkDuel.Sim;
using DunkDuel.Sim.Data;
using DunkDuel.Sim.Session;

namespace DunkDuel.Web
{
    /// <summary>Embedded JSON tables (the same files Unity loads from Resources).</summary>
    internal sealed class EmbeddedData : IDataSource
    {
        public string Read(string name)
        {
            using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DunkDuelData/" + name + ".json");
            if (s == null) return null;
            using var r = new StreamReader(s, Encoding.UTF8);
            return r.ReadToEnd();
        }
    }

    /// <summary>Input written by the page once per tick.</summary>
    internal sealed class PageInput : IInputSource
    {
        public FrameInput Value;
        public FrameInput NextInput(in GameState state, int player) => Value;
    }

    public static partial class Graybox
    {
        private const int FighterFields = 26;
        private static SimTables _tables;
        private static CpuTiers _tiers;
        private static LocalSession _session;
        private static PageInput _human;
        private static StepEvents _events;
        private static bool _p0Human;

        public static void Main() { }

        /// <summary>Loads the tables and returns a JSON description of the roster, tiers and court for the page.</summary>
        [JSExport]
        public static string Init()
        {
            var data = new EmbeddedData();
            _tables = SimTables.Load(data);
            _tiers = CpuTiers.Load(_tables, data);
            var sb = new StringBuilder();
            CourtTable c = _tables.T.Court;
            sb.Append("{\"version\":\"").Append(SimBuildInfo.Version(_tables).ToString("X16")).Append("\",");
            sb.Append("\"court\":{");
            Num(sb, "rimX", c.RimX); Num(sb, "rimY", c.RimY); Num(sb, "rimR", c.RimRadius); Num(sb, "ballR", c.BallRadius);
            Num(sb, "boardX", c.BackboardX); Num(sb, "boardBottom", c.BackboardBottom); Num(sb, "boardTop", c.BackboardTop);
            Num(sb, "baseline", c.BaselineX); Num(sb, "arc", c.ArcX); Num(sb, "check", c.CheckX); Num(sb, "left", c.LeftBoundX);
            Num(sb, "ft", c.FreeThrowX, true);
            sb.Append("},\"match\":{\"target\":").Append(_tables.T.Match.RoundTargetPoints)
              .Append(",\"roundFrames\":").Append(_tables.T.Match.RoundFrames)
              .Append(",\"shotClock\":").Append(_tables.T.Match.ShotClockFrames)
              .Append(",\"hypeMax\":").Append(_tables.T.Hype.Max).Append(",\"hypeBar\":").Append(_tables.T.Hype.Bar).Append("},");
            sb.Append("\"fighters\":[");
            for (int i = 0; i < _tables.FighterCount; i++)
            {
                FighterStats f = _tables.Fighters[i];
                CharacterDefinition d = f.Def;
                if (i > 0) sb.Append(',');
                sb.Append("{\"id\":\"").Append(d.Id).Append("\",\"name\":\"").Append(d.Name).Append("\",\"nick\":\"").Append(d.Nickname)
                  .Append("\",\"mastery\":\"").Append(d.Mastery).Append("\",");
                Num(sb, "height", f.Height); Num(sb, "reach", f.Reach); Num(sb, "jump", f.JumpHeight); Num(sb, "push", f.PushW);
                Num(sb, "zoneMin", d.Finisher.ZoneMin); Num(sb, "zoneMax", d.Finisher.ZoneMax);
                sb.Append("\"finisher\":\"").Append(d.Finisher.Name).Append("\",");
                sb.Append("\"tricks\":[\"").Append(d.TrickForward.Name).Append("\",\"").Append(d.TrickBack.Name).Append("\",\"").Append(d.TrickDefense.Name).Append("\"],");
                AttributeBlock a = d.Attributes;
                sb.Append("\"attr\":[").Append(a.Speed).Append(',').Append(a.Handles).Append(',').Append(a.Range).Append(',')
                  .Append(a.Finishing).Append(',').Append(a.Defense).Append(',').Append(a.Strength).Append("]}");
            }
            sb.Append("],\"tiers\":[");
            for (int i = 0; i < 5; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(_tiers[(CpuTier)i].Name).Append('"');
            }
            sb.Append("],");
            Names<SimEventType>(sb, "events");
            Names<ActionId>(sb, "actions");
            Names<Phase>(sb, "phases");
            Names<ShotKind>(sb, "kinds");
            Names<ShotZone>(sb, "zones");
            Names<ReleaseGrade>(sb, "grades");
            Names<ShotResult>(sb, "results");
            sb.Append("\"impacts\":{");
            bool first = true;
            for (int e = 0; e < (int)SimEventType.Count; e++)
            {
                ImpactDef d = _tables.ImpactByEvent[e];
                if (d == null) continue;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(((SimEventType)e).ToString()).Append("\":{\"camera\":\"").Append(d.Camera).Append("\",\"callout\":\"")
                  .Append(d.Callout).Append("\",\"crowd\":\"").Append(d.Crowd).Append("\",\"hitStop\":").Append(d.HitStop).Append('}');
            }
            sb.Append("}}");
            return sb.ToString();
        }

        private static void Names<T>(StringBuilder sb, string key) where T : struct, Enum
        {
            sb.Append('"').Append(key).Append("\":[");
            Array values = Enum.GetValues(typeof(T));
            int max = 0;
            foreach (object v in values) max = Math.Max(max, Convert.ToInt32(v));
            for (int i = 0; i <= max; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('"').Append(Enum.GetName(typeof(T), i) ?? "").Append('"');
            }
            sb.Append("],");
        }

        private static void Num(StringBuilder sb, string key, Fix v, bool last = false)
        {
            sb.Append('"').Append(key).Append("\":").Append(v.ToString(4));
            if (!last) sb.Append(',');
        }

        /// <summary>Starts a match. tier0 &lt; 0 means player 0 is human (page input).</summary>
        [JSExport]
        public static void NewMatch(int fighter0, int fighter1, int tier0, int tier1, int seed, int roundsToWin, bool training, bool simple)
        {
            var setup = MatchSetup.Versus(fighter0, fighter1, (ulong)(uint)seed, roundsToWin);
            setup.Mode = training ? MatchMode.Training : MatchMode.Versus;
            setup.InfiniteHype = training;
            setup.Simple0 = simple;
            _p0Human = tier0 < 0;
            _human = new PageInput();
            IInputSource p0 = _p0Human ? (IInputSource)_human : new CpuPlayer(_tables, _tiers, (CpuTier)tier0, 0, (ulong)(uint)seed * 3 + 1);
            IInputSource p1 = new CpuPlayer(_tables, _tiers, (CpuTier)tier1, 1, (ulong)(uint)seed * 7 + 2);
            _session = new LocalSession(_tables, setup, p0, p1);
            _events = new StepEvents();
            _session.Ledger.Spawned += _events.Add;
        }

        /// <summary>Advances one 60 Hz tick with the page's input for player 0.</summary>
        [JSExport]
        public static void Tick(int inputBits)
        {
            if (_session == null) return;
            _human.Value = new FrameInput((ushort)inputBits);
            _session.Tick();
        }

        /// <summary>Numeric snapshot for rendering plus the events since the last call.</summary>
        [JSExport]
        [return: JSMarshalAs<JSType.Array<JSType.Number>>]
        public static double[] Frame()
        {
            GameState s = _session.State;
            int evCount = _events.Count;
            var f = new double[16 + 2 * FighterFields + 6 + 10 + 8 + 2 + 1 + evCount * 6];
            int i = 0;
            f[i++] = s.Tick; f[i++] = s.Frame; f[i++] = (int)s.Phase; f[i++] = s.PhaseFrame; f[i++] = s.Round; f[i++] = s.Offense;
            f[i++] = s.NeedsClear ? 1 : 0; f[i++] = s.SuddenDeath ? 1 : 0; f[i++] = s.RoundClock; f[i++] = s.ShotClock;
            f[i++] = s.HitStop; f[i++] = s.SlowFrames; f[i++] = s.RoundWinner; f[i++] = s.MatchWinner; f[i++] = s.RoundsToWin; f[i++] = (int)s.Mode;
            Fighter(f, ref i, s.F0);
            Fighter(f, ref i, s.F1);
            f[i++] = (int)s.Ball.Mode; f[i++] = s.Ball.Holder; f[i++] = D(s.Ball.X); f[i++] = D(s.Ball.Y); f[i++] = D(s.Ball.VX); f[i++] = D(s.Ball.VY);
            f[i++] = s.Shot.Active ? 1 : 0; f[i++] = s.Shot.Shooter; f[i++] = (int)s.Shot.Kind; f[i++] = (int)s.Shot.Zone; f[i++] = (int)s.Shot.Grade;
            f[i++] = (int)s.Shot.Result; f[i++] = (int)s.Shot.Stage; f[i++] = D(s.Shot.MakeChance); f[i++] = D(s.Shot.Contest); f[i++] = s.Shot.Points;
            f[i++] = s.Fin.Active ? 1 : 0; f[i++] = s.Fin.Attacker; f[i++] = s.Fin.Frame; f[i++] = s.Fin.Frames; f[i++] = s.Fin.DenyStart;
            f[i++] = s.Fin.DenyFrames; f[i++] = s.Fin.HasRing ? 1 : 0; f[i++] = s.Fin.Attempted ? 1 : 0;
            f[i++] = _session.LastInput0.Bits; f[i++] = _session.LastInput1.Bits;
            f[i++] = evCount;
            for (int e = 0; e < evCount; e++)
            {
                SimEvent ev = _events[e];
                f[i++] = (int)ev.Type; f[i++] = ev.Actor; f[i++] = ev.A; f[i++] = ev.B; f[i++] = D(ev.X); f[i++] = D(ev.Y);
            }
            _events.Clear();
            return f;
        }

        private static void Fighter(double[] f, ref int i, in FighterState p)
        {
            f[i++] = D(p.X); f[i++] = D(p.Y); f[i++] = D(p.VX); f[i++] = D(p.VY); f[i++] = p.Facing; f[i++] = (int)p.Action;
            f[i++] = p.ActionFrame; f[i++] = p.MoveSlot; f[i++] = p.Peak; f[i++] = p.Takeoff; f[i++] = (int)p.PendingKind; f[i++] = p.Hype;
            f[i++] = D(p.Balance); f[i++] = p.Score; f[i++] = p.RoundsWon; f[i++] = p.CharIndex; f[i++] = p.Status; f[i++] = p.Streak;
            f[i++] = p.Rhythm; f[i++] = p.ComboLinks; f[i++] = p.Beaten; f[i++] = p.PlantFrames; f[i++] = p.Param; f[i++] = p.ActionFlags;
            f[i++] = p.FgMade; f[i++] = p.FgAtt;
        }

        /// <summary>Presentation-side conversion; the sim itself never sees a float or double.</summary>
        private static double D(Fix v) => v.Raw / 4294967296.0;

        /// <summary>Collects ledger events between frames.</summary>
        private sealed class StepEvents
        {
            private readonly System.Collections.Generic.List<SimEvent> _list = new System.Collections.Generic.List<SimEvent>();
            public int Count => _list.Count;
            public SimEvent this[int i] => _list[i];
            public void Add(SimEvent e, int age) => _list.Add(e);
            public void Clear() => _list.Clear();
        }
    }
}
