# Roadmap

## v0.1.0
- [ ] Fix play-button visual (doesn't work on mac at all, no first update on linux)
- [ ] Fix VLC recogn on Mac. MPV seems to work. Maybe add `VideoLAN.LibVLC.Mac`
- [X] Fix remove-feed on save
- [X] Fix download percentage top right 
- [X] Rework default theme(+ as default)
- [X] Add more logging to engine recogn.
- [X] Add more commands to menubar
- [ ] Add NAudio for windows fallback
    - [ ] Implement
    - [ ] Test on win/linux/mac
    - [X] Update documentation
- [X] Update macos-installer
- [X] Update readme (download-section)

## v0.2.0
- [ ] Add native os-player interop (pausing via headphones, os-ui, ...)
- [ ] Fix arrow-keys support

## General (good-first-issue?)

### Refactor 
- [ ] Refactor Shell: split into subclasses
- [X] Refactor CmdParser: Replace if-hell yanderedev-style. This is rediculous and was just meant for testing... Map?

### UX
- [ ] Rethink first-letter-highlighting
 
