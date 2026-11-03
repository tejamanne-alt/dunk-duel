# SPIKES.md — P0 spikes

P0 decides go/no-go before art production: a BLE echo test between an iPhone and an Android phone, a
cross-device determinism test, and a look-dev slice on a mid-tier phone. The device runs need real hardware and a
Unity build; everything that can be measured headless has been, and is recorded below as a proxy.

## 1. BLE echo, iPhone ↔ Android, 60 Hz for 10 minutes

**Procedure.** Build to both phones. On each: **Developer ▸ BLE Echo Spike**. Phone A taps *Host Court* (Android
first, then repeat with the iPhone hosting); phone B taps *Join*. The screen sends one 20-byte INPUT packet per 60 Hz
tick and echoes the peer's on CONTROL heartbeats; it logs RTT, jitter, loss, connection interval and negotiated
payload to `persistentDataPath/ble_spike_<date>.csv` (no device names or addresses in the log). Run 10 minutes in each
role direction, phones 1 m and 5 m apart, screen on. Repeat with **L2CAP** enabled in the developer menu.

**Record here:**

| Host → Guest | Distance | Conn. interval | RTT p50 / p95 | Jitter p95 | INPUT loss | Notes |
| --- | --- | --- | --- | --- | --- | --- |
| Android → iPhone | 1 m | | | | | |
| Android → iPhone | 5 m | | | | | |
| iPhone → Android | 1 m | | | | | |
| iPhone → Android | 5 m | | | | | |

**Pass:** RTT p95 ≤ 90 ms (2 frames of delay + rollback cover ~45 ms one way), jitter p95 ≤ 20 ms, INPUT loss ≤ 5 %
(8-frame redundancy absorbs it), no link drops in 10 minutes. If iPhone-hosting fails as reported for Nearby
Connections, keep "Android hosts in mixed pairs" (already the default) and document the failing combination.

**Headless proxy (done).** Two full rollback sessions over the simulated radio (`SimulatedLink`), CPU players on
both phones, 10 full matches per row, 3 % INPUT loss:

| One-way latency / jitter | Input delay | Desyncs | Max rollback | Rollback p95 | Resimulated frames per 100 ticks |
| --- | --- | --- | --- | --- | --- |
| 15 ms / 7 ms | 1 | 0 | 3 | 1 | 4.5 |
| 30 ms / 15 ms | 2 | 0 | 4 | 1 | 4.9 |
| 45 ms / 22 ms | 3 | 0 | 4 | 1 | 5.1 |

Desync detection and recovery (host streams state in 160-byte chunks, guest resimulates) and the 3 s pause /
20 s reconnect window are covered by `NetTests`. The BLE C# layer (framing, records, heartbeat, SPSC ring, transport
state machine over a loopback backend) is covered by 42 tests; the Kotlin bridge compiles against android-all 14;
the Swift bridge cannot be compiled here (CoreBluetooth needs Apple's toolchain): the first Xcode device build
reported two L2CAP stream errors, since fixed, and the next device build confirms it.

## 2. Cross-device determinism

**Procedure.** Build to both phones. **Developer ▸ Determinism Check** replays
`Tests/EditMode/Data/golden_10000.ddreplay` (10,000 ticks, a full CPU match) and compares the final state hash with
`golden_10000.json`. Record PASS/FAIL and the hash for: Unity EditMode (Mono), iPhone (IL2CPP ARM64), Android
(IL2CPP ARM64), and the headless .NET run.

| Runtime | Final hash | Result |
| --- | --- | --- |
| .NET 8 headless (x64) | `258A81EDC8A79206` | PASS (`GoldenReplayMatchesCommittedHash`) |
| Unity EditMode | | |
| iPhone | | |
| Android | | |

**Pass:** identical hash everywhere. The sim uses only integer and Q31.32 fixed-point math; `IlGateTests` proves
the compiled assembly contains no floating-point opcodes, signatures or locals.

## 3. Look-dev slice on a mid-tier phone

**Procedure.** One court (recommended: The Crown or Rivalry Night), two final-quality fighters, crowd, effects,
final shaders. Profile on a mid-tier reference phone (for example a 3-year-old mainstream Android and an iPhone 12
class device) for 10 minutes: frame time p50/p95, thermal state over time, memory.

| Device | Tier | p50 / p95 frame time | Thermal after 10 min | Memory | Notes |
| --- | --- | --- | --- | --- | --- |
| | Mid | | | | |
| | High | | | | |

**Pass:** p95 ≤ 16.6 ms on High and Mid, ≥ 55 fps after a 10-minute soak, memory under the tier ceiling.

**Headless proxy (done).** Resimulating 8 frames takes 14.6 µs on the build container (x64). The budget is 1 ms on
a Low-tier phone, so there is ~60× headroom for ARM and IL2CPP overhead before rollback cost matters.

## Go / no-go

Go requires all three device tables above to pass. Until then the decision is **pending hardware**; nothing in the
headless results argues against go.
