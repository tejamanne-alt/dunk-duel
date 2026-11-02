namespace DunkDuel.Transport.BLE
{
    /// <summary>
    /// Wire constants shared with the native bridges. Any change here must be mirrored in
    /// Assets/Plugins/iOS/DunkDuelNative/DunkDuelBLE.swift and Assets/Plugins/Android/DunkDuelBle.kt.
    /// </summary>
    /// <remarks>
    /// GATT layout (one custom primary service):
    ///   INPUT   write-without-response + notify   20-byte input packets (guest writes, host notifies)
    ///   CONTROL write-with-response + indicate     framed CONTROL fragments (see <see cref="BleFraming"/>)
    ///   PSM     read                                u16 LE L2CAP PSM published by the host (0 = none)
    /// Advertising: the host advertises the service UUID. iOS hosts also set the local name
    /// "DD-XXXX[:name]"; Android hosts (which cannot set a local name per advertisement) put
    /// [code x4 ASCII][platform byte][name UTF-8 &lt;= 8 bytes] in the scan response as service data.
    ///
    /// Native to managed records, batched by Poll: [u8 kind][u16 length LE][payload].
    ///   RecInput    payload = one INPUT packet
    ///   RecControl  payload = one CONTROL fragment (reassembled in C#)
    ///   RecState    payload = [u8 TransportState][u8 TransportError] (numeric values of DunkDuel.Net enums)
    ///   RecPeer     payload = [i8 rssi][u8 n][address UTF-8][u8 n][local name UTF-8][u8 n][service data]
    ///   RecLink     payload = [u16 max payload LE][u32 connection interval micros LE]
    ///   RecCanHost  payload = [u8 0|1]
    ///   RecDropped  payload = [u32 count LE] INPUT packets the native queue dropped under back-pressure
    /// </remarks>
    public static class BleProtocol
    {
        public const string ServiceUuid = "DD0E0001-8E2A-4C1B-9D3F-6A5B4C3D2E1F";
        public const string InputUuid = "DD0E0002-8E2A-4C1B-9D3F-6A5B4C3D2E1F";
        public const string ControlUuid = "DD0E0003-8E2A-4C1B-9D3F-6A5B4C3D2E1F";
        public const string PsmUuid = "DD0E0004-8E2A-4C1B-9D3F-6A5B4C3D2E1F";
        public const string CccdUuid = "00002902-0000-1000-8000-00805F9B34FB";

        public const string LocalNamePrefix = "DD-";
        public const char LocalNameSeparator = ':';
        public const int CourtCodeLength = 4;

        /// <summary>Netcode input packet: u16 latest frame, u16 acked frame, 8 x u16 inputs.</summary>
        public const int InputPacketBytes = 20;

        /// <summary>Default ATT payload (MTU 23 - 3) before any MTU exchange.</summary>
        public const int DefaultPayload = 20;

        /// <summary>Largest fragment the framing will produce (BLE attribute values max out at 512).</summary>
        public const int MaxPayload = 512;

        /// <summary>Largest CONTROL message accepted from the session layer.</summary>
        public const int MaxControlMessage = 1024;

        /// <summary>31-byte scan response minus the 128-bit service-data header (1 length + 1 type + 16 UUID).</summary>
        public const int MaxServiceDataBytes = 13;
        public const int MaxServiceDataNameBytes = MaxServiceDataBytes - CourtCodeLength - 1;

        /// <summary>Kept short so iOS can fit the local name into the scan response.</summary>
        public const int MaxLocalNameBytes = 20;

        /// <summary>CONTROL message type bytes reserved for the transport heartbeat (never seen by the session).</summary>
        public const byte HeartbeatPing = 0xFE;
        public const byte HeartbeatPong = 0xFF;

        public const byte RecInput = 0;
        public const byte RecControl = 1;
        public const byte RecState = 2;
        public const byte RecPeer = 3;
        public const byte RecLink = 4;
        public const byte RecCanHost = 5;
        public const byte RecDropped = 6;
        public const int RecordHeaderBytes = 3;

        public const byte PlatformAndroid = (byte)'A';
        public const byte PlatformIos = (byte)'I';

        public const string PlatformNameAndroid = "android";
        public const string PlatformNameIos = "ios";
        public const string PlatformNameUnknown = "unknown";
    }
}
