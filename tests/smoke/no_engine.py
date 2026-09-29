#!/usr/bin/env python3
"""Start podliner with no audio engine anywhere and check it says so.

With no VLC, mpv or ffplay podliner died with an unhandled exception, exit
code 134 and a stack trace in the terminal: the first thing someone saw after
installing it without its optional dependencies. It has to exit with 1 and a
message naming what to install.

mpv and ffplay are hidden with an empty PATH. libvlc is a library, not on
PATH; where it is installed it gets hidden with bubblewrap. Where it is
installed and bubblewrap is not, the test cannot hide it and says so rather
than passing.

usage: no_engine.py <binary>
"""
import ctypes.util, fcntl, glob, os, pty, re, select, shutil, signal, struct, sys, tempfile, termios, time


def libvlc_files():
    found = []
    for d in ("/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/local/lib"):
        found += [p for p in glob.glob(os.path.join(d, "libvlc.so*")) if os.path.isfile(p) and not os.path.islink(p)]
    return found


def main():
    binary = os.path.abspath(sys.argv[1])
    cfg = tempfile.mkdtemp(prefix="podliner-noengine-")
    empty_path = tempfile.mkdtemp(prefix="podliner-nopath-")

    argv = [binary, "--log-dir", os.path.join(cfg, "logs")]
    vlc = libvlc_files()
    if vlc:
        bwrap = shutil.which("bwrap")
        if not bwrap:
            print("SKIP: libvlc is installed here and bubblewrap is not, so it cannot be hidden")
            return 2
        hide = []
        for f in vlc:
            hide += ["--ro-bind", "/dev/null", f]
        argv = [bwrap, "--dev-bind", "/", "/", *hide, *argv]

    pid, fd = pty.fork()
    if pid == 0:
        os.environ["TERM"] = "xterm-256color"
        os.environ["XDG_CONFIG_HOME"] = cfg
        os.environ["PATH"] = empty_path
        os.execv(argv[0], argv)

    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 40, 140, 0, 0))
    out = bytearray()
    code = None
    start = time.time()
    while time.time() - start < 30:
        r, _, _ = select.select([fd], [], [], 0.2)
        if fd in r:
            try:
                chunk = os.read(fd, 65536)
            except OSError:
                chunk = b""
            out.extend(chunk)
        w, status = os.waitpid(pid, os.WNOHANG)
        if w == pid:
            code = os.waitstatus_to_exitcode(status)
            break
    if code is None:
        os.kill(pid, signal.SIGKILL)
        os.waitpid(pid, 0)

    text = re.sub(r"\x1b\[[0-9;?]*[a-zA-Z]", "", out.decode("utf-8", "replace"))
    shutil.rmtree(cfg, ignore_errors=True)
    shutil.rmtree(empty_path, ignore_errors=True)

    checks = {
        "exits by itself": code is not None,
        "exit code 1": code == 1,
        "no stack trace": "Unhandled exception" not in text and "   at " not in text,
        "names the fix": all(w in text for w in ("vlc", "mpv", "ffmpeg")),
    }
    for name, ok in checks.items():
        print(f"{'ok  ' if ok else 'FAIL'}  {name}")
    if not all(checks.values()):
        print(f"exit code: {code}")
        print("output:")
        print("\n".join(l for l in text.splitlines() if l.strip())[:1200])
        return 1
    print("ok: with no audio engine podliner says what to install")
    return 0


if __name__ == "__main__":
    sys.exit(main())
