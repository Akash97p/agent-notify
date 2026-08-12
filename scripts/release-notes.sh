#!/usr/bin/env bash
# Builds the GitHub Release description for a tag by extracting that version's section from
# CHANGELOG.md and appending the install and compare links.
#
# Usage: ./scripts/release-notes.sh v0.0.2-alpha.1 [previous-tag] > notes.md
#
# Exits non-zero when the changelog has no section for the version, so a release cannot be
# published with an empty or wrong description.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CHANGELOG="$ROOT/CHANGELOG.md"
REPO="Akash97p/agent-notify"

tag="${1:-}"
[ -n "$tag" ] || { echo "usage: release-notes.sh <tag> [previous-tag]" >&2; exit 1; }

version="${tag#v}"
previous="${2:-}"

[ -f "$CHANGELOG" ] || { echo "CHANGELOG.md not found." >&2; exit 1; }

# Take everything between this version's heading and the next version heading.
section="$(awk -v want="## [$version]" '
    index($0, want) == 1 { capture = 1; next }
    capture && /^## \[/ { exit }
    capture { print }
' "$CHANGELOG")"

# Trim leading and trailing blank lines.
section="$(printf '%s\n' "$section" | sed -e '/./,$!d' -e :a -e '/^\n*$/{$d;N;ba' -e '}')"

if [ -z "$section" ]; then
    echo "CHANGELOG.md has no '## [$version]' section. Add one before tagging." >&2
    exit 1
fi

prerelease_note=""
case "$tag" in
    *-*) prerelease_note=$'> **This is a prerelease.** It is intended for testing and evaluation: expect incomplete\n> features, breaking changes, and unsigned binaries. The mature `1.0.0` release is reserved for a\n> future stable milestone.\n\n' ;;
esac

printf '%s' "$prerelease_note"
printf '%s\n\n' "$section"

cat <<EOF
## Install

**Windows** — download \`AgentNotifySetup.exe\` below and run it. No separate .NET runtime is needed.
The installer is not Authenticode-signed, so Windows may show a SmartScreen prompt.

**macOS and Linux**

\`\`\`sh
curl -fsSL https://raw.githubusercontent.com/$REPO/main/scripts/install.sh | sh
\`\`\`

This verifies the published SHA-256 checksum before installing \`agentnotify\` and \`agentnotifyd\`
into \`~/.local/bin\`. To install this exact version, set \`AGENTNOTIFY_VERSION=$tag\`.
See [Installing on macOS and Linux](https://github.com/$REPO/blob/main/docs/INSTALLATION_UNIX.md).

## Verifying downloads

\`SHA256SUMS.txt\` covers the Windows installer. \`SHA256SUMS-portable.txt\` covers the portable
archives. Check a download before running it:

\`\`\`sh
sha256sum -c SHA256SUMS-portable.txt --ignore-missing
\`\`\`

## Documentation

[Getting started](https://github.com/$REPO#readme) ·
[CLI reference](https://github.com/$REPO/blob/main/docs/CLI.md) ·
[API](https://github.com/$REPO/blob/main/docs/API.md) ·
[Configuration](https://github.com/$REPO/blob/main/docs/CONFIGURATION.md) ·
[Troubleshooting](https://github.com/$REPO/blob/main/docs/TROUBLESHOOTING.md) ·
[What is verified](https://github.com/$REPO/blob/main/docs/VERIFICATION.md)
EOF

if [ -n "$previous" ]; then
    printf '\n**Full commit log**: https://github.com/%s/compare/%s...%s\n' "$REPO" "$previous" "$tag"
fi
