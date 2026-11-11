#!/usr/bin/env python3
"""Play a 70 s episode with mpv and check where it gets marked played (#40).

By default an episode counts as played 30 s before its end (or at 90%), so
this one is marked at about 40 s. With MarkPlayedAtEnd (":played-at end") it
must stay unplayed until playback actually reaches the end; that run sets it
with the command, so it also checks that the command is saved. Reads the mark
and the position it was set at from library.json, which is saved the moment
the mark is set.

mpv runs through a wrapper with --ao=null, so nothing reaches a sound card.

usage: played_at_end.py <binary> [--only near-end|end]
"""
import argparse, fcntl, functools, glob, http.server, json, os, pty, select, shutil, signal
import socketserver, stat, struct, subprocess, sys, tempfile, termios, threading, time

LEN_S = 70
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


def start(binary, cfg, args, path):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ.update(TERM="xterm-256color", XDG_CONFIG_HOME=cfg, PATH=path)
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


def prepared(binary, base, path):
    cfg = tempfile.mkdtemp(prefix="podliner-played-")
    opml = os.path.join(cfg, "in.opml")
    with open(opml, "w") as f:
        f.write('<?xml version="1.0"?><opml version="2.0"><body>'
                f'<outline text="Played Show" type="rss" xmlUrl="{base}/feed.xml"/></body></opml>')
    pid, fd = start(binary, cfg, ["--opml-import", opml], path)
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
    settings = os.path.join(cfg, "podliner", "appsettings.json")
    with open(settings) as f:
        d = json.load(f)
    d.update(EnginePreference="mpv", Volume0_100=0, RefreshIntervalMinutes=0)
    with open(settings, "w") as f:
        json.dump(d, f)
    return cfg


def scenario(mode, binary, base, path):
    at_end = mode == "end"
    cfg = prepared(binary, base, path)
    try:
        pid, fd = start(binary, cfg, [], path)
        pump(fd, 4)
        if at_end:
            os.write(fd, b":played-at end\r"); pump(fd, 1)
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
        started = time.time()

        # watch library.json until the mark shows up or the episode is long over
        marked_pos = marked_after = None
        last_pos = 0
        while time.time() - started < LEN_S + 15:
            pump(fd, 0.5)
            try:
                ep = library(cfg)["Episodes"][0]
            except (OSError, ValueError, LookupError):
                continue
            last_pos = (ep.get("Progress") or {}).get("LastPosMs") or 0
            if ep.get("ManuallyMarkedPlayed"):
                marked_pos, marked_after = last_pos, time.time() - started
                break

        os.write(fd, b"q")
        gone = wait_gone(pid, fd)
        with open(os.path.join(cfg, "podliner", "appsettings.json")) as f:
            saved = json.load(f).get("MarkPlayedAtEnd")

        if marked_pos is None:
            ok, detail = False, f"never marked played, last saved position {last_pos / 1000:.1f}s"
        elif at_end:
            ok = marked_pos >= (LEN_S - 2) * 1000
            detail = f"marked at {marked_pos / 1000:.1f}s of {LEN_S}s, {marked_after:.0f}s after play (want the end)"
        else:
            ok = marked_pos <= (LEN_S - 25) * 1000
            detail = f"marked at {marked_pos / 1000:.1f}s of {LEN_S}s, {marked_after:.0f}s after play (want about {LEN_S - 30}s)"
        if saved is not at_end:
            ok, detail = False, f"{detail}, but MarkPlayedAtEnd saved as {saved}"
        ok = ok and gone
        if not ok:
            for log in glob.glob(os.path.join(cfg, "logs", "*.log")):
                with open(log, errors="replace") as f:
                    print("".join(f.readlines()[-25:]))
        return ok, detail
    finally:
        shutil.rmtree(cfg, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("binary")
    ap.add_argument("--only", nargs="*")
    a = ap.parse_args()

    mpv = shutil.which("mpv")
    if not shutil.which("ffmpeg") or not mpv:
        print("needs ffmpeg and mpv")
        return 2

    www = tempfile.mkdtemp(prefix="podliner-played-www-")
    bindir = tempfile.mkdtemp(prefix="podliner-played-bin-")
    wrapper = os.path.join(bindir, "mpv")
    with open(wrapper, "w") as f:
        f.write(f'#!/bin/sh\nexec "{mpv}" --ao=null "$@"\n')
    os.chmod(wrapper, os.stat(wrapper).st_mode | stat.S_IXUSR)
    path = bindir + os.pathsep + os.environ.get("PATH", "")

    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                    f"anullsrc=r=44100:cl=mono", "-t", str(LEN_S), "-q:a", "9",
                    os.path.join(www, "ep.mp3")], check=True)
    base = serve(www)
    with open(os.path.join(www, "feed.xml"), "w") as f:
        f.write('<?xml version="1.0"?><rss version="2.0"><channel><title>Played Show</title>'
                '<link>http://x.test</link><description>d</description>'
                '<item><title>Silent Episode</title><guid>p1</guid>'
                '<pubDate>Mon, 10 Aug 2026 10:00:00 GMT</pubDate>'
                f'<enclosure url="{base}/ep.mp3" type="audio/mpeg" length="1"/></item>'
                '</channel></rss>')

    failures = 0
    for mode in ["near-end", "end"]:
        if a.only and mode not in a.only:
            continue
        try:
            ok, detail = scenario(mode, a.binary, base, path)
        except Exception as e:
            ok, detail = False, f"error: {e}"
        failures += not ok
        print(f"{mode:<9} {'ok  ' if ok else 'FAIL'}  {detail}", flush=True)

    shutil.rmtree(www, ignore_errors=True)
    shutil.rmtree(bindir, ignore_errors=True)
    print()
    print("ok: near-end marks early, end waits for the end" if not failures
          else f"FAIL: {failures} scenario(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
