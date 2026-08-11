#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="${AGENTNOTIFY_DOTNET_EXE:-/mnt/d/dev/dotnet/dotnet.exe}"
OUT="$ROOT/artifacts/cli"
if [[ ! -x "$DOTNET_EXE" ]]; then
  echo "Windows .NET SDK not found at: $DOTNET_EXE" >&2
  exit 1
fi
mkdir -p "$OUT"
cd "$ROOT"
exec "$DOTNET_EXE" publish src/AgentNotify.Cli/AgentNotify.Cli.csproj \
  --configuration Release --runtime win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true --output "$OUT" "$@"
