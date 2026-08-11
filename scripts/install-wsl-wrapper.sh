#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
DEST="${HOME}/.local/bin"
mkdir -p "$DEST"
install -m 755 "$ROOT/scripts/agentnotify" "$DEST/agentnotify"
hash -r 2>/dev/null || true
echo "Installed $DEST/agentnotify"
if [[ ":${PATH}:" != *":${DEST}:"* ]]; then
  echo "Add this directory to PATH: export PATH=\"\$HOME/.local/bin:\$PATH\""
fi
