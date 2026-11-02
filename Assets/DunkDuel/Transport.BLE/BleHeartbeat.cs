namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Transport-level heartbeat on CONTROL: ping [0xFE][u32 seq][u64 send time], pong echoes it with 0xFF.
    /// The sender timestamps with its own clock, so no clock sync is needed. RTT and jitter are smoothed the
    /// RFC 3550 way (RTT gain 1/8, jitter gain 1/16). Note that CONTROL rides an acknowledged characteristic,
    /// so this RTT slightly overstates the INPUT path. Engine-free.
    /// </summary>
    public sealed class BleHeartbeat
    {
        public const int PingBytes = 13;

        private long _lastPing = long.MinValue;
        private uint _seq;
        private bool _hasSample;
        private int _lastSample;

        public int IntervalMicros = 500000;
        public int RttMicros { get; private set; }
        public int JitterMicros { get; private set; }
        public int Samples { get; private set; }
        public long LastPongMicros { get; private set; }

        public void Reset()
        {
            _lastPing = long.MinValue;
            _hasSample = false;
            RttMicros = 0;
            JitterMicros = 0;
            Samples = 0;
            LastPongMicros = 0;
        }

        public bool Due(long nowMicros) => _lastPing == long.MinValue || nowMicros - _lastPing >= IntervalMicros;

        /// <summary>Writes a ping into <paramref name="buf"/> (at least <see cref="PingBytes"/>) and returns its length.</summary>
        public int WritePing(byte[] buf, long nowMicros)
        {
            _lastPing = nowMicros;
            _seq++;
            buf[0] = BleProtocol.HeartbeatPing;
            PutU32(buf, 1, _seq);
            PutU64(buf, 5, (ulong)nowMicros);
            return PingBytes;
        }

        /// <summary>Turns a received ping into the pong to send back (same buffer layout). Returns its length or 0 if malformed.</summary>
        public static int WritePong(byte[] ping, int length, byte[] dst)
        {
            if (length < PingBytes) return 0;
            System.Buffer.BlockCopy(ping, 0, dst, 0, PingBytes);
            dst[0] = BleProtocol.HeartbeatPong;
            return PingBytes;
        }

        /// <summary>Consumes a pong. Returns false when malformed or from the future.</summary>
        public bool OnPong(byte[] pong, int length, long nowMicros)
        {
            if (length < PingBytes || pong[0] != BleProtocol.HeartbeatPong) return false;
            long sent = (long)GetU64(pong, 5);
            long rtt = nowMicros - sent;
            if (rtt < 0 || rtt > 10000000) return false;
            int sample = (int)rtt;
            if (!_hasSample)
            {
                RttMicros = sample;
                JitterMicros = 0;
                _hasSample = true;
            }
            else
            {
                RttMicros += (sample - RttMicros) / 8;
                int d = sample - _lastSample;
                if (d < 0) d = -d;
                JitterMicros += (d - JitterMicros) / 16;
            }
            _lastSample = sample;
            Samples++;
            LastPongMicros = nowMicros;
            return true;
        }

        private static void PutU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static void PutU64(byte[] b, int o, ulong v)
        {
            for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (8 * i));
        }

        private static ulong GetU64(byte[] b, int o)
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)b[o + i] << (8 * i);
            return v;
        }
    }
}
