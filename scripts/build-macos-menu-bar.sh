#!/usr/bin/env bash
# Builds the native AppKit quota menu-bar executable. Run on macOS with the Command Line Tools.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SOURCE="$ROOT/src/AgentNotify.MenuBar/main.swift"
OUT="${1:-$ROOT/artifacts/macos-menu-bar}"
ARCH="${2:-$(uname -m)}"
MIN_VERSION="${AGENTNOTIFY_MACOS_MIN_VERSION:-13.0}"

case "$ARCH" in
  x86_64) target="x86_64-apple-macos${MIN_VERSION}" ;;
  arm64) target="arm64-apple-macos${MIN_VERSION}" ;;
  *) echo "Unsupported macOS menu-bar architecture '$ARCH'." >&2; exit 1 ;;
esac

command -v xcrun >/dev/null 2>&1 || { echo "xcrun is required to build the macOS menu bar." >&2; exit 1; }
mkdir -p "$OUT"
xcrun swiftc \
  -swift-version 5 \
  -O \
  -target "$target" \
  -sdk "$(xcrun --sdk macosx --show-sdk-path)" \
  "$SOURCE" \
  -o "$OUT/agentnotify-menubar"
chmod 0755 "$OUT/agentnotify-menubar"

# Release archives are not Developer ID signed/notarized yet, but every Mach-O still needs a valid
# local ad-hoc signature. The installer repeats this after download so copied/quarantined files work.
command -v codesign >/dev/null 2>&1 || { echo "codesign is required to package the macOS menu bar." >&2; exit 1; }
codesign --force --sign - "$OUT/agentnotify-menubar"
codesign --verify --strict "$OUT/agentnotify-menubar"
