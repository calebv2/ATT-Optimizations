# ATT Optimizations

Server-side performance and navigation mods for **A Township Tale**. Install the modules you want on the dedicated server.

| Mod | Version | Purpose | Download |
| --- | --- | --- | --- |
| [Cave Performance](CavePerformance/README.md) | 0.2.5 | Cave-local pathfinding, scan recovery, generation and load pacing, and Wyrm and Turabada targets | [Download ZIP](https://github.com/calebv2/ATT-Optimizations/raw/main/downloads/CavePerformance-0.2.5.zip) |
| [Forest Performance](ForestPerformance/README.md) | 0.2.7 | Navigation tile fixes, sliced ownership indexing, forest load pacing, and population prewarm | [Download ZIP](https://github.com/calebv2/ATT-Optimizations/raw/main/downloads/ForestPerformance-0.2.7.zip) |
| [Daily Garbage Collection](DailyGarbageCollection/README.md) | 0.1.0 | Replaces two repeating forced-collection paths with one daily request at 6 AM Eastern | [Download ZIP](https://github.com/calebv2/ATT-Optimizations/raw/main/downloads/DailyGarbageCollection-0.1.0.zip) |

## Compatibility

These builds target the dedicated server's **Root.Township 0.0.39.2**, assembly MVID `29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff`, with **MelonLoader 0.7.3** and **Harmony 2.10.2**. They check the game assembly at startup and disable themselves for an unsupported build. Compatibility with other server builds is not established.

## Install

1. Download the ZIP for each mod you want.
2. Stop the server normally and back up your existing mods and configuration.
3. Copy the ZIP's `Mods/*.dll` into the server's `Mods` directory.
4. Remove the replaced standalone DLLs listed in each mod's README, including copies in loader-scanned subfolders. Their components are already embedded in the new DLLs.
5. Start the server normally. Check the server log for `[Cave_Performance] READY v0.2.5`, `[Forest_Performance] READY v0.2.7`, or `Daily GC active` for each installed mod.

Cave and Forest Performance create `UserData/<ModName>/config.txt` on first startup. Edit settings while the server is stopped and keep any existing settings you want. Daily Garbage Collection uses the Eastern schedule and keeps a marker under `UserData/DailyGarbageCollection` to avoid repeating the request after a restart.

These mods do not hot-load or restart the server. Installing Forest Performance does not request a forest reset. A single native generation instruction or saved-entity batch can still take a long time; the pacing settings are not hard frame-time limits.

## Build from source

The repository contains all three modules' source, embedded runtime components, and regression fixtures. Use the game and loader assemblies from your own server installation.

Use a .NET SDK capable of building .NET Framework 4.7.2 projects. On Linux, install Mono and its framework reference assemblies as well. Set `ATT_GAME_PATH` to the directory containing `A Township Tale_Data`, `MelonLoader`, and `Mods`:

```bash
export ATT_GAME_PATH="/path/to/dedicated-server"
bash build.sh
```

Set `DOTNET_BIN` if you use a different .NET SDK executable. You can also build a project directly.

```bash
dotnet build CavePerformance/CavePerformance.csproj -c Release -p:GamePath="/path/to/dedicated-server"
dotnet build ForestPerformance/ForestPerformance.csproj -c Release -p:GamePath="/path/to/dedicated-server"
dotnet build DailyGarbageCollection/DailyGarbageCollection.csproj -c Release -p:GamePath="/path/to/dedicated-server"
```

Each project's DLL is under its `bin/Release/net472` directory.

## Tests

```bash
export ATT_GAME_PATH="/path/to/dedicated-server"
bash CavePerformance/test.sh
bash ForestPerformance/test.sh
bash DailyGarbageCollection/test.sh
```

The runners build sibling projects as needed and exercise game-method binding, patch composition, rollback, configuration, loading gates, navigation, cache ordering, and the daily schedule. Run the offline tests using Mono. Unity internal-call warnings can appear outside Unity. Check the exit status and PASS messages.

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
| `CavePerformance/CavePopulationTargets.cs` | Wyrm and Turabada target scaling and balanced rounding |
| `CavePerformance/Modules/CaveNavigationRecovery/` | Completed-scan validation, bounded scan retries, and wander request recovery |
| `ForestPerformance/ForestInstructionBatching.cs` | Native forest instruction batch limits |
| `ForestPerformance/Connectivity.cs` | Conservative local connectivity repair with native fallback |
| `ForestPerformance/PopulationRestorePrewarm.cs` | Startup preparation for native population serializers and definition assets |
| `ForestPerformance/Modules/ForestNavigationGuard/` | Overworld graph constraints and rebuilt-tile cutting notifications |
| `ForestPerformance/Modules/ForestResetFix/` | Forest reset/cache task ordering and failure recovery |
| `CavePerformance/Timing.cs`, `ServerTrace.cs`, and embedded `*Diagnostics` components | Optional built-in timing and observations used by the current releases |
| Each project's `TestAccess.cs`, `tests/`, and `test.sh` | Existing regression fixtures and their runner |
| `DailyGarbageCollection/Patch.cs` and `DailySchedule.cs` | Suppression of two explicit collection paths and the daily schedule |
| `PerformanceCompatibility/` | Shared startup reporting code linked into the cave and forest DLLs |

The [downloads directory](downloads/) contains the three versioned ZIPs and their SHA-256 checksums. Each ZIP contains its server DLL and documentation. Cave and Forest Performance also include example configurations.
