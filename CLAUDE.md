# CLAUDE.md — Dunk Duel

1v1 streetball fighting game for iOS and Android. Unity 6.3 LTS + URP for the client; a deterministic,
engine-free C# simulation underneath. The design spec is the "Dunk Duel — Game Concept & Build Prompt" doc;
this file is the working agreement for anyone (human or agent) changing the code.

## Non-negotiables (from the build prompt)

1. Original IP only. No Capcom/Street Fighter names, characters, UI layouts, sounds or system names anywhere.
2. `DunkDuel.Sim` is deterministic and engine-free: pure C#, 64-bit fixed point (`Fix`, Q31.32), fixed 60 Hz
   tick. No `UnityEngine`, `float`, `double`, `System.Random`, `DateTime`, or order-dependent dictionary iteration.
3. Simulation and presentation are separate. Presentation reads sim state and events; the sim never reads presentation.
4. Every gameplay number lives in data (`Assets/DunkDuel/Data/Resources/DunkDuelData/*.json`), baked into immutable
   tables (`SimTables`). No magic numbers in rules code.
5. Local multiplayer needs no server: BLE behind `ITransport`, LAN UDP as the second implementation.
6. Landscape only, safe areas respected, 60 fps on High and Mid tiers.
7. Ask before adding any paid asset, SDK or service.

## Layout

```
Assets/DunkDuel/
  Sim/             DunkDuel.Sim (no engine refs): Math (Fix, Rng), Core (GameState, FrameInput, SimEvent, StateCodec,
                   SimBuildInfo), Data (Json, TableBinder, Tables, SimTables), Rules (Simulation.*.cs), Session
                   (IMatchSession, LocalSession, EventLedger, Replay)
  AI/              DunkDuel.AI: CpuPlayer (delayed perception, utility scoring, habits), CpuTiers/Rival blend
  Net/             DunkDuel.Net: ITransport, RollbackSession, NetLobby (handshake), packets, LoopbackTransport
  Transport.LAN/   UDP fallback (same-Wi-Fi / hotspot)
  Transport.BLE/   BLE ITransport + iOS/Android backends, framing, editor build post-processors, tests
  Presentation/    Unity: match driver, graybox court/fighters, camera, impact director, audio, haptics, quality tiers
  UI/              Unity: app bootstrap, menus, HUD, touch/gamepad input, lobby, modes, progression
  Editor/          Unity: project setup, sim-hash pre-build, asset postprocessor, build scripts
  Tests/EditMode/  Shared NUnit tests: run in Unity's Test Runner AND headless (Headless/tests)
  Data/Resources/DunkDuelData/   tuning, moves, impacts, ai, fighters/*.json (the single source of numbers)
Assets/Plugins/iOS|Android/      Swift/Kotlin bridges (BLE, haptics)
Headless/          .NET 8 solution that compiles the same sources (netstandard2.1, C# 9) + test project
Tools/BalanceLab/  Headless CPU-vs-CPU runner, balance gate, automated tuner
Tools/SimTool/     hash (sim code hash), golden (determinism replay), trace, matchup diagnostics
Tools/WebGraybox/  The sim + CPU compiled to WebAssembly with a canvas front end (dev playtest tool)
Tools/Blender/     Export/validation, VAT baker, LOD generation, graybox mannequins and court
```

## Commands

Inner loop — run after every change; never move on from a red loop:

```bash
dotnet build Headless/DunkDuel.Headless.sln -c Release        # analyzers + warnings-as-errors
dotnet test Headless/tests/DunkDuel.Tests                     # rules, determinism, static gates, netcode, BLE framing
dotnet format whitespace Headless/DunkDuel.Headless.sln --verify-no-changes
dotnet run --project Tools/SimTool -- hash --check            # SimBuildInfo.g.cs matches the sim sources
dotnet run --project Tools/SimTool -- golden --check          # committed 10,000-tick replay matches sim + data
```

After changing anything under `Assets/DunkDuel/Sim/`: `dotnet run --project Tools/SimTool -- hash` (regenerates the
code hash; the Unity Editor also does it before every player build), then `-- golden` to refresh the 10,000-frame
determinism replay. Data changes (any JSON under `DunkDuelData/`) also change the replay: run `-- golden` after them.

Balance and diagnostics:

```bash
dotnet run -c Release --project Tools/BalanceLab -- --matches 1000          # gate: every pairing within 45-55%, tiers rise
dotnet run -c Release --project Tools/BalanceLab -- --tune 200 --matches 150 --apply   # automated data search
dotnet run -c Release --project Tools/BalanceLab -- --tune 70 --matches 700 --only meera --apply   # targeted, larger samples
dotnet run --project Tools/SimTool -- matchup meera juno 100                # why does a matchup go the way it does
dotnet run --project Tools/SimTool -- trace kofi matas legend 42            # event log of one CPU match
```

Web graybox (playtest the real sim in a browser):

```bash
dotnet publish Tools/WebGraybox -c Release
cd Tools/WebGraybox/bin/Release/net8.0/publish/wwwroot && python3 -m http.server 8080
# open http://localhost:8080  (?autostart=1&mode=watch&court=3 for a CPU-vs-CPU demo)
```

Blender pipeline: `python3 -m pytest Tools/Blender/tests` (pure-Python rules); with `bpy` installed the full suite runs
headless. Usage per script is in `Tools/Blender/README.md`.

Unity (6000.3 LTS): open the repo root as the project, run **DunkDuel ▸ Project Setup** once (or
`-batchmode -executeMethod DunkDuel.Editor.ProjectSetup.Run`), then EditMode tests in the Test Runner. Unity creates
`.meta` files on first import: commit them. Never hand-edit scene or prefab YAML; build through Editor APIs.
App identity lives in `Editor/AppIdentity.cs`: bundle ID / Android package `com.astradite.dunkduel`, iOS automatic
signing with the Teja Manne team (Team ID `JYC5MKYDS7`). Commit `ProjectSettings/*.asset` after the first open.

## Gates

| Gate | How | Status |
| --- | --- | --- |
| Static | `IlGateTests` scans the compiled Sim IL/signatures; `StaticGateTests` scans sources and the asmdef | automated, green |
| Rules | `ReadTests`, `RulesTests`, `ShotModelTests`, `FinisherTests` | automated, green |
| Determinism | 10,000-frame replay (`DeterminismTests`, golden file, `SimTool golden --check`); device check in the in-game developer menu | headless green; devices pending |
| Balance Lab | `Tools/BalanceLab` (Legend tier, both sides, 45-55%) + tier ladder | green: pairings 47.0-51.9% over 2,000 matches each; each tier beats the one below 59-65%; Finishers land 78.3% |
| Netcode soak | `NetTests` loopback soak under BLE-like latency/jitter/loss; real gate is 30 iPhone-Android matches | loopback green; devices pending |
| Performance | p95 ≤ 16.6 ms on reference High/Mid phones, 10-min thermal soak ≥ 55 fps | device-only, pending |

## Conventions and pitfalls

- Adding a field to `GameState`/`FighterState`: add it to `StateCodec.Walk` (hash + serialization share one field list),
  keep the snapshot ≤ 2 KB (`StateSerializationRoundTripsAndFitsTwoKilobytes`), regenerate the hash and golden replay.
- New numbers go into JSON with a typed field in `Tables.cs` (`TableBinder` rejects unknown and missing keys; units via
  `[Unit]`, aliases via `[JsonKey]`). Per-second values are baked per frame; never convert at runtime.
- Sim/AI/Net code must compile as C# 9 / netstandard2.1 (Unity): no records, `init`, default interface methods,
  `BitOperations`, `System.HashCode`, or generic attributes.
- Events are facts. Presentation reacts through `EventLedger` (rollback-aware: orphaned effects fade, late ones spawn
  with an age). Presentation must never call into rules or mutate `GameState`.
- `x?.Invoke(queue.Dequeue())` skips the argument when `x` is null — dequeue first (this hung both transports once).
- The CPU sees a snapshot `ReactionFrames` old (never faster than 12), knows only its own fighter exactly, never reads
  the RNG or a shot's rolled outcome, and has its own RNG stream.
- Tuner wins at 150 matches per side are partly noise it selected for (one run looked balanced at 46-53% and
  measured 40-53% at 1,000). Always confirm with `--matches 1000` before committing tuned data.
- Keep `DECISIONS.md` current: decision, options considered, reason.
