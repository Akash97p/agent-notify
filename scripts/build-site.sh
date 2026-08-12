#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
OUT="$ROOT/_site"

if [[ "$(basename "$ROOT")" != "agent-notify" ]]; then
  echo "Refusing to build from unexpected repository root: $ROOT" >&2
  exit 1
fi

rm -rf "$OUT"
mkdir -p "$OUT"
cp -R "$ROOT/site/." "$OUT/"
cp "$ROOT/assets/branding/an.png" "$OUT/an.png"
test -s "$OUT/index.html"
echo "Built documentation site at $OUT"

