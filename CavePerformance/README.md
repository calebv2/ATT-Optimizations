# Cave Performance 0.2.5

Server-only MelonLoader mod for A Township Tale. It combines cave performance changes and navigation recovery in one `CavePerformance.dll`.

## What it does

- Selects two native pathfinding workers at startup by default. Use `path_workers=0` for the game's selection, or select one worker explicitly.
- Associates native NPC path requests with their cave and searches that cave's completed navigation graph first. The association covers the inspected Agent/Walker relationship, native directional wandering, initial Walker nearest-node lookup, and supported same-layer navigation. Unknown owners, custom requests, invalid graphs, and unsuccessful local searches use native fallback behavior.
- Shares a 4 ms generation target between existing rendering iterator steps, deferring later work after the target is used. Individual native calls remain indivisible.
- Starts cave scans using a separate nonblocking path-queue lock while workers finish. It yields during that wait and releases only its own extra lock. The scan's own Unity work remains on its native execution path.
- Separates successful dynamic-load completions so at most one complete saved-entity batch is released per frame for this mod. The queue holds 32 pending gates; overflow continues through the native path. Save/shutdown handling drains pending work.
- Shares admission between static and generated cave scans, validates navigation completion, and retries failed scans up to three attempts, five seconds apart. It clears stale failed wander state and allows up to two delayed retries of the original request.
- Defaults eligible Wyrm targets to 75% of their normal value. Regular Turabada targets on generated layers have a separate multiplier. Balanced rounding handles small populations, and existing creatures remain in place.
- Includes optional bounded timing, cave-step, path-queue, and combat observations. Those components are compiled into the DLL; separate diagnostic mod downloads are unnecessary.

Local searches retain native constraints and use full searches when eligibility checks fail. The loading gates preserve original failures and release complete batches; they do not split entity restoration across frames. A native instruction, scan step, or entity batch can still exceed the generation target.

## Requirements and installation

Target: Root.Township **0.0.39.2**, MVID `29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff`, MelonLoader **0.7.3**, Harmony **2.10.2**. Unsupported game assemblies disable the mod.

With the server stopped, put `CavePerformance.dll` in `Mods`. Remove these replaced standalone DLLs from `Mods` and any loader-scanned subfolders:

- `CaveNavigationRecovery.dll`
- `CaveStepDiagnostics.dll`
- `PathfindingQueueDiagnostics.dll`
- `MobCombatDiagnostics.dll`

Start the server normally and check for `[Cave_Performance] READY v0.2.5` and component errors. Remove the replaced standalone copies before startup to avoid duplicate hooks.

## Configuration

The first startup creates `UserData/CavePerformance/config.txt`. Settings are read at startup; malformed or unsupported values disable initialization.

| Setting | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Enable the mod |
| `path_workers` | `2` | `0` for native selection; otherwise `1` or `2` |
| `cave_local_search` | `true` | Prefer the owning cave's completed graph for eligible requests |
| `generation_ms` | `4` | Shared target between generation steps; `0` uses native allowance; maximum `16` |
| `separate_entity_loads` | `true` | Gate complete entity batches onto later frames |
| `fine_timing` | `true` | Record bounded detailed timings |
| `cave_wyrm_multiplier` | `0.75` | Wyrm target multiplier from `0` to `1`; `1` restores the native calculation |
| `cave_turabada_multiplier` | `1` | Regular generated-layer Turabada multiplier from `0` to `1`. Set `0.75` for a 25% reduction |
| `navigation_recovery` | `true` | Enable embedded scan/wander recovery |
| `cave_step_diagnostics` | `true` | Enable embedded cave-step observations |
| `pathfinding_queue_diagnostics` | `true` | Enable embedded path-queue observations |
| `mob_combat_diagnostics` | `true` | Enable embedded cave combat observations |
| `cave_diagnostic_min_layer` | `90` | Nonnegative request/combat recorder floor; does not restrict fixes or wyrm policy |
| `async_cave_scan_start` | `true` | Yield while obtaining the extra scan-start queue lock |

To retain the fixes without the diagnostic writers, set `fine_timing`, `cave_step_diagnostics`, `pathfinding_queue_diagnostics`, and `mob_combat_diagnostics` to `false` before startup. Keep `navigation_recovery=true` to retain navigation recovery.

When enabled, recordings are written under `UserData/CavePerformance/`, with embedded component sessions below `Diagnostics/<ComponentName>/`. Timings are inclusive wall time; nested rows must not be added together as independent costs. Recorded packet preparation or send timing does not prove client delivery.

## Build, test, and remove

See the repository [build and test instructions](../README.md). Run `bash CavePerformance/test.sh` from the repository root with `ATT_GAME_PATH` set. The runner builds Forest Performance and Daily Garbage Collection from this repository for composition checks.

To disable the mod, stop the server normally and remove the DLL or set `enabled=false` before the next startup. No save conversion is performed. Removing a file does not unload a running mod. If restoring the old split stack, restore the previous cave performance DLL and its matching standalone components together.

Do not combine this recovery stack with the default cave wrapper in CheetosServerOptimization 0.2.0; that combination has a known cancellation-cleanup incompatibility.
