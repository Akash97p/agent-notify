#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DOTNET_EXE="${AGENTNOTIFY_DOTNET_EXE:-/mnt/d/dev/dotnet/dotnet.exe}"
ARTIFACTS="$ROOT/artifacts"
PAYLOAD="$ARTIFACTS/payload"
APP_OUT="$ARTIFACTS/app-publish"
CLI_OUT="$ARTIFACTS/cli-publish"
SETUP_OUT="$ARTIFACTS/setup-publish"

if [[ ! -x "$DOTNET_EXE" ]]; then
  echo "Windows .NET SDK not found at: $DOTNET_EXE" >&2
  exit 1
fi

rm -rf "$PAYLOAD" "$APP_OUT" "$CLI_OUT" "$SETUP_OUT"
mkdir -p "$PAYLOAD" "$APP_OUT" "$CLI_OUT" "$SETUP_OUT"
cd "$ROOT"

PUBLISH_ARGS=(--configuration Release --runtime win-x64 --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false)

"$DOTNET_EXE" publish src/AgentNotify.App/AgentNotify.App.csproj "${PUBLISH_ARGS[@]}" --output "$APP_OUT"
"$DOTNET_EXE" publish src/AgentNotify.Cli/AgentNotify.Cli.csproj "${PUBLISH_ARGS[@]}" --output "$CLI_OUT"

cp "$APP_OUT/AgentNotify.Tray.exe" "$PAYLOAD/AgentNotify.Tray.exe"
cp "$CLI_OUT/agentnotify.exe" "$PAYLOAD/agentnotify.exe"

PAYLOAD_WINDOWS="$(wslpath -w "$PAYLOAD")"
"$DOTNET_EXE" publish src/AgentNotify.Setup/AgentNotify.Setup.csproj "${PUBLISH_ARGS[@]}" \
  -p:PayloadDir="$PAYLOAD_WINDOWS" -p:RequirePayload=true --output "$SETUP_OUT"

cp "$SETUP_OUT/AgentNotifySetup.exe" "$ARTIFACTS/AgentNotifySetup.exe"
python3 /home/akash/.codex/skills/.system/skill-creator/scripts/quick_validate.py distribution/agentnotify

test -s "$ARTIFACTS/AgentNotifySetup.exe"
echo "Created $ARTIFACTS/AgentNotifySetup.exe"
ls -lh "$ARTIFACTS/AgentNotifySetup.exe" "$PAYLOAD/AgentNotify.Tray.exe" "$PAYLOAD/agentnotify.exe"
