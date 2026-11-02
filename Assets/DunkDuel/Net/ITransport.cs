using System;
using System.Collections.Generic;

namespace DunkDuel.Net
{
    /// <summary>
    /// Logical channels. INPUT carries the 20-byte input packets (unreliable, unordered: BLE write without
    /// response + notify, UDP). CONTROL carries handshake, hashes, state chunks, pause and rematch
    /// (reliable, ordered: BLE write with response + indicate, UDP with acks).
    /// </summary>
    public enum TransportChannel : byte
    {
        Input = 0,
        Control = 1,
    }

    public enum TransportState : byte
    {
        Idle = 0,
        Hosting = 1,      // advertising a court, waiting for a guest
        Scanning = 2,     // looking for nearby courts
        Connecting = 3,
        Connected = 4,
        Disconnected = 5, // link lost; a session may try to reconnect
        Failed = 6,       // unrecoverable (radio off, permission denied, unsupported)
    }

    /// <summary>Why a transport is in <see cref="TransportState.Failed"/>.</summary>
    public enum TransportError : byte
    {
        None = 0,
        RadioOff = 1,
        PermissionDenied = 2,
        AdvertisingUnsupported = 3, // some Android phones can only join
        ConnectFailed = 4,
        ProtocolMismatch = 5,
        Internal = 6,
    }

    /// <summary>Link quality as measured by the transport (heartbeats on CONTROL, sequence gaps where available).</summary>
    public struct TransportStats
    {
        public int RttMicros;
        public int JitterMicros;
        /// <summary>Packet loss on the INPUT channel, per mille (0..1000), when the transport can measure it.</summary>
        public int LossPermille;
        public long PacketsSent;
        public long PacketsReceived;
        public long BytesSent;
        public long BytesReceived;
        /// <summary>Negotiated payload size per packet (BLE ATT MTU - 3, or UDP datagram budget).</summary>
        public int MaxPayload;
        /// <summary>BLE connection interval in microseconds when known (0 otherwise).</summary>
        public int ConnectionIntervalMicros;
    }

    /// <summary>A nearby court found while scanning.</summary>
    public struct PeerInfo
    {
        /// <summary>Opaque transport address (BLE identifier / MAC, or IP:port).</summary>
        public string Address;
        public string CourtCode;
        public string PlayerName;
        public int SignalDbm;
        public string Platform; // "ios", "android", "lan"
    }

    /// <summary>
    /// A local link to exactly one other phone. No servers: BLE (iPhone and Android) or LAN UDP behind the same
    /// contract. Implementations never raise events from background threads; callers pump <see cref="Poll"/>
    /// once per frame on the main thread and events fire from inside it.
    /// </summary>
    public interface ITransport : IDisposable
    {
        string Name { get; }
        TransportState State { get; }
        TransportError Error { get; }
        TransportStats Stats { get; }
        bool CanHost { get; }

        /// <summary>Advertise a court with a 4-character code; the first guest that connects is accepted.</summary>
        void Host(string courtCode, string playerName);

        /// <summary>Start discovering nearby courts. Results appear in <see cref="Peers"/>.</summary>
        void StartScan();
        void StopScan();
        IReadOnlyList<PeerInfo> Peers { get; }

        /// <summary>Connect to a discovered court.</summary>
        void Join(PeerInfo peer);

        /// <summary>Queue bytes for the peer. INPUT may drop under load; CONTROL is delivered in order or the link fails.</summary>
        void Send(TransportChannel channel, byte[] data, int offset, int count);

        /// <summary>(channel, buffer, length). The buffer is only valid during the callback.</summary>
        event Action<TransportChannel, byte[], int> OnReceive;
        event Action<TransportState> OnStateChanged;

        /// <summary>Delivers queued receives and state changes on the calling thread.</summary>
        void Poll();

        void Disconnect();
    }
}
