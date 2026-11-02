using System;
using System.Text;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE
{
    /// <summary>One record inside a native poll batch.</summary>
    public struct BleRecord
    {
        public byte Kind;
        public int Offset;  // payload start in the batch buffer
        public int Length;  // payload length
    }

    /// <summary>
    /// Reader/writer for the batched record format the native bridges return from poll:
    /// [u8 kind][u16 length LE][payload] repeated. Writers exist for tests and fake backends.
    /// </summary>
    public static class BleRecords
    {
        /// <summary>Advances <paramref name="cursor"/> over the next record. Returns false at the end or on a truncated record.</summary>
        public static bool Next(byte[] buffer, int length, ref int cursor, out BleRecord rec)
        {
            rec = default;
            if (cursor + BleProtocol.RecordHeaderBytes > length) return false;
            int len = buffer[cursor + 1] | (buffer[cursor + 2] << 8);
            if (cursor + BleProtocol.RecordHeaderBytes + len > length) return false;
            rec.Kind = buffer[cursor];
            rec.Offset = cursor + BleProtocol.RecordHeaderBytes;
            rec.Length = len;
            cursor = rec.Offset + len;
            return true;
        }

        public static bool TryParseState(byte[] b, in BleRecord r, out TransportState state, out TransportError error)
        {
            state = TransportState.Idle;
            error = TransportError.None;
            if (r.Length < 2) return false;
            byte s = b[r.Offset], e = b[r.Offset + 1];
            if (s > (byte)TransportState.Failed || e > (byte)TransportError.Internal) return false;
            state = (TransportState)s;
            error = (TransportError)e;
            return true;
        }

        public static bool TryParseLink(byte[] b, in BleRecord r, out int maxPayload, out int intervalMicros)
        {
            maxPayload = 0;
            intervalMicros = 0;
            if (r.Length < 6) return false;
            int o = r.Offset;
            maxPayload = b[o] | (b[o + 1] << 8);
            intervalMicros = b[o + 2] | (b[o + 3] << 8) | (b[o + 4] << 16) | (b[o + 5] << 24);
            return true;
        }

        public static bool TryParseCount(byte[] b, in BleRecord r, out int count)
        {
            count = 0;
            if (r.Length < 4) return false;
            int o = r.Offset;
            count = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
            return count >= 0;
        }

        /// <summary>[i8 rssi][u8 n][address][u8 n][local name][u8 n][service data]</summary>
        public static bool TryParsePeer(byte[] b, in BleRecord r, out int rssi, out string address, out string localName,
            out int serviceDataOffset, out int serviceDataLength)
        {
            rssi = 0;
            address = null;
            localName = null;
            serviceDataOffset = 0;
            serviceDataLength = 0;
            int p = r.Offset;
            int end = r.Offset + r.Length;
            if (p + 2 > end) return false;
            rssi = (sbyte)b[p++];
            int n = b[p++];
            if (p + n > end || n == 0) return false;
            address = Decode(b, p, n);
            p += n;
            if (p + 1 > end) return false;
            n = b[p++];
            if (p + n > end) return false;
            localName = n > 0 ? Decode(b, p, n) : string.Empty;
            p += n;
            if (p + 1 > end) return false;
            n = b[p++];
            if (p + n > end) return false;
            serviceDataOffset = p;
            serviceDataLength = n;
            return true;
        }

        // ------------------------------------------------------------------ writers (tests, fake backends)

        public static byte[] Make(byte kind, byte[] payload)
        {
            payload = payload ?? Array.Empty<byte>();
            var r = new byte[BleProtocol.RecordHeaderBytes + payload.Length];
            r[0] = kind;
            r[1] = (byte)payload.Length;
            r[2] = (byte)(payload.Length >> 8);
            Buffer.BlockCopy(payload, 0, r, BleProtocol.RecordHeaderBytes, payload.Length);
            return r;
        }

        public static byte[] Data(TransportChannel channel, byte[] data, int offset, int count)
        {
            var payload = new byte[count];
            Buffer.BlockCopy(data, offset, payload, 0, count);
            return Make(channel == TransportChannel.Input ? BleProtocol.RecInput : BleProtocol.RecControl, payload);
        }

        public static byte[] State(TransportState state, TransportError error) =>
            Make(BleProtocol.RecState, new[] { (byte)state, (byte)error });

        public static byte[] Link(int maxPayload, int intervalMicros) => Make(BleProtocol.RecLink, new[]
        {
            (byte)maxPayload, (byte)(maxPayload >> 8),
            (byte)intervalMicros, (byte)(intervalMicros >> 8), (byte)(intervalMicros >> 16), (byte)(intervalMicros >> 24),
        });

        public static byte[] CanHost(bool canHost) => Make(BleProtocol.RecCanHost, new[] { canHost ? (byte)1 : (byte)0 });

        public static byte[] Peer(int rssi, string address, string localName, byte[] serviceData)
        {
            byte[] a = Encoding.UTF8.GetBytes(address ?? string.Empty);
            byte[] n = Encoding.UTF8.GetBytes(localName ?? string.Empty);
            byte[] s = serviceData ?? Array.Empty<byte>();
            if (a.Length > 255 || n.Length > 255 || s.Length > 255) throw new ArgumentException("peer field too long");
            var payload = new byte[1 + 1 + a.Length + 1 + n.Length + 1 + s.Length];
            int p = 0;
            payload[p++] = (byte)(sbyte)Math.Max(-128, Math.Min(127, rssi));
            payload[p++] = (byte)a.Length;
            Buffer.BlockCopy(a, 0, payload, p, a.Length);
            p += a.Length;
            payload[p++] = (byte)n.Length;
            Buffer.BlockCopy(n, 0, payload, p, n.Length);
            p += n.Length;
            payload[p++] = (byte)s.Length;
            Buffer.BlockCopy(s, 0, payload, p, s.Length);
            return Make(BleProtocol.RecPeer, payload);
        }

        private static string Decode(byte[] b, int offset, int count)
        {
            try
            {
                return new UTF8Encoding(false, false).GetString(b, offset, count);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }
    }
}
