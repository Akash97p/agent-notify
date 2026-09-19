#!/bin/sh
set -eu

root="$(CDPATH= cd -- "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT INT TERM

fake_bin="$work/bin"
install_root="$work/install"
request_log="$work/requests.log"
mkdir -p "$fake_bin"

cat > "$fake_bin/uname" <<'EOF'
#!/bin/sh
case "${1:-}" in
    -s) printf 'Darwin\n' ;;
    -m) printf 'x86_64\n' ;;
    *)  printf 'Darwin\n' ;;
esac
EOF

cat > "$fake_bin/curl" <<'EOF'
#!/bin/sh
set -eu
url=
output=
while [ "$#" -gt 0 ]; do
    case "$1" in
        -o) shift; output="$1" ;;
        -*) ;;
        *) url="$1" ;;
    esac
    shift
done

printf '%s\n' "$url" >> "$INSTALL_TEST_REQUEST_LOG"
case "$url" in
    https://api.github.com/repos/Akash97p/agent-notify/releases?per_page=1)
        printf '[{"tag_name":"v9.8.7-test"}]\n'
        ;;
    */agentnotify-osx-x64.tar.gz)
        printf 'fake portable archive\n' > "$output"
        ;;
    */SHA256SUMS-portable.txt)
        if [ "${INSTALL_TEST_LEGACY:-0}" = 1 ]; then
            exit 22
        fi
        if command -v sha256sum >/dev/null 2>&1; then
            hash="$(printf 'fake portable archive\n' | sha256sum | awk '{print $1}')"
        else
            hash="$(printf 'fake portable archive\n' | shasum -a 256 | awk '{print $1}')"
        fi
        printf '%s  agentnotify-osx-x64.tar.gz\n' "$hash" > "$output"
        ;;
    */SHA256SUMS.txt)
        if command -v sha256sum >/dev/null 2>&1; then
            hash="$(printf 'fake portable archive\n' | sha256sum | awk '{print $1}')"
        else
            hash="$(printf 'fake portable archive\n' | shasum -a 256 | awk '{print $1}')"
        fi
        printf '%s  agentnotify-osx-x64.tar.gz\n' "$hash" > "$output"
        ;;
    *)
        printf 'unexpected URL: %s\n' "$url" >&2
        exit 22
        ;;
esac
EOF

cat > "$fake_bin/tar" <<'EOF'
#!/bin/sh
set -eu
destination=
while [ "$#" -gt 0 ]; do
    if [ "$1" = -C ]; then
        shift
        destination="$1"
    fi
    shift
done
[ -n "$destination" ]
mkdir -p "$destination/agentnotify-osx-x64"
printf 'cli\n' > "$destination/agentnotify-osx-x64/agentnotify"
printf 'broker\n' > "$destination/agentnotify-osx-x64/agentnotifyd"
printf 'menu bar\n' > "$destination/agentnotify-osx-x64/agentnotify-menubar"
EOF

chmod +x "$fake_bin/uname" "$fake_bin/curl" "$fake_bin/tar"

output="$(
    PATH="$fake_bin:$PATH" \
    INSTALL_TEST_REQUEST_LOG="$request_log" \
    AGENTNOTIFY_PREFIX="$install_root" \
    /bin/sh "$root/scripts/install.sh"
)"
printf '%s\n' "$output" | grep -q 'from v9.8.7-test'
test -x "$install_root/agentnotify"
test -x "$install_root/agentnotifyd"
test -x "$install_root/agentnotify-menubar"
grep -q '/releases/download/v9.8.7-test/SHA256SUMS-portable.txt$' "$request_log"

: > "$request_log"
INSTALL_TEST_LEGACY=1 \
PATH="$fake_bin:$PATH" \
INSTALL_TEST_REQUEST_LOG="$request_log" \
AGENTNOTIFY_VERSION=v0.0.1-alpha.1 \
AGENTNOTIFY_PREFIX="$install_root" \
/bin/sh "$root/scripts/install.sh" >/dev/null
grep -q '/releases/download/v0.0.1-alpha.1/SHA256SUMS-portable.txt$' "$request_log"
grep -q '/releases/download/v0.0.1-alpha.1/SHA256SUMS.txt$' "$request_log"

printf 'install.sh regression checks passed\n'
