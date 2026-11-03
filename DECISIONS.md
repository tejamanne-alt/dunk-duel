# DECISIONS.md

Each entry: the decision, the options considered, and why. Entries marked **needs sign-off** interpret or extend the
design spec; they are implemented as recommended and easy to change in data, but the designer should confirm them.

## Architecture

**D1. One set of sources, two compilers.** Engine-free assemblies live under `Assets/DunkDuel` with asmdefs for Unity
and are also compiled by `Headless/*.csproj` (netstandard2.1, C# 9) for CI, the Balance Lab and tools.
Options: separate .NET library imported as a DLL; Unity-only code. Chosen because the same files are checked by
analyzers, tests and the Balance Lab without the Editor, and Unity still compiles them natively (no DLL drift).

**D2. Fixed point is Q31.32 in a `long` (`Fix`).** Options: Q48.16 (cheap multiply, coarse), float with strict-math
flags (not reliable across Mono/IL2CPP/ARM). Q31.32 gives sub-nanometre precision on a 12 m court; multiply uses a
128-bit split, divide a 32-step long division, both round half away from zero and are verified against `BigInteger`.

**D3. Data tables are JSON, parsed with integer math.** Options: ScriptableObjects (Unity-only, float fields),
generated C# constants. JSON is diffable, loads in Unity (`Resources`), headless and WebAssembly alike; decimals are
parsed straight to fixed point (no float ever), and `TableBinder` rejects unknown and missing keys. Units are explicit
(`[Unit(PerSecond)]` etc.), so data is written in m/s and seconds and baked per frame.

**D4. Hit-stop and slow motion live in the sim.** Options: presentation-only freeze. Freezing only visuals would let
inputs act on an invisible state and desync perception between phones. In the sim, a hit-stop tick consumes inputs
(presses are buffered, ages frozen) but does not advance gameplay; slow motion advances gameplay every Nth tick.
Both phones see identical timing and replays stay frame-exact. Camera, callout and crowd parts stay in presentation.

**D5. Sim version = hash(sim source code) ⊕ hash(data tables).** The code hash is generated into
`SimBuildInfo.g.cs` (Tools/SimTool or the Editor pre-build step); a test fails when it is stale. The handshake
refuses peers with a different version.

**D6. Session contracts live in `DunkDuel.Sim.Session`.** `IMatchSession`, `IInputSource`, `LocalSession`,
`EventLedger`, `Replay`. Presentation depends on Sim only (per the architecture table), so the rollback-aware event
ledger has to be visible to it; Net implements `IMatchSession` with `RollbackSession`.

## Netcode and transports

**D7. `ITransport.Send(channel, bytes, offset, count)`.** The spec lists `Send(bytes)`. A channel parameter keeps the
INPUT packet exactly 20 bytes (it maps to BLE write-without-response, CONTROL to write-with-response) instead of
spending a byte on routing. Discovery (`StartScan`/`Peers`) and `Poll()` (main-thread delivery) were added for the lobby.

**D8. Input delay is chosen once per match** from the handshake's median RTT (one-way ticks, clamped 1-3). Options:
changing delay mid-match. Changing it mid-match needs input duplication or dropping; keeping it fixed per match is
simpler and still adaptive per connection. Leading peers slow down one tick at a time when the frame advantage ≥ 2.

**D9. Hashes are compared only when final** (every input before the hashed tick confirmed), recomputed from the
snapshot ring, so mispredicted timelines never cause false desyncs. On mismatch the host streams its state in
160-byte chunks; the guest adopts it and resimulates to the present.

**D10. LAN transport is single-threaded.** Non-blocking sockets pumped in `Poll()`; broadcast discovery; CONTROL is
reliable and ordered (sequence numbers, acks, 100 ms retransmit); INPUT is fire-and-forget (redundancy is in the packet).

## Rules interpretations

**D11. Drive vs a defender who neither walls nor jumps: quickness battle.** **needs sign-off.** The Read table defines
drive-vs-wall (charge) and drive-vs-contest (blow-by) but not a defender who simply stands. If such a drive were always
cut off, passive defense would beat every drive and baiting would be pointless (CPU playtests showed shooting becoming
dominant). At first contact the attacker beats the defender with chance
`0.20 + 0.05·ΔSpeed + 0.03·(Handles-5) - 0.04·(Defense-5)` (clamped 5-50%, halved if the defender is already sliding),
rolled once with the match RNG; otherwise it is a push that gains ground and bumps Balance. Option B: always cut off.

**D12. Charges are decided at first contact** (with a 6-frame grace for contact flicker): planting during a push no
longer draws a charge, matching "planted at least 8 frames earlier".

**D13. Contest model.** **needs sign-off.** `Q = timing × distance × height × defense multiplier`; `C = 0.45 Q`.
Timing falls off linearly over 16 frames from the defender's jump apex. A grounded hand contributes a floor of 0.2 on
jumpers and 0.7 on rim finishes ("verticality": a big standing in front contests a layup without jumping), and a jump
never contests worse than standing still. Distance is full inside 0.75 m and fades to zero by 3 m (± 0.6 m per metre of
reach difference from 2.5 m); contests from behind (more than 0.35 m past the shooter) are ×0.5 except Chase-Down.
Height fades over 1.1 m below the release point. Planted, unplanting, stumbling, knocked-down or off-balance defenders
contest 0. Blocks are deterministic (no second roll): a jumping contest with timing ≥ 0.65 and distance ≥ 0.75 blocks
when `Q·(block power + 0.35·height advantage) − protection ≥ 0.8`, where a Perfect release adds 0.45 protection and a
dunk 0.30. Earlier values (0.55 rim floor, ×0.35 from behind, a jump that could contest worse than standing) let
guards finish over bigs and were changed after Balance Lab diagnostics.

**D14. Release controls.** Hold SHOOT to gather, release to shoot; tricks that end in a shot release on TRICK release,
or on a SHOOT/TRICK tap when the button is no longer held when the gather begins. Trick slots: TRICK toward/neutral =
forward Trick, TRICK away/down = back Trick. Amped = the AMP bit (touch: swipe up from TRICK; controller: modifier).

**D15. Denial costs its bar on the attempt, hit or miss** (one ring per Finisher). Option: refund on miss. Charging on
the attempt prevents mashing and keeps the timing ring meaningful. **needs sign-off.**

**D16. Clear rule:** shooting, scoring Tricks and Finishers are disabled until the handler has been beyond the arc
after a stop (steal, block, charge, defensive rebound, Denial). Offensive rebounds keep the clear status.

**D17. Shot clock** runs only while the offense holds the ball; it resets on possession change and on an offensive
rebound after a rim touch. A violation is a check ball for the other player.

**D24. No body contact while a stop must be cleared.** Pushboxes are off while the handler still has to take the ball
past the arc. Option: keep contact. With contact, a defender standing between the rebounder and the arc could body
the clear until the shot clock expired (seen in CPU matches); the clear is a reset, not a contested phase. Regression
test `ClearingPastTheDefenderIsNotBlocked`.

**D25. Drive and post bumps are paced by frames** (`driveBumpInterval`, post `bumpInterval`), not by contact events.
Slow drivers dropping under drive speed used to re-trigger "first contact" every 2-3 frames and drain Balance in a
second. Regression test `DriveBumpsRespectTheirInterval`.

## CPU and balance

**D18. CPU perception and execution.** The CPU reads a snapshot delayed by its tier's reaction frames (Legend 12,
never less), knows its own fighter exactly (proprioception), and never reads the RNG or rolled shot outcomes. Release
timing error is a seeded near-normal spread per tier; reads use per-zone/shot-clock habit counts.

**D19. Balance Lab tuning knobs.** **needs sign-off.** The roster attributes stay exactly as designed (36-point
budget, single 9). The automated tuner adjusts only derived per-fighter data (jump-shot and finish gather frames, jump
rise frames, reach factor, pushbox) and CPU style weights, with guards that keep Juno and Meera layup-only. It evaluates
every candidate on identical match seeds. Reported numbers are CPU-vs-CPU proxies; human playtests confirm.
Small samples overfit: a 150-match-per-side search that looked balanced on its own seeds (46-53%) measured 40-53%
on the gate's 1,000-match run, so final passes use `--only <fighter>` with 700 matches per side and the result is
always re-measured on the gate's separate seeds. The search moved CPU style weights noticeably (Kofi now drives far
more than he shoots, Meera's shoot weight fell to 0.55 yet she still takes about 8 threes a match because her shot
quality scores high); these are CPU personalities, not player rules, and are worth a designer's look.

**D20. Finisher landing rate** is tuned to 75-80% through the 4-frame Denial window and the CPU's denial timing
spread (Legend 6.8 frames, All-Star 7.4). Legend-vs-Legend measures 77.4% landed.

**D26. Match length.** **needs sign-off.** Legend-vs-Legend CPU matches average about 145 s, under the spec's
4-5 minutes: CPU players shoot about 64% and rarely run the 8 s shot clock down. Option A: change nothing until
human playtests measure real matches (humans miss more, hesitate and pause). Option B: lengthen rounds in data
(`roundTargetPoints` 7 → 11 or `roundSeconds` 90 → 120), which changes the feel of every round. Recommendation: A,
because the CPU numbers are a proxy; B is a one-line data change if human matches also come in short.

## Unity

**D21. No hand-written scene/prefab YAML.** The app is built in code at runtime; `DunkDuel ▸ Project Setup` creates
the Boot scene, URP quality assets and player settings through Editor APIs. Optional packages are guarded with asmdef
`versionDefines` so the project still compiles if one fails to resolve.

**D22. `.meta` files are not committed by the build agent.** Unity generates them on first import; commit them then.
asmdefs reference each other by name, so no GUIDs are needed.

**D27. App identity and iOS signing defaults.** Bundle ID and Android package `com.astradite.dunkduel`; iOS automatic
signing with the Teja Manne developer team. `AppIdentity` applies them in Project Setup, on Editor load and before
every iOS/Android build, replacing only empty or placeholder values so a deliberate Player Settings change survives.
Xcode needs the 10-character Team ID, which is not the code in the certificate name ("Apple Development: Teja Manne
(BX42AX95QW)" carries a per-person certificate ID; the Team ID is the certificate's OU). Options: (A) hard-code a
Team ID, which needed a value we did not have; (B) leave signing to Xcode, which is the manual step being removed;
(C) read the OU of this Mac's "Teja Manne" signing certificates with `security find-certificate` (public
certificates only, no keychain prompt) and save it in Player Settings, with `AppIdentity.AppleTeamId` to pin it.
Chosen: C, falling back to a clear Console warning when no single team is found.

## Tools

**D23. Web graybox.** The real Sim + AI compiled to WebAssembly with a canvas front end, for instant playtests, shared
review links and agent visual checks (Playwright screenshots/recordings). It is a dev tool; the product is the Unity
client. It compiles the shared sources at C# 11 (needed for `JSMarshalAs<T>`); C# 9 compatibility is still enforced by
the headless build.
