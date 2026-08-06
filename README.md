![Driftwood](assets/banner.png)

An open-world survival crafting game, built from scratch in C# on .NET 11 and OpenGL.

Spawn with nothing, punch wood, work up through tools and benches into a world where
everything is destructible. Every world is procedurally generated from a seed.

## Status

**A game you can play.** Walk into a world with nothing, mine what you can reach by hand, pick up
what it drops, and craft your way from a stick to a stormglass pickaxe. Build with it, store it in
a chest, smelt it in a furnace, light it with a lantern, close the window and come back to it.

**387 blocks, 110 items, 107 recipes and 9 smelts** — every one of which the audit proves is
reachable from bare hands, in seven rounds, with no starting kit.

| System | State |
| --- | --- |
| Chunk storage, seeded worldgen, greedy meshing, streaming | working |
| Sunlight and coloured block light, incremental relight | working |
| Player controller — walk, jump, sneak, climb, swim, collide with shapes | working |
| Model-driven blocks — slabs, stairs, fences, doors, panes, torches | working |
| Break and place, hardness, cracking, particles, material sounds | working |
| Items, drops, a 36-slot inventory, recipes, tags, tool tiers | working |
| Workstations — bench, furnace, blast furnace, stonecutter, chests | working |
| Interface — pixel font, tabbed screens, recipe book, key rebinding | working |
| Save and load, autosave, backups, a start screen | working |
| Sky, day and night, clouds, weather particles | working |
| Creatures — models, art, a wandering herd, voices | first four |
| Creature drops, hostiles, farming | not started |
| Armour, signals, rails, fluid flow | not started |
| Controller support | not started |

## The world

Materials with real names where a real name exists — nobody owns the word copper — and ours where
the name would have to be invented anyway.

| | |
| --- | --- |
| Ground | grass, dirt, sand, gravel, clay, snow, sandstone |
| Rock | stone, deepstone, and the coralstone / driftstone / saltstone intrusions |
| Ore | coal, copper, iron, gold, azurite, stormglass, emberstone |
| Growth | driftoak logs, leaves and planks, vines, meadowgrass, seaflax, marshlily |
| Worked | bricks, glass, smokeglass, nine cut-stone families, each with slabs, stairs and walls |

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
Driftwood.exe                          the menu, over a world it is already flying
Driftwood.exe --seed driftwood         named seed; words are hashed, digits are literal
Driftwood.exe --world stonebreak       open a named world, or make one
Driftwood.exe --ocean 10               less water; default is 25% of the surface
Driftwood.exe --chunks 24 --vsync      wider view, capped to display refresh
Driftwood.exe --audit --seed 12345     headless: generate, mesh, run every check, exit
Driftwood.exe --bench                  fly a fixed path, report frame-time percentiles, exit
```

Ocean coverage is calibrated rather than emergent: the generator samples its own height field and
shifts it so the requested share of the surface lands at or below sea level. The same request holds
across seeds, so one seed does not hand you a continent and the next an archipelago.

| Key | Walking | Flying |
| --- | --- | --- |
| Arrow keys, `WASD` | move | move |
| `Space` | jump | up |
| `Ctrl` | sneak — will not walk off a ledge | down |
| `Shift` | sprint | boost |
| Hold left | swing at the outlined block until it gives | |
| Hold right | place, or use what you are looking at | |
| `E` | your own pockets, equipment and a two-by-two | |
| `B` | fold the recipe book out beside them | |
| `Esc` | options — controls, video, audio, world, saves | |
| `F3` / `F5` | walk or fly / cycle the view | |

The button does not edit the world; it starts a swing, and the swing edits the world. That is why
holding one mines at a readable pace rather than at the speed of the event queue, and why there is
always something on screen causing the block to go.

Every key is rebindable, and bindings are stored as **names** rather than codes, so the settings
file can be read, checked and edited by hand without the game's input library being involved.

## Crafting

Recipes are authored as rectangles and matched against a trimmed grid, so a shape is a shape
wherever it sits. Tags — `#planks`, `#rough_stone`, `#coals`, `#logs` — let one row cover a whole
axis of materials.

Where something can be made is a separate question from whether it fits: `Recipe.Station` gates on
the workstation, and the grid still gates on size. Six things can be made in bare hands. Everything
else wants a bench, a furnace, or a stonecutter.

Tools run six rungs — wood, stone, copper, gold, iron, stormglass — across four heads, generated
from two tables rather than written out. **Tier and speed are separate columns**, which is what
makes the ladder a choice: gold is quicker than iron and reaches less far.

`--audit` walks the whole tree from empty hands and refuses to pass if anything has become
unreachable, which is what stops a recipe change quietly orphaning half the game.

## Creatures

Animals are modelled and painted in code, the same way the blocks are, so the game ships with its
own and needs nothing installed to have them.

A creature is a table of boxes and a palette. The art is generated per texel by turning each pixel
back into the point on the animal's surface it belongs to and colouring it there — so a marking is
a shape in space and wraps round the seams rather than stopping dead at every edge.

The skeleton layout is deliberately compatible with the nets that entity texture packs are painted
against, so a pack you already have can dress them; anything it does not carry keeps ours.

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

## Texture packs

Driftwood draws its own block textures, so it never needs anything else to look complete. It can
also read a texture pack you already have and use whatever that pack carries.

```
Driftwood.exe --pack C:\packs\SomePack.zip
Driftwood.exe --pack C:\packs\SomePack --texture-size 64
```

A folder, a `.zip`, or a packaged pack, and **three different internal layouts** are recognised by
looking at what is inside rather than at the extension. A pack is treated as a **sparse set of
overrides**: anything it does not carry keeps Driftwood's own art, so a half-finished pack still
leaves a complete world. Nothing is copied, unpacked or written back out — the pack is read where it
sits and closed again.

Animated textures play, at whatever frame rate the pack asks for, with the whole mip chain uploaded
per layer so a lake does not freeze at distance.

Driftwood's block names are deliberately its own, so the correspondence between them and a pack's
file names lives in one explicit table in `BlockTextureSet`. That is the only place the two
vocabularies meet.

```
Driftwood.exe --pack-report --pack C:\packs\SomePack.zip
Driftwood.exe --pack-coverage --pack C:\packs\SomePack.zip
```

The first says which of Driftwood's layers the pack supplied and which kept our art — the answer to
"is the pack even being used". The second reads the pack and groups what it carries that Driftwood
has nothing to put it on, which is how content gets planned against something real.

## Saving

A save is **the seed and a diff**. A chunk is a pure function of its seed and position, decoration
included, so terrain is never written down: an hour of walking and forty blocks of building is forty
blocks on disk.

Everything is stored **by name** through a per-save palette, never by id. Ids are registration order
and every release moves them; a format keyed on ids passes every round trip and comes back with
stone bricks turned into ladders the first time a block is inserted. The check resolves a palette
against a registry whose ids have all moved.

Sections are tagged and length-prefixed, so a file written by a newer build is read by an older one
with the unknown parts skipped **and preserved**. Writes go through a temporary file and a move, and
three rotating backups are copied rather than moved — moving leaves a moment with no world on disk,
and that moment is when the power goes off.

## Auditing a world

`--audit` generates and meshes a world without opening a window, then reports a block census,
terrain relief, timings and **over a hundred checks**. It exits non-zero if any fails, so a seed
plus its report is a receipt that survives into later phases.

```
relief        surface y 35..101 (span 66), mean 62.7
land          41.4% of columns above sea level 62
...
  [PASS] the ore ladder holds         coal 0.62 > copper 0.42 > iron 0.32 > gold 0.11 > stormglass 0.044
  [PASS] everything is reachable from bare hands 110 of 110 items in 7 rounds
  [PASS] recipes match what they are made of 107 recipes and 9 smelts, each laid back into a grid
  [PASS] a world survives being written down names survive every id moving
```

Bands rather than floors, and relations rather than absolutes wherever an absolute can be satisfied
by a broken world. Every tier of ore can sit inside its own band and still come out in the wrong
order, so the ladder is checked as an ordering too.

**Checks are control-tested against a build deliberately broken to fail them.** More than one has
been rewritten because the control showed it could not have caught what it was for — a check that
restates the code it is checking passes every build, including the broken one.

## Seeing the interface, and the thing in your hand

Two instruments exist because a headless check cannot see a screen.

```
Driftwood.exe --ui-check      open every screen in turn, read the pixels back, exit
Driftwood.exe --shot <folder> photograph the hand and the screens, exit
```

`--ui-check` opens each screen and reads the framebuffer, because everything short of that proves
only that geometry was *built* — and geometry was built correctly all the way through a fault where
nothing arrived on screen. A count of quads cannot tell "not drawn" from "drawn somewhere else"; a
pixel can.

`--shot` exists because a tile can be looked at and a tile **in a fist** cannot: that is a
projection, a swing, a grip and two entirely different arm poses on top of the drawing.

## Measuring frame time

`--audit` proves the world is correct. It says nothing about whether looking at that world is
smooth, so `--bench` answers the other half: it waits for the world to finish streaming in, flies a
fixed circular path at a fixed speed, and reports where the time went.

```
Driftwood.exe --bench                  15 seconds on the default path
Driftwood.exe --bench 60 --vsync       longer run, synchronised to the display
Driftwood.exe --bench --uploads 1      starve the per-frame upload budget
Driftwood.exe --bench --stall 20       inject a known 20 ms hitch every 200th frame
```

The gates are on wall-clock time, not on percentiles of frames. A control run with a 20 ms stall
injected every 200th frame — twenty hitches a second, four tenths of the run spent stalled — sailed
through a p99 gate untouched, because 305 bad frames out of 60,862 is only p99.5. When a frame costs
a tenth of a millisecond, frame count stops being a denominator anybody lives in.

`--stall` exists so those gates can be shown a fault of known size: 5 ms trips the wall-clock gate
alone, 20 ms trips both, and a clean build trips neither.

## Layout

```
src/Driftwood.Core     voxel storage, worldgen, meshing, lighting, physics, items, recipes,
                       saves, the player and creature models — no graphics API, runs headless
src/Driftwood.Client   window, GL context, renderers, input, audio
tools/IconForge        derives the committed icon and banner from the source artwork
```

Core carries no rendering dependency on purpose: everything about how the world is built stays
testable from the command line. The player's pose, the camera boom, where a held tool sits in a
fist and where an animal puts its feet all live there too, so they can be stepped at a fixed rate
and measured without opening a window.

No third-party packages beyond Silk.NET. **PNG and WAV decoding are both ours**, which is what lets
the game read a texture pack, a skin sheet and a sound without taking on a licence to answer for.
