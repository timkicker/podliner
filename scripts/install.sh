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
$TOOL installer (Linux)

Usage: install.sh [--version X.Y.Z] [--system] [--prefix DIR] [--alias pl] [--prune] [--uninstall]

Options:
  --version X.Y.Z   Install specific version (default: latest release)
  --system          Install to /usr/local (requires sudo)
  --prefix DIR      Install root (defaults: ~/.local or /usr/local)
  --alias NAME      Also create a symlink NAME -> $TOOL
  --prune           Remove all old versions (keep active)
  --uninstall       Remove symlinks and installed versions
  -h, --help        Show this help
EOF
}

# ---- parse args ----
while [[ $# -gt 0 ]]; do
  case "$1" in
    --version) VERSION="${2:-}"; shift 2;;
    --system) SYSTEM=1; shift;;
    --prefix) PREFIX="${2:-}"; shift 2;;
    --alias)  ALIAS="${2:-}"; shift 2;;
    --prune)  PRUNE=1; shift;;
    --uninstall) UNINSTALL=1; shift;;
    -h|--help) usage; exit 0;;
    *) echo "Unknown arg: $1"; usage; exit 1;;
  esac
done

# ---- prereqs ----
have() { command -v "$1" >/dev/null 2>&1; }

progress_dl() { # args: url outpath
  local url="$1" out="$2"
  if have curl; then
    curl -fL --progress-bar "$url" -o "$out"
  elif have wget; then
    wget --progress=bar:force:noscroll -O "$out" "$url" 2>&1 | \
      stdbuf -o0 awk '/%/ {printf("\r%s",$0)} END {print ""}'
  else
    echo "Need curl or wget to download." >&2
    return 1
  fi
}

json_latest_tag() {
  if have curl; then
    curl -fsSL "https://api.github.com/repos/$REPO/releases/latest"
  else
    wget -q -O - "https://api.github.com/repos/$REPO/releases/latest"
  fi
}

if [[ "$(uname -s)" != "Linux" ]]; then
  echo "This installer targets Linux. For macOS/Windows, use the release archives."
  exit 1
fi

ARCH="$(uname -m)"
case "$ARCH" in
  x86_64) RID="linux-x64";;
  aarch64|arm64) RID="linux-arm64";;
  *) echo "Unsupported arch: $ARCH"; exit 1;;
esac

# ---- install layout ----
if [[ $SYSTEM -eq 1 ]]; then
  ROOT="${PREFIX:-/usr/local}"
  OPT="$ROOT/opt/$TOOL"
  BIN="$ROOT/bin"
  SUDO="sudo"
else
  ROOT="${PREFIX:-$HOME/.local}"
  OPT="$ROOT/opt/$TOOL"
  BIN="$ROOT/bin"
  SUDO=""
fi

mkdir -p "$OPT" "$BIN"

# uninstall flow
if [[ $UNINSTALL -eq 1 ]]; then
  echo "Uninstalling $TOOL ..."
  $SUDO rm -f "$BIN/$TOOL"
  [[ -n "${ALIAS:-}" ]] && $SUDO rm -f "$BIN/$ALIAS"
  if [[ -d "$OPT" ]]; then
    echo "Keeping versions under $OPT (remove manually or use --prune)."
  fi
  echo "Done."
  exit 0
fi

# latest version
if [[ -z "$VERSION" ]]; then
  VERSION="$(json_latest_tag | sed -nE 's/.*\"tag_name\": *\"v?([^\"]+)\".*/\1/p' | head -n1)"
  [[ -z "$VERSION" ]] && { echo "Could not determine latest version."; exit 1; }
else
  VERSION="${VERSION#v}"
fi

ASSET="podliner-${RID}.tar.gz"
BASEURL="https://github.com/$REPO/releases/download/v$VERSION"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

echo "Installing $TOOL v$VERSION for $RID -> $OPT"

echo "Downloading assets..."
progress_dl "$BASEURL/$ASSET" "$TMP/$ASSET" || { echo "Download failed: $ASSET"; exit 1; }
progress_dl "$BASEURL/SHA256SUMS" "$TMP/SHA256SUMS" || { echo "Download failed: SHA256SUMS"; exit 1; }

# verify checksum
echo "Verifying checksum..."
pushd "$TMP" >/dev/null
if have sha256sum; then
  grep " $ASSET\$" SHA256SUMS | sha256sum -c -
elif have shasum; then
  SUM=$(grep " $ASSET\$" SHA256SUMS | awk '{print $1}')
  echo "$SUM  $ASSET" | shasum -a 256 -c -
else
  echo "No sha256 tool found (sha256sum or shasum)."; exit 1
fi
popd >/dev/null

DST="$OPT/$VERSION"
rm -rf "$DST"
mkdir -p "$DST"
tar -C "$DST" -xzf "$TMP/$ASSET"

# find executable robustly (supports flat and nested tarballs)
find_exe() {
  local root="$1"
  local exe
  exe="$(find "$root" -type f -perm -111 -name "$TOOL" -print -quit)"
  [[ -n "$exe" ]] && echo "$exe"
}

EXE="$(find_exe "$DST" || true)"
if [[ -z "$EXE" ]]; then
  # try common layout: DST/podliner/podliner
  if [[ -x "$DST/$TOOL/$TOOL" ]]; then
    EXE="$DST/$TOOL/$TOOL"
  fi
fi

if [[ -z "$EXE" ]]; then
  echo "Unexpected archive layout. $TOOL executable not found under $DST"
  echo "Tree sample:"
  find "$DST" -maxdepth 2 -print | sed 's/^/  /' | head -n 50
  exit 1
fi

# switch symlinks atomically
$SUDO ln -sf "$EXE" "$BIN/$TOOL"
if [[ -n "${ALIAS:-}" ]]; then
  $SUDO ln -sf "$BIN/$TOOL" "$BIN/$ALIAS"
fi

echo "Installed $TOOL to $BIN/$TOOL"
if ! echo ":$PATH:" | grep -q ":$BIN:"; then
  echo "NOTE: $BIN is not in your PATH."
  echo "Add this to your shell rc, e.g.:  export PATH=\"$BIN:\$PATH\""
fi

# engine hint
if ! have mpv && ! have vlc; then
  echo
  echo "No audio engine found. Install one of:"
  if have apt;   then echo "  sudo apt install mpv     # or: sudo apt install vlc"; fi
  if have dnf;   then echo "  sudo dnf install mpv     # or: sudo dnf install vlc"; fi
  if have pacman;then echo "  sudo pacman -S mpv       # or: sudo pacman -S vlc"; fi
fi

# prune old versions
if [[ $PRUNE -eq 1 && -d "$OPT" ]]; then
  echo "Pruning old versions in $OPT ..."
  CURRENT="$(readlink -f "$BIN/$TOOL" 2>/dev/null || true)"
  for vdir in "$OPT"/*; do
    [[ -d "$vdir" ]] || continue
    if [[ -n "$CURRENT" && "$CURRENT" == "$vdir"/* ]]; then continue; fi
    echo "Removing $vdir"
    $SUDO rm -rf "$vdir"
  done
fi

echo "Done. Try:  $TOOL --version"
