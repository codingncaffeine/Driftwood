![Driftwood](assets/banner.png)

An open-world survival crafting game, built from scratch in C# on .NET 11 and OpenGL.

Spawn with nothing, punch wood, work up through tools and benches into a world where
everything is destructible. Every world is procedurally generated from a seed.

## Status

**P3 — the gathering loop.** You can walk into a world, look at a block, work it loose over as
long as its material deserves, and watch it go. There is no inventory to put it in yet, so nothing
is kept and nothing can be crafted.

| System | State |
| --- | --- |
| Chunk storage, block registry | working |
| Seeded procedural worldgen | working — terrain, caves, ore tiers, rock variety, trees |
| Greedy mesher with ambient occlusion | working |
| Chunk renderer, frustum culling | working |
| Chunk streaming around the player | working |
| Sunlight and coloured block light | working |
| Player controller — walk, jump, sneak, collide | working |
| Block hardness, hold to mine, cracking overlay | working |
| Player model, third-person camera, skin import | working |
| Block textures, alpha cutout, texture pack import | working |
| Item drops, inventory, crafting, recipes | not started |
| Save / load | not started |
| Sound | not started |
| Controller support | not started |

## The world

Twenty-six materials, all of which generate somewhere a player will meet them. Names are ours
where a name is worth having, and plain where a real material already has one — nobody owns the
word copper.

| | |
| --- | --- |
| Ground | grass, dirt, sand, gravel, clay, snow, sandstone |
| Rock | stone, deepstone, and the coralstone / driftstone / saltstone intrusions |
| Ore | coal, copper, iron, gold, azurite, stormglass, emberstone |
| Growth | driftoak logs, leaves and planks, vines |

Snow lies on cold ground and on high ground, deepstone takes over below roughly y 20, and the ore
ladder runs from coal at about 0.6% of all rock down to stormglass at 0.04%. Every one of those
rates is a banded check in `--audit` rather than a number somebody liked the look of.

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

You spawn walking. `F3` swaps to a free-flying camera and back, `F5` cycles the view.

| Key | Walking | Flying |
| --- | --- | --- |
| Arrow keys, `WASD` | move | move |
| `Space` | jump | up |
| `Ctrl` | sneak — will not walk off a ledge | down |
| `Shift` | sprint | boost |
| Hold left | swing at the outlined block until it gives | |
| Hold right | place a block against it, once per swing | |
| `Esc` | release or recapture the mouse | |
| `F1` | wireframe | |
| `F2` | frustum culling on / off | |
| `F3` | walk / fly | |
| `F5` | first person, over the shoulder, facing | |

The button does not edit the world; it starts a swing, and the swing edits the world. That is why
holding one mines at a readable pace rather than at the speed of the event queue, and why there is
always something on screen causing the block to go.

## Skins

The player model reads a skin sheet you already have, in either layout the format has used.

```
Driftwood.exe --skin C:\skins\somebody.png
Driftwood.exe --skin C:\skins\somebody.png --skin-model classic
```

64×64 or the older 64×32, at any multiple of 64. Arm width is detected by looking for the texels
only a four-wide arm can reach, since a bare PNG carries that nowhere else; `--skin-model` says so
outright when a sheet is drawn ambiguously. Driftwood paints its own skin in code, so a build with
no art folder still has a player in it.

## Auditing a world

`--audit` generates and meshes a world without opening a window, then reports a block
census, terrain relief, timings and a set of checks. It exits non-zero if any check
fails, so a seed plus its report is a receipt that survives into later phases.

```
relief        surface y 35..101 (span 66), mean 62.7
land          41.4% of columns above sea level 62
...
  [PASS] every material is in the world 25 of 26 blocks generate
  [PASS] coal rate in band            0.624% of rock (want 0.30-1.20)
  [PASS] the ore ladder holds         coal 0.62 > copper 0.42 > iron 0.32 > gold 0.11 > stormglass 0.044
  [PASS] snow lies high and cold      15.3% of open ground, mean y 77.5 against grass at 70.3
```

Bands rather than floors, and relations rather than absolutes wherever an absolute can be
satisfied by a broken world. Every tier of ore can sit inside its own band and still come out in
the wrong order, so the ladder is checked as an ordering too. Snow coverage swung between 4% and
38% across five seeds on one unchanged constant — climate runs on a 1,400-block wavelength and the
audit samples a few hundred — so what is gated is that snow sits *higher* than grass, which held on
every seed.

## Texture packs

Driftwood draws its own block textures, so it never needs anything else to look complete. It can
also read a texture pack you already have and use whatever that pack carries.

```
Driftwood.exe --pack C:\packs\SomePack.zip
Driftwood.exe --pack C:\packs\SomePack --texture-size 64
```

A folder or a `.zip`, either way. A pack is treated as a **sparse set of overrides**: anything it
does not carry keeps Driftwood's own art, so a half-finished pack still leaves a complete world.
Nothing is copied, unpacked or written back out — the pack is read where it sits and closed again.

Driftwood's block names are deliberately its own, so the correspondence between them and a pack's
file names lives in one explicit table in `BlockTextureSet`. That is the only place the two
vocabularies meet.

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
src/Driftwood.Core     voxel storage, worldgen, meshing, lighting, physics, the player model
                       and animator — no graphics API, runs headless
src/Driftwood.Client   window, GL context, renderers, input
tools/IconForge        derives the committed icon and banner from the source artwork
```

Core carries no rendering dependency on purpose: everything about how the world is built stays
testable from the command line. The player's pose and the camera boom live there too, so a walk
cycle and a swing can be stepped at a fixed rate and measured without opening a window.

No third-party packages beyond Silk.NET. PNG encoding and decoding are ours, which is what lets
the game read a texture pack and a skin sheet without taking on a licence to answer for.
