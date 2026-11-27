# Daily Garbage Collection 0.1.0

Copy `DailyGarbageCollection.dll` from the download's `Mods` folder into
the dedicated server's `Mods` folder while the server is stopped. Start
normally and look for `Daily GC active` in the startup log.

Server-only MelonLoader mod for the inspected Root.Township assembly (MVID `29bc0e84-8eb2-4dfa-b551-ea0ca73b66ff`). It applies two targeted Harmony patches at startup:

1. It suppresses `GarbageCollectTimer.Start`, which only starts the game's repeating headless-server explicit collection coroutine.
2. It replaces the single parameterless `GC.Collect()` call inside the `AstarPath.ScanAsync` iterator with a counter-only no-op. This affects scans of any graph that execute that exact iterator, including caves; it does not suppress the scan itself.

It requests one explicit full `GC.Collect()` at 06:00 America/New_York each day while the server is running. Time zone resolution tries IANA `America/New_York`, then Windows `Eastern Standard Time`, and uses the installed zone's daylight-saving rules. The scheduler checks on the game update thread, so it runs at the first available update, not with hard real-time precision. It accepts a delayed update/start during 06:00:00–06:04:59 and skips the day if the server starts later. It never runs a missed 06:00 job at midday.

A marker at `UserData/DailyGarbageCollection/last-request-date.txt` is written before the collection call. This avoids repeat requests after a restart in the same window. A crash after writing the marker but before the call completes can skip that day's request. Do not delete the marker to force a rerun. Logs identify successful activation, time zone, request and return wall time. The wall time is not necessarily the GC pause duration.

**Automatic runtime garbage collection stays enabled.** The mod does not intercept all `GC.Collect` calls globally: other explicit callers, such as administrative commands or the game's `ApplicationManager.CleanMemory`, can still request collections. Therefore this schedules one explicit daily request in place of the two named game paths; it cannot guarantee exactly one actual collection per day. It does not clean or delete game items, chunk data, or world files.

The mod checks the game MVID, timer and iterator signatures, exact scan-call count, persisted marker validity, time zone availability and initial write access. If startup validation or patching fails, it removes its own patches and logs an error; if rollback itself fails, it explicitly reports that the policy is uncertain. If the game's collection timer was already started before this mod installs, startup refuses the partial change. Watch the server log for `Daily GC active` after startup; a successful offline test does not prove Unity integration, time-zone registry support or a successful 06:00 call in the live server.

This changes pause/memory behavior. Monitor managed heap, process/container RSS, available memory, swap, update stalls and player load for at least a day, especially during cave activity. If the heap or RSS grows persistently, if automatic GCs still pause the server, or if the daily request causes an unacceptable stall, revise or roll back. Stopping the timer and scan requests does not solve leaks or remove every GC pause.

## Build, test and rollback

Set `ATT_GAME_PATH` as described in the repository [build instructions](../README.md), then run `bash DailyGarbageCollection/test.sh` from the repository root. The offline suite checks installed method metadata and patch attachment, coexistence with the cave diagnostics built from source, unchanged scan return and exception behavior, timer restoration, Eastern standard/daylight time conversion, and marker behavior. Standalone host Mono can log unresolved Unity internal-call warnings.

Remove `game-source/Mods/DailyGarbageCollection.dll` before a later normal startup to revert to the game's original behavior. Removing it while the process is running does not unload its patches. Keep the marker and diagnostic logs for investigation. No server restart is performed by the build or staging process.
