#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SITE="$ROOT/site"
PUBLIC="$SITE/public"
OUT="$ROOT/_site"

# Keep both local and CI documentation builds free of framework telemetry.
export NEXT_TELEMETRY_DISABLED=1

if [[ "$(basename "$ROOT")" != "agent-notify" ]]; then
  echo "Refusing to build from unexpected repository root: $ROOT" >&2
  exit 1
fi

rm -rf "$PUBLIC/favicon" "$PUBLIC/schemas"
mkdir -p "$PUBLIC/favicon" "$PUBLIC/schemas"
cp -R "$SITE/favicon/." "$PUBLIC/favicon/"
cp "$ROOT/assets/branding/an.png" "$PUBLIC/an.png"
cp "$ROOT/src/AgentNotify.Protocol/Schemas/arc-0.1.schema.json" "$PUBLIC/schemas/"
cp "$SITE/.nojekyll" "$PUBLIC/.nojekyll"

npm ci --prefix "$SITE" --no-audit --no-fund
npm run typecheck --prefix "$SITE"
npm run build --prefix "$SITE"

rm -rf "$OUT"
mkdir -p "$OUT"
cp -R "$SITE/out/." "$OUT/"

# Preserve the previous .html documentation URLs while canonical links use clean trailing slashes.
while IFS= read -r page; do
  slug="$(basename "$(dirname "$page")")"
  cp "$page" "$OUT/docs/$slug.html"
done < <(find "$OUT/docs" -mindepth 2 -maxdepth 2 -name index.html -type f | sort)

test -s "$OUT/index.html"
test -s "$OUT/404.html"
test -s "$OUT/docs/index.html"
test -s "$OUT/docs/cli/index.html"
test -s "$OUT/docs/api/index.html"
test -s "$OUT/docs/channels/index.html"
test -s "$OUT/docs/arc/index.html"
test -s "$OUT/docs/arc.html"
test -s "$OUT/schemas/arc-0.1.schema.json"
test -n "$(find "$OUT/_next/static" -type f -name '*.css' -print -quit)"
echo "Built Next.js documentation site at $OUT"
