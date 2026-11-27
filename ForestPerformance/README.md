# Forest Performance 0.2.7

Server-only MelonLoader mod for A Township Tale. It combines forest performance, navigation, and reset/cache fixes in one `ForestPerformance.dll`.

## What it does

- Lowers the inspected native forest instruction batch from ten instructions to one by default. Completed instructions with no result still count toward the limit. Native ordering, results, undo behavior, yields, and exceptions are retained.
- Separates successful dynamic-load completions so at most one complete saved-entity batch is released per frame for this mod. The queue holds 32 pending gates; overflow uses the native continuation. Save/shutdown handling drains pending work.
- Repairs connectivity labels locally for eligible native tile updates when bounded checks prove that old boundary connectivity is preserved and area ownership is unambiguous. Splits, merges, custom/cross-graph cases, active modifiers, uncertain metadata, and other unsupported cases use native full fills. Full scans remain native.
- Rebuilds the navigation ownership index in cooperative main-thread slices and discards stale work on graph changes. An optional outside-connectivity check can prove more cases eligible for a local repair.
- Corrects the overworld-only nearest-node constraint so graph indices above 31 cannot alias through wrapped graph masks.
- Notifies cutting about rebuilt overworld Recast tiles on eligible partial updates, avoiding notifications for every unchanged tile. Full updates and unsupported cases retain native behavior.
- Preserves terrain snapshot arguments for river cache generation, recovers exact failed/canceled cache operations, and waits for final partial cache tasks and writes before reset completion and seed commitment.
- Checks for detached child prefabs during unload. Prepares the native population serializer and definition cache at startup to reduce first-restore stalls.
- Bridge and terrain worker hooks require a recognized ForestRewrite build. Check the startup compatibility report for attachment results.
- Includes optional bounded forest-step and performance observations inside the DLL.

The instruction and loading changes operate between complete native units of work. A single instruction, entity batch, graph operation, or physics step can still take a long time. Connectivity repair preserves equivalent reachability; area label numbers may differ from a full renumbering.

## Requirements and installation

Target: Root.Township **0.0.39.2**, MVID `29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff`, MelonLoader **0.7.3**, Harmony **2.10.2**. Unsupported game assemblies disable the mod.

With the server stopped, put `ForestPerformance.dll` in `Mods`. Remove these replaced standalone DLLs from `Mods` and any loader-scanned subfolders:

- `ForestNavigationGuard.dll`
- `ForestResetFix.dll`
- `ForestStepDiagnostics.dll`

Start the server normally and check for `[Forest_Performance] READY v0.2.7` and component errors. Remove the replaced standalone copies before startup to avoid duplicate hooks.

Installation and ordinary exploration do not request a forest reset. The existing `forestresetfix` console command namespace is retained. Its `status` and `job` commands are read-only. A manual reset remains a maintenance operation with player disconnection, join locks, and a restart after successful completion. The separate ForestSeedUtil mod's seed preference handling is preserved.

## Configuration

The first startup creates `UserData/ForestPerformance/config.txt`. Settings are read at startup; malformed or unsupported values disable initialization.

| Setting | Default | Meaning |
| --- | --- | --- |
| `enabled` | `true` | Enable the mod |
| `navigation_fixes` | `true` | Enable graph-constraint and rebuilt-tile fixes |
| `reset_fixes` | `true` | Enable embedded reset/cache fixes |
| `forest_step_diagnostics` | `true` | Enable embedded step and performance recordings |
| `connectivity_updates` | `true` | Enable conservative local connectivity repair |
| `connectivity_outside_proof` | `false` | Enable the bounded outside-connectivity check |
| `forest_instructions_per_frame` | `1` | Instruction batch limit from `1` to `10`; `0` retains native behavior |
| `forest_generation_budget_ms` | `4` | Shared allowance between forest generation steps |
| `separate_entity_loads` | `true` | Gate complete entity batches onto later frames |
| `rewrite_terrain_masks` | `true` | Allow terrain preparation on verified ForestRewrite builds |
| `population_restore_prewarm` | `true` | Prepare native population save metadata and load definitions at startup |

Set `forest_step_diagnostics=false` before startup to keep the fixes without forest recorders. When enabled, recordings are written under `UserData/ForestPerformance/`, with component sessions below `Diagnostics/ForestStepDiagnostics/`. Timings are inclusive wall time; nested rows must not be added together as independent costs.

## Build, test, and remove

See the repository [build and test instructions](../README.md). Run `bash ForestPerformance/test.sh` from the repository root with `ATT_GAME_PATH` set. The suite builds the sibling Cave Performance source for composition checks.

Set `ATT_FOREST_REWRITE_DLL` to test an optional ForestRewrite build from a separate file. The tests verify terrain preparation on supported builds and confirm that unrecognized builds leave those hooks disabled.

To disable the mod, stop the server normally and remove the DLL or set `enabled=false` before the next startup. No save conversion is performed. Removing a file does not unload a running mod. To restore the old split stack, remove the new DLL and restore the three matching standalone DLLs while the server is stopped.
