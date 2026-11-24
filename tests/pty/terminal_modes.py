"""Verify ownership of alternate-screen transitions with a real CLI PTY."""
import fcntl
import os
from pathlib import Path
import pty
import select
import signal
import struct
import subprocess
import tempfile
import termios
import time

CLI = Path(__file__).resolve().parents[2] / "src/Auxim.Cli/bin/Release/net10.0/auxim.dll"
ENTER = b"\x1b[?1049h"
LEAVE = b"\x1b[?1049l"


def run(home, inline, exit_kind):
    if exit_kind == "exception":
        home.touch()  # A regular file cannot hold runtime state.
    master, slave = pty.openpty()
    fcntl.ioctl(slave, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    before = termios.tcgetattr(slave)
    env = {key: value for key, value in os.environ.items() if not key.startswith("AUXIM_")}
    env.update(TERM="xterm-256color", AUXIM_HOME=str(home), AUXIM_PROVIDER="local",
               AUXIM_NO_ALT_SCREEN="1" if inline else "0")
    args = ["dotnet", str(CLI)]
    process = subprocess.Popen(args, cwd=home.parent, env=env, stdin=slave, stdout=slave, stderr=slave)
    data = bytearray()

    def drain():
        if select.select([master], [], [], .1)[0]:
            data.extend(os.read(master, 65536))

    try:
        deadline = time.monotonic() + 8
        if exit_kind != "exception":
            while b"\x1b[?2004h" not in data:
                assert process.poll() is None and time.monotonic() < deadline, bytes(data[-300:])
                drain()
            if exit_kind == "normal":
                os.write(master, b"/exit\r")
            else:
                process.send_signal(exit_kind)

        while process.poll() is None:
            assert time.monotonic() < deadline, exit_kind
            drain()
        while select.select([master], [], [], .05)[0]:
            drain()

        expected = {"normal": 0, "exception": 1}.get(exit_kind)
        if expected is None:
            expected = 128 + exit_kind
        assert process.returncode == expected, bytes(data[-300:])
        if exit_kind == "exception":
            assert b"Error:" in data
        assert data.count(ENTER) == data.count(LEAVE) == (0 if inline else 1)
        if not inline:
            assert data.index(ENTER) < data.index(LEAVE)
        if b"\x1b[?2004h" in data:
            assert data.rfind(b"\x1b[?2004l") > data.rfind(b"\x1b[?2004h")
        after = termios.tcgetattr(slave)
        before[3] &= ~termios.PENDIN
        after[3] &= ~termios.PENDIN
        assert before == after
        print(f"PASS {'inline' if inline else 'alternate'} exit={exit_kind}, screen ownership and termios restored", flush=True)
    finally:
        if process.poll() is None:
            process.kill()
            process.wait()
        os.close(master)
        os.close(slave)


with tempfile.TemporaryDirectory(prefix="auxim-terminal-modes-") as directory:
    root = Path(directory)
    for inline in (True, False):
        for exit_kind in ("normal", "exception", signal.SIGTERM, signal.SIGHUP):
            run(root / f"{int(inline)}-{exit_kind}", inline, exit_kind)

    env = {key: value for key, value in os.environ.items() if not key.startswith("AUXIM_")}
    env.update(AUXIM_HOME=str(root / "command"), AUXIM_PROVIDER="local", TERM="dumb")
    alias = subprocess.run(["dotnet", str(CLI), "tools"], env=env, capture_output=True, timeout=8, check=True)
    direct = subprocess.run(["dotnet", str(CLI), "tool", "list"], env=env, capture_output=True, timeout=8, check=True)
    assert alias.stdout and alias.stdout == direct.stdout and alias.stderr == direct.stderr
    print("PASS noninteractive tools keeps tool list output and exit code", flush=True)
