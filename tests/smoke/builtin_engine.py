#!/usr/bin/env python3
"""Play episodes through the built-in engine in the real app.

#3: a Mac without VLC or mpv had nothing that could pause, seek or change
speed. podliner now carries its own engine. This runs the published binary
with --engine builtin in a real pty against a local server that honours
Range and counts the bytes it sends, and checks per episode:

  plays    the position runs, and L twice lands near 2:10
  jumps    :seek 45:00 lands there through a request opened near 45:00,
           and no request streams through the 43 minutes before it
  resumes  a later start picks up where the last one left off

Episodes are one hour of mp3 from ffmpeg: CBR with an Info header, VBR with
a Xing table, and CBR without any header.

usage: builtin_engine.py <binary>
"""
import fcntl, glob, http.server, json, os, pty, re, select, shutil, signal, socketserver
import struct, subprocess, sys, tempfile, termios, threading, time

KINDS = {
    "cbr":    ["-b:a", "128k"],
    "vbr":    ["-q:a", "5"],
    "noxing": ["-b:a", "128k", "-write_xing", "0"],
}


class Server:
    def __init__(self, root):
        self.requests = []      # [start byte, bytes sent] per mp3 request
        srv = self

        class H(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                p = os.path.join(root, self.path.lstrip("/"))
                if not os.path.isfile(p):
                    self.send_error(404)
                    return
                size = os.path.getsize(p)
                start, end = 0, size - 1
                m = re.match(r"bytes=(\d*)-(\d*)", self.headers.get("Range", ""))
                if m and m.group(1):
                    start = int(m.group(1))
                    if m.group(2):
                        end = min(int(m.group(2)), size - 1)
                    self.send_response(206)
                    self.send_header("Content-Range", f"bytes {start}-{end}/{size}")
                else:
                    self.send_response(200)
                self.send_header("Content-Type", "application/rss+xml" if p.endswith(".xml") else "audio/mpeg")
                self.send_header("Content-Length", str(end - start + 1))
                self.end_headers()
                entry = [start, 0]
                if p.endswith(".mp3"):
                    srv.requests.append(entry)
                with open(p, "rb") as f:
                    f.seek(start)
                    left = end - start + 1
                    try:
                        while left > 0:
                            c = f.read(min(16384, left))
                            if not c:
                                break
                            self.wfile.write(c)
                            left -= len(c)
                            entry[1] += len(c)
                    except (BrokenPipeError, ConnectionResetError):
                        pass

        socketserver.ThreadingTCPServer.allow_reuse_address = True
        self.httpd = socketserver.ThreadingTCPServer(("127.0.0.1", 0), H)
        self.httpd.daemon_threads = True
        threading.Thread(target=self.httpd.serve_forever, daemon=True).start()
        self.base = f"http://127.0.0.1:{self.httpd.server_address[1]}"


def start(binary, cfg, args):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ.update(TERM="xterm-256color", XDG_CONFIG_HOME=cfg)
        os.execv(binary, [binary, "--log-dir", os.path.join(cfg, "logs"), *args])
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    return pid, fd


def pump(fd, seconds):
    end = time.time() + seconds
    while time.time() < end:
        r, _, _ = select.select([fd], [], [], 0.1)
        if fd in r:
            try:
                if not os.read(fd, 65536):
                    return
            except OSError:
                return


def quit(pid, fd):
    os.write(fd, b"q")
    end = time.time() + 20
    while time.time() < end:
        pump(fd, 0.2)
        if os.waitpid(pid, os.WNOHANG)[0] == pid:
            return True
    os.kill(pid, signal.SIGKILL)
    os.waitpid(pid, 0)
    return False


def position_s(cfg):
    with open(os.path.join(cfg, "podliner", "library.json")) as f:
        eps = json.load(f).get("Episodes", [])
    return max(((e.get("Progress") or {}).get("LastPosMs") or 0) for e in eps) / 1000.0


def play_first(fd):
    os.write(fd, b"h"); pump(fd, 0.5)
    os.write(fd, b"l"); pump(fd, 0.5)
    os.write(fd, b"\r")


def scenario(kind, binary, server, size):
    cfg = tempfile.mkdtemp(prefix=f"podliner-builtin-{kind}-")
    try:
        opml = os.path.join(cfg, "in.opml")
        with open(opml, "w") as f:
            f.write('<?xml version="1.0"?><opml version="2.0"><body>'
                    f'<outline text="Builtin {kind}" type="rss" xmlUrl="{server.base}/{kind}.xml"/></body></opml>')
        # in a terminal the import also fetches the feed, which puts the
        # episode in the library
        pid, fd = start(binary, cfg, ["--opml-import", opml])
        pump(fd, 6)
        quit(pid, fd)

        path = os.path.join(cfg, "podliner", "appsettings.json")
        with open(path) as f:
            d = json.load(f)
        d.update(EnginePreference="builtin", Volume0_100=0, RefreshIntervalMinutes=0)
        with open(path, "w") as f:
            json.dump(d, f)

        pid, fd = start(binary, cfg, [])
        pump(fd, 3)
        play_first(fd)
        pump(fd, 5)
        os.write(fd, b"L"); pump(fd, 1.5)
        os.write(fd, b"L"); pump(fd, 2)
        ok_exit = quit(pid, fd)
        minutes = position_s(cfg)

        # a long jump: without segments the decoder would read all 43
        # minutes of audio before it, 43 MB through one request
        server.requests.clear()
        pid, fd = start(binary, cfg, [])
        pump(fd, 3)
        play_first(fd)
        pump(fd, 3)
        os.write(fd, b":"); pump(fd, 0.8)
        os.write(fd, b"seek 45:00\r"); pump(fd, 3)
        reqs = list(server.requests)
        ok_exit = quit(pid, fd) and ok_exit
        first = position_s(cfg)

        pid, fd = start(binary, cfg, [])
        pump(fd, 3)
        play_first(fd)
        pump(fd, 4)
        quit(pid, fd)
        second = position_s(cfg)

        log = "".join(open(p, errors="replace").read() for p in glob.glob(os.path.join(cfg, "logs", "*.log")))
        checks = {
            "builtin chosen": "chosen='builtin'" in log,
            "L jumps a minute": 125 <= minutes <= 140,
            "long jump lands": 2700 <= first <= 2710,
            "long jump opens near 45:00": any(start >= size * 0.7 for start, _ in reqs),
            "nothing streams the minutes before": all(sent < size * 0.4 for _, sent in reqs),
            "resumes": second >= first + 1,
            "exits": ok_exit,
        }
        longest = max((sent for _, sent in reqs), default=0)
        detail = (f"L twice {minutes:.0f}s, 45:00 jump {first:.0f}s in {len(reqs)} requests, "
                  f"longest {longest / 1e6:.1f} MB, "
                  f"resumed to {second:.0f}s")
        if not all(checks.values()):
            detail += "  failed: " + ", ".join(k for k, v in checks.items() if not v)
            detail += "\n" + "\n".join(l for l in log.splitlines() if "builtin" in l or "ERR" in l or "WRN" in l)[-2500:]
        return all(checks.values()), detail
    finally:
        shutil.rmtree(cfg, ignore_errors=True)


def main():
    binary = os.path.abspath(sys.argv[1])
    if not shutil.which("ffmpeg"):
        print("needs ffmpeg")
        return 2

    www = tempfile.mkdtemp(prefix="podliner-builtin-www-")
    server = Server(www)
    for kind, args in KINDS.items():
        subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                        "sine=frequency=300:duration=3600", "-ac", "2", *args,
                        os.path.join(www, f"{kind}.mp3")], check=True)
        size = os.path.getsize(os.path.join(www, f"{kind}.mp3"))
        with open(os.path.join(www, f"{kind}.xml"), "w") as f:
            f.write(f'<?xml version="1.0"?><rss version="2.0"><channel><title>Builtin {kind}</title>'
                    '<link>http://x.test</link><description>d</description>'
                    f'<item><title>Hour {kind}</title><guid>{kind}-1</guid>'
                    '<pubDate>Mon, 10 Aug 2026 10:00:00 GMT</pubDate>'
                    f'<enclosure url="{server.base}/{kind}.mp3" type="audio/mpeg" length="{size}"/></item>'
                    '</channel></rss>')

    failures = 0
    for kind in KINDS:
        try:
            ok, detail = scenario(kind, binary, server, os.path.getsize(os.path.join(www, f"{kind}.mp3")))
        except Exception as e:
            ok, detail = False, f"error: {e}"
        failures += not ok
        print(f"{kind:<7} {'ok  ' if ok else 'FAIL'}  {detail}", flush=True)

    shutil.rmtree(www, ignore_errors=True)
    print()
    print("ok: the built-in engine plays, seeks and resumes" if not failures else f"FAIL: {failures} kind(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
