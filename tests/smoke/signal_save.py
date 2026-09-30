#!/usr/bin/env python3
"""Close podliner with a signal while it plays, and check the position was kept.

Closing the terminal window sends SIGHUP, shutting down sends SIGTERM. Neither
went through the exit cleanup, so nothing was saved on the way out, and the
position during playback is only written every 30 s: up to 30 s of listening
was lost every time. This plays a local episode for 20 s, ends podliner the
given way and reads the position back from library.json.

  q        the normal quit, for comparison
  sigterm  kill <pid>, a shutdown
  sighup   the terminal window closed

usage: signal_save.py <binary> [--only NAME...]
"""
import argparse, fcntl, glob, functools, http.server, json, os, pty, select, shutil, signal
import socketserver, struct, subprocess, sys, tempfile, termios, threading, time

PLAY_S = 20
WANT_MS = 16_000        # near the 20 s played; the last periodic save is up to 30 s old


AUDIO_REQUESTS = []


def serve(www):
    class Quiet(http.server.SimpleHTTPRequestHandler):
        def log_message(self, *a):
            pass

        def do_GET(self):
            if self.path.endswith(".mp3"):
                AUDIO_REQUESTS.append(time.time())
            super().do_GET()

    handler = functools.partial(Quiet, directory=www)
    srv = socketserver.ThreadingTCPServer(("127.0.0.1", 0), handler)
    srv.daemon_threads = True
    threading.Thread(target=srv.serve_forever, daemon=True).start()
    return f"http://127.0.0.1:{srv.server_address[1]}"


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


def wait_gone(pid, fd, seconds=20):
    end = time.time() + seconds
    while time.time() < end:
        pump(fd, 0.2)
        if os.waitpid(pid, os.WNOHANG)[0] == pid:
            return True
    os.kill(pid, signal.SIGKILL)
    os.waitpid(pid, 0)
    return False


def library(cfg):
    with open(os.path.join(cfg, "podliner", "library.json")) as f:
        return json.load(f)


def prepared(binary, base):
    """A config dir whose library holds one local episode, engine mpv, volume 0.

    mpv, because ffplay reports no position at all."""
    cfg = tempfile.mkdtemp(prefix="podliner-signal-")
    opml = os.path.join(cfg, "in.opml")
    with open(opml, "w") as f:
        f.write('<?xml version="1.0"?><opml version="2.0"><body>'
                f'<outline text="Signal Show" type="rss" xmlUrl="{base}/feed.xml"/></body></opml>')
    pid, fd = start(binary, cfg, ["--opml-import", opml])
    end = time.time() + 25
    while time.time() < end:
        pump(fd, 0.5)
        try:
            if library(cfg).get("Episodes"):
                break
        except (OSError, ValueError):
            pass
    os.write(fd, b"q")
    wait_gone(pid, fd)
    if not library(cfg).get("Episodes"):
        raise RuntimeError("could not prepare a library with the local feed")
    path = os.path.join(cfg, "podliner", "appsettings.json")
    with open(path) as f:
        d = json.load(f)
    d.update(EnginePreference="mpv", Volume0_100=0, RefreshIntervalMinutes=0)
    with open(path, "w") as f:
        json.dump(d, f)
    return cfg


def scenario(name, binary, base):
    cfg = prepared(binary, base)
    try:
        pid, fd = start(binary, cfg, [])
        pump(fd, 4)
        # to the episode list, play the one episode; a cold first start on
        # a runner can take longer than that to take keys, so go again
        # until the audio is actually asked for
        AUDIO_REQUESTS.clear()
        for _ in range(4):
            os.write(fd, b"h"); pump(fd, 0.5)
            os.write(fd, b"l"); pump(fd, 0.5)
            os.write(fd, b"\r")
            end = time.time() + 5
            while not AUDIO_REQUESTS and time.time() < end:
                pump(fd, 0.2)
            if AUDIO_REQUESTS:
                break
        pump(fd, PLAY_S)

        if name == "q":
            os.write(fd, b"q")
        else:
            os.kill(pid, {"sigterm": signal.SIGTERM, "sighup": signal.SIGHUP}[name])
        gone = wait_gone(pid, fd)

        eps = library(cfg)["Episodes"]
        pos = max((e.get("Progress") or {}).get("LastPosMs") or 0 for e in eps)
        ok = gone and pos >= WANT_MS
        if not ok:
            for log in glob.glob(os.path.join(cfg, "logs", "*.log")):
                with open(log, errors="replace") as f:
                    print("".join(f.readlines()[-25:]))
        return ok, f"exited={gone} saved position {pos / 1000:.1f}s (want at least {WANT_MS / 1000:.0f}s)"
    finally:
        shutil.rmtree(cfg, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("binary")
    ap.add_argument("--only", nargs="*")
    a = ap.parse_args()

    if not shutil.which("ffmpeg") or not shutil.which("mpv"):
        print("needs ffmpeg and mpv")
        return 2

    www = tempfile.mkdtemp(prefix="podliner-signal-www-")
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                    "sine=frequency=440:duration=120", "-q:a", "9",
                    os.path.join(www, "ep.mp3")], check=True)
    base = serve(www)
    with open(os.path.join(www, "feed.xml"), "w") as f:
        f.write('<?xml version="1.0"?><rss version="2.0"><channel><title>Signal Show</title>'
                '<link>http://x.test</link><description>d</description>'
                '<item><title>Sine Episode</title><guid>s1</guid>'
                '<pubDate>Mon, 10 Aug 2026 10:00:00 GMT</pubDate>'
                f'<enclosure url="{base}/ep.mp3" type="audio/mpeg" length="1"/></item>'
                '</channel></rss>')

    failures = 0
    for name in ["q", "sigterm", "sighup"]:
        if a.only and name not in a.only:
            continue
        try:
            ok, detail = scenario(name, a.binary, base)
        except Exception as e:
            ok, detail = False, f"error: {e}"
        failures += not ok
        print(f"{name:<8} {'ok  ' if ok else 'FAIL'}  {detail}", flush=True)

    shutil.rmtree(www, ignore_errors=True)
    print()
    print("ok: the position survives every way of ending podliner" if not failures
          else f"FAIL: {failures} scenario(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
