#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
: "${ATT_GAME_PATH:?Set ATT_GAME_PATH to the dedicated server installation directory}"
export ATT_GAME_PATH
export ATT_CAVE_PERFORMANCE_DLL="$PWD/bin/Release/net472/CavePerformance.dll"
export ATT_FOREST_PERFORMANCE_DLL="${ATT_FOREST_PERFORMANCE_DLL:-$PWD/../ForestPerformance/bin/Release/net472/ForestPerformance.dll}"
dotnet_path="${DOTNET_BIN:-dotnet}"
"$dotnet_path" build CavePerformance.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
"$dotnet_path" build ../ForestPerformance/ForestPerformance.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
"$dotnet_path" build tests/Tests.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
test_deps="$(mktemp -d)"
trap 'rm -rf "$test_deps"' EXIT
for file in "$ATT_GAME_PATH/A Township Tale_Data/Managed/"*.dll; do
  name="${file##*/}"
  case "$name" in mscorlib.dll|System*.dll|netstandard.dll) continue;; esac
  ln -s "$file" "$test_deps/$name"
done
MONO_PATH="$ATT_GAME_PATH/MelonLoader/net35:$test_deps:$ATT_GAME_PATH/UserLibs:$ATT_GAME_PATH/Mods" mono tests/bin/Release/net472/CavePerformance.Tests.exe
for component in CaveNavigationRecovery CaveStepDiagnostics PathfindingQueueDiagnostics MobCombatDiagnostics; do
  "$dotnet_path" build "tests/$component/Tests.csproj" -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
  MONO_PATH="$PWD/bin/Release/net472:$ATT_GAME_PATH/MelonLoader/net35:$test_deps:$ATT_GAME_PATH/UserLibs:$ATT_GAME_PATH/Mods" mono "tests/$component/bin/Release/net472/$component.Tests.exe"
done
