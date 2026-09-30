#!/usr/bin/env python3
"""Run --opml-import and --opml-export the way a Homebrew formula test would.

Homebrew could not package podliner: its formula test runs the binary with
no terminal, no audio engine and no network, and needs one real action that
ends on its own. --opml-import and --opml-export only ran inside the TUI, so
there was none. Here, with stdout and stderr piped, an empty PATH (no mpv,
no ffplay), libvlc hidden and a feed url nothing listens on:

  import+export  one run imports a file and exports it again
  export         a second run exports what the first one saved
  dry-run        changes nothing
  bad file       exits 1 and names the file

usage: headless_opml.py <binary>
"""
import glob, os, shutil, subprocess, sys, tempfile

FEED = "http://127.0.0.1:9/nothing-listens-here.xml"


def libvlc_files():
    found = []
    for d in ("/usr/lib", "/usr/lib64", "/usr/lib/x86_64-linux-gnu", "/usr/lib/aarch64-linux-gnu", "/usr/local/lib"):
        found += [p for p in glob.glob(os.path.join(d, "libvlc.so*")) if os.path.isfile(p) and not os.path.islink(p)]
    return found


def main():
    binary = os.path.abspath(sys.argv[1])
    cfg = tempfile.mkdtemp(prefix="podliner-headless-")
    empty_path = tempfile.mkdtemp(prefix="podliner-nopath-")

    wrap = []
    vlc = libvlc_files()
    if vlc:
        bwrap = shutil.which("bwrap")
        if not bwrap:
            print("SKIP: libvlc is installed here and bubblewrap is not, so it cannot be hidden")
            return 2
        wrap = [bwrap, "--dev-bind", "/", "/"]
        for f in vlc:
            wrap += ["--ro-bind", "/dev/null", f]

    env = dict(os.environ, XDG_CONFIG_HOME=cfg, PATH=empty_path)

    def run(*args):
        p = subprocess.run([*wrap, binary, "--no-file-logs", *args], env=env, stdin=subprocess.DEVNULL,
                           stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, timeout=30)
        return p.returncode, p.stdout + p.stderr

    opml_in = os.path.join(cfg, "in.opml")
    with open(opml_in, "w") as f:
        f.write('<?xml version="1.0"?><opml version="2.0"><head><title>t</title></head><body>'
                f'<outline text="Smoke Feed" type="rss" xmlUrl="{FEED}"/></body></opml>')
    out1 = os.path.join(cfg, "out1.opml")
    out2 = os.path.join(cfg, "out2.opml")

    def text(path):
        return open(path).read() if os.path.exists(path) else ""

    checks = {}
    try:
        code, log = run("--opml-import", opml_in, "--opml-export", out1)
        checks["import+export exits 0"] = code == 0
        checks["import+export wrote the feed"] = FEED in text(out1)

        code, log2 = run("--opml-export", out2)
        log += log2
        checks["export alone exits 0"] = code == 0
        checks["the import was saved"] = FEED in text(out2) and "Smoke Feed" in text(out2)

        dry = os.path.join(cfg, "dry.opml")
        with open(dry, "w") as f:
            f.write('<?xml version="1.0"?><opml version="2.0"><body>'
                    '<outline text="Other" type="rss" xmlUrl="http://127.0.0.1:9/other.xml"/></body></opml>')
        code, log3 = run("--opml-import", dry, "--import-mode", "dry-run", "--opml-export", out2)
        log += log3
        checks["dry run exits 0"] = code == 0
        checks["dry run changed nothing"] = "other.xml" not in text(out2)

        code, log4 = run("--opml-import", os.path.join(cfg, "missing.opml"))
        log += log4
        checks["a bad file exits 1"] = code == 1
        checks["a bad file is named"] = "missing.opml" in log4
        checks["no stack trace"] = "Unhandled exception" not in log and "   at " not in log
    except subprocess.TimeoutExpired:
        checks["finishes within 30 s"] = False
        log = ""
    finally:
        shutil.rmtree(cfg, ignore_errors=True)
        shutil.rmtree(empty_path, ignore_errors=True)

    for name, ok in checks.items():
        print(f"{'ok  ' if ok else 'FAIL'}  {name}")
    if not all(checks.values()):
        print("output:")
        print(log[:1500])
        return 1
    print("ok: opml import and export work with no terminal, engine or network")
    return 0


if __name__ == "__main__":
    sys.exit(main())
