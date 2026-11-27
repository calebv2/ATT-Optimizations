#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
: "${ATT_GAME_PATH:?Set ATT_GAME_PATH to the dedicated server installation directory}"
export ATT_GAME_PATH
export ATT_CAVE_PERFORMANCE_DLL="$PWD/../CavePerformance/bin/Release/net472/CavePerformance.dll"
dotnet_path="${DOTNET_BIN:-dotnet}"
"$dotnet_path" build DailyGarbageCollection.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
"$dotnet_path" build ../CavePerformance/CavePerformance.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
"$dotnet_path" build tests/Smoke.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
test_deps="$(mktemp -d)"
trap 'rm -rf "$test_deps"' EXIT
managed="$ATT_GAME_PATH/A Township Tale_Data/Managed"
for f in "$managed"/*.dll; do
 name="${f##*/}"
 case "$name" in mscorlib.dll|System*.dll|netstandard.dll) continue;; esac
 ln -s "$f" "$test_deps/$name"
done
MONO_PATH="$ATT_GAME_PATH/MelonLoader/net35:$test_deps:$ATT_GAME_PATH/UserLibs" mono tests/bin/Release/net472/Smoke.exe
