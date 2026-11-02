using System;
using DunkDuel.Net;

namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// The native half of the BLE transport (CoreBluetooth on iOS, android.bluetooth on Android).
    /// All methods are called from the Unity main thread. Native code reports everything (data, state,
    /// peers, link parameters) as records drained by <see cref="Poll"/>, in the order they happened.
    /// </summary>
    public interface IBleBackend : IDisposable
    {
        /// <summary>"ios", "android", "editor" or a test name.</summary>
        string Platform { get; }

        /// <summary>False in the Editor and on platforms without a native bridge.</summary>
        bool Supported { get; }

        /// <summary>Human-readable reason when <see cref="Supported"/> is false.</summary>
        string UnsupportedReason { get; }

        void Init(bool useL2cap);

        /// <summary>Initial answer; later changes arrive as RecCanHost records.</summary>
        bool CanHost();

        /// <param name="localNameUtf8">"DD-XXXX[:name]" (iOS advertises it; Android cannot set a local name).</param>
        /// <param name="serviceData">[code][platform][name] (Android puts it in the scan response; iOS ignores it).</param>
        void Host(byte[] localNameUtf8, byte[] serviceData);

        void StartScan();
        void StopScan();
        void Join(string address);

        /// <summary>Hands one packet (INPUT) or one CONTROL fragment to the radio queue. False if refused.</summary>
        bool Send(TransportChannel channel, byte[] data, int offset, int count);

        /// <summary>
        /// Copies pending records into <paramref name="buffer"/> (the backend may replace it with a larger one).
        /// Returns the number of bytes written; 0 when nothing is pending.
        /// </summary>
        int Poll(ref byte[] buffer);

        void Disconnect();
    }

    /// <summary>Editor and unsupported platforms: reports Failed once, never crashes, never sends.</summary>
    public sealed class UnsupportedBleBackend : IBleBackend
    {
        private bool _reported;

        public UnsupportedBleBackend(string platform, string reason)
        {
            Platform = platform;
            UnsupportedReason = reason;
        }

        public string Platform { get; }
        public bool Supported => false;
        public string UnsupportedReason { get; }

        public void Init(bool useL2cap) { }
        public bool CanHost() => false;
        public void Host(byte[] localNameUtf8, byte[] serviceData) => _reported = false;
        public void StartScan() => _reported = false;
        public void StopScan() { }
        public void Join(string address) => _reported = false;
        public bool Send(TransportChannel channel, byte[] data, int offset, int count) => false;
        public void Disconnect() { }
        public void Dispose() { }

        public int Poll(ref byte[] buffer)
        {
            if (_reported) return 0;
            _reported = true;
            byte[] rec = BleRecords.State(TransportState.Failed, TransportError.Internal);
            Buffer.BlockCopy(rec, 0, buffer, 0, rec.Length);
            return rec.Length;
        }
    }
}
