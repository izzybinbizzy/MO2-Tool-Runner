# MO2 Tool Runner

Runs your Mod Organizer 2 generators for Skyrim SE / AE in the right order, unattended, and puts your profile back exactly as it was.

Shader caches, DynDOLOD's clean save, Wrye Bash (guided), Synthesis, SnowFixer, AutoBlend, PGPatcher, xLODGen + ACMOS roads, the grass cache, TreeLODGen, TexGen, DynDOLOD or AnvilLOD, Pandora and a save to play at the end. Every output is switched off before the first tool and on again right after its own tool rebuilt it; the load order is put back after every step, also after a crash or a Stop.

Download and instructions: the Nexus page.

## Build

Windows, .NET 10 SDK.

```
dotnet build "src/MO2 Tool Runner.slnx" -c Release
dotnet run --project src/Runner.Tests -c Release
```

- `src/Runner.Core` - the engine (no window)
- `src/Runner.App` - the window and the command line (`MO2 Tool Runner.exe`)
- `src/Runner.Tests` - offline tests against a fake Mod Organizer folder

## License

GPL-3.0-or-later. The tools it runs belong to their authors.
