# Driftwood

An open-world survival crafting game, built from scratch in C# on .NET 11 and OpenGL.

Spawn with nothing, punch wood, work up through tools and benches into a world where
everything is destructible. Every world is procedurally generated from a seed.

## Status

**P1 — streaming and meshing.** Voxel core, seeded worldgen, greedy meshing and chunk
streaming are in. The game itself is not; there is no player, no inventory and no crafting yet.

| System | State |
| --- | --- |
| Chunk storage, block registry | working |
| Seeded procedural worldgen | working — terrain, caves, ore, trees |
| Greedy mesher with ambient occlusion | working |
| Chunk renderer, frustum culling, fly camera | working |
| Chunk streaming around the viewer | working |
| Sunlight and coloured block light | working |
| Player controller — walk, jump, sneak, collide | working |
| Block break / place | not started |
| Inventory, crafting, recipes | not started |
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
Driftwood.exe --ocean 10               less water; default is 25% of the surface
Driftwood.exe --chunks 24 --vsync      wider view, capped to display refresh
Driftwood.exe --audit --seed 12345     headless: generate, mesh, print a census, exit
Driftwood.exe --bench                  fly a fixed path, report frame-time percentiles, exit
```

Ocean coverage is calibrated rather than emergent: the generator samples its own height field
and shifts it so the requested share of the surface lands at or below sea level. The same
request holds across seeds, so one seed does not hand you a continent and the next an
archipelago.

You spawn walking. `F3` swaps to a free-flying camera and back.

| Key | Walking | Flying |
| --- | --- | --- |
| Arrow keys, `WASD` | move | move |
| `Space` | jump | up |
| `Ctrl` | sneak — will not walk off a ledge | down |
| `Shift` | sprint | boost |
| `Esc` | release or recapture the mouse | |
| `F1` | wireframe | |
| `F2` | frustum culling on / off | |
| `F3` | walk / fly | |

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

## Measuring frame time

`--audit` proves the world is correct. It says nothing about whether looking at that world is
smooth, so `--bench` answers the other half: it waits for the world to finish streaming in, flies
a fixed circular path at a fixed speed, and reports where the time went.

```
Driftwood.exe --bench                  15 seconds on the default path
Driftwood.exe --bench 60 --vsync       longer run, synchronised to the display
Driftwood.exe --bench --uploads 1      starve the per-frame upload budget
Driftwood.exe --bench --stall 20       inject a known 20 ms hitch every 200th frame
```

The gates are on wall-clock time, not on percentiles of frames. A control run with a 20 ms stall
injected every 200th frame — twenty hitches a second, four tenths of the run spent stalled — sailed
through a p99 gate untouched, because 305 bad frames out of 60,862 is only p99.5. When a frame
costs a tenth of a millisecond, frame count stops being a denominator anybody lives in.

```
hitches       1 frames over 4.12 ms (2x p50 or +4 ms) — 0.1/s, 0.0% of the wall clock
dropped       0 frames over 16.67 ms — 0.0/s a 60 Hz display would miss
  [PASS] time lost to hitches         0.0% of the run in 1 frames over 4.12 ms (want < 2%)
  [PASS] holds 60 Hz                  0.0 frames/s over 16.67 ms (want < 0.5)
```

`--stall` exists so those gates can be shown a fault of known size: 5 ms trips the wall-clock
gate alone, 20 ms trips both, and a clean build trips neither.

## Layout

```
src/Driftwood.Core     voxel storage, worldgen, meshing — no graphics API, runs headless
src/Driftwood.Client   window, GL context, renderer, input
```

Core carries no rendering dependency on purpose: everything about how the world is built
stays testable from the command line.
