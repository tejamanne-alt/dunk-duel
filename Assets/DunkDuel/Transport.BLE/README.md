# DunkDuel.Transport.BLE

Bluetooth Low Energy implementation of `DunkDuel.Net.ITransport` for iPhone–Android cross-play.

| Piece | File |
| --- | --- |
| Transport (state machine, framing, heartbeat, peers, stats) | `BleTransport.cs` |
| Wire constants (UUIDs, record kinds, limits) | `BleProtocol.cs` |
| CONTROL fragmentation / reassembly | `BleFraming.cs` |
| Advertisement build/parse (court code, name, platform) | `BleAdvert.cs` |
| Native record batch reader/writer | `BleRecords.cs` |
| Lock-free SPSC ring for outgoing INPUT | `SpscRing.cs` |
| RTT/jitter heartbeat (CONTROL 0xFE/0xFF, never reaches the session) | `BleHeartbeat.cs` |
| Android 12+ / legacy runtime permissions | `BlePermissions.cs` |
| Session log (no names or addresses) | `BleSessionLog.cs` |
| iOS bridge (DllImport `__Internal`) | `IosBleBackend.cs` → `Assets/Plugins/iOS/DunkDuelNative/DunkDuelBLE.swift` |
| Android bridge (AndroidJavaObject) | `AndroidBleBackend.cs` → `Assets/Plugins/Android/DunkDuelBle.kt` |
| Build steps (Info.plist, CoreBluetooth, Swift settings; manifest permissions) | `Editor/` |
| EditMode tests with an in-process loopback backend | `Tests/` |

## Protocol

- Service `DD0E0001-8E2A-4C1B-9D3F-6A5B4C3D2E1F`
  - INPUT `DD0E0002-…` write-without-response + notify: exactly one 20-byte input packet per write.
  - CONTROL `DD0E0003-…` write-with-response + indicate: fragments of CONTROL messages (≤ 1024 bytes).
  - PSM `DD0E0004-…` read: u16 LE L2CAP PSM published by the host (0 = none).
- Host = peripheral. iOS advertises the local name `DD-XXXX[:name]`; Android cannot set a per-advertisement
  name, so it puts `[code×4][platform 'A'|'I'][name ≤ 8 bytes]` into the scan response as service data.
- Guest = central scanning by service UUID. Android phones prefer to host in mixed pairs (`PrefersHosting`).
- CONTROL fragment header: byte 0 = START (0x80) | END (0x40) | sequence (6 bits); START fragments add the
  total length as u16 LE. Fragments never exceed the negotiated payload, so no long (prepared) writes happen.
- Native → C# records, one batch per poll: `[u8 kind][u16 len LE][payload]` (INPUT, CONTROL fragment, STATE,
  PEER, LINK, CAN_HOST, DROPPED). State and error codes are the numeric values of the `DunkDuel.Net` enums.
- L2CAP (`BleL2capOption.Prefer`, off by default) carries INPUT only; CONTROL stays on GATT to keep its order.

## Verified here vs. needs a phone

Verified headless: framing, advertisement parsing, the SPSC ring under two threads, and the full transport
state machine against the loopback backend (Tests/); the Kotlin bridge compiles against the Android 14
framework classes and its JNI signatures match `AndroidBleBackend`. The Swift bridge cannot be compiled off Apple
platforms; the first Xcode device build found two errors in the L2CAP stream code (an implicitly unwrapped
`outputStream` copied into a plain optional), now fixed by unwrapping every stream access in one helper, and all 12
`__Internal` functions match their C# `DllImport` names and types. Not verifiable without devices: real radio behaviour (MTU, connection interval, flow control,
iOS↔Android quirks), permission dialogs, and the L2CAP path. The P0 radio spike covers these.
