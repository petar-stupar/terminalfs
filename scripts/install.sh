#!/bin/sh
# Install terminalfs from a GitHub release.
#
#   curl -fsSL https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.sh | sh
#
#   --version v0.1.0   a particular release; the latest by default
#   --bin-dir DIR      where to put the binary; ~/.local/bin by default
#
# Nothing here needs root unless you point --bin-dir somewhere that does.
set -eu

REPO="petar-stupar/terminalfs"
VERSION="${TERMINALFS_VERSION:-latest}"
BIN_DIR="${TERMINALFS_BIN_DIR:-$HOME/.local/bin}"

# Read from the text below and not from the file: the documented way to run this is through a
# pipe, where "$0" is the shell itself and there is no script for sed to read the header out of.
usage() {
    cat <<'USAGE'
Install terminalfs from a GitHub release.

  curl -fsSL https://raw.githubusercontent.com/petar-stupar/terminalfs/main/scripts/install.sh | sh

  --version v0.1.0   a particular release; the latest by default
  --bin-dir DIR      where to put the binary; ~/.local/bin by default

Nothing here needs root unless you point --bin-dir somewhere that does.
Through a pipe, pass arguments after -s --:

  curl -fsSL .../install.sh | sh -s -- --version v0.1.0
USAGE
}

while [ $# -gt 0 ]; do
    case "$1" in
        --version) VERSION="${2:?--version needs a value}"; shift 2 ;;
        --bin-dir) BIN_DIR="${2:?--bin-dir needs a value}"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "install.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
done

fail() { echo "install.sh: $*" >&2; exit 1; }

need() { command -v "$1" >/dev/null 2>&1 || fail "$1 is required and was not found"; }

need curl
need tar

# --- which build ---------------------------------------------------------------------------

case "$(uname -s)" in
    Darwin) os=osx ;;
    Linux)  os=linux ;;
    *) fail "$(uname -s) has no build here; Windows has scripts/install.ps1, and everything else
        can be built from source with 'dotnet publish'" ;;
esac

case "$(uname -m)" in
    arm64|aarch64) arch=arm64 ;;
    x86_64|amd64)  arch=x64 ;;
    *) fail "$(uname -m) has no build here" ;;
esac

rid="$os-$arch"

# --- which release -------------------------------------------------------------------------

if [ "$VERSION" = latest ]; then
    # The API answer is JSON and jq is not assumed, so the tag is read out of it directly.
    VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" \
        | sed -n 's/.*"tag_name": *"\([^"]*\)".*/\1/p' | head -1)"
    [ -n "$VERSION" ] || fail "could not find the latest release of $REPO"
fi

case "$VERSION" in v*) tag="$VERSION" ;; *) tag="v$VERSION" ;; esac
bare="${tag#v}"

archive="terminalfs-$bare-$rid.tar.gz"
base="https://github.com/$REPO/releases/download/$tag"

# --- fetch and verify ----------------------------------------------------------------------

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT INT TERM

echo "install.sh: fetching $archive"
# curl's own stderr is left alone: "no such release" and "could not resolve host" and "407 from
# your proxy" are three different problems, and replacing all of them with one sentence of ours
# sends people to the release page when the fault is the network.
curl -fsSL -o "$tmp/$archive" "$base/$archive" \
    || fail "could not fetch $archive from release $tag (curl's reason is above)"
curl -fsSL -o "$tmp/SHA256SUMS" "$base/SHA256SUMS" \
    || fail "could not fetch SHA256SUMS from release $tag (curl's reason is above)"

expected="$(grep " $archive\$" "$tmp/SHA256SUMS" | awk '{print $1}' | head -1)"
[ -n "$expected" ] || fail "SHA256SUMS does not mention $archive"

if command -v sha256sum >/dev/null 2>&1; then
    actual="$(sha256sum "$tmp/$archive" | awk '{print $1}')"
else
    actual="$(shasum -a 256 "$tmp/$archive" | awk '{print $1}')"
fi

[ "$actual" = "$expected" ] || fail "checksum mismatch for $archive: expected $expected, got $actual"
# This says the download is intact, not that it is authentic: the sums come from the same place
# as the archive, over the same connection, with no signature. Whoever could replace one could
# replace both.
echo "install.sh: checksum matches the published SHA256SUMS"

# --- install -------------------------------------------------------------------------------

tar -xzf "$tmp/$archive" -C "$tmp"
[ -f "$tmp/terminalfs" ] || fail "$archive does not contain terminalfs"

mkdir -p "$BIN_DIR"
install -m 755 "$tmp/terminalfs" "$BIN_DIR/terminalfs" 2>/dev/null \
    || { cp "$tmp/terminalfs" "$BIN_DIR/terminalfs" && chmod 755 "$BIN_DIR/terminalfs"; }

# A binary downloaded by curl carries no quarantine attribute, but one that arrived through a
# browser does, and macOS then refuses to run it. Clearing it is harmless when it is absent.
if [ "$os" = osx ] && command -v xattr >/dev/null 2>&1; then
    xattr -d com.apple.quarantine "$BIN_DIR/terminalfs" 2>/dev/null || true
fi

echo "install.sh: installed $tag to $BIN_DIR/terminalfs"

case ":$PATH:" in
    *":$BIN_DIR:"*)
        echo "install.sh: run 'terminalfs --help'" ;;
    *)
        echo "install.sh: $BIN_DIR is not on your PATH. Add it:"
        echo "    echo 'export PATH=\"$BIN_DIR:\$PATH\"' >> ~/.zshrc   # or ~/.bashrc" ;;
esac
