# Driftwood

An open-world survival crafting game, built from scratch in C# on .NET 11 and OpenGL.

Spawn with nothing, punch wood, work up through tools and benches into a world where
everything is destructible. Every world is procedurally generated from a seed.

## Status

**P0 — engine spike.** Voxel core, seeded worldgen and the chunk renderer are in.
The game itself is not; there is no player, no inventory and no crafting yet.

| System | State |
| --- | --- |
| Chunk storage, block registry | working |
| Seeded procedural worldgen | working — terrain, caves, ore, trees |
| Face-culling mesher with ambient occlusion | working |
| Chunk renderer, fly camera | working |
| Player controller, block break/place | not started |
| Inventory, crafting, recipes | not started |
| Lighting propagation | not started |
| Save / load | not started |
| Controller support | not started |

## Building

Requires the .NET 11 SDK.

```
build-release.bat
```

Or `dotnet build Driftwood.sln -c Release`. Output lands at
`src\Driftwood.Client\bin\Release\net11.0\Driftwood.exe`.

## Running

```
Driftwood.exe                          random seed
Driftwood.exe --seed driftwood         named seed; words are hashed, digits are literal
Driftwood.exe --chunks 24 --vsync      wider world, capped to display refresh
Driftwood.exe --audit --seed 12345     headless: generate, mesh, print a census, exit
```

| Key | Action |
| --- | --- |
| `WASD` | move |
| `Space` / `Ctrl` | up / down |
| `Shift` / `Alt` | boost / slow |
| `Esc` | release or recapture the mouse |
| `F1` | wireframe |

## Auditing a world

`--audit` generates and meshes a world without opening a window, then reports a block
census, terrain relief, timings and a set of checks. It exits non-zero if any check
fails, so a seed plus its report is a receipt that survives into later phases.

```
relief        surface y 35..101 (span 66), mean 62.7
land          41.4% of columns above sea level 62
...
  [PASS] coal rate in band            0.653% of stone (want 0.30-1.50)
  [PASS] iron rate in band            0.354% of stone (want 0.15-0.80)
```

## Layout

```
src/Driftwood.Core     voxel storage, worldgen, meshing — no graphics API, runs headless
src/Driftwood.Client   window, GL context, renderer, input
```

Core carries no rendering dependency on purpose: everything about how the world is built
stays testable from the command line.
