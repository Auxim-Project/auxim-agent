"""Drive History with SGR reports and verify text delivered to a local clipboard double."""
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import signal
import struct
import sys
import tempfile
import termios
import time

from approval_grid import frame

CLI = Path(__file__).resolve().parents[2] / "src/Auxim.Cli/bin/Release/net10.0/auxim.dll"


def run(home):
    copied = home / "copied.txt"
    mode = home / "backend-mode"
    backend = home / ("pbcopy" if sys.platform == "darwin" else "wl-copy")
    backend.write_text('#!/bin/sh\n'
                       'if [ -f "$COPY_TEST_FAIL" ]; then exit 1; fi\nexec /bin/cat > "$COPY_TEST_OUTPUT"\n')
    backend.chmod(0o700)
    sessions = home / "sessions"
    sessions.mkdir()
    first = "FIRST 中文🙂 e\u0301 marker"
    wrapped = "SOFT-" + "abcdefghijklmnopqrstuvwxyz" * 5 + "-END"
    code = "    print('中文🙂')"
    many = [f"Line {i:02}." for i in range(40)]
    last = "LAST-MARKER"
    reply = "\n".join([wrapped, "```python", code, "```", *many, last])
    (sessions / "source.json").write_text(json.dumps({
        "id": "source", "title": "Copy fixture",
        "createdAt": "2026-11-18T00:00:00Z", "updatedAt": "2026-11-18T00:00:00Z",
        "messages": [{"role": "user", "content": first}, {"role": "assistant", "content": reply}]
    }))
    (home / "current_session").write_text("source")
    env = {k: v for k, v in os.environ.items() if not k.startswith("AUXIM_")}
    env.update(AUXIM_HOME=str(home), AUXIM_PROVIDER="local", TERM="xterm-256color",
               PATH=str(home) + os.pathsep + env["PATH"], WAYLAND_DISPLAY="auxim-test",
               DISPLAY="", COPY_TEST_OUTPUT=str(copied), COPY_TEST_FAIL=str(mode))
    child, fd = pty.fork()
    if child == 0:
        os.execvpe("dotnet", ["dotnet", str(CLI)], env)
    width = 80
    height = 24
    data = bytearray()

    def resize(columns):
        nonlocal width
        width = columns
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", height, width, 0, 0))
        os.kill(child, signal.SIGWINCH)

    def grid():
        return frame(bytes(data), width, height)[0]

    def drain(seconds):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            if not select.select([fd], [], [], min(.025, max(0, deadline - time.monotonic())))[0]:
                continue
            try:
                chunk = os.read(fd, 65536)
                if not chunk:
                    break
                data.extend(chunk)
            except OSError:
                break

    def send(keys):
        os.write(fd, keys.encode())
        drain(.09)

    def wait(check, seconds=6):
        deadline = time.monotonic() + seconds
        while not check():
            assert time.monotonic() < deadline, "\n".join(grid())
            drain(.05)

    def position(text):
        return next(((row.index(text) + 1, y + 1) for y, row in enumerate(grid()) if text in row), None)

    def mouse(code, x, y, final="M"):
        send(f"\x1b[<{code};{x};{y}{final}")

    def hit(text, code=0):
        wait(lambda: position(text) is not None)
        mouse(code, *position(text))

    def at_end(text):
        x, y = position(text)
        return x + len(text) - 1, y

    done = False
    try:
        resize(width)
        wait(lambda: position("Main") and b"\x1b[?1000h" in data)
        send("/history\r")
        wait(lambda: position("FIRST"))
        hit("FIRST")
        mouse(32, 75, 18)
        # A stationary pointer at the bottom keeps scrolling without more SGR reports.
        wait(lambda: position(last), seconds=8)
        mouse(0, *at_end(last), final="m")
        wait(lambda: position("Copy · selected"))
        assert b"\x1b[7m" in data
        mouse(64, 2, 5)
        assert position("Copy · selected")
        resize(32)
        wait(lambda: position("Copy · selected") and position("History"))
        hit("Copy ·")
        wait(lambda: position("Copied.") and copied.exists())
        expected = "\n".join([first, "", wrapped, code, *many, last])
        assert copied.read_text() == expected, repr(copied.read_text())

        resize(80)
        drain(.3)
        send("\x1b")
        wait(lambda: not position("Copy ·") and position("History"))
        send("\x1b[6~" * 8)
        wait(lambda: position(last))
        mouse(0, *at_end(last))
        mouse(32, 3, 3)
        mouse(64, 3, 3)  # Wheel also moves the active endpoint.
        wait(lambda: position("FIRST"), seconds=8)
        mouse(0, *position("FIRST"), final="m")
        assert position("Copy · selected")
        # Require a fresh write for each key, even if the old text matches.
        for keys in ("\x03", "\x1b[99;5u") + (("\x1b[99;9u",) if sys.platform == "darwin" else ()):
            copied.unlink()
            send(keys)
            wait(lambda: position("Copied.") and copied.exists())
            assert copied.read_text() == expected

        mode.touch()
        send("\x03")
        wait(lambda: position("rejected") or position("could not"))
        assert position("History") and position("Copy ·") and copied.read_text() == expected
        mode.unlink()
        send("\x1b")
        wait(lambda: not position("Copy ·") and position("History"))
        send("\x1b")
        wait(lambda: position("Main"))
        send("/session\r")
        wait(lambda: position("Sessions"))
        send("\x1b[C")
        wait(lambda: position("Session Preview") and position("FIRST"))
        hit("FIRST")
        x, y = position("FIRST")
        mouse(32, x + 4, y)
        mouse(0, x + 4, y, final="m")
        copied.unlink()
        send("\x03")
        wait(lambda: position("Copied.") and copied.exists() and copied.read_text() == "FIRST")
        send("\x1b")
        wait(lambda: position("Session Preview") and not position("Copy ·"))
        send("\x1b")
        wait(lambda: position("Sessions"))
        send("\x1b")
        wait(lambda: position("Main"))
        send("/exit\r")
        deadline = time.monotonic() + 5
        while not done:
            drain(.03)
            pid, status = os.waitpid(child, os.WNOHANG)
            if pid:
                done = True
                assert os.waitstatus_to_exitcode(status) == 0
            assert time.monotonic() <= deadline
        assert termios.tcgetattr(fd)[3] & termios.ECHO and b"\x1b[?1049l" in data
        print("PASS History and Preview: cross-screen, reverse, stationary edge, wheel, resize, Unicode, soft wrap, copy/failure, Esc")
    finally:
        if not done:
            os.kill(child, signal.SIGKILL)
            os.waitpid(child, 0)
        os.close(fd)


with tempfile.TemporaryDirectory(prefix="auxim-history-selection-") as directory:
    run(Path(directory))
