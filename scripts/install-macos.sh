#!/usr/bin/env bash
set -euo pipefail

REPO="timkicker/podliner"
TOOL="podliner"
VERSION=""
SYSTEM=0
PREFIX=""
ALIAS=""
PRUNE=0
UNINSTALL=0

usage() {
  cat <<EOF
$TOOL macOS installer

Usage: install-macos.sh [--version X.Y.Z] [--system] [--prefix DIR] [--alias pl] [--prune] [--uninstall]

Options:
  --version X.Y.Z   Install specific version (default: latest release)
  --system          Install to /usr/local (requires sudo)
  --prefix DIR      Install root (defaults: ~/Applications for user; /usr/local for system)
  --alias NAME      Also create a symlink NAME -> $TOOL
  --prune           Remove all old versions (keep active)
  --uninstall       Remove symlinks and installed versions
EOF
}

# ---- args ----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="$2"; shift 2;;
    --system) SYSTEM=1; shift;;
    --prefix) PREFIX="$2"; shift 2;;
    --alias) ALIAS="$2"; shift 2;;
    --prune) PRUNE=1; shift;;
    --uninstall) UNINSTALL=1; shift;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1"; usage; exit 1;;
  esac
done

# ---- prereqs ----
have() { command -v "$1" >/dev/null 2>&1; }
dl() {  # url out
  if have curl; then curl -fsSL "$1" -o "$2"; elif have wget; then wget -q "$1" -O "$2"; else return 1; fi
}

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer targets macOS."
  exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
  x86_64) RID="osx-x64";;
  arm64)  RID="osx-arm64";;
  *) echo "Unsupported arch: $ARCH"; exit 1;;
esac

# layout
if [[ $SYSTEM -eq 1 ]]; then
  ROOT="${PREFIX:-/usr/local}"
  OPT="$ROOT/opt/$TOOL"
  BIN="$ROOT/bin"
  SUDO="sudo"
else
  ROOT="${PREFIX:-$HOME/Applications}"
  OPT="$ROOT/$TOOL"
  BIN="$HOME/bin"
  SUDO=""
fi

mkdir -p "$OPT" "$BIN"

# uninstall
if [[ $UNINSTALL -eq 1 ]]; then
  echo "Uninstalling $TOOL ..."
  rm -f "$BIN/$TOOL"
  [[ -n "$ALIAS" ]] && rm -f "$BIN/$ALIAS"
  if [[ $SYSTEM -eq 1 ]]; then
    [[ -d "$OPT" ]] && echo "Keeping versions under $OPT (use --prune to remove)."
  else
    [[ -d "$OPT" ]] && echo "Keeping versions under $OPT (use --prune to remove)."
  fi
  echo "Done."
  exit 0
fi

# version
if [[ -z "$VERSION" ]]; then
  if have curl; then
    VERSION=$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -nE 's/.*"tag_name": *"v?([^"]+)".*/\1/p' | head -n1)
  elif have wget; then
    VERSION=$(wget -q -O - "https://api.github.com/repos/$REPO/releases/latest" | sed -nE 's/.*"tag_name": *"v?([^"]+)".*/\1/p' | head -n1)
  fi
  [[ -z "$VERSION" ]] && { echo "Could not determine latest version."; exit 1; }
fi

ASSET="podliner-${RID}.tar.gz"
BASEURL="https://github.com/$REPO/releases/download/v$VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Installing $TOOL v$VERSION for $RID → $OPT"

echo "Downloading assets..."
dl "$BASEURL/$ASSET" "$TMP/$ASSET" || { echo "Download failed: $ASSET"; exit 1; }
dl "$BASEURL/SHA256SUMS" "$TMP/SHA256SUMS" || { echo "Download failed: SHA256SUMS"; exit 1; }

# verify
echo "Verifying checksum..."
pushd "$TMP" >/dev/null
if have shasum; then
  SUM=$(grep " $ASSET\$" SHA256SUMS | awk '{print $1}')
  [[ -z "$SUM" ]] && { echo "No checksum entry for $ASSET"; exit 1; }
  echo "$SUM  $ASSET" | shasum -a 256 -c -
elif have sha256sum; then
  grep " $ASSET\$" SHA256SUMS | sha256sum -c -
else
  echo "No sha256 tool found (shasum/sha256sum)."; exit 1
fi
popd >/dev/null

DST="$OPT/$VERSION"
mkdir -p "$DST"
tar -C "$DST" -xzf "$TMP/$ASSET"

# expect DST/podliner/podliner
BINPATH="$DST/podliner/podliner"
if [[ ! -x "$BINPATH" ]]; then
  echo "Unexpected archive layout."; exit 1
fi

# Remove Gatekeeper quarantine if present (best effort)
if have xattr; then
  xattr -dr com.apple.quarantine "$BINPATH" 2>/dev/null || true
fi

# symlinks
$SUDO ln -sf "$BINPATH" "$BIN/$TOOL"
if [[ -n "$ALIAS" ]]; then
  $SUDO ln -sf "$BIN/$TOOL" "$BIN/$ALIAS"
fi

echo "Installed $TOOL to $BIN/$TOOL"
# PATH hint
if ! echo ":$PATH:" | grep -q ":$BIN:"; then
  echo "NOTE: $BIN is not in your PATH."
  echo "Add this to your shell rc, e.g.:  export PATH=\"$BIN:\$PATH\""
fi

# prune
if [[ $PRUNE -eq 1 && -d "$OPT" ]]; then
  echo "Pruning old versions in $OPT ..."
  current="$($SUDO readlink "$BIN/$TOOL" 2>/dev/null || echo "$BINPATH")"
  for vdir in "$OPT"/*; do
    [[ -d "$vdir" ]] || continue
    if [[ "$current" == "$vdir/podliner/podliner" ]]; then continue; fi
    echo "Removing $vdir"
    $SUDO rm -rf "$vdir"
  done
fi

echo "Done. Try:  $TOOL --version"

