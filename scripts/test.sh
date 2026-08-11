#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="${AGENTNOTIFY_DOTNET_EXE:-/mnt/d/dev/dotnet/dotnet.exe}"
if [[ ! -x "$DOTNET_EXE" ]]; then
  echo "Windows .NET SDK not found at: $DOTNET_EXE" >&2
  exit 1
fi
cd "$ROOT"
exec "$DOTNET_EXE" test tests/AgentNotify.Tests/AgentNotify.Tests.csproj --configuration Release "$@"
