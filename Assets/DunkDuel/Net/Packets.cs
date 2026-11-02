using System;
using System.Text;
using DunkDuel.Sim;

namespace DunkDuel.Net
{
    /// <summary>
    /// The 20-byte INPUT packet: u16 latest frame, u16 acknowledged frame, 8 x u16 inputs (frames latest-7..latest).
    /// It fits the default BLE ATT payload, and the 8-frame overlap absorbs dropped packets. Little-endian.
    /// </summary>
    public static class InputPacket
    {
        public const int Size = 20;
        public const int Frames = 8;

        /// <summary>Writes a packet for full frame numbers; <paramref name="ring"/> is indexed by frame &amp; mask.</summary>
        public static void Write(byte[] buf, int latestFrame, int ackFrame, FrameInput[] ring, int ringMask)
        {
            Put16(buf, 0, latestFrame & 0xFFFF);
            Put16(buf, 2, ackFrame < 0 ? 0xFFFF : ackFrame & 0xFFFF);
            for (int i = 0; i < Frames; i++)
            {
                int f = latestFrame - (Frames - 1) + i;
                ushort bits = f >= 0 ? ring[f & ringMask].Bits : (ushort)0;
                Put16(buf, 4 + 2 * i, bits);
            }
        }

        public static ushort Latest(byte[] buf) => (ushort)(buf[0] | (buf[1] << 8));
        public static ushort Ack(byte[] buf) => (ushort)(buf[2] | (buf[3] << 8));
        public static FrameInput Input(byte[] buf, int index) => new FrameInput((ushort)(buf[4 + 2 * index] | (buf[5 + 2 * index] << 8)));

        /// <summary>Full frame number for a 16-bit wire frame: the value congruent mod 65536 closest to <paramref name="reference"/>.</summary>
        public static int Unwrap(ushort wire, int reference)
        {
            int baseFrame = reference & ~0xFFFF;
            int candidate = baseFrame | wire;
            if (candidate - reference > 0x8000) candidate -= 0x10000;
            else if (reference - candidate > 0x8000) candidate += 0x10000;
            return candidate;
        }

        private static void Put16(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
        }
    }

    /// <summary>CONTROL message types (first byte). 0xFE/0xFF are reserved for transport heartbeats.</summary>
    public enum ControlType : byte
    {
        Hello = 0x01,
        Config = 0x02,
        Ping = 0x03,
        Pong = 0x04,
        Start = 0x05,
        Hash = 0x06,
        StateChunk = 0x07,
        Rematch = 0x08,
        Quit = 0x09,
        Reject = 0x0A,
        Ready = 0x0B,
        Resync = 0x0C,
    }

    /// <summary>Little-endian writer/reader for CONTROL messages.</summary>
    public struct MsgWriter
    {
        public byte[] Buf;
        public int Pos;

        public MsgWriter(byte[] buf, ControlType type)
        {
            Buf = buf;
            Pos = 0;
            U8((byte)type);
        }

        public void U8(int v) => Buf[Pos++] = (byte)v;
        public void U16(int v)
        {
            Buf[Pos++] = (byte)v;
            Buf[Pos++] = (byte)(v >> 8);
        }
        public void U32(uint v)
        {
            for (int i = 0; i < 4; i++) Buf[Pos++] = (byte)(v >> (8 * i));
        }
        public void U64(ulong v)
        {
            for (int i = 0; i < 8; i++) Buf[Pos++] = (byte)(v >> (8 * i));
        }
        public void Str(string s, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(bytes.Length, maxBytes);
            U8(n);
            Array.Copy(bytes, 0, Buf, Pos, n);
            Pos += n;
        }
        public void Bytes(byte[] src, int offset, int count)
        {
            U16(count);
            Array.Copy(src, offset, Buf, Pos, count);
            Pos += count;
        }
    }

    public struct MsgReader
    {
        private readonly byte[] _buf;
        private readonly int _len;
        public int Pos;

        public MsgReader(byte[] buf, int len)
        {
            _buf = buf;
            _len = len;
            Pos = 1; // skip type
        }

        public ControlType Type => (ControlType)_buf[0];
        public bool Ok => Pos <= _len;

        private void Need(int n)
        {
            if (Pos + n > _len) throw new FormatException("control message truncated");
        }

        public int U8()
        {
            Need(1);
            return _buf[Pos++];
        }

        public int U16()
        {
            Need(2);
            int v = _buf[Pos] | (_buf[Pos + 1] << 8);
            Pos += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)_buf[Pos++] << (8 * i);
            return v;
        }

        public ulong U64()
        {
            Need(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)_buf[Pos++] << (8 * i);
            return v;
        }

        public string Str()
        {
            int n = U8();
            Need(n);
            string s = Encoding.UTF8.GetString(_buf, Pos, n);
            Pos += n;
            return s;
        }

        public int Bytes(byte[] dst, int offset)
        {
            int n = U16();
            Need(n);
            Array.Copy(_buf, Pos, dst, offset, n);
            Pos += n;
            return n;
        }
    }
}
