
# COMMANDS

Quick reference for keys and commands in **Podliner**.  
Full in-app help: `:h`

---

## Table of contents
- [Most used](#most-used)
- [Key bindings](#key-bindings)
- [Commands by category](#commands-by-category)
  - [Playback](#playback)
  - [Navigation](#navigation)
  - [Downloads](#downloads)
  - [Queue](#queue)
  - [Feeds](#feeds)
  - [Sort and filter](#sort-and-filter)
  - [Player and theme](#player-and-theme)
  - [Network and engine](#network-and-engine)
  - [OPML](#opml)
  - [Sync (gPodder)](#sync-gpodder)
  - [Misc](#misc)
- [Engines guide](#engines-guide)
- [OPML import and export](#opml-import-and-export)
- [gPodder sync](#gpodder-sync)

---

## Most used

**Playback**
- `Enter`: play selected
- `Space`: pause or resume
- `← / →`: seek 10s backward or forward
- `[ / ]`: slower or faster
- `=` or `1`: reset speed to 1.0x

**Navigation**
- `j / k`: move selection down or up
- `g / G`: jump to start or end
- `/`: search in current list

**Downloads**
- `d`: toggle download flag or show status
- `:downloads`: open downloads view

**Filters and view**
- `u`: toggle unplayed only
- `:feed all|saved|downloaded|history|queue`: switch view

**Misc**
- `t`: toggle theme
- `q`: quit
- `:w` or `:wq`: save or save and quit

---

## Key bindings

| Key                    | Action                                         | Notes                           |
|------------------------|------------------------------------------------|---------------------------------|
| `Space`                | Toggle play or pause                           |                                 |
| `← / →`                | Seek -10s / +10s                               |                                 |
| `H / L`                | Seek -60s / +60s                               |                                 |
| `- / +`                | Volume down / up                               | 0 to 100                        |
| `[ / ]`                | Slower / faster                                |                                 |
| `=` or `1`             | Reset speed to 1.0x                            |                                 |
| `2 / 3`                | Speed presets 1.25x / 1.5x                     |                                 |
| `Enter`                | Play selected episode                           |                                 |
| `i`                    | Open Shownotes tab                             |                                 |
| `Esc` (in Shownotes)   | Back to Episodes                               |                                 |
| `j / k`                | Move selection down / up                       |                                 |
| `h / l`                | Focus feeds / episodes                         |                                 |
| `J / K`                | Next / previous unplayed                       |                                 |
| `⇧J / ⇧K`             | Move item down / up in Queue                    |                                 |
| `m`                    | Toggle played flag                             |                                 |
| `u`                    | Toggle unplayed filter                         | Use `f` instead if you remapped |
| `d`                    | Toggle download flag                           |                                 |
| `:`                    | Enter command mode                             |                                 |
| `/`                    | Search (Enter to apply, `n` to repeat)         |                                 |
| `t`                    | Toggle theme                                   |                                 |
| `F12`                  | Logs overlay                                   |                                 |
| `q`                    | Quit                                           |                                 |

---

## Commands by category

### Playback
- `:toggle`  
  Toggle pause or resume.
- `:seek [ +N | -N | NN% | mm:ss | hh:mm:ss ]`  
  Seek by seconds, percentage, or timestamp.  
  Examples: `:seek +10`, `:seek 80%`, `:seek 12:34`, `:seek 01:02:03`
- `:jump <hh:mm[:ss] | +/-sec | %>`  
  Same syntax as `:seek`.  
  Examples: `:jump 10%`, `:jump +90`, `:jump 00:30`
- `:replay [N]`  
  Replay from 0:00 or jump back N seconds.  
  Examples: `:replay`, `:replay 30`
- `:vol [N | +/-N]`  
  Set or change volume (0 to 100).  
  Examples: `:vol 70`, `:vol +5`, `:vol -10`
- `:speed [S | +/-D]`  
  Set or change speed (0.25 to 3.0).  
  Examples: `:speed 1.0`, `:speed +0.1`, `:speed -0.25`

### Navigation
- `:next` / `:prev`  
  Select next or previous item without autoplay.
- `:play-next` / `:play-prev`  
  Play next or previous item.
- `:next-unplayed` / `:prev-unplayed`  
  Play next or previous unplayed item.
- `:goto top|start|bottom|end`  
  Select list position.  
  Examples: `:goto top`, `:goto end`
- `:now`  
  Select the currently playing episode.
- `:zt` `:zz` `:zb` (aliases `:H` `:M` `:L`)  
  Position the current row at top, middle, or bottom.

### Downloads
- `:download [start|cancel]` (alias `:dl`)  
  Mark or unmark the episode for download, optionally start or cancel.  
  Examples: `:download`, `:dl`, `:dl cancel`
- `:downloads [retry-failed | clear-queue | open-dir]`  
  Show downloads overview and actions.  
  Examples: `:downloads`, `:downloads retry-failed`, `:downloads open-dir`
- `:save [on|off|true|false|+|-]`  
  Toggle or set Saved (star) for selected episode.  
  Examples: `:save`, `:save on`, `:save -`

### Queue
- `:queue add|toggle|rm|remove|clear|move <up|down|top|bottom>|shuffle|uniq` (alias `q`)  
  Queue operations for the selected episode or list.  
  Examples:  
  `:queue add`  
  `q`  
  `:queue move up`  
  `:queue shuffle`  
  `:queue uniq`  
  `:queue clear`

### Feeds
- `:add <rss-url>` (alias `:a`)  
  Add a new podcast feed.  
  Examples: `:add https://example.com/feed.xml`, `:a https://…`
- `:refresh` (aliases `:update` `:r`)  
  Refresh all feeds.
- `:remove-feed` (aliases `:rm-feed` `:feed remove`)  
  Remove the currently selected feed.
- `:feed all|saved|downloaded|history|queue`  
  Switch to a virtual feed.  
  Examples: `:feed all`, `:feed queue`
- `:history clear | size <n>`  
  History actions.  
  Examples: `:history clear`, `:history size 500`

### Sort and filter
- `:sort show | reset | reverse | by <pubdate|title|played|progress|feed> [asc|desc]`
  Sort the episode list.
  Examples: `:sort show`, `:sort reverse`, `:sort by title asc`
- `:sort feeds show | reset | by <title|updated|unplayed> [asc|desc]`
  Sort the feed panel. Persisted across sessions.
  Examples: `:sort feeds by title asc`, `:sort feeds by unplayed desc`, `:sort feeds show`
- `:filter [unplayed|all|toggle]`
  Set or toggle unplayed filter.
  Examples: `:filter unplayed`, `:filter toggle`

### Player and theme
- `:audioPlayer [top|bottom|toggle]`  
  Place the player bar.  
  Examples: `:audioPlayer top`, `:audioPlayer toggle`
- `:theme [toggle|base|accent|native|auto]`  
  Switch theme or toggle.  
  Examples: `:theme`, `:theme native`

### Network and engine
- `:net online|offline|toggle`  
  Set or toggle offline mode.  
  Examples: `:net`, `:net offline`, `:net toggle`
- `:play-source [auto|local|remote|show]`  
  Prefer playback source.  
  Examples: `:play-source`, `:play-source show`, `:play-source local`
- `:engine [show|help|auto|vlc|mpv|ffplay|diag]`  
  Select or inspect playback engine.  
  Examples: `:engine`, `:engine mpv`, `:engine help`, `:engine diag`

### OPML
- `:opml import <path> [--update-titles] | export [<path>]`  
  Import or export OPML.  
  Examples:  
  `:opml import ~/feeds.opml`  
  `:opml import feeds.opml --update-titles`  
  `:opml export`  
  `:opml export ~/stui-feeds.opml`

### Sync (gPodder)
- `:sync login <server> <user> <pass>`
  Log in and store credentials.
  Example: `:sync login https://gpodder.net alice mypass`
- `:sync logout`
  Remove credentials and stop syncing.
- `:sync`
  Full sync — pull subscription changes then push pending actions.
- `:sync push`
  Upload subscription changes and queued play actions.
- `:sync pull`
  Download subscription changes from the server.
- `:sync status`
  Show server URL, device ID, auto-sync state, last sync time, and pending action count.
- `:sync device <id>`
  Set this device's ID (default: `podliner-<hostname>`).
  Example: `:sync device my-laptop`
- `:sync auto [on|off]`
  Enable or disable auto-sync on startup and exit. Omit argument to toggle.
  Examples: `:sync auto on`, `:sync auto off`
- `:sync help`
  Show the in-app sync guide.

### Misc
- `:help` (alias `:h`)  
  Show this help.
- `:open [site|audio]`  
  Open episode website or audio in the system default.  
  Examples: `:open`, `:open site`, `:open audio`
- `:copy url|title|guid`  
  Copy episode info to clipboard.  
  Examples: `:copy`, `:copy url`, `:copy title`, `:copy guid`
- `:logs [N]`  
  Show logs overlay (tail).  
  Examples: `:logs`, `:logs 1000`
- `:osd <text>`  
  Show a transient on-screen message.  
  Example: `:osd Hello world`
- `:quit` (alias `:q`)  
  Quit application.
- `:w` or `:wq`  
  Save or save and quit.

---

## Engines guide

```
VLC (libVLC) - default
- Supports seek, pause, volume, speed, local files, HTTP
- Recommended. Mature and feature complete.

MPV (mpv, IPC)
- Supports seek, pause, volume, speed, local files, HTTP
- Requires mpv in PATH. Uses IPC socket.

Media Foundation (Windows only)
- Supports seek, pause, volume, local files, HTTP
- Built in on Windows. No extra install needed.
- Speed control not supported.

FFplay (limited)
- Supports play and stop only
- Coarse seek by restart (-ss). Speed and volume only at start.
- Intended as last-resort fallback.
```

**Switching engines**
```
:engine                  -> show current engine and capabilities
:engine help             -> show this guide
:engine auto             -> prefer VLC then MPV then FFplay
:engine vlc|mpv|ffplay   -> set preference
:engine diag             -> show active engine, caps, preference and last used
```

**Notes**
- On FFplay, `:seek` restarts playback at the new position (coarse seek).
- If an action is not supported by the active engine, a short OSD hint appears.
- Linux: install `vlc`, `mpv`, `ffmpeg`.
- macOS: `brew install vlc mpv ffmpeg`
- Windows: install VLC, MPV, FFmpeg and add them to PATH.

---

## OPML import and export

**Import**
```
:opml import <path> [--update-titles]
- Reads OPML 2.0
- Default policy:
  • Groups are ignored (flat import)
  • Existing feed titles are not overwritten
  • No online validation
```

**Export**
```
:opml export [<path>]
- Writes a flat OPML (UTF-8) with all current feeds
- If path is omitted, a sensible default is used
```

**Examples**
```
:opml import ~/feeds.opml
:opml import feeds.opml --update-titles
:opml export
:opml export ~/stui-feeds.opml
```

---

## gPodder sync

Sync subscriptions and play history with any gPodder API v2 compatible server.

**Supported servers**
- [gpodder.net](https://gpodder.net) — public, free
- Nextcloud with the gPodder app
- Any self-hosted gPodder API v2 compatible server

**Quick start**
```
:sync login https://gpodder.net <user> <pass>
:sync
:sync auto on
```

**Password storage**
Credentials are stored in the OS keyring when available (libsecret on Linux, Keychain on macOS, Credential Store on Windows). If the keyring is unavailable, the password is saved in `gpodder.json` as a plaintext fallback with a one-time warning.

**Offline behaviour**
All sync operations check the network state first. If offline (`:net offline` or no connection), sync returns immediately. Pending play actions accumulate while offline and are uploaded on the next successful push.

**Device ID**
The default device ID is `podliner-<hostname>` (max 64 characters). Change it with `:sync device <id>` before the first login if you want a specific name on the server.
