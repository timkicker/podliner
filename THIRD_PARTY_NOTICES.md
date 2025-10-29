# Third-party notices

This project uses open-source components. We thank the authors. Below is a non-exhaustive list of main dependencies and their licenses. Refer to each project for full license texts.

| Component | License | Link | Notes |
|---|---|---|---|
| Terminal.Gui | MIT | https://github.com/migueldeicaza/gui.cs | UI toolkit |
| Serilog | Apache-2.0 | https://serilog.net/ | Logging |
| Serilog.Sinks.File | Apache-2.0 | https://github.com/serilog/serilog-sinks-file | File sink |
| AngleSharp | MIT | https://anglesharp.github.io/ | HTML parsing |
| CodeHollow.FeedReader | MIT | https://github.com/codehollow/FeedReader | RSS/Atom |
| Microsoft.Data.Sqlite | MIT | https://learn.microsoft.com/dotnet/standard/data/sqlite/ | SQLite provider |
| LibVLCSharp | LGPL-2.1-or-later | https://github.com/videolan/libvlcsharp | VLC bindings |
| VideoLAN.LibVLC.Windows | LGPL-2.1-or-later | https://www.nuget.org/packages/VideoLAN.LibVLC.Windows | LibVLC binaries for Windows |
| NAudio | MIT | https://github.com/naudio/NAudio | Windows audio (Media Foundation) |
| mpv | GPL-2.0-or-later | https://mpv.io/ | External player (optional) |
| FFmpeg / ffplay | LGPL/GPL (per build options) | https://ffmpeg.org/legal.html | External tools (optional) |
| VLC / LibVLC | LGPL-2.1-or-later | https://www.videolan.org/legal.html | Engine used via LibVLCSharp |

**Notes**

- If you redistribute LibVLC binaries (e.g. via `VideoLAN.LibVLC.Windows`), include the corresponding LGPL license text and keep binary notices intact. Provide a link to the source (VideoLAN) as required by LGPL.
- mpv and FFmpeg are **not** bundled in standard builds; users install them separately. Their licenses apply to those binaries.
- Full license texts are provided by the respective NuGet packages and repositories.
