# MO2 Tool Runner

Runs the generators of a Mod Organizer 2 setup for Skyrim Special Edition / Anniversary Edition in the right order, unattended, and puts your profile back exactly as it was.

Shader caches, DynDOLOD's clean save, Wrye Bash (guided), Synthesis, SnowFixer, AutoBlend, PGPatcher, xLODGen with ACMOS roads, the grass cache, TreeLODGen, TexGen, DynDOLOD or AnvilLOD, Pandora, and a save to play at the end.

- **Download:** the Nexus page (the zip holds the program, its readme, its changelog and the licence).
- **The full guide:** [the wiki](https://github.com/izzybinbizzy/MO2-Tool-Runner/wiki).

## What it does

- **The right order.** The default order comes from the tools' own instructions (dyndolod.info and the SnowFixer, AutoBlend and PGPatcher pages). You can reorder the steps; a move that breaks one of those rules opens a warning that says why.
- **No tool reads old output.** Every output is switched off before the first tool, and each is switched on again right after its own tool rebuilt it.
- **Your profile comes back.** The load order and mod list are put back byte for byte after every step. That also holds when a tool fails, when you press Stop, and after a crash or a power cut.
- **A failed step loses nothing.** It gets its old output back, so nothing is left empty or half made.
- **Problems are found first.** Before anything changes it checks every tool and setting: a missing tool, DynDOLOD unpacked over an older version, DynDOLOD not in Expert mode, low disk space, a running game or download.
- **Mod Organizer's own snags handled.** Steam started when the game needs it, a second Mod Organizer's "already running" box answered, a crash reporter left in the virtual folder waited for, a finished download stuck as "unfinished" not taken for a download, SKSE's "plugin failed to load" box and mods' in-game boxes answered.
- **Hands off.** It notices a finished or stuck tool within 2 minutes. It stops on a question it does not know and shows it to you, and it never brings a window to the front.
- **The tools keep their own settings.** "Learn its settings" opens a tool in its own window; you set it up and close it, and every run uses those settings. The runner never copies another author's window.
- **In-game steps.** With DevBench the clean save and the save to play run by themselves. Without it they are guided step by step. Starting a new character? Tick **New game** beside START and both are left out.
- **A preview before a run** shows the tools in order, every mod switched off and why, when the game starts, and about how long it takes.
- **Also:**
  - tool update checks (Nexus and GitHub)
  - a run at a set time (a Windows scheduled task)
  - the mods your LOD is built from, with each one's page
  - a history of every change made to your Mod Organizer
  - themes

## The steps

| key | step | what it does |
|---|---|---|
| `shaders` | Shader caches | The game builds each shader mod's cache at its main menu (Community Shaders, Open Shaders, Bottled Shaders), only when that shader mod, its settings or the game changed. |
| `cleansave` | Clean save | DynDOLOD's clean save, done in game. Off in a new setup. |
| `wryebash` | Wrye Bash | The Bashed Patch, guided (Wrye Bash has no unattended mode). Off in a new setup. |
| `synthesis` | Synthesis | Your saved Synthesis pipeline. |
| `snowfixer` | SnowFixer | Every snow mesh and record into one plugin. |
| `autoblend` | AutoBlend | Landscape meshes that blend instead of cut. |
| `pgpatcher` | PGPatcher | Parallax, complex material and PBR patching. |
| `xlodgen` | xLODGen | Terrain LOD for every worldspace. |
| `acmos` | ACMOS Road Generator | Roads painted into the fresh terrain LOD. |
| `grass` | Grass cache | The grass precache (Faster NGIO, or the game itself). |
| `treelodgen` | TreeLODGen | 3D tree LOD models for DynDOLOD. |
| `texgen` | TexGen | Billboards and LOD textures. |
| `dyndolod` | DynDOLOD | Object, tree and grass LOD. |
| `anvillod` | AnvilLOD | Object, tree and grass LOD in place of DynDOLOD. |
| `pandora` | Pandora Behaviour Engine+ | Behaviour files for animation mods. |
| `playsave` | Save to play | The clean save loaded with every new output on, you taken back to where you stood, saved. Off in a new setup. |

## Requirements

- Windows 10 or 11
- Skyrim SE / AE (Steam or GOG); not Skyrim VR
- Mod Organizer 2.5
- [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- SKSE, for the steps that start the game
- Each tool you want it to run, in Mod Organizer's Executables list
- Optional: [DevBench](https://www.nexusmods.com/skyrimspecialedition/mods/181326), so the in-game steps run by themselves

## Quick start

1. Unpack the zip anywhere outside the game folder and outside Mod Organizer.
2. Start `MO2 Tool Runner.exe` on its own, not from Mod Organizer. It closes and starts Mod Organizer itself.
3. **New setup** (Ctrl+N): pick `ModOrganizer.exe`, its instance and a profile. The steps are found from your Executables list.
4. **Settings** (Ctrl+,): for each step, **Learn its settings (opens its own window)**.
5. **Check** (F5), then **Start** (Ctrl+Enter).

## Command line

```
"MO2 Tool Runner.exe" run --setup <file> [--only <step> | --from <step> | --until <step>]
"MO2 Tool Runner.exe" check --setup <file>
"MO2 Tool Runner.exe" preview --setup <file>
"MO2 Tool Runner.exe" lod --setup <file> --step <step>
"MO2 Tool Runner.exe" updates --setup <file>
"MO2 Tool Runner.exe" restore [--setup <file>]
"MO2 Tool Runner.exe" stop | stop-after
"MO2 Tool Runner.exe" changes --setup <file> [--look]
```

| exit code | meaning |
|---|---|
| 0 | finished / clean |
| 1 | failed / refused |
| 2 | stopped |
| 3 | wrong arguments |
| 4 | (`updates`) a tool has a newer version |

## Building from source

Windows, .NET 10 SDK.

```
dotnet build "src/MO2 Tool Runner.slnx" -c Release
dotnet run --project src/Runner.Tests -c Release
```

- `src/Runner.Core` - the engine: setups, the run and its record, the steps, Mod Organizer and the game (no window).
- `src/Runner.App` - the window and the command line (`MO2 Tool Runner.exe`).
- `src/Runner.Tests` - the offline tests, against a fake Mod Organizer folder in a scratch directory. They need no MO2 and no game.

The release is one framework-dependent, single-file exe: `dotnet publish src/Runner.App -c Release`.

## Credits

The tools it runs belong to their authors. MO2 Tool Runner only starts the copy the player downloaded and presses its buttons; none of their files are in this repository or the download.

- DynDOLOD, TexGen and xLODGen - sheson
- PGPatcher - hakasapl and its contributors
- Snow Fixer, AutoBlend and TreeLODGen - Cl3mus
- ACMOS Road Generator - DoubleYou
- Faster NGIO Grass Cache - hanotak
- AnvilLOD - Julio005
- Synthesis - Noggog and the Mutagen team
- Pandora Behaviour Engine Plus - the Pandora team
- Wrye Bash - the Wrye Bash team
- DevBench - alandtse
- Mod Organizer 2 - the Mod Organizer 2 team

Built on Microsoft .NET and System.Management (MIT).

## License

GPL-3.0-or-later.
