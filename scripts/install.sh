#!/usr/bin/env sh
# AgentNotify installer for macOS and Linux.
#
#   curl -fsSL https://raw.githubusercontent.com/Akash97p/agent-notify/main/scripts/install.sh | sh
#
# Downloads the release archive for this machine, verifies its SHA-256 against the published
# SHA256SUMS.txt, and installs the agentnotify CLI and the agentnotifyd broker into ~/.local/bin.
#
# Environment:
#   AGENTNOTIFY_VERSION   Release tag to install (default: latest).
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
    base="https://github.com/$REPO/releases/latest/download"
else
    base="https://github.com/$REPO/releases/download/$version"
fi

tmp="$(mktemp -d)"
# shellcheck disable=SC2064
trap "rm -rf '$tmp'" EXIT INT TERM

echo "Downloading $archive…"
fetch "$base/$archive" "$tmp/$archive" || fail "could not download $base/$archive"

# Verifying the checksum is not optional: this script pipes a downloaded binary straight onto PATH.
echo "Verifying checksum…"
fetch "$base/SHA256SUMS.txt" "$tmp/SHA256SUMS.txt" || fail "could not download the checksum file."

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

echo "Installing to $PREFIX…"
mkdir -p "$PREFIX"
tar -xzf "$tmp/$archive" -C "$tmp"
for binary in agentnotify agentnotifyd; do
    install -m 0755 "$tmp/agentnotify-$rid/$binary" "$PREFIX/$binary" 2>/dev/null \
        || { cp "$tmp/agentnotify-$rid/$binary" "$PREFIX/$binary" && chmod 0755 "$PREFIX/$binary"; }
done

echo
echo "Installed:"
echo "  $PREFIX/agentnotify   command-line client"
echo "  $PREFIX/agentnotifyd  broker"
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
