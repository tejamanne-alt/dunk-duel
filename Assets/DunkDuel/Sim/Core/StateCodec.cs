using System;

namespace DunkDuel.Sim
{
    /// <summary>Field visitor used for hashing, serializing and deserializing <see cref="GameState"/>.</summary>
    public interface IStateVisitor
    {
        void Visit(ref ulong v);
        void Visit(ref long v);
        void Visit(ref int v);
        void Visit(ref uint v);
        void Visit(ref ushort v);
        void Visit(ref byte v);
    }

    /// <summary>
    /// The single authoritative field list of <see cref="GameState"/>. Hash, serialization and
    /// deserialization all walk this list, so they can never drift apart. Little-endian, fixed order.
    /// </summary>
    public static class StateCodec
    {
        public const int MaxBytes = 2048;
        public const uint Magic = 0x44445331; // "DDS1"

        public static void Walk<TV>(ref GameState s, ref TV v) where TV : struct, IStateVisitor
        {
            v.Visit(ref s.Tick);
            v.Visit(ref s.Frame);
            v.Visit(ref s.Rng);
            E(ref v, ref s.Phase);
            v.Visit(ref s.PhaseFrame);
            v.Visit(ref s.Round);
            v.Visit(ref s.Offense);
            B(ref v, ref s.NeedsClear);
            B(ref v, ref s.SuddenDeath);
            B(ref v, ref s.ClockExpired);
            v.Visit(ref s.RoundClock);
            v.Visit(ref s.ShotClock);
            v.Visit(ref s.HitStop);
            v.Visit(ref s.SlowFrames);
            v.Visit(ref s.SlowDiv);
            v.Visit(ref s.SlowPhase);
            v.Visit(ref s.RoundWinner);
            v.Visit(ref s.MatchWinner);
            v.Visit(ref s.RoundsToWin);
            byte mode = (byte)s.Mode; v.Visit(ref mode); s.Mode = (MatchMode)mode;
            B(ref v, ref s.InfiniteHype);
            v.Visit(ref s.PendingRelease);
            Fighter(ref v, ref s.F0);
            Fighter(ref v, ref s.F1);
            Ball(ref v, ref s.Ball);
            Shot(ref v, ref s.Shot);
            Fin(ref v, ref s.Fin);
        }

        private static void Fighter<TV>(ref TV v, ref FighterState f) where TV : struct, IStateVisitor
        {
            v.Visit(ref f.CharIndex);
            v.Visit(ref f.Status);
            F(ref v, ref f.X);
            F(ref v, ref f.Y);
            F(ref v, ref f.VX);
            F(ref v, ref f.VY);
            byte facing = (byte)f.Facing; v.Visit(ref facing); f.Facing = (sbyte)facing;
            byte action = (byte)f.Action; v.Visit(ref action); f.Action = (ActionId)action;
            v.Visit(ref f.MoveSlot);
            v.Visit(ref f.ActionFlags);
            v.Visit(ref f.ActionFrame);
            v.Visit(ref f.Peak);
            v.Visit(ref f.Takeoff);
            v.Visit(ref f.Param);
            byte kind = (byte)f.PendingKind; v.Visit(ref kind); f.PendingKind = (ShotKind)kind;
            v.Visit(ref f.PendingPoints);
            v.Visit(ref f.PlantFrames);
            v.Visit(ref f.Beaten);
            v.Visit(ref f.Lag);
            v.Visit(ref f.NoContest);
            v.Visit(ref f.BumpTimer);
            v.Visit(ref f.LastContactFrame);
            v.Visit(ref f.Hype);
            F(ref v, ref f.Balance);
            v.Visit(ref f.BalanceIdle);
            v.Visit(ref f.Score);
            v.Visit(ref f.RoundsWon);
            v.Visit(ref f.Streak);
            v.Visit(ref f.Rhythm);
            v.Visit(ref f.ComboLinks);
            v.Visit(ref f.ComboTimer);
            v.Visit(ref f.Held);
            v.Visit(ref f.PrevHeld);
            v.Visit(ref f.AgeShoot);
            v.Visit(ref f.AgeShake);
            v.Visit(ref f.AgeTrick);
            v.Visit(ref f.AgeFinisher);
            B(ref v, ref f.TrickAmp);
            v.Visit(ref f.DirHistory);
            v.Visit(ref f.FgMade);
            v.Visit(ref f.FgAtt);
            v.Visit(ref f.DeepMade);
            v.Visit(ref f.DeepAtt);
            v.Visit(ref f.Dunks);
            v.Visit(ref f.Steals);
            v.Visit(ref f.Blocks);
            v.Visit(ref f.Charges);
            v.Visit(ref f.AnkleBreaks);
            v.Visit(ref f.Posters);
            v.Visit(ref f.Finishers);
            v.Visit(ref f.FinisherAtt);
            v.Visit(ref f.Denials);
            v.Visit(ref f.Turnovers);
            v.Visit(ref f.BitesDrawn);
        }

        private static void Ball<TV>(ref TV v, ref BallState b) where TV : struct, IStateVisitor
        {
            byte mode = (byte)b.Mode; v.Visit(ref mode); b.Mode = (BallMode)mode;
            v.Visit(ref b.Holder);
            v.Visit(ref b.LastTouch);
            F(ref v, ref b.X);
            F(ref v, ref b.Y);
            F(ref v, ref b.VX);
            F(ref v, ref b.VY);
            v.Visit(ref b.NoGrab);
            B(ref v, ref b.RimTouched);
        }

        private static void Shot<TV>(ref TV v, ref ShotState s) where TV : struct, IStateVisitor
        {
            B(ref v, ref s.Active);
            v.Visit(ref s.Shooter);
            byte kind = (byte)s.Kind; v.Visit(ref kind); s.Kind = (ShotKind)kind;
            byte zone = (byte)s.Zone; v.Visit(ref zone); s.Zone = (ShotZone)zone;
            byte grade = (byte)s.Grade; v.Visit(ref grade); s.Grade = (ReleaseGrade)grade;
            byte result = (byte)s.Result; v.Visit(ref result); s.Result = (ShotResult)result;
            byte stage = (byte)s.Stage; v.Visit(ref stage); s.Stage = (ShotStage)stage;
            B(ref v, ref s.Made);
            v.Visit(ref s.Points);
            B(ref v, ref s.Posterizer);
            B(ref v, ref s.HeatShot);
            B(ref v, ref s.Buzzer);
            B(ref v, ref s.DrainOnMake);
            v.Visit(ref s.Frame);
            v.Visit(ref s.StageFrames);
            F(ref v, ref s.TargetX);
            F(ref v, ref s.TargetY);
            F(ref v, ref s.MakeChance);
            F(ref v, ref s.Contest);
        }

        private static void Fin<TV>(ref TV v, ref FinisherState f) where TV : struct, IStateVisitor
        {
            B(ref v, ref f.Active);
            v.Visit(ref f.Attacker);
            v.Visit(ref f.Frame);
            v.Visit(ref f.Frames);
            v.Visit(ref f.DenyStart);
            v.Visit(ref f.DenyFrames);
            B(ref v, ref f.HasRing);
            B(ref v, ref f.Attempted);
            F(ref v, ref f.StartX);
            F(ref v, ref f.EndX);
        }

        private static void F<TV>(ref TV v, ref Fix f) where TV : struct, IStateVisitor
        {
            long raw = f.Raw;
            v.Visit(ref raw);
            f = Fix.FromRaw(raw);
        }

        private static void B<TV>(ref TV v, ref bool b) where TV : struct, IStateVisitor
        {
            byte x = b ? (byte)1 : (byte)0;
            v.Visit(ref x);
            b = x != 0;
        }

        private static void E<TV>(ref TV v, ref Phase p) where TV : struct, IStateVisitor
        {
            byte x = (byte)p;
            v.Visit(ref x);
            p = (Phase)x;
        }

        // ------------------------------------------------------------------ public API

        public static ulong Hash(ref GameState s)
        {
            var h = new Hasher { H = 14695981039346656037UL };
            Walk(ref s, ref h);
            return h.H;
        }

        /// <summary>Serializes into <paramref name="buffer"/> (at least <see cref="MaxBytes"/>). Returns bytes written.</summary>
        public static int Write(ref GameState s, byte[] buffer)
        {
            var w = new Writer { Buf = buffer, Pos = 4 };
            Walk(ref s, ref w);
            uint magic = Magic;
            buffer[0] = (byte)magic;
            buffer[1] = (byte)(magic >> 8);
            buffer[2] = (byte)(magic >> 16);
            buffer[3] = (byte)(magic >> 24);
            return w.Pos;
        }

        public static GameState Read(byte[] buffer, int length)
        {
            if (length < 4) throw new ArgumentException("state too short");
            uint magic = (uint)(buffer[0] | (buffer[1] << 8) | (buffer[2] << 16) | (buffer[3] << 24));
            if (magic != Magic) throw new ArgumentException("not a DunkDuel state");
            var r = new Reader { Buf = buffer, Pos = 4, Len = length };
            var s = new GameState();
            Walk(ref s, ref r);
            if (r.Pos != length) throw new ArgumentException("state length mismatch");
            return s;
        }

        public static int SerializedSize()
        {
            var s = new GameState();
            var c = new Counter();
            Walk(ref s, ref c);
            return c.Bytes + 4;
        }

        private struct Hasher : IStateVisitor
        {
            public ulong H;

            private void Mix(ulong v, int bytes)
            {
                unchecked
                {
                    for (int i = 0; i < bytes; i++)
                    {
                        H ^= (byte)(v >> (8 * i));
                        H *= 1099511628211UL;
                    }
                }
            }

            public void Visit(ref ulong v) => Mix(v, 8);
            public void Visit(ref long v) => Mix((ulong)v, 8);
            public void Visit(ref int v) => Mix((uint)v, 4);
            public void Visit(ref uint v) => Mix(v, 4);
            public void Visit(ref ushort v) => Mix(v, 2);
            public void Visit(ref byte v) => Mix(v, 1);
        }

        private struct Writer : IStateVisitor
        {
            public byte[] Buf;
            public int Pos;

            private void Put(ulong v, int bytes)
            {
                for (int i = 0; i < bytes; i++) Buf[Pos++] = (byte)(v >> (8 * i));
            }

            public void Visit(ref ulong v) => Put(v, 8);
            public void Visit(ref long v) => Put((ulong)v, 8);
            public void Visit(ref int v) => Put((uint)v, 4);
            public void Visit(ref uint v) => Put(v, 4);
            public void Visit(ref ushort v) => Put(v, 2);
            public void Visit(ref byte v) => Put(v, 1);
        }

        private struct Reader : IStateVisitor
        {
            public byte[] Buf;
            public int Pos;
            public int Len;

            private ulong Get(int bytes)
            {
                if (Pos + bytes > Len) throw new ArgumentException("state truncated");
                ulong v = 0;
                for (int i = 0; i < bytes; i++) v |= (ulong)Buf[Pos++] << (8 * i);
                return v;
            }

            public void Visit(ref ulong v) => v = Get(8);
            public void Visit(ref long v) => v = (long)Get(8);
            public void Visit(ref int v) => v = (int)(uint)Get(4);
            public void Visit(ref uint v) => v = (uint)Get(4);
            public void Visit(ref ushort v) => v = (ushort)Get(2);
            public void Visit(ref byte v) => v = (byte)Get(1);
        }

        private struct Counter : IStateVisitor
        {
            public int Bytes;
            public void Visit(ref ulong v) => Bytes += 8;
            public void Visit(ref long v) => Bytes += 8;
            public void Visit(ref int v) => Bytes += 4;
            public void Visit(ref uint v) => Bytes += 4;
            public void Visit(ref ushort v) => Bytes += 2;
            public void Visit(ref byte v) => Bytes += 1;
        }
    }
}
