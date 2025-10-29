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
  --version X.Y.Z   Install specific version (default: latest stable release)
  --system          Install to /usr/local (or Homebrew prefix if detected; requires sudo)
  --prefix DIR      Install root (defaults: ~/Applications for user; /usr/local or Homebrew for system)
  --alias NAME      Also create a symlink NAME -> $TOOL
  --prune           Remove all old versions (keep active)
  --uninstall       Remove symlinks and (with --prune) installed versions
  -h, --help        Show this help message
EOF
}

# ---- args ----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2;;
    --system) SYSTEM=1; shift;;
    --prefix) PREFIX="${2:-}"; shift 2;;
    --alias)  ALIAS="${2:-}";  shift 2;;
    --prune)  PRUNE=1; shift;;
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

# ---- layout ----
SUDO=""
if [[ $SYSTEM -eq 1 ]]; then
  # Prefer Homebrew prefix for system installs if present (e.g. /opt/homebrew on Apple Silicon)
  if [[ -z "${PREFIX}" ]] && have brew; then
    HB="$(brew --prefix 2>/dev/null || true)"
  else
    HB=""
  fi
  ROOT="${PREFIX:-${HB:-/usr/local}}"
  OPT="$ROOT/opt/$TOOL"
  BIN="$ROOT/bin"
  SUDO="sudo"
else
  ROOT="${PREFIX:-$HOME/Applications}"
  OPT="$ROOT/$TOOL"
  BIN="$HOME/bin"
fi

# ---- uninstall ----
if [[ $UNINSTALL -eq 1 ]]; then
  echo "Uninstalling $TOOL ..."
  $SUDO rm -f "$BIN/$TOOL" || true
  if [[ -n "$ALIAS" ]]; then $SUDO rm -f "$BIN/$ALIAS" || true; fi
  if [[ $PRUNE -eq 1 && -d "$OPT" ]]; then
    echo "Removing all installed versions under $OPT ..."
    $SUDO rm -rf "$OPT"
  else
    if [[ -d "$OPT" ]]; then echo "Keeping versions under $OPT (use --prune to remove)."; fi
  fi
  echo "Done."
  exit 0
fi

# ---- version (stable latest only; pass --version for RC/beta) ----
if [[ -z "$VERSION" ]]; then
  if have curl; then
    VERSION="$(curl -fsSL "https://api.github.com/repos/$REPO/releases/latest" | sed -nE 's/.*"tag_name": *"v?([^"]+)".*/\1/p' | head -n1 || true)"
  elif have wget; then
    VERSION="$(wget -q -O - "https://api.github.com/repos/$REPO/releases/latest" | sed -nE 's/.*"tag_name": *"v?([^"]+)".*/\1/p' | head -n1 || true)"
  fi
  if [[ -z "$VERSION" ]]; then
    echo "Could not determine latest version (stable). For RC/beta, pass --version X.Y.Z"
    exit 1
  fi
fi

ASSET="podliner-${RID}.tar.gz"
BASEURL="https://github.com/$REPO/releases/download/v$VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Installing $TOOL v$VERSION for $RID -> $OPT"

# Create directories (may require sudo for system installs)
$SUDO mkdir -p "$OPT" "$BIN"

echo "Downloading assets..."
dl "$BASEURL/$ASSET" "$TMP/$ASSET" || { echo "Download failed: $ASSET"; exit 1; }
dl "$BASEURL/SHA256SUMS" "$TMP/SHA256SUMS" || { echo "Download failed: SHA256SUMS"; exit 1; }

# ---- verify ----
echo "Verifying checksum..."
pushd "$TMP" >/dev/null
if have shasum; then
  SUM="$(grep " $ASSET\$" SHA256SUMS | awk '{print $1}')"
  if [[ -z "$SUM" ]]; then echo "No checksum entry for $ASSET"; exit 1; fi
  echo "$SUM  $ASSET" | shasum -a 256 -c - >/dev/null
elif have sha256sum; then
  grep " $ASSET\$" SHA256SUMS | sha256sum -c - >/dev/null
else
  echo "No sha256 tool found (shasum/sha256sum)."; exit 1
fi
popd >/dev/null

# ---- extract ----
DST="$OPT/$VERSION"
$SUDO rm -rf "$DST" 2>/dev/null || true
$SUDO mkdir -p "$DST"
tar -C "$DST" -xzf "$TMP/$ASSET"

# expected: DST/podliner/podliner
BINPATH="$DST/podliner/podliner"
if [[ ! -x "$BINPATH" ]]; then
  echo "Unexpected archive layout (not found or not executable): $BINPATH"
  exit 1
fi

# Remove Gatekeeper quarantine if present (best effort)
if have xattr; then
  xattr -dr com.apple.quarantine "$BINPATH" 2>/dev/null || true
fi

# ---- symlinks ----
$SUDO ln -sf "$BINPATH" "$BIN/$TOOL"
if [[ -n "$ALIAS" ]]; then
  $SUDO ln -sf "$BIN/$TOOL" "$BIN/$ALIAS"
fi

echo "Installed $TOOL to $BIN/$TOOL"

# ---- PATH hint ----
if ! echo ":$PATH:" | grep -q ":$BIN:"; then
  echo "NOTE: $BIN is not in your PATH."
  SHELL_NAME="$(basename "${SHELL:-}")"
  case "$SHELL_NAME" in
    zsh)  RC_FILE="$HOME/.zprofile";  ADD='export PATH="'"$BIN"':$PATH"';;
    bash) RC_FILE="$HOME/.bashrc";    ADD='export PATH="'"$BIN"':$PATH"';;
    fish) RC_FILE="$HOME/.config/fish/config.fish"; ADD='set -gx PATH '"$BIN"' $PATH';;
    *)    RC_FILE=""; ADD='export PATH="'"$BIN"':$PATH"';;
  esac
  if [[ -n "$RC_FILE" ]]; then
    echo "Add this line to your shell config (then restart the shell):"
    echo "  echo '$ADD' >> $RC_FILE"
  else
    echo "Add this to your shell config:"
    echo "  $ADD"
  fi
fi

# ---- prune ----
if [[ $PRUNE -eq 1 && -d "$OPT" ]]; then
  echo "Pruning old versions in $OPT ..."
  # Try to detect current target; if readlink fails, keep everything
  current="$($SUDO readlink "$BIN/$TOOL" 2>/dev/null || echo "$BINPATH")"
  for vdir in "$OPT"/*; do
    [[ -d "$vdir" ]] || continue
    [[ "$current" == "$vdir/podliner/podliner" ]] && continue
    echo "Removing $vdir"
    $SUDO rm -rf "$vdir"
  done
fi

echo "Done. Try:  $TOOL --version"
