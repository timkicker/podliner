# Roadmap

## Next release

### Bugs
- [X] Fix engine preference reset on restart (`ConfigStore` validates `libvlc`, `AudioEngineExt.ToWire` writes `vlc`/`mediafoundation`, so both fall back to `auto`)
- [X] Fix the same wrong engine list in `ConfigStoreValidationTests`
- [X] Fix `CS8602` in `DownloadManager.cs:732` and `EngineUseCase.cs:31`
- [X] Fix `CS0472` dead null checks in `UiHelpBrowserDialog.cs:155` and `:221`
- [X] Clear the remaining compiler warnings in the test projects
- [ ] Fix playerui update (windows only?)
- [X] Player control row overlaps itself below ~140 columns (now drops controls by tier: download, then skips + volume bar, then ±spd)

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
- [X] `CliEntrypoint` (87 lines, pure arg parsing)
- [X] `DownloadRetryPolicy` (78 lines, pure)
- [X] `RssParser` (215 lines, pure, feeds every episode field)
- [X] `VlcPathResolver` (244 lines)
- [ ] `AudioPlayerFactory` engine selection chain (255 lines, needs the probes behind an interface first)
- [X] `NetworkMonitor` hysteresis, extracted into `NetworkFlipPolicy`
- [X] `DownloadIndexStore` and `OpmlIo`
- [X] `FeedHttpFetcher`
- [X] `UiPlayerPanel` via the harness above
- [ ] `UiEpisodesPane`, `UiFeedsPane`, `UiOsdOverlay` via the harness above
- [ ] `UI/Wiring/*` (7 classes, ~750 lines, no tests)

### Refactor
- [ ] Refactor Shell: split into subclasses
- [X] Drop the duplicate `QueueUseCase` construction in `CmdCases` (first instance is overwritten immediately)
- [X] Add a `VerySlowNetwork` status, the second stage in `PlaybackCoordinator` fires `SlowNetwork` twice
- [ ] Update CLAUDE.md (describes `CmdQueueModule`/`CmdSyncModule` and "12 handler modules"; actual layout is `Command/Handler/` plus `Command/UseCases/`, and the stores, `AppServices`, `SleepTimer`, `UndoStack`, `ChaptersFetcher` and the gPodder flavors are missing)

## Later

### Engine
- [ ] Add internal mac fallback engine
- [ ] Add native os-player interop (pausing via headphones, os-ui, ...)

### UX
- [ ] Rethink first-letter-highlighting

### Bugs
- [X] Refresh/Redraw ui on mac/linux after moving window (issue #4). Terminal.Gui detects resizes by polling, not by SIGWINCH: `CursesDriver.ProcessWinChange` → `Curses.CheckWinChange` compares ncurses' `LINES`/`COLS` globals against a cached copy, and only runs from `CursesDriver.Refresh` and the input loop. A missed poll leaves every Toplevel at the old frame. Fixed with `UiShell.ForceRedraw` (public Terminal.Gui API only), wired to `:redraw`, Ctrl+L, and a self-heal check in the 250ms UI tick.

### Packaging
- [ ] Add the nixpkgs entry to README and the bug-report template once the package lands

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
