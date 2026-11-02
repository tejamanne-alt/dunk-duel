using System;
using System.Globalization;
using System.IO;
using System.Text;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Debug telemetry for a BLE session: state changes, RTT, jitter, loss, packet counts, and (when the
    /// netcode feeds it) the rollback histogram and desync count. Never records names or addresses; peers
    /// appear only as short hashes. Engine-free; <see cref="BleSessionLogFiles"/> picks the device path.
    /// </summary>
    public sealed class BleSessionLog
    {
        private readonly StringBuilder _sb = new StringBuilder(8192);
        private readonly Func<long> _clock;
        private readonly long _start;
        private long _lastSample = long.MinValue;
        private bool _truncated;

        public int MaxChars = 512 * 1024;
        public int SampleIntervalMicros = 1000000;

        public BleSessionLog(Func<long> clockMicros)
        {
            _clock = clockMicros ?? throw new ArgumentNullException(nameof(clockMicros));
            _start = _clock();
            Line("ble session log v1");
        }

        public string Text => _sb.ToString();

        /// <summary>Free-form note. Callers must not pass personal data.</summary>
        public void Line(string text)
        {
            if (_sb.Length > MaxChars)
            {
                if (!_truncated) _sb.Append("... log truncated\n");
                _truncated = true;
                return;
            }
            long ms = (_clock() - _start) / 1000;
            _sb.Append(ms.ToString(CultureInfo.InvariantCulture).PadLeft(8)).Append("ms ").Append(text).Append('\n');
        }

        public void State(TransportState state, TransportError error) =>
            Line("state " + state + (error != TransportError.None ? " error=" + error : string.Empty));

        public void Peer(in PeerInfo peer) =>
            Line("peer " + BleAdvert.ShortHash(peer.Address) + " court=" + peer.CourtCode + " platform=" + peer.Platform + " rssi=" + peer.SignalDbm);

        /// <summary>Throttled link sample (once per <see cref="SampleIntervalMicros"/>).</summary>
        public void Sample(in TransportStats s)
        {
            long now = _clock();
            if (_lastSample != long.MinValue && now - _lastSample < SampleIntervalMicros) return;
            _lastSample = now;
            Line("link rtt=" + Ms(s.RttMicros) + " jitter=" + Ms(s.JitterMicros) + " loss=" + (s.LossPermille / 10.0).ToString("0.0", CultureInfo.InvariantCulture)
                + "% sent=" + s.PacketsSent + " recv=" + s.PacketsReceived + " payload=" + s.MaxPayload
                + (s.ConnectionIntervalMicros > 0 ? " interval=" + Ms(s.ConnectionIntervalMicros) : string.Empty));
        }

        /// <summary>Netcode summary: rollback histogram, desyncs, input delay.</summary>
        public void Net(in NetStats n)
        {
            var sb = new StringBuilder("net delay=");
            sb.Append(n.InputDelay).Append(" rollback last=").Append(n.LastRollback).Append(" max=").Append(n.MaxRollback)
                .Append(" p95=").Append(n.RollbackP95).Append(" desyncs=").Append(n.Desyncs).Append(" recovered=").Append(n.DesyncRecoveries)
                .Append(" stalls=").Append(n.StallTicks);
            if (n.RollbackHistogram != null)
            {
                sb.Append(" hist=[");
                for (int i = 0; i < n.RollbackHistogram.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(n.RollbackHistogram[i]);
                }
                sb.Append(']');
            }
            Line(sb.ToString());
        }

        public void WriteTo(string path)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, _sb.ToString());
        }

        private static string Ms(int micros) => (micros / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + "ms";
    }

    /// <summary>Where session logs go on device.</summary>
    public static class BleSessionLogFiles
    {
        public static string DefaultPath()
        {
            string name = "ble-session-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log";
#if UNITY_5_3_OR_NEWER
            return Path.Combine(UnityEngine.Application.persistentDataPath, "logs", name);
#else
            return Path.Combine(Path.GetTempPath(), name);
#endif
        }
    }
}
