# Dunk Duel

A 1v1 streetball fighting game for iPhone and Android: two players, one ball, one hoop, best of three rounds. It
plays side-on in landscape like a 2.5D fighter, and every possession is a read: drive or shoot against wall or contest.

This repository is the client and the tools: a Unity 6.3 LTS (URP) project built on a deterministic, engine-free C#
simulation, with rollback netcode over Bluetooth LE (iPhone ↔ Android) and LAN, five CPU tiers, four fighters and
nine courts.

## What is here

| Area | Where | State |
| --- | --- | --- |
| Deterministic sim: rules, the Read, shots, ball, meters, 12 Signature Tricks, 4 Finishers + Denial, rounds, hit-stop | `Assets/DunkDuel/Sim` | done, 124 tests green headless |
| Data: every gameplay number | `Assets/DunkDuel/Data/Resources/DunkDuelData/*.json` | done |
| CPU (Rookie → Legend, habits, Rival mode, personalities) | `Assets/DunkDuel/AI` | done |
| Rollback netcode, handshake, desync recovery, reconnect | `Assets/DunkDuel/Net` | done, loopback soak green |
| BLE transport (Swift CoreBluetooth, Kotlin Android BLE, C# bridge) | `Assets/DunkDuel/Transport.BLE`, `Assets/Plugins` | written; C# and Kotlin compile-checked; needs devices |
| LAN UDP transport | `Assets/DunkDuel/Transport.LAN` | done, localhost test green |
| Unity presentation, HUD, touch/gamepad, menus, modes, editor tooling | `Assets/DunkDuel/Presentation`, `UI`, `Editor` | written; needs a first open in Unity |
| Balance Lab + automated tuner | `Tools/BalanceLab` | done, gate passes: every pairing 47-52% at Legend |
| Web graybox (the real sim in WebAssembly) | `Tools/WebGraybox` | done |
| Blender pipeline (export/validate, VAT crowds, LODs, graybox) | `Tools/Blender` | done, tested with headless Blender 4.5 |

## Quick start

Play the graybox in a browser (the same C# sim and CPU as the game):

```bash
dotnet publish Tools/WebGraybox -c Release
cd Tools/WebGraybox/bin/Release/net8.0/publish/wwwroot
python3 -m http.server 8080
```

Open http://localhost:8080. Keys: arrows move (↓ walls on defense, protects on offense, ↓→ posts up with Matas),
J shoot (hold, release at the peak), K shake, L trick (hold U for Amped), Space turbo, I finisher/deny, H hitboxes.

Run the checks:

```bash
dotnet test Headless/tests/DunkDuel.Tests
dotnet run -c Release --project Tools/BalanceLab -- --matches 1000
```

Open in Unity 6.3 LTS: add the repo root in Unity Hub, open it, run **DunkDuel ▸ Project Setup**, press Play.

## Documents

- `CLAUDE.md` — working agreement: non-negotiables, layout, commands, gates, pitfalls.
- `DECISIONS.md` — every decision with the options considered and why; items marked *needs sign-off*.
- `SPIKES.md` — P0 device spikes (BLE echo, cross-device determinism, look-dev slice) and headless proxies.
- `Tools/BalanceLab/balance-report.json` — the latest 1,000-match Balance Lab gate result.
- `Tools/Blender/README.md`, `Assets/DunkDuel/Transport.BLE/README.md` — pipeline and transport details.

All characters, courts, names and designs are original.
