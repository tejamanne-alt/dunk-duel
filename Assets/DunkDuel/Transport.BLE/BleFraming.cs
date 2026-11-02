using System;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// CONTROL framing: splits a message (1..1024 bytes) into fragments that each fit one BLE packet, and
    /// reassembles them on the other side. CONTROL travels on a reliable, ordered characteristic
    /// (write with response / indicate), so fragments arrive in order; the sequence number catches stack bugs.
    /// </summary>
    /// <remarks>
    /// Fragment header, byte 0: bit 7 START, bit 6 END, bits 0-5 sequence (mod 64, continuous across messages).
    /// START fragments carry the total message length as u16 LE in bytes 1-2. A single-fragment message is
    /// START|END. Engine-free so it can be unit tested headless.
    /// </remarks>
    public static class BleFraming
    {
        public const byte FlagStart = 0x80;
        public const byte FlagEnd = 0x40;
        public const byte SeqMask = 0x3F;
        public const int StartHeader = 3;
        public const int ContinueHeader = 1;
        public const int MinPayload = StartHeader + 1;

        /// <summary>Number of fragments for a <paramref name="length"/>-byte message with <paramref name="payload"/>-byte packets.</summary>
        public static int FragmentCount(int length, int payload)
        {
            Validate(length, payload);
            int first = payload - StartHeader;
            if (length <= first) return 1;
            int per = payload - ContinueHeader;
            return 1 + (length - first + per - 1) / per;
        }

        /// <summary>
        /// Writes fragment <paramref name="index"/> of the message into <paramref name="dst"/> (at least
        /// <paramref name="payload"/> bytes) and returns the fragment length.
        /// </summary>
        public static int WriteFragment(byte[] msg, int offset, int length, int payload, int index, byte seq, byte[] dst)
        {
            Validate(length, payload);
            if (msg == null) throw new ArgumentNullException(nameof(msg));
            if (dst == null || dst.Length < payload) throw new ArgumentException("fragment buffer too small", nameof(dst));
            if (offset < 0 || offset + length > msg.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            int first = payload - StartHeader;
            int per = payload - ContinueHeader;
            int start, count;
            byte flags;
            if (index == 0)
            {
                start = 0;
                count = length < first ? length : first;
                flags = FlagStart;
            }
            else
            {
                start = first + (index - 1) * per;
                if (start >= length) throw new ArgumentOutOfRangeException(nameof(index));
                int left = length - start;
                count = left < per ? left : per;
                flags = 0;
            }
            if (start + count >= length) flags |= FlagEnd;
            dst[0] = (byte)(flags | (seq & SeqMask));
            int p = 1;
            if (index == 0)
            {
                dst[1] = (byte)length;
                dst[2] = (byte)(length >> 8);
                p = StartHeader;
            }
            Buffer.BlockCopy(msg, offset + start, dst, p, count);
            return p + count;
        }

        public static byte NextSeq(byte seq) => (byte)((seq + 1) & SeqMask);

        private static void Validate(int length, int payload)
        {
            if (length < 1 || length > BleProtocol.MaxControlMessage)
                throw new ArgumentOutOfRangeException(nameof(length), "CONTROL messages are 1.." + BleProtocol.MaxControlMessage + " bytes");
            if (payload < MinPayload || payload > BleProtocol.MaxPayload)
                throw new ArgumentOutOfRangeException(nameof(payload));
        }
    }

    /// <summary>Reassembles <see cref="BleFraming"/> fragments into whole CONTROL messages.</summary>
    public sealed class BleReassembler
    {
        private readonly byte[] _buf = new byte[BleProtocol.MaxControlMessage];
        private int _have;
        private int _total;
        private bool _active;
        private int _expectSeq = -1;

        /// <summary>Fragments rejected (sequence gaps, bad lengths, orphan continuations, abandoned messages).</summary>
        public int Errors { get; private set; }

        /// <summary>Holds the last completed message; valid until the next <see cref="Accept"/>.</summary>
        public byte[] Message => _buf;

        /// <summary>
        /// Feeds one fragment. Returns the message length when it completes a message (bytes in
        /// <see cref="Message"/>), 0 when more fragments are needed, -1 when the fragment was rejected.
        /// </summary>
        public int Accept(byte[] data, int offset, int count)
        {
            if (data == null || count < 1 || offset < 0 || offset + count > data.Length)
            {
                Fail();
                return -1;
            }
            byte header = data[offset];
            int seq = header & BleFraming.SeqMask;
            bool start = (header & BleFraming.FlagStart) != 0;
            bool end = (header & BleFraming.FlagEnd) != 0;
            int p = offset + 1;
            int n = count - 1;

            if (start)
            {
                if (_active) Errors++; // previous message abandoned mid-way
                if (n < 2)
                {
                    Fail();
                    return -1;
                }
                int total = data[p] | (data[p + 1] << 8);
                p += 2;
                n -= 2;
                if (total < 1 || total > _buf.Length)
                {
                    Fail();
                    return -1;
                }
                _active = true;
                _total = total;
                _have = 0;
            }
            else if (!_active || seq != _expectSeq)
            {
                Fail();
                return -1;
            }

            if (_have + n > _total)
            {
                Fail();
                return -1;
            }
            Buffer.BlockCopy(data, p, _buf, _have, n);
            _have += n;
            _expectSeq = (seq + 1) & BleFraming.SeqMask;

            if (!end) return 0;
            if (_have != _total)
            {
                Fail();
                return -1;
            }
            _active = false;
            return _total;
        }

        public void Reset()
        {
            _active = false;
            _have = 0;
            _total = 0;
            _expectSeq = -1;
        }

        private void Fail()
        {
            Errors++;
            _active = false;
            _have = 0;
            _total = 0;
        }
    }
}
