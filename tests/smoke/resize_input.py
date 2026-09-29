#!/usr/bin/env python3
"""After a terminal resize, the next key must still be the key that was typed.

podliner tells ncurses the new size itself when Terminal.Gui misses a resize
(#4). It used ncurses' resizeterm for that, which also pushes KEY_RESIZE back
into the input queue; Terminal.Gui 1.19 drains that and then maps the next
real character as a function key. The first key after every resize arrived
as "Unknown": type ":" and no command box opened, and the rest of the command
ran as shortcuts (a space toggled playback, "t" switched the theme). Shipped
in 2.0.0, found by clicking through the app.

This resizes a real pty a few times and types a command straight after.

usage: resize_input.py <binary>
"""
import fcntl, glob, os, pty, select, shutil, signal, struct, sys, tempfile, termios, time


def main():
    binary = sys.argv[1]
    cfg = tempfile.mkdtemp(prefix="podliner-resize-")
    os.makedirs(os.path.join(cfg, "podliner"))
    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["XDG_CONFIG_HOME"] = cfg
        os.execv(binary, [binary, "--log-dir", os.path.join(cfg, "logs"), "--log-level", "debug"])

    def size(rows, cols):
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", rows, cols, 0, 0))
        os.kill(pid, signal.SIGWINCH)

    out = bytearray()

    def pump(s):
        end = time.time() + s
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

    def log():
        files = glob.glob(os.path.join(cfg, "logs", "*.log"))
        return open(files[0], errors="replace").read().splitlines() if files else []

    size(40, 140)
    # wait for the ui to take over the screen, like the other smoke tests;
    # a fixed sleep was not enough on a slow runner
    deadline = time.time() + 60
    while b"\x1b[?1049h" not in out and time.time() < deadline:
        pump(0.5)
    if b"\x1b[?1049h" not in out:
        print("FAIL: podliner never switched to the alternate screen")
        return 1
    pump(3)

    failures = 0
    for i, (rows, cols) in enumerate([(30, 90), (40, 140), (24, 80), (45, 160)]):
        size(rows, cols)
        pump(2)
        mark = len(log())
        minutes = 10 + i
        os.write(fd, b":")
        pump(0.3)
        os.write(fd, f"sleep {minutes}m".encode())
        pump(0.3)
        os.write(fd, b"\r")
        pump(1)
        cmds = [l.split("cmd ", 1)[1].strip() for l in log()[mark:] if "cmd :" in l]
        ok = cmds == [f":sleep {minutes}m"]
        failures += not ok
        print(f"after resize to {cols}x{rows}: {'ok  ' if ok else 'FAIL'} got {cmds or 'no command'}")
        if not ok:
            for line in log()[-12:]:
                print("    log:", line[:140])

    os.write(fd, b"q")
    pump(2)
    try:
        os.kill(pid, signal.SIGKILL)
    except ProcessLookupError:
        pass
    shutil.rmtree(cfg, ignore_errors=True)
    print()
    print("ok: typing after a resize reaches the command box" if not failures
          else f"FAIL: {failures} resize(s) ate the first key")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
