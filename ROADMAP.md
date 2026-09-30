# Roadmap

## Next release

### Bugs
- [X] Homebrew could not package podliner: its formula test needs one real action with no terminal, no audio engine and no network, and `--opml-import`/`--opml-export` only ran once the TUI was up (chenrui333/homebrew-tap#4952). Without a terminal they now do their work before an engine is looked for, print what happened and exit; import only records the subscriptions and the next start fetches them. `tests/smoke/headless_opml.py` runs them the way the formula would
- [X] SIGTERM ended podliner without the exit cleanup, so the last save never ran and up to 30s of listening position was lost; SIGHUP from a closed terminal window lost it about half the time. Both now quit the way `q` does. `tests/smoke/signal_save.py` plays 20s and checks the saved position after `q`, SIGTERM and SIGHUP; on 2.1.0 half of the signal runs lost it
- [X] With no VLC, mpv or ffplay installed podliner died on start with an unhandled `InvalidOperationException`, exit code 134 and a stack trace, the first thing someone saw after installing it without its optional dependencies. It now says what to install and exits with 1. `tests/smoke/no_engine.py` in CI, run before the job installs an engine
- [X] With `JOURNAL_STREAM` set, podliner wrote no log file even with an explicit `--log-dir` or `PODLINER_LOG_DIR`, although #12 promised those override the journal. The variable leaks into terminals run as systemd services (GNOME Terminal among them), so those users never had a log to attach to a bug report. Found when the CI runner, a service itself, produced no log. An explicit directory now always wins; the journal only changes the default
- [X] The first key after every terminal resize was lost. The #4 rescue in 2.0.0 told ncurses the size with `resizeterm`, which also pushes `KEY_RESIZE` back into the input; Terminal.Gui 1.19 drains that and then maps the next real character as a function key, so it arrived as `Unknown`. Type `:` straight after resizing and no command box opened, and the rest of the command ran as shortcuts (`:sleep 13m` became play/pause and two speed changes). 1.3.1 was fine. Now `resize_term`, which resizes without the push; the layout still follows every resize. Found by clicking through the app. `tests/smoke/resize_input.py` in CI
- [X] Every rebuild of the feed list threw the episode selection away. `UiShell.SetFeeds` ended with `RefreshEpisodesForSelectedFeed(_episodes)`, and `_episodes` was a list nothing ever filled, a leftover from before the runtime stores; the episode pane was blanked and the caller's repaint started again at the top. Harmless while only `:refresh` and feed changes rebuilt the list, but with a timed refresh (#32) it would have pulled the cursor to the top every hour. Found by hand on a copy of a real library: on episode 8, after a pass, back on episode 1
- [X] `--offline` and `:net offline` lasted about a second. `NetworkMonitor`'s first probe wrote its result straight into `NetworkOnline`, right after `--offline` had cleared it, and the periodic probes undid `:net offline` after three successes. Measured on 2.0.2: start with `--offline`, `:refresh` six seconds later, and the feed server got a request. With feeds now fetched on a timer that would have meant fetching on a connection the user had said to leave alone. `AppData.ForcedOffline` now records the choice; a probe may report the network lost but never found while it is set
- [X] Feeds were only ever fetched on `:refresh`, so a library could sit on months-old episodes with nothing on screen to say so (#32). `FeedRefresher` now runs a pass a few seconds after start and every `RefreshIntervalMinutes` (default 60, `0` off, 5 minutes to a week), measured from the last attempt so a dead server is not hit every tick, and remembered across restarts through `LastRefreshAt`. `:refresh auto <minutes>|off` sets it at runtime. The sidebar title shows the age, `Feeds · 12m ago`, and failures, `Feeds · 12m · 2 failed`; a timed pass never puts up a message. It also makes `:feed auto-download on` do something, which only ever fired on a manual refresh. `tests/smoke/auto_refresh.py` checks it against a local RSS server
- [X] Quitting left mouse tracking and the alternate screen on, so the shell printed mouse reports as text (#34). The restore ran last in the exit cleanup, and `QuitApp`'s 1.5s `Environment.Exit` watchdog fired first whenever that cleanup ran long. The gPodder push always did: `DispatchToUi` queued onto a main loop that `Application.Run` had already left, and waited for it. The push hung twice over: `DispatchToUi` queued onto the stopped loop, and every `await` inside it posted its continuation to Terminal.Gui's `MainLoopSyncContext`, which nothing pumps after `Run`. Restore now runs first and from `ProcessExit`, the sync context is cleared and dispatch runs inline once the loop has stopped, and the flush save moved ahead of the network steps (it was cut off every time; data only survived because the 1s deferred save beat the 1.5s kill). Every quit path now exits in under 0.3s instead of being killed at 1.5s. `tests/smoke/exit_teardown.py` checks the real bytes of 13 exit paths, the no-playback ones in CI
- [X] Every backslash was eaten by `CmdParser.Tokenize`, which treated all of them as escapes. `:opml import C:\Users\tim\feeds.opml` arrived as `C:Userstimfeeds.opml`, so every path-taking command was broken on Windows, `--opml-import`/`--opml-export` included. A backslash now only escapes a quote or whitespace. Found by photographing the running app on a Windows runner
- [X] The player bar reported 0% on a fresh install. `ShowStartupEpisode` paints the persisted volume and only runs when there is an episode to resume; with an empty library nothing painted it and the real value was 65
- [ ] Unicode glyphs render wrong in the Windows console: the clock, the queue and the pause symbol come out as boxes or filled blobs. `UIGlyphSet` and `--ascii` exist for this; the open question is whether Windows should pick ASCII automatically. Visible in `win-look` artifacts
- [X] Headless start crashed with an unhandled .NET exception (`0xE0434352`, EXCEPTION_COMPLUS). Terminal.Gui's WindowsDriver cannot get a console output window when stdout is redirected and `Application.Init` was never guarded. Present since at least v1.3.1, verified by running that tag the same way; it only surfaced when the winget validator rejected the 2.0.0 submission (microsoft/winget-pkgs#432756). A `win-smoke` workflow now runs the published exe headless on every push and pull request
- [X] Fix engine preference reset on restart (`ConfigStore` validates `libvlc`, `AudioEngineExt.ToWire` writes `vlc`/`mediafoundation`, so both fall back to `auto`)
- [X] Fix the same wrong engine list in `ConfigStoreValidationTests`
- [X] Fix `CS8602` in `DownloadManager.cs:732` and `EngineUseCase.cs:31`
- [X] Fix `CS0472` dead null checks in `UiHelpBrowserDialog.cs:155` and `:221`
- [X] Clear the remaining compiler warnings in the test projects
- [ ] Fix playerui update (windows only?)
- [X] Player control row overlaps itself below ~140 columns (now drops controls by tier: download, then skips + volume bar, then ±spd)
- [X] `:add <url>` fetched, persisted and logged the feed and then left the sidebar empty until the next start. `FeedService` lives in Infra and writes through `AppFacade` into `LibraryStore`, which the App-side `FeedStore`/`EpisodeStore` snapshot caches cannot observe, so they kept handing out a list from before the feed existed. `LibraryStore.Revision` now stamps every structural change and both caches compare against it. Same root cause for episodes pulled by `:refresh`
- [X] `:engine show` printed one of its three lines. `UiOsdOverlay` was pinned to `Height 3` and sized its width off the raw string, newlines included, so the box ran past the screen edge and painted only the middle line
- [X] `:copy` reported "copied" and left the clipboard untouched on Wayland. The Linux path only tried xclip and xsel, and xclip exits 0 on a Wayland session without owning the selection; the result was never checked either. `wl-copy` is tried first there now and a non-zero exit falls through to the next tool
- [X] Help browser: the `Search:` label sat at the same X/Y as the search field and was overdrawn, so both tabs showed a blank first row
- [X] `:theme` with no argument toggled the theme and then wiped `ThemePref`, losing the choice on the next start; unknown names silently applied MenuAccent
- [X] F12 logs overlay showed a single line above 27 blank rows: `TextView.MoveEnd` parks the view on the last line even when the log fits
- [X] A search could not be left: the episodes pane re-applies the last query on every rebuild and `:search clear` only rebuilt the list. `:search clear` now drops the filter and Esc on the list does the same
- [X] The Chapters tab had no keyboard route at all, `h`/`l` stopped at the shownotes tab. They now walk the full row: feeds, episodes, shownotes, chapters
- [X] `:chapter` dispatched its work from a bare `Task.Run` with no catch, so a failure there produced no message at all (the pattern CLAUDE.md forbids)
- [X] `:queue add` was a second name for `toggle`, so running it on a queued episode dropped it back out. `add` now appends, `toggle` toggles, both say which way it went
- [X] `:queue rm` was not undoable although `:undo` advertises reverting the last destructive action. It now restores the episode at the index it held
- [X] The help browser and COMMANDS.md still named the default OPML export `stui-feeds.opml` after the rename, while the code writes `podliner-feeds.opml`. A test now fails if the old name reappears in any help text
- [X] No way to delete a download. `:download` on a finished one called `Forget()`, which drops the bookkeeping and leaves the file, and reported "Download unqueued". Added `:download rm` (`IDownloadManager.DeleteLocalFile`) and made the bare command say what is actually true
- [X] `JsonStoreContractTests.Debounced_SaveAsync_coalesces_bursts` slept a flat 300ms against a 100ms debounce and failed on every macOS and Windows CI runner. It polls for the timer now

### Dependencies
- [X] Drop unused `Microsoft.Data.Sqlite` from Infra (removes the only high-severity advisory in the build)
- [X] Bump AngleSharp past 1.1.2 (moderate advisory)

### CI
- [X] Add a build+test workflow on push and pull request (today only `release.yml` on tags, nothing gates a merge)
- [X] Align target frameworks (app/infra/core on net9.0, both test projects on net10.0, CI pins 9.0.x)

### UI test harness
- [X] Add a `FakeDriver` fixture so `Application.Init` runs headless in `dotnet test`
- [X] Add a screen-readback helper over `FakeDriver.Contents` (`int[,,]`, rune at index 0)
- [X] Snapshot tests for the three-pane layout at 80x25 and at a narrow width
- [X] Regression test for the play-button glyph (was broken twice, issue #1 and v1.1.0)
- [X] Regression test for redraw after a resize event (issue #4)

### Coverage gaps, cheapest first
- [X] Theme rendering (every ThemeMode applies, renders and survives a resize)
- [X] `LoggerSetup` log-directory resolution (regression guard for issue #19, logs next to the binary)
- [X] `AppBridge` (round-trips every persisted preference through a real save and reload)
- [X] `UiShellKeyBindings` (the whole keyboard map)
- [X] `HelpCatalog` (tied to the parser so a renamed command breaks the build)
- [X] `UiMenuBarFactory` (every entry clicked, checks it dispatches something real)
- [X] `CliEntrypoint` (87 lines, pure arg parsing)
- [X] `DownloadRetryPolicy` (78 lines, pure)
- [X] `RssParser` (215 lines, pure, feeds every episode field)
- [X] `VlcPathResolver` (244 lines)
- [X] `AudioPlayerFactory` engine selection chain, extracted into `EngineSelectionPolicy`
- [X] `NetworkMonitor` hysteresis, extracted into `NetworkFlipPolicy`
- [X] `DownloadIndexStore` and `OpmlIo`
- [X] `FeedHttpFetcher`
- [X] `UiPlayerPanel` via the harness above
- [X] `UiFeedsPane` and `UiOsdOverlay` via the harness above
- [X] `UiEpisodesPane` via the harness above
- [X] `UI/Wiring/*` decision logic, extracted into `PlaySourceResolver` and `DownloadProgressSummary`
- [X] `UiSelectionWiring` and `UiPlaybackEventBridge` (narrowed to the deps they use instead of the whole `AppServices`)
- [X] `UiCommandWiring` search half and `UiInitialRender`'s startup-episode pick (both narrowed to the deps they use)
- [X] `IFeedService` extracted; `UiFeedWiring` remove and refresh covered, including the failure aggregation
- [X] `IDownloadManager` extracted; `UiDownloaderBridge` covered including the two-second badge throttle
- [X] `AppServices` takes interfaces; `AppServicesBuilder` assembles a complete one from fakes
- [X] `UiComposer.WireUi` covered end to end, including command routing
- [X] Removed the dead `switchEngine` chain: it was threaded from `Program.Main` through `WireUi`, `UiCommandWiring`, `CmdRouter` into `CmdContext.SwitchEngine` and never read; `:engine` has always gone through the delegate `CmdCases` was built with

### Refactor
- [X] `UiPlaybackWiring` play path covered through the full `WireUi` fixture
- [X] Fixed a misleading message: with `:play-source local` and no download the app said "offline: not downloaded" even while online; it now names the setting
- [X] Refactor Shell: split into partial files (UiShell.cs 1027 → 485 lines, plus Feeds/Theme/Chapters/Navigation)
- [X] Drop the duplicate `QueueUseCase` construction in `CmdCases` (first instance is overwritten immediately)
- [X] Add a `VerySlowNetwork` status, the second stage in `PlaybackCoordinator` fires `SlowNetwork` twice
- [X] Update CLAUDE.md

## Later

### Engine
- [ ] Add internal mac fallback engine
- [ ] Add native os-player interop (pausing via headphones, os-ui, ...)

### UX
- [X] Rethink first-letter-highlighting: the `_` hotkey markers are gone from all 19 menu labels
- [X] `q` now only quits; the bare-`q` alias for `:queue add` is gone from `QueueUseCase` and the help catalog

### Bugs
- [X] The Queue view was empty after every restart: it renders in queue order, and that order was only pushed after a queue command, never at startup. The queue itself was persisted correctly the whole time
- [X] Menu separators rendered as a literal `-` row: Terminal.Gui draws a `null` child as a rule, the code passed `new MenuItem("-", "", null)`
- [X] `:speed +0.1` / `-0.1` accumulated floating-point error and wrote `0.9999999999999997` into appsettings.json; values now snap to two decimals
- [X] The player bar showed 0% volume after launch: `ShowStartupEpisode` took `volume` and `speed` and used neither, so the persisted values were never painted. Found by driving the real app under tmux, not by the test suite
- [X] `ConfigStore` rejected the theme name `User`, the toggle's fourth stop, and rewrote it to `auto` on load. Not user-visible, because `UiThemeResolver` maps `auto` to `User` as well, but the stored value was wrong and would have drifted with any change of default. Also dropped `HighContrast`, which was never a `ThemeMode`
- [X] Removed `:feed remove` from the help catalog: it was documented as an alias of `:remove-feed` but `FeedUseCase` has no such subcommand, so it only ever printed a usage line
- [X] Refresh/Redraw ui on mac/linux after moving window (issue #4). Terminal.Gui detects resizes by polling, not by SIGWINCH: `CursesDriver.ProcessWinChange` → `Curses.CheckWinChange` compares ncurses' `LINES`/`COLS` globals against a cached copy, and only runs from `CursesDriver.Refresh` and the input loop. A missed poll leaves every Toplevel at the old frame. Fixed with `UiShell.ForceRedraw` (public Terminal.Gui API only), wired to `:redraw`, Ctrl+L, and a self-heal check in the 250ms UI tick.

### Packaging
- [ ] Nix packaging has no owner. odilf packaged podliner for his own dotfiles while fixing the Terminal.Gui HintPath (PR #27) but will not submit it to nixpkgs, so nothing is landing. Either someone picks it up or the README and the bug-report template keep no nix entry.
      Whoever does it needs a wrapper setting `LD_LIBRARY_PATH` and `LIBVLC_PLUGIN_PATH`, because LibVLCSharp dlopens libvlc at runtime and engine detection otherwise falls back to mpv or ffplay without saying so.

## Done

### v1.2.x
- [X] Add chapters (Podcast-2.0 JSON and ID3 CHAP)
- [X] Add nextcloud support for gpodder
- [X] Fix download folder-settings

### v1.1.0
- [X] Fix play-button visual on mac

### v1.0.0
- [X] Fix play-button visual
- [X] Fix VLC recogn on Mac. MPV seems to work. Maybe add `VideoLAN.LibVLC.Mac`
- [X] Fix remove-feed on save
- [X] Fix download percentage top right
- [X] Rework default theme(+ as default)
- [X] Add more logging to engine recogn.
- [X] Add more commands to menubar
- [X] Add NAudio for windows fallback
    - [X] Implement
    - [X] Test on win/linux/mac
    - [X] Update documentation
- [X] Update macos-installer
- [X] Update readme (download-section)
- [X] Refactor CmdParser: Replace if-hell yanderedev-style. This is rediculous and was just meant for testing.
