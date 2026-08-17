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
# The page template is an input to the generator, not a published page.
rm -rf "$OUT/templates"
cp "$ROOT/assets/branding/an.png" "$OUT/an.png"
# render documentation pages (dependency-free Python)
python3 "$ROOT/scripts/render-docs.py" "$ROOT" "$OUT"
test -s "$OUT/index.html"
test -s "$OUT/docs/index.html"
test -s "$OUT/docs/cli.html"
test -s "$OUT/docs/api.html"
test -s "$OUT/docs/channels.html"
echo "Built documentation site at $OUT"

