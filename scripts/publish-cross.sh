#!/usr/bin/env bash
# Publishes the portable AgentNotify binaries — the agentnotify CLI and the agentnotifyd broker —
# as self-contained single files for every supported runtime, then writes SHA-256 checksums. macOS
# archives also include the native AppKit quota menu-bar client.
#
# The Windows tray application and its installer are NOT built here; they are Windows-only and are
# produced by scripts/package.sh.
#
# Usage:
#   ./scripts/publish-cross.sh                 # every runtime
#   ./scripts/publish-cross.sh linux-x64       # one or more specific runtimes
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/artifacts/cross"

# Prefer a native dotnet when one exists (Linux and macOS developers); fall back to the Windows SDK
# this repository is normally built with from WSL.
if [[ -n "${AGENTNOTIFY_DOTNET_EXE:-}" ]]; then
  DOTNET="$AGENTNOTIFY_DOTNET_EXE"
elif command -v dotnet >/dev/null 2>&1; then
  DOTNET="$(command -v dotnet)"
elif [[ -x /mnt/d/dev/dotnet/dotnet.exe ]]; then
  DOTNET=/mnt/d/dev/dotnet/dotnet.exe
else
  echo "No .NET 10 SDK found. Install dotnet, or set AGENTNOTIFY_DOTNET_EXE." >&2
  exit 1
fi

ALL_RUNTIMES=(win-x64 linux-x64 linux-arm64 osx-x64 osx-arm64)
if [[ $# -gt 0 ]]; then
  RUNTIMES=("$@")
else
  RUNTIMES=("${ALL_RUNTIMES[@]}")
fi

if [[ " ${RUNTIMES[*]} " == *" osx-"* && "$(uname -s)" != "Darwin" ]]; then
  echo "macOS archives include a native AppKit executable and must be packaged on macOS." >&2
  echo "Run this script on macOS, or request only win/linux runtimes." >&2
  exit 1
fi

PROJECTS=(
  "src/AgentNotify.Cli/AgentNotify.Cli.csproj"
  "src/AgentNotify.Host/AgentNotify.Host.csproj"
)

# Fail before any long publish rather than partway through a multi-runtime run.
command -v tar >/dev/null 2>&1 || { echo "'tar' is required." >&2; exit 1; }

rm -rf "$OUT"
mkdir -p "$OUT"
cd "$ROOT"

for rid in "${RUNTIMES[@]}"; do
  # Reject an unknown runtime rather than producing an archive nobody can run.
  if [[ ! " ${ALL_RUNTIMES[*]} " == *" $rid "* ]]; then
    echo "Unknown runtime '$rid'. Supported: ${ALL_RUNTIMES[*]}" >&2
    exit 1
  fi

  stage="$OUT/agentnotify-$rid"
  mkdir -p "$stage"
  echo "==> $rid"

  for project in "${PROJECTS[@]}"; do
    "$DOTNET" publish "$project" \
      --configuration Release \
      --runtime "$rid" \
      --self-contained true \
      -p:PublishSingleFile=true \
      -p:IncludeNativeLibrariesForSelfExtract=true \
      -p:EnableCompressionInSingleFile=true \
      -p:DebugType=none \
      --output "$stage" \
      --nologo --verbosity quiet
  done

  if [[ "$rid" == osx-* ]]; then
    arch="${rid#osx-}"
    [[ "$arch" == "x64" ]] && arch=x86_64
    menu_stage="$OUT/menu-$rid"
    "$ROOT/scripts/build-macos-menu-bar.sh" "$menu_stage" "$arch"
    cp "$menu_stage/agentnotify-menubar" "$stage/agentnotify-menubar"
    rm -rf "$menu_stage"
  fi

  # Ship the licence and the agent skill next to the binaries so a downloaded archive is complete.
  cp "$ROOT/LICENSE" "$stage/LICENSE"
  cp "$ROOT/THIRD_PARTY_NOTICES.md" "$stage/THIRD_PARTY_NOTICES.md"
  cp "$ROOT/distribution/agentnotify/SKILL.md" "$stage/SKILL.md"

  rm -f "$stage"/*.pdb

  if [[ "$rid" == win-* ]]; then
    # Windows users expect a zip. Prefer the zip tool, but fall back to python so the script does
    # not depend on a package that is missing from many minimal Linux and WSL installs.
    if command -v zip >/dev/null 2>&1; then
      ( cd "$OUT" && zip -qr "agentnotify-$rid.zip" "agentnotify-$rid" )
    elif command -v python3 >/dev/null 2>&1; then
      ( cd "$OUT" && python3 -c "
import pathlib, sys, zipfile
root = pathlib.Path(sys.argv[1])
with zipfile.ZipFile(root.with_suffix('.zip'), 'w', zipfile.ZIP_DEFLATED) as archive:
    for path in sorted(root.rglob('*')):
        if path.is_file():
            archive.write(path, path.relative_to(root.parent))
" "agentnotify-$rid" )
    else
      echo "Creating the $rid archive needs either 'zip' or 'python3'. Install one, or pass only non-Windows runtimes." >&2
      exit 1
    fi
  else
    chmod +x "$stage/agentnotify" "$stage/agentnotifyd" "$stage"/agentnotify-menubar 2>/dev/null || true
    ( cd "$OUT" && tar -czf "agentnotify-$rid.tar.gz" "agentnotify-$rid" )
  fi

  rm -rf "$stage"
done

# Only checksum the archives that this run actually produced; an unmatched glob would otherwise
# abort the script under `set -euo pipefail` after the archives were already built.
# GNU find -printf, xargs -r, and sha256sum are all missing on macOS, where developers also run this.
(
  cd "$OUT"
  shopt -s nullglob
  archives=( *.tar.gz *.zip )
  if (( ${#archives[@]} )); then
    if command -v sha256sum >/dev/null 2>&1; then
      sha256sum "${archives[@]}"
    else
      shasum -a 256 "${archives[@]}"
    fi | sort -k 2 > SHA256SUMS.txt
  fi
)

echo
echo "Archives in $OUT:"
ls -1 "$OUT"
