# Roadmap

## v1.0.0
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

## v1.1.0
- [ ] Fix playerui update (windows only?)
- [X] Fix play-button visual on mac

## v1.2.0
- [ ] Add native os-player interop (pausing via headphones, os-ui, ...)

## General (good-first-issue?)

### Engine
- [ ] Add internal mac fallback engine

### Refactor 
- [ ] Refactor Shell: split into subclasses
- [X] Refactor CmdParser: Replace if-hell yanderedev-style. This is rediculous and was just meant for testing.

### UX
- [ ] Rethink first-letter-highlighting

### Bugs
- [ ] Refresh/Redraw ui on mac/linux after moving window
