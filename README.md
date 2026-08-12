![Driftwood](assets/banner.png)

Driftwood is an open-world survival crafting game built from scratch in C# and OpenGL. Start with
empty hands, gather materials, make tools and workstations, build, explore, fight, trade, learn
magic, command companions, and return to the same persistent procedurally generated world later.

[**Download v0.3.0**](https://github.com/codingncaffeine/Driftwood/releases/tag/v0.3.0)
· [**Player handbook**](https://github.com/codingncaffeine/Driftwood/wiki)
· [Release notes](https://github.com/codingncaffeine/Driftwood/releases/tag/v0.3.0)

## What is in the game?

- Seeded 384-block-tall worlds with biomes, caves, ore seams, ruins, settlements, weather, flowing
  water and lava, and a deep molten Emberdeep region.
- Destructible terrain, building, storage, crafting, smelting, farming, rails, signals, armour,
  shields, bows, thrown items, hunger, breath, fire, and dozens of creatures.
- Five authored exploration-site families, persistent discoveries and encounters, resident trading,
  treasure routes, an explored-world map, and saved waypoints.
- Classless progression to level 20 with four attributes, gold, a character sheet, 19 spells, four
  automatic spell ranks, eight prepared slots, three loadouts, and four commanded companions.
- Cascaded shadows, ambient occlusion, HDR and bloom, temporal antialiasing, pack-aware weather and
  materials, refractive water, and material-matched interaction and spell particles.
- Keyboard and mouse plus SDL3 controller support, rebindable controls, controller spell banks,
  radial hotbar selection, target assist, rumble, and controller navigation throughout the UI.
- Local and Modrinth-backed shelves for texture and sound packs, plus local and online player skins.

Driftwood v0.3.0 is a playable single-player Windows x64 release. Multiplayer is planned but is not
included yet. Exact current content and control counts are generated from the game registries in the
[live reference](https://github.com/codingncaffeine/Driftwood/wiki/Live-Registry-Reference).

## Play

Download `Driftwood-v0.3.0-win-x64.zip` from the
[release page](https://github.com/codingncaffeine/Driftwood/releases/tag/v0.3.0), extract it, and run
`Driftwood.exe`. The build is portable and self-contained; playing does not require .NET or an
installer. The executable is not code-signed, so Windows may show an unfamiliar-app warning.

On a first day:

1. Break a log with empty hands and collect it.
2. Open the inventory and make planks, sticks, and basic tools.
3. Build a bench for the full crafting grid, then make storage and a furnace.
4. Find food and shelter, set a bed after dark, and begin exploring.

The [Getting Started guide](https://github.com/codingncaffeine/Driftwood/wiki/Getting-Started)
continues from there.

## Basic controls

| Input | Action |
| --- | --- |
| `WASD` or arrows | Move |
| `Space` / `Ctrl` / `Shift` | Jump / sneak / sprint |
| Hold left / right mouse | Mine or attack / place or use |
| `E` or `I` | Inventory, equipment, and character tabs |
| `B` | Fold the recipe book in or out |
| `M` | Open or close the explored-world map |
| Hold `R` | Release mouse-look and click a prepared spell |
| `Esc` | Options, controls, packs, skins, saves, and exit |

Every key and discrete controller action is rebindable. See
[Controls and Controller](https://github.com/codingncaffeine/Driftwood/wiki/Controls-and-Controller)
for the full keyboard, UI, spell, pet, and gamepad layouts.

## Handbook

The [Driftwood wiki](https://github.com/codingncaffeine/Driftwood/wiki) owns the detailed
documentation, organized by subject:

- [Survival](https://github.com/codingncaffeine/Driftwood/wiki/Survival),
  [gathering and mining](https://github.com/codingncaffeine/Driftwood/wiki/Gathering,-Tools-and-Mining),
  [crafting](https://github.com/codingncaffeine/Driftwood/wiki/Crafting,-Stations-and-Recipes), and
  [combat](https://github.com/codingncaffeine/Driftwood/wiki/Combat,-Equipment-and-Projectiles)
- [World, biomes, and fluids](https://github.com/codingncaffeine/Driftwood/wiki/World,-Biomes-and-Fluids),
  [creatures and spawning](https://github.com/codingncaffeine/Driftwood/wiki/Creatures-and-Spawning),
  [exploration and trading](https://github.com/codingncaffeine/Driftwood/wiki/Exploration-and-Trading),
  and [graphics and effects](https://github.com/codingncaffeine/Driftwood/wiki/Graphics-and-Effects)
- [Progression and magic](https://github.com/codingncaffeine/Driftwood/wiki/Magic-and-Progression),
  including generated descriptions and rank tables for every spell
- [Texture packs](https://github.com/codingncaffeine/Driftwood/wiki/Texture-Packs),
  [skins](https://github.com/codingncaffeine/Driftwood/wiki/Skins), and
  [audio and sound packs](https://github.com/codingncaffeine/Driftwood/wiki/Audio-and-Sound-Packs)
- [Saves and recovery](https://github.com/codingncaffeine/Driftwood/wiki/Saves-and-Recovery),
  [troubleshooting](https://github.com/codingncaffeine/Driftwood/wiki/Troubleshooting), and
  [developer tools and architecture](https://github.com/codingncaffeine/Driftwood/wiki/Developer-Tools-and-Architecture),
  with a separate [command-line reference](https://github.com/codingncaffeine/Driftwood/wiki/Command-Line-Reference)

## Build from source

The source build requires the .NET 11 SDK:

```text
build-release.bat
```

Or run `dotnet build Driftwood.sln -c Release`. The client output lands under
`src/Driftwood.Client/bin/Release/net11.0`. Release packaging, audits, visual checks, command-line
tools, and the source layout are documented in
[Developer Tools and Architecture](https://github.com/codingncaffeine/Driftwood/wiki/Developer-Tools-and-Architecture).

Driftwood's code is free and open-source under GPL-3.0-only.
