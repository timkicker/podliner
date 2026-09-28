#!/usr/bin/env python3
"""Quit podliner every way there is, in a real pty, and check what it leaves.

Issue #34: podliner switched on mouse tracking and the alternate screen and
never switched them off, so the shell that got the terminal back printed
mouse reports as text. The restore sat at the end of the exit cleanup, and a
1500 ms watchdog called Environment.Exit whenever that cleanup ran long. The
gPodder push on exit always ran long: it dispatched onto a main loop that was
no longer running and waited for it.

Every scenario runs with gPodder auto-sync pointed at a server that accepts
and never answers, which is the slowest exit a user can have. Per scenario:

  exited   the process is gone within 20 s
  fast     it left within 1 s of the quit; around 1.5 s means the watchdog
           killed it instead of the cleanup finishing
  restored every private mode that was switched on is off again at the end
  mpv      no mpv child of this podliner outlives it (playback scenarios)

usage: exit_teardown.py <binary> [--library DIR] [--playback] [--only NAME...]

  --library DIR  copy an existing config dir (with library.json) instead of
                 starting empty; needed for the playback and save scenarios
  --playback     add the scenarios that start mpv; needs mpv and a library
"""
import argparse, json, os, pty, select, shutil, signal, socket, struct, subprocess
import sys, tempfile, termios, fcntl, threading, time

MODES = ["?1000", "?1002", "?1003", "?1006", "?1015", "?1049"]
WATCHDOG_S = 1.0

# (name, steps, needs_playback[, gpodder]). A step is ("keys", bytes, wait_s),
# ("signal", signum, 0) or ("play", None, wait_s). gpodder is "refused" by
# default, a server that says no at once; "slow" is one that never answers.
SCENARIOS = [
    ("q",              [("keys", b"q", 0)],                                        False),
    ("Q",              [("keys", b"Q", 0)],                                        False),
    ("ctrl-q",         [("keys", b"\x11", 0)],                                     False),
    (":q",             [("keys", b":", 0.8), ("keys", b"q\r", 0)],                  False),
    (":quit!",         [("keys", b":", 0.8), ("keys", b"quit!\r", 0)],              False),
    (":wq",            [("keys", b":", 0.8), ("keys", b"wq\r", 0)],                 False),
    ("help, q, q",     [("keys", b":", 0.8), ("keys", b"h\r", 2.0),
                        ("keys", b"q", 1.0), ("keys", b"q", 0)],                   False),
    ("logs, esc, q",   [("keys", b"\x1b[24~", 2.0), ("keys", b"\x1b", 1.0),
                        ("keys", b"q", 0)],                                        False),
    ("sigterm",        [("signal", signal.SIGTERM, 0)],                            False),
    ("playing, q",     [("play", None, 12), ("keys", b"q", 0)],                    True),
    ("playing, sigterm", [("play", None, 12), ("signal", signal.SIGTERM, 0)],      True),
    ("playing, sighup",  [("play", None, 12), ("signal", signal.SIGHUP, 0)],       True),
    # a gPodder server that never answers: the push can outlast the watchdog,
    # which is allowed, but the terminal must already be back by then
    ("q, slow gpodder",  [("keys", b"q", 0)],                                     False, "slow"),
]


def black_hole():
    s = socket.socket()
    s.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    s.bind(("127.0.0.1", 0))
    s.listen(64)
    held = []

    def run():
        while True:
            try:
                c, _ = s.accept()
                held.append(c)          # keep it open, never answer
            except OSError:
                return

    threading.Thread(target=run, daemon=True).start()
    return s.getsockname()[1]


def refused_port():
    s = socket.socket()
    s.bind(("127.0.0.1", 0))
    port = s.getsockname()[1]
    s.close()                       # nothing listens here any more
    return port


def mpv_of(pid):
    # only the mpv whose ipc socket carries this podliner's pid
    r = subprocess.run(["pgrep", "-a", "-x", "mpv"], capture_output=True, text=True).stdout
    return [int(l.split()[0]) for l in r.splitlines() if f"podliner-mpv-{pid}-" in l]


def make_config(library, port):
    cfg = tempfile.mkdtemp(prefix="podliner-exit-")
    target = os.path.join(cfg, "podliner")
    if library:
        shutil.copytree(library, target)
        for junk in ("gpodder.json",):
            try: os.remove(os.path.join(target, junk))
            except FileNotFoundError: pass
    else:
        os.makedirs(target)
    with open(os.path.join(target, "gpodder.json"), "w") as f:
        json.dump({
            "ServerUrl": f"http://127.0.0.1:{port}",
            "Username": "smoke", "Password": "smoke",
            "DeviceId": "smoke", "AutoSync": True, "Flavor": "gpoddernet",
        }, f)
    # silent: nobody should hear a test
    path = os.path.join(target, "appsettings.json")
    try:
        with open(path) as f: conf = json.load(f)
    except (FileNotFoundError, ValueError):
        conf = {}
    conf["Volume0_100"] = 0
    with open(path, "w") as f: json.dump(conf, f)
    return cfg


def run_scenario(binary, cfg, steps, extra_args):
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["XDG_CONFIG_HOME"] = cfg
        os.execv(binary, [binary, "--log-dir", os.path.join(cfg, "logs"), *extra_args])

    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    out = bytearray()

    def pump(seconds):
        end = time.time() + seconds
        while time.time() < end:
            r, _, _ = select.select([fd], [], [], 0.1)
            if fd in r:
                try:
                    chunk = os.read(fd, 65536)
                except OSError:
                    return
                if not chunk:
                    return
                out.extend(chunk)

    deadline = time.time() + 60
    while b"\x1b[?1049h" not in out and time.time() < deadline:
        pump(0.5)
    if b"\x1b[?1049h" not in out:
        os.kill(pid, signal.SIGKILL); os.waitpid(pid, 0)
        return {"error": "never switched to the alternate screen"}
    pump(3)

    had_mpv = []
    for kind, arg, wait in steps[:-1]:
        if kind == "keys":
            os.write(fd, arg)
        elif kind == "play":
            os.write(fd, b"\r")         # play the selected episode
        pump(wait)
        if kind == "play":
            had_mpv = mpv_of(pid)

    kind, arg, _ = steps[-1]
    if kind == "keys":
        os.write(fd, arg)
    else:
        os.kill(pid, arg)
    quit_at = time.time()

    exited = False
    while time.time() - quit_at < 20:
        pump(0.1)
        if os.waitpid(pid, os.WNOHANG)[0] == pid:
            exited = True
            break
    took = time.time() - quit_at
    if not exited:
        os.kill(pid, signal.SIGKILL); os.waitpid(pid, 0)
    pump(0.3)
    time.sleep(1.5)

    data = bytes(out)
    left_on = []
    for m in MODES:
        on, off = f"\x1b[{m}h".encode(), f"\x1b[{m}l".encode()
        if data.count(on) and data.rfind(off) < data.rfind(on):
            left_on.append(m)

    leftover = mpv_of(pid)
    for p in leftover:
        try: os.kill(p, signal.SIGTERM)
        except ProcessLookupError: pass

    return {"exited": exited, "took": took, "left_on": left_on,
            "had_mpv": bool(had_mpv), "mpv_left": len(leftover)}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("binary")
    ap.add_argument("--library")
    ap.add_argument("--playback", action="store_true")
    ap.add_argument("--only", nargs="*")
    a = ap.parse_args()

    if a.playback and not shutil.which("mpv"):
        print("--playback needs mpv on PATH"); return 2
    if a.playback and not a.library:
        print("--playback needs --library with episodes to play"); return 2

    ports = {"refused": refused_port(), "slow": black_hole()}
    failures = 0
    print(f"{'scenario':<18}{'exit':>6}{'secs':>7}  {'restored':<24}{'mpv':>6}")
    for name, steps, needs_playback, *rest in SCENARIOS:
        gpodder = rest[0] if rest else "refused"
        if needs_playback and not a.playback: continue
        if a.only and name not in a.only: continue
        cfg = make_config(a.library, ports[gpodder])
        extra = ["--engine", "mpv"] if needs_playback else []
        r = run_scenario(a.binary, cfg, steps, extra)
        shutil.rmtree(cfg, ignore_errors=True)

        if "error" in r:
            print(f"{name:<18} ERROR {r['error']}"); failures += 1; continue

        restored = "yes" if not r["left_on"] else "NO " + ",".join(r["left_on"])
        mpv = "-" if not needs_playback else (
              "none?" if not r["had_mpv"] else ("gone" if r["mpv_left"] == 0 else f"LEFT {r['mpv_left']}"))
        bad = (not r["exited"]) or r["left_on"] or r["took"] > WATCHDOG_S \
              or (needs_playback and r["mpv_left"])
        # a closed terminal (sighup) has nothing left to restore, and a
        # signal is not a quit the cleanup races against
        if name.endswith("sighup"):
            bad = (not r["exited"]) or bool(r["mpv_left"])
        if gpodder == "slow":
            bad = (not r["exited"]) or bool(r["left_on"]) or r["took"] > 3.0
        flag = "FAIL" if bad else "ok"
        failures += bool(bad)
        print(f"{name:<18}{('yes' if r['exited'] else 'NO'):>6}{r['took']:>7.2f}  {restored:<24}{mpv:>6}  {flag}")

    print()
    print("ok: every exit left the terminal as it found it" if not failures
          else f"FAIL: {failures} scenario(s)")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
