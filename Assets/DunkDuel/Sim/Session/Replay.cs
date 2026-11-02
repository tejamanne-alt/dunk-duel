using System;
using System.Collections.Generic;
using DunkDuel.Sim.Data;

namespace DunkDuel.Sim.Session
{
    /// <summary>
    /// A match saved as inputs: a small header plus two 16-bit inputs per tick (~4 bytes per tick,
    /// a few kilobytes per minute after compression). Replays only play back on the same sim version.
    /// </summary>
    public sealed class Replay
    {
        public const uint Magic = 0x44445231; // "DDR1"
        public const int FormatVersion = 1;

        public ulong SimVersion;
        public MatchSetup Setup;
        public readonly List<uint> Inputs = new List<uint>(); // p0 in low 16 bits, p1 in high 16 bits
        public ulong FinalHash;

        public int Ticks => Inputs.Count;

        public FrameInput P0(int tick) => new FrameInput((ushort)(Inputs[tick] & 0xFFFF));
        public FrameInput P1(int tick) => new FrameInput((ushort)(Inputs[tick] >> 16));

        public byte[] ToBytes()
        {
            var w = new List<byte>(64 + Inputs.Count * 4);
            Put(w, Magic, 4);
            Put(w, FormatVersion, 2);
            Put(w, SimVersion, 8);
            Put(w, (ulong)Setup.Fighter0, 1);
            Put(w, (ulong)Setup.Fighter1, 1);
            Put(w, Setup.Seed, 8);
            Put(w, (ulong)Setup.RoundsToWin, 1);
            Put(w, (ulong)Setup.Mode, 1);
            Put(w, Setup.Simple0 ? 1UL : 0UL, 1);
            Put(w, Setup.Simple1 ? 1UL : 0UL, 1);
            Put(w, (ulong)Setup.FirstOffense, 1);
            Put(w, Setup.InfiniteHype ? 1UL : 0UL, 1);
            Put(w, (ulong)Setup.CourtIndex, 1);
            Put(w, FinalHash, 8);
            Put(w, (ulong)Inputs.Count, 4);
            for (int i = 0; i < Inputs.Count; i++) Put(w, Inputs[i], 4);
            return w.ToArray();
        }

        public static Replay FromBytes(byte[] data)
        {
            int pos = 0;
            ulong Get(int n)
            {
                if (pos + n > data.Length) throw new ArgumentException("replay truncated");
                ulong v = 0;
                for (int i = 0; i < n; i++) v |= (ulong)data[pos++] << (8 * i);
                return v;
            }
            if (Get(4) != Magic) throw new ArgumentException("not a Dunk Duel replay");
            int version = (int)Get(2);
            if (version != FormatVersion) throw new ArgumentException("unsupported replay format " + version);
            var r = new Replay { SimVersion = Get(8) };
            r.Setup.Fighter0 = (int)Get(1);
            r.Setup.Fighter1 = (int)Get(1);
            r.Setup.Seed = Get(8);
            r.Setup.RoundsToWin = (int)Get(1);
            r.Setup.Mode = (MatchMode)Get(1);
            r.Setup.Simple0 = Get(1) != 0;
            r.Setup.Simple1 = Get(1) != 0;
            r.Setup.FirstOffense = (int)Get(1);
            r.Setup.InfiniteHype = Get(1) != 0;
            r.Setup.CourtIndex = (int)Get(1);
            r.FinalHash = Get(8);
            int count = (int)Get(4);
            for (int i = 0; i < count; i++) r.Inputs.Add((uint)Get(4));
            return r;
        }

        private static void Put(List<byte> w, ulong v, int n)
        {
            for (int i = 0; i < n; i++) w.Add((byte)(v >> (8 * i)));
        }

        private static void Put(List<byte> w, int v, int n) => Put(w, (ulong)(uint)v, n);
    }

    public sealed class ReplayRecorder
    {
        public readonly Replay Replay;

        public ReplayRecorder(ulong simVersion, in MatchSetup setup)
        {
            Replay = new Replay { SimVersion = simVersion, Setup = setup };
        }

        public void Record(FrameInput p0, FrameInput p1) => Replay.Inputs.Add(p0.Bits | ((uint)p1.Bits << 16));
    }

    /// <summary>Plays a replay through the sim and reports hashes (determinism gate, cross-device checks).</summary>
    public static class ReplayRunner
    {
        public struct Result
        {
            public GameState Final;
            public ulong FinalHash;
            /// <summary>FNV-1a chain over the state hash every <c>chainInterval</c> ticks.</summary>
            public ulong ChainHash;
            public int Ticks;
        }

        public static Result Run(SimTables tables, Replay replay, int chainInterval = 30)
        {
            var sim = new Simulation(tables);
            GameState s = sim.CreateInitialState(replay.Setup);
            ulong chain = 14695981039346656037UL;
            for (int t = 0; t < replay.Ticks; t++)
            {
                s = sim.Step(s, replay.P0(t), replay.P1(t)).State;
                if (chainInterval > 0 && (t + 1) % chainInterval == 0)
                {
                    ulong h = s.Hash();
                    unchecked
                    {
                        for (int b = 0; b < 8; b++)
                        {
                            chain ^= (byte)(h >> (8 * b));
                            chain *= 1099511628211UL;
                        }
                    }
                }
            }
            return new Result { Final = s, FinalHash = s.Hash(), ChainHash = chain, Ticks = replay.Ticks };
        }
    }
}
