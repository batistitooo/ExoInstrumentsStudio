#!/usr/bin/env bash
# ExoInstruments Studio. Builds the engine against the mod's Core/ and Session/ in place,
# then serves the interface. Nothing in the mod tree is written to.
set -euo pipefail

cd "$(dirname "$0")"

PORT="${PORT:-5227}"

echo "building…"
dotnet build Engine/ExoStudio.csproj -v q --nologo

exec dotnet run --project Engine/ExoStudio.csproj --no-build -- --port "$PORT" "$@"
