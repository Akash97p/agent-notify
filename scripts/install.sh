#!/usr/bin/env sh
# AgentNotify installer for macOS and Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/Akash97p/agent-notify/main/scripts/install.sh | sh
#
# Downloads the release archive for this machine, verifies its SHA-256 against the published
# portable checksum file, and installs the agentnotify CLI and the agentnotifyd broker into
# ~/.local/bin. macOS archives also install the native quota menu-bar client beside the broker.
#
# Environment:
#   AGENTNOTIFY_VERSION   Release tag to install (default: newest published release).
#   AGENTNOTIFY_PREFIX    Install directory   (default: $HOME/.local/bin).
set -eu

REPO="Akash97p/agent-notify"
PREFIX="${AGENTNOTIFY_PREFIX:-$HOME/.local/bin}"

fail() {
    echo "error: $*" >&2
    exit 1
}

need() {
    command -v "$1" >/dev/null 2>&1 || fail "'$1' is required but was not found."
}

need uname
need mkdir
need tar

if command -v curl >/dev/null 2>&1; then
    fetch() { curl -fsSL "$1" -o "$2"; }
    fetch_stdout() { curl -fsSL "$1"; }
elif command -v wget >/dev/null 2>&1; then
    fetch() { wget -qO "$2" "$1"; }
    fetch_stdout() { wget -qO- "$1"; }
else
    fail "either curl or wget is required."
fi

case "$(uname -s)" in
    Linux)  os=linux ;;
    Darwin) os=osx ;;
    *)      fail "unsupported operating system '$(uname -s)'. AgentNotify supports Linux, macOS, and Windows." ;;
esac

case "$(uname -m)" in
    x86_64|amd64)  arch=x64 ;;
    aarch64|arm64) arch=arm64 ;;
    *)             fail "unsupported architecture '$(uname -m)'. Prebuilt binaries exist for x64 and arm64." ;;
esac

rid="$os-$arch"
archive="agentnotify-$rid.tar.gz"

version="${AGENTNOTIFY_VERSION:-}"
if [ -z "$version" ]; then
    # GitHub's /releases/latest URL excludes prereleases. AgentNotify is currently
    # prerelease-only, so ask the public releases API for the newest published tag.
    version="$(fetch_stdout "https://api.github.com/repos/$REPO/releases?per_page=1" \
        | awk -F '"' '/"tag_name":/ { print $4; exit }')"
    [ -n "$version" ] || fail "could not determine the newest published release."
fi
base="https://github.com/$REPO/releases/download/$version"

tmp="$(mktemp -d)"
# shellcheck disable=SC2064
trap "rm -rf '$tmp'" EXIT INT TERM

echo "Downloading ${archive} from ${version}…"
fetch "$base/$archive" "$tmp/$archive" || fail "could not download $base/$archive"

# Verifying the checksum is not optional: this script pipes a downloaded binary straight onto PATH.
echo "Verifying checksum…"
if ! fetch "$base/SHA256SUMS-portable.txt" "$tmp/SHA256SUMS.txt"; then
    # v0.0.1-alpha.1 predates the separate Windows and portable checksum filenames.
    fetch "$base/SHA256SUMS.txt" "$tmp/SHA256SUMS.txt" \
        || fail "could not download the portable checksum file."
fi

expected="$(grep " $archive\$" "$tmp/SHA256SUMS.txt" | awk '{print $1}')"
[ -n "$expected" ] || fail "no checksum published for $archive."

if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$tmp/$archive" | awk '{print $1}')"
elif command -v shasum >/dev/null 2>&1; then
    actual="$(shasum -a 256 "$tmp/$archive" | awk '{print $1}')"
else
    fail "neither sha256sum nor shasum is available; refusing to install unverified binaries."
fi

[ "$actual" = "$expected" ] || fail "checksum mismatch for $archive. Expected $expected, got $actual."

echo "Installing to ${PREFIX}…"
mkdir -p "$PREFIX"
tar -xzf "$tmp/$archive" -C "$tmp"
binaries="agentnotify agentnotifyd"
if [ "$os" = "osx" ] && [ -f "$tmp/agentnotify-$rid/agentnotify-menubar" ]; then
    binaries="$binaries agentnotify-menubar"
fi
for binary in $binaries; do
    install -m 0755 "$tmp/agentnotify-$rid/$binary" "$PREFIX/$binary" 2>/dev/null \
        || { cp "$tmp/agentnotify-$rid/$binary" "$PREFIX/$binary" && chmod 0755 "$PREFIX/$binary"; }
done

# macOS refuses to run these as published. The release is cross-built on a Linux
# runner, so the adhoc signature baked in there is not one this kernel accepts,
# and the process is SIGKILLed the instant it starts — exit 137, no output, no
# log line, nothing to search for. Re-signing adhoc locally is what makes it
# runnable; it grants no trust the binary did not already have.
if [ "$os" = "osx" ] && command -v codesign >/dev/null 2>&1; then
    for binary in $binaries; do
        codesign --force --sign - "$PREFIX/$binary" >/dev/null 2>&1 \
            || echo "Warning: could not re-sign $binary; if it exits immediately, run: codesign --force --sign - $PREFIX/$binary" >&2
    done
fi

echo
echo "Installed:"
echo "  $PREFIX/agentnotify   command-line client"
echo "  $PREFIX/agentnotifyd  broker"
if [ "$os" = "osx" ] && [ -x "$PREFIX/agentnotify-menubar" ]; then
    echo "  $PREFIX/agentnotify-menubar  native quota menu bar (started by the broker)"
fi
echo

case ":$PATH:" in
    *":$PREFIX:"*) ;;
    *) echo "Add $PREFIX to your PATH, then open a new shell:"
       echo "  export PATH=\"$PREFIX:\$PATH\""
       echo ;;
esac

echo "Start the broker:"
echo "  agentnotifyd &"
echo "Then check it:"
echo "  agentnotify health"
