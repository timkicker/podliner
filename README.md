<!--
README for podliner — skeleton.
Replace all {{PLACEHOLDER}} markers before publishing.
Keep screenshots small (<1–2 MB each); store under assets/screens/.
-->

<p align="center">
  <!-- Dark/Light logo variants optional -->
  <!--
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="assets/branding/podliner-logo-dark.png">
    <source media="(prefers-color-scheme: light)" srcset="assets/branding/podliner-logo-light.png">
    <img alt="podliner" src="assets/branding/podliner-logo.png" width="360">
  </picture>
  -->
  <img alt="podliner" src="assets/branding/podliner-logo-alt.png" width="360">
</p>

<p align="center">
  <b>Podcasts in any terminal. Fast, clean, offline. </b>
</p>

<p align="center">
  <!-- Badges: replace with real links -->
  <a href="https://github.com/timkicker/podliner/actions">
    <img alt="CI" src="https://img.shields.io/github/actions/workflow/status/timkicker/podliner/release.yml?label=build">
  </a>
  <a href="https://github.com/timkicker/podliner/releases/latest">
    <img alt="Release" src="https://img.shields.io/github/v/release/timkicker/podliner?display_name=tag&sort=semver">
  </a>
  <a href="LICENSE">
    <img alt="License" src="https://img.shields.io/badge/license-GPLv3-blue.svg">
  </a>
  <img alt="Platforms" src="https://img.shields.io/badge/platforms-linux%20%7C%20macOS%20%7C%20windows-666">
</p>

---

## Table of Contents
- [Why podliner?](#why-podliner)
- [Screenshots](#screenshots)
- [Install (stable releases)](#install-stable-releases)
  - [Linux](#linux)
  - [macOS](#macos)
  - [Windows](#windows)
  - [Update / Uninstall / Prune](#update--uninstall--prune)
  - [Verify checksums](#verify-checksums)
- [Quick start](#quick-start)
- [Migrate from other players (OPML)](#migrate-from-other-players-opml)
- [Commands (essentials)](#commands-essentials)
- [Configuration & data](#configuration--data)
- [Audio engines](#audio-engines)
- [FAQ / Troubleshooting](#faq--troubleshooting)
- [Contributing](#contributing)
- [Bug reports & logs](#bug-reports--logs)
- [Roadmap](#roadmap)
- [License & credits](#license--credits)

---

## Why podliner?

- **Keyboard-first & mouse-friendly.** Full mouse support (click, select, scroll) with fast TUI feedback.
- **Vim keys & commands.** Familiar navigation (`j/k`, `gg/G`, `dd` for remove from queue, `/` to search) plus concise colon-commands (`:add <url>`, `:queue`, `:play`, `:export-opml`, `:import-opml`).
- **Offline-ready.** Download episodes, resume where you left off, manage a queue.
- **Easy migration.** OPML import/export to move subscriptions between players.
- **Cross-platform.** Single-file builds for Linux, macOS, and Windows.
- **Engine choice.** Works with mpv, ffplay (FFmpeg), or VLC where available.

> No telemetry. Config lives in your user profile. All local.


## Screenshots
<!-- Replace images; keep descriptive ALT text. -->
<p align="center">
  <img src="assets/screens/01-episodes.png" alt="Episodelist with player" width="48%"/>
  <img src="assets/screens/02-details.png"  alt="Episode-details with shownotes" width="48%"/>
</p>
<p align="center">
  <img src="assets/screens/03-help.png" alt="Help / Shortcuts" width="70%"/>
</p>


## Install (stable releases)
> RCs/Betas are **not** exposed via `/releases/latest`. The commands below always fetch the latest **stable**.

### Linux
```bash
bash <(curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install.sh)
```
- User install: `~/.local/bin/podliner` (ensure `~/.local/bin` is on your `PATH`).
- System install:
  ```bash
  curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install.sh | sudo bash -s -- --system
  ```
  Places binary link under `/usr/local/bin/podliner`.

### macOS
```bash
bash <(curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install-macos.sh)
```
- User install: `~/bin/podliner` (add `export PATH="$HOME/bin:$PATH"` to `~/.zprofile` or `~/.zshrc`).
- System install:
  ```bash
  bash <(curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install-macos.sh) --system
  ```
  Symlink under `/usr/local/bin/podliner`.  
- Note: installer removes Gatekeeper quarantine flag (`xattr -dr com.apple.quarantine`) best-effort.

### Windows
Open **PowerShell** (as user) and run:
```powershell
irm https://github.com/timkicker/podliner/releases/latest/download/install.ps1 | iex
```
- User install: links into `%LOCALAPPDATA%\Microsoft\WindowsApps\podliner.exe`
  (usually on `PATH`; if not, add it via **System → Advanced → Environment Variables**).
- System install (admin PowerShell):
```powershell
irm https://github.com/timkicker/podliner/releases/latest/download/install.ps1 | iex
Install-Podliner -System
```

### Update / Uninstall / Prune
- Update to latest stable: re-run the install command.
- Uninstall (Linux/macOS):
  ```bash
  bash <(curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install.sh) --uninstall
  ```
  ```bash
  bash <(curl -fsSL https://github.com/timkicker/podliner/releases/latest/download/install-macos.sh) --uninstall
  ```
- Uninstall (Windows, PowerShell):
  ```powershell
  irm https://github.com/timkicker/podliner/releases/latest/download/install.ps1 | iex
  Uninstall-Podliner
  ```
- Prune old versions (keep active): same scripts with `--prune` / `Prune-Podliner`.

### Verify checksums
```bash
cd /tmp
curl -fsSLO https://github.com/timkicker/podliner/releases/latest/download/SHA256SUMS
# Example for Linux x64:
curl -fsSLO https://github.com/timkicker/podliner/releases/latest/download/podliner-linux-x64.tar.gz
grep 'podliner-linux-x64.tar.gz$' SHA256SUMS | sha256sum -c -
```
macOS:
```bash
shasum -a 256 podliner-osx-{{arm64|x64}}.tar.gz | grep -F "$(grep 'podliner-osx-{{rid}}.tar.gz$' SHA256SUMS)"
```


## Quick start
```bash
podliner                 # launch the TUI
# In the UI:
#  - Add a feed (paste URL), press Enter to fetch
#  - Select episode → press Enter to play
#  - q to quit
```
> If you see “No audio engine found”, install one of: **mpv**, **ffplay** (ffmpeg), or **VLC**. See [Audio engines](#audio-engines).


## Migrate from other players (OPML)
Most podcast players support **OPML** export/import.

- **Export** your subscriptions from the old player as `subscriptions.opml` (name doesn’t matter).
- In **podliner**, open the import dialog and select the OPML file.  
  *(Alternatively place the OPML file under {{CONFIG_DIR}} and use the import command if you prefer CLI—adjust this line to your actual UI flow.)*
- To **export** your current subscriptions from podliner, use the export action; it will write an OPML file to {{EXPORT_PATH}}.
- After import, refresh feeds once to populate episodes.

> OPML contains feed URLs only (no playback positions). For progress sync, use your current backup method if any.


## Commands (essentials)
> Full help: `podliner --help`

Inside the TUI (defaults; adjust after confirming):
- **Enter** – play selected episode
- **Space** – pause/resume
- **d** – download / show download status
- **f** – toggle unplayed filter
- **/** – search
- **t** – toggle theme
- **q** – quit

*(Keep this list short; expand later under `docs/commands.md` if needed.)*


## Configuration & data
> Replace with the exact paths from code before publishing.

- **Config**:  
  - Linux: `~/.config/podliner/`  
  - macOS: `~/Library/Application Support/podliner/`  
  - Windows: `%APPDATA%\podliner\`
- **Logs**: next to the executable under `logs/` (file pattern `podliner-.log`)  
  Example: `…/podliner/logs/podliner-YYYYMMDD.log`
- **Downloads**: {{IF APPLICABLE: path or “same as configured in app”}}
- **OPML**: imports/exports under [Migrate from other players (OPML)](#migrate-from-other-players-opml)

> Back up `appdata.json` (or your config filename) to migrate settings to another machine.


## Audio engines
podliner can use different players:
- **mpv** (recommended)
- **ffplay** (part of ffmpeg)
- **VLC** (via LibVLC; Windows support included with `VideoLAN.LibVLC.Windows`)

Install examples:
- Debian/Ubuntu: `sudo apt-get install -y mpv ffmpeg`
- Fedora: `sudo dnf install -y mpv ffmpeg`
- Arch: `sudo pacman -S --needed mpv ffmpeg`
- macOS: `brew install mpv ffmpeg` *(if you use Homebrew)*
- Windows: download mpv or rely on VLC package as configured.

Engine selection & fallback:
- Default is `auto` (prefer local download if available; else remote if online).
- You can switch engines from within the UI (hot swap) {{CONFIRM KEY}}.


## FAQ / Troubleshooting
**`podliner: command not found`**  
Add install path to `PATH`:
- Linux: `export PATH="$HOME/.local/bin:$PATH"` (in `~/.bashrc` / `~/.zshrc`)
- macOS: `export PATH="$HOME/bin:$PATH"` (in `~/.zprofile` / `~/.zshrc`)
- Windows: ensure `%LOCALAPPDATA%\Microsoft\WindowsApps` is on PATH

**“No audio engine found”**  
Install `mpv` or `ffplay` (see above), then restart.

**UI doesn’t refresh**  
Update to the latest stable release; if still reproducible, include logs and OS/RID in your bug report (see below).

**Reset config**  
Quit, then move the config directory away (see [Configuration & data](#configuration--data)), restart to re-initialize.


## Contributing
We welcome small, focused PRs.

**Local dev:**
```bash
dotnet build
dotnet run --project StuiPodcast.App
```

**Guidelines (short):**
- C# with `nullable` enabled; keep logging via Serilog.
- Prioritize robustness and UX over features.
- Open an issue before large refactors.

**PR flow:**
- Fork → branch → PR to `main`
- Prefer squash merges; keep commit messages imperative and scoped.

*(Optionally move details to `docs/contributing.md` later.)*


## Bug reports & logs
When filing an issue, please include:
- `podliner --version` output (shows exact version + RID)
- OS + architecture (e.g., `linux-x64`, `osx-arm64`, `win-x64`)
- Steps to reproduce (short and precise)
- **Logs**: attach the most recent file from `logs/` next to the binary (pattern `podliner-*.log`)  
  Example: `…/podliner/logs/podliner-20250101.log`

Security-sensitive issues: contact tim@kicker.dev.


## Roadmap
- Stabilization & bugfixes
- Better download/queue management
- Docs: full commands reference
- Packaging: {{OPTIONAL: Homebrew / winget / deb/rpm}}

*(Track progress via Issues / Milestones.)*


## License & credits
- License: [GPLv3](LICENSE)
- Thanks: [Terminal.Gui](https://github.com/migueldeicaza/gui.cs), [Serilog](https://serilog.net/), [mpv](https://mpv.io/), [ffmpeg](https://ffmpeg.org/), [VLC](https://www.videolan.org/), and contributors.
