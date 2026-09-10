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
- [ ] `UiCommandWiring` command routing, `UiFeedWiring`, `UiDownloaderBridge` (still need `CmdCases` / `DownloadManager` / `FeedService` behind interfaces)

### Refactor
- [X] Refactor Shell: split into partial files (UiShell.cs 1027 → 485 lines, plus Feeds/Theme/Chapters/Navigation)
- [X] Drop the duplicate `QueueUseCase` construction in `CmdCases` (first instance is overwritten immediately)
- [X] Add a `VerySlowNetwork` status, the second stage in `PlaybackCoordinator` fires `SlowNetwork` twice
- [X] Update CLAUDE.md

## Later

### Engine
- [ ] Add internal mac fallback engine
- [ ] Add native os-player interop (pausing via headphones, os-ui, ...)

### UX
- [ ] Rethink first-letter-highlighting
- [ ] `q` means two different things: the `q` key quits, but bare `q` typed into the command box is a documented alias for `:queue add` (`QueueUseCase.Handle`). The help browser lists both. Pick one and drop the other.

### Bugs
- [X] `ConfigStore` rejected the theme name `User`, the toggle's fourth stop, and rewrote it to `auto` on load. Not user-visible, because `UiThemeResolver` maps `auto` to `User` as well, but the stored value was wrong and would have drifted with any change of default. Also dropped `HighContrast`, which was never a `ThemeMode`
- [X] Removed `:feed remove` from the help catalog: it was documented as an alias of `:remove-feed` but `FeedUseCase` has no such subcommand, so it only ever printed a usage line
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
