#!/usr/bin/env python3
"""Check that podliner fetches feeds on its own, and only when it should.

Issue #32: feeds were only fetched when someone typed :refresh, so a library
could sit on months-old episodes with nothing on screen to say so. This runs
the published binary in a real pty against a local RSS server that counts
requests, and never presses a key.

  stale     never refreshed: a pass runs on start, the new episode shows up
            and the sidebar says "just now"
  recent    refreshed ten minutes ago: nothing is fetched on start
  offline   --offline: nothing is fetched, although a pass would be due
  off       RefreshIntervalMinutes 0: nothing is fetched
  timer     a pass that comes due while podliner runs is picked up by the
            timer, with no restart, and not before it is due

usage: auto_refresh.py <binary> [--only NAME...]
"""
import argparse, datetime, glob, http.server, json, os, pty, select, shutil, signal
import socketserver, struct, sys, tempfile, termios, fcntl, threading, time

NEW_EPISODE = "Brand New Episode 999"


class Feed:
    """A local RSS server that counts how often it is asked."""

    def __init__(self):
        self.requests = []
        self.items = ["Old Episode 001"]
        feed = self

        class H(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                feed.requests.append(time.time())
                body = feed.xml().encode()
                self.send_response(200)
                self.send_header("Content-Type", "application/rss+xml")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *a):
                pass

        self.srv = socketserver.ThreadingTCPServer(("127.0.0.1", 0), H)
        self.srv.daemon_threads = True
        threading.Thread(target=self.srv.serve_forever, daemon=True).start()
        self.url = f"http://127.0.0.1:{self.srv.server_address[1]}/feed.xml"

    def xml(self):
        items = []
        for i, title in enumerate(self.items):
            day = 10 + i
            items.append(
                f"<item><title>{title}</title><guid>ep-{i}</guid>"
                f"<pubDate>Mon, {day:02d} Aug 2026 10:00:00 GMT</pubDate>"
                f'<enclosure url="{self.url[:-8]}ep{i}.mp3" type="audio/mpeg" length="1000"/>'
                f"</item>")
        return ('<?xml version="1.0" encoding="UTF-8"?><rss version="2.0"><channel>'
                "<title>Smoke Feed</title><link>http://example.test</link>"
                "<description>test</description>" + "".join(items) + "</channel></rss>")


def run(binary, cfg, args, seconds, stop_when=None):
    """Run podliner for `seconds`, then quit with q. Returns the screen bytes."""
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["XDG_CONFIG_HOME"] = cfg
        os.execv(binary, [binary, "--log-dir", os.path.join(cfg, "logs"), *args])
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    out = bytearray()

    def pump(s):
        end = time.time() + s
        while time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.1)
            if fd in r:
                try:
                    c = os.read(fd, 65536)
                except OSError:
                    return
                if not c:
                    return
                out.extend(c)

    end = time.time() + seconds
    while time.time() < end:
        pump(0.5)
        if stop_when and stop_when():
            pump(1.5)
            break
    os.write(fd, b"q")
    t = time.time()
    while time.time() - t < 15:
        pump(0.2)
        if os.waitpid(pid, os.WNOHANG)[0] == pid:
            break
    else:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)
    return bytes(out)


def settings(cfg):
    return os.path.join(cfg, "podliner", "appsettings.json")


def patch_settings(cfg, **values):
    path = settings(cfg)
    with open(path) as f:
        d = json.load(f)
    d.update(values)
    with open(path, "w") as f:
        json.dump(d, f)


def iso(dt):
    return dt.isoformat()


def library_titles(cfg):
    with open(os.path.join(cfg, "podliner", "library.json")) as f:
        return [e.get("Title") for e in json.load(f).get("Episodes", [])]


def prepared(binary, feed):
    """A config dir whose library already holds the smoke feed."""
    cfg = tempfile.mkdtemp(prefix="podliner-refresh-")
    opml = os.path.join(cfg, "in.opml")
    with open(opml, "w") as f:
        f.write('<?xml version="1.0"?><opml version="2.0"><head><title>t</title></head><body>'
                f'<outline text="Smoke Feed" type="rss" xmlUrl="{feed.url}"/></body></opml>')
    run(binary, cfg, ["--opml-import", opml], 25,
        stop_when=lambda: os.path.exists(os.path.join(cfg, "podliner", "library.json"))
        and "Old Episode 001" in library_titles(cfg))
    if "Old Episode 001" not in library_titles(cfg):
        raise RuntimeError("could not prepare a library with the smoke feed")
    return cfg


def scenario(name, binary, feed):
    now = datetime.datetime.now(datetime.timezone.utc)
    cfg = prepared(binary, feed)
    feed.items = ["Old Episode 001", NEW_EPISODE]      # something new to find
    feed.requests.clear()
    started = time.time()

    if name == "stale":
        patch_settings(cfg, LastRefreshAt=None, RefreshIntervalMinutes=60)
        screen = run(binary, cfg, [], 30, stop_when=lambda: NEW_EPISODE in library_titles(cfg))
        titles = library_titles(cfg)
        ok = bool(feed.requests) and NEW_EPISODE in titles \
             and NEW_EPISODE.encode() in screen and b"just now" in screen
        detail = (f"requests={len(feed.requests)} new-in-library={NEW_EPISODE in titles} "
                  f"new-on-screen={NEW_EPISODE.encode() in screen} title-just-now={b'just now' in screen}")

    elif name == "recent":
        patch_settings(cfg, LastRefreshAt=iso(now - datetime.timedelta(minutes=10)), RefreshIntervalMinutes=60)
        run(binary, cfg, [], 12)
        ok = not feed.requests
        detail = f"requests={len(feed.requests)} (want 0)"

    elif name == "offline":
        patch_settings(cfg, LastRefreshAt=None, RefreshIntervalMinutes=60)
        run(binary, cfg, ["--offline"], 12)
        ok = not feed.requests
        detail = f"requests={len(feed.requests)} (want 0)"

    elif name == "off":
        patch_settings(cfg, LastRefreshAt=None, RefreshIntervalMinutes=0)
        run(binary, cfg, [], 12)
        ok = not feed.requests
        detail = f"requests={len(feed.requests)} (want 0)"

    elif name == "timer":
        # due twelve seconds after start: the start check (3s) must not fetch,
        # the timer (every 10s) must
        due_in = 12
        # measured right before the launch: preparing the library above runs
        # podliner for up to 25s, and a clock taken before that put the due
        # moment in the past
        launch = datetime.datetime.now(datetime.timezone.utc)
        patch_settings(cfg, RefreshIntervalMinutes=60,
                       LastRefreshAt=iso(launch - datetime.timedelta(minutes=60) + datetime.timedelta(seconds=due_in)))
        started = time.time()
        run(binary, cfg, [], 50, stop_when=lambda: bool(feed.requests))
        first = feed.requests[0] - started if feed.requests else None
        ok = first is not None and due_in - 1 <= first <= due_in + 15
        detail = f"first request after {first:.1f}s (want between {due_in}s and {due_in + 15}s)" if first else "no request"

    else:
        raise ValueError(name)

    shutil.rmtree(cfg, ignore_errors=True)
    return ok, detail


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("binary")
    ap.add_argument("--only", nargs="*")
    a = ap.parse_args()

    feed = Feed()
    failures = 0
    for name in ["stale", "recent", "offline", "off", "timer"]:
        if a.only and name not in a.only:
            continue
        try:
            ok, detail = scenario(name, a.binary, feed)
        except Exception as e:
            ok, detail = False, f"error: {e}"
        failures += not ok
        print(f"{name:<9} {'ok  ' if ok else 'FAIL'}  {detail}", flush=True)

    print()
    print("ok: feeds are fetched when due and only then" if not failures
          else f"FAIL: {failures} scenario(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
