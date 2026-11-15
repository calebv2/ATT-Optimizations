# ATT Optimizations

Server-side performance and navigation mods for **A Township Tale**. Clients do not need either mod. Install Cave Performance, Forest Performance, or both.

| Mod | Version | Purpose | Download |
| --- | --- | --- | --- |
| [Cave Performance](CavePerformance/README.md) | 0.2.1 | Cave pathfinding, generation pacing, navigation recovery, and wyrm population targets | [Release](https://github.com/calebv2/ATT-Optimizations/releases/tag/cave-performance-v0.2.1) |
| [Forest Performance](ForestPerformance/README.md) | 0.2.0 | Forest instruction pacing, navigation updates, connectivity repair, and reset/cache fixes | [Release](https://github.com/calebv2/ATT-Optimizations/releases/tag/forest-performance-v0.2.0) |

## Compatibility

These builds target the dedicated server's **Root.Township 0.0.39.2**, assembly MVID `29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff`, with **MelonLoader 0.7.3** and **Harmony 2.10.2**. They check the game assembly at startup and disable themselves for an unsupported build. Compatibility with other server builds is not established.

## Install

1. Download the release ZIP for each mod you want.
2. Stop the server normally and back up your existing mods and configuration.
3. Copy the ZIP's `Mods/*.dll` into the server's `Mods` directory.
4. Remove the replaced standalone DLLs listed in each mod's README, including copies in loader-scanned subfolders. Their components are already embedded in the new DLLs.
5. Start the server normally. Check the server log for `[Cave_Performance] READY v0.2.1` and/or `[Forest_Performance] READY v0.2.0`, and check for component errors.

Each mod creates its configuration under `UserData/<ModName>/config.txt` if it is absent. Edit it while the server is stopped; settings take effect at startup. The example configurations are references and do not need to be copied over an existing configuration.

These mods do not hot-load or restart the server. Installing Forest Performance does not request a forest reset. A single native generation instruction or saved-entity batch can still take a long time; the pacing settings are not hard frame-time limits.

## Build from source

The repository contains both mods' complete source, embedded runtime components, existing regression tests, and build/test scripts. Game and loader assemblies must come from your own server installation and are not distributed here.

Use a .NET SDK capable of building .NET Framework 4.7.2 projects. On Linux, install Mono and its framework reference assemblies as well. Set `ATT_GAME_PATH` to the directory containing `A Township Tale_Data`, `MelonLoader`, and `Mods`:

```bash
export ATT_GAME_PATH="/path/to/dedicated-server"
bash build.sh
```

Set `DOTNET_BIN` if the .NET SDK executable is not named `dotnet` on your path. You can also build either project directly:

```bash
dotnet build CavePerformance/CavePerformance.csproj -c Release -p:GamePath="/path/to/dedicated-server"
dotnet build ForestPerformance/ForestPerformance.csproj -c Release -p:GamePath="/path/to/dedicated-server"
```

Outputs are `CavePerformance/bin/Release/net472/CavePerformance.dll` and `ForestPerformance/bin/Release/net472/ForestPerformance.dll`.

## Tests

```bash
export ATT_GAME_PATH="/path/to/dedicated-server"
bash CavePerformance/test.sh
bash ForestPerformance/test.sh
```

The tests build both sibling projects and exercise game-method binding, patch composition and rollback, configuration, loading gates, navigation, and cache/reset ordering. The cave compatibility fixtures additionally expect `DailyGarbageCollection.dll` in the server's `Mods` directory; neither release requires it to run. Run the offline tests using Mono. Unity internal-call warnings can appear outside Unity; check the test exit status and PASS messages.

Offline fixtures do not exercise live scenes, physics, clients, or actual NPC combat. They do not establish a measured reduction in live server stalls.

## Source layout

| Files | Responsibility |
| --- | --- |
| `CavePerformance/Core.cs`, `ForestPerformance/Core.cs` | Mod entry points, startup checks, patch installation, updates, and shutdown |
| Each project's `Settings.cs` | Startup configuration defaults, parsing, and validation |
| Each project's `Modules.cs` | Shared lifecycle for embedded components and duplicate standalone-mod checks |
| `CavePerformance/Search.cs` | Worker selection, NPC-to-cave request association, and local nearest-node lookup |
| `CavePerformance/ScanPacing.cs` | Generation scheduling and nonblocking cave scan start |
| Each project's `Loading.cs` | Later-frame gates for complete native saved-entity restoration batches |
| `CavePerformance/WyrmPopulation.cs` | Cave-only wyrm target scaling and balanced rounding |
| `CavePerformance/Modules/CaveNavigationRecovery/` | Completed-scan validation, bounded scan retries, and wander request recovery |
| `ForestPerformance/ForestInstructionBatching.cs` | Native forest instruction batch limits |
| `ForestPerformance/Connectivity.cs` | Conservative local connectivity repair with native fallback |
| `ForestPerformance/Modules/ForestNavigationGuard/` | Overworld graph constraints and rebuilt-tile cutting notifications |
| `ForestPerformance/Modules/ForestResetFix/` | Forest reset/cache task ordering and failure recovery |
| `CavePerformance/Timing.cs`, `ServerTrace.cs`, and embedded `*Diagnostics` components | Optional built-in timing and observations used by the current releases |
| Each project's `TestAccess.cs`, `tests/`, and `test.sh` | Existing regression fixtures and their runner |

The release downloads contain the mod DLL, its documentation, and an example configuration. Standalone diagnostic mods, collected logs, verification reports, debug symbols, game files, and server saves are not bundled.
