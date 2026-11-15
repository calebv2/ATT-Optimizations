#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
: "${ATT_GAME_PATH:?Set ATT_GAME_PATH to the dedicated server installation directory}"
dotnet_path="${DOTNET_BIN:-dotnet}"
"$dotnet_path" build CavePerformance/CavePerformance.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
"$dotnet_path" build ForestPerformance/ForestPerformance.csproj -c Release --nologo "-p:GamePath=$ATT_GAME_PATH"
