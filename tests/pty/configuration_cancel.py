"""Exercise configuration prompts on a controlling PTY with an isolated Auxim home."""
import fcntl
import os
from pathlib import Path
import pty
import re
import select
import signal
import struct
import tempfile
import termios
import time

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/Auxim.Cli/bin/Release/net10.0/auxim.dll"


def run(home, kitty):
    env = {key: value for key, value in os.environ.items()
           if not key.startswith("AUXIM_") and key not in ("OPENAI_API_KEY", "NO_COLOR")}
    env.update(AUXIM_HOME=str(home), AUXIM_PROVIDER="local", TERM="xterm-256color")
    if not kitty:
        env["AUXIM_NO_ALT_SCREEN"] = "1"
    config, secrets = home / "config.json", home / ".env"
    pid, fd = pty.fork()
    if pid == 0:
        os.execvpe("dotnet", ["dotnet", str(CLI)], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 30, 120, 0, 0))
    data = bytearray()
    pending = b""
    flags = 0
    stack = []

    def pump():
        nonlocal pending, flags
        if not select.select([fd], [], [], 0.03)[0]:
            return
        try:
            chunk = os.read(fd, 65536)
        except OSError:
            return
        data.extend(chunk)
        pending += chunk
        end = 0
        for match in re.finditer(rb"\x1b\[[0-?]*[ -/]*[@-~]", pending):
            code = match.group()
            end = match.end()
            if code in (b"\x1b[>1u", b"\x1b[>0u"):
                stack.append(flags)
                flags = int(chr(code[-2]))
            elif code == b"\x1b[<u":
                flags = stack.pop()
            elif code == b"\x1b[?u" and kitty:
                os.write(fd, f"\x1b[?{flags}u".encode())
            elif code == b"\x1b[6n":
                os.write(fd, b"\x1b[20;1R")
        tail = pending[end:]
        pending = tail[tail.rfind(b"\x1b"):] if b"\x1b" in tail else b""

    def wait(marker, start):
        deadline = time.monotonic() + 8
        while marker not in data[start:]:
            assert time.monotonic() < deadline, (kitty, marker, bytes(data[-800:]))
            pump()

    def step(keys, marker):
        start = len(data)
        os.write(fd, keys)
        wait(marker, start)
        return start

    def wizard_to_key():
        step(b"/model set\r", b"Provider [")
        step(b"1\r", b"Model [")
        step(b"\r", b"Base URL [")
        step(b"\r", b"Save API key now?")
        assert not config.exists(), "configuration saved before the final prompts"
        step(b"\r", b"OPENAI_API_KEY:")
        return len(data)

    def verify_composer():
        start = step(b"wizard recovery check\r", b"Auxim received: wizard recovery check")
        wait(b"done in", start)
        deadline = time.monotonic() + 8
        while flags != int(kitty):
            assert time.monotonic() < deadline, "keyboard mode was not restored"
            pump()

    reaped = False
    try:
        wait(b"\x1b[>1u\x1b[?u" if kitty else b"auxim", 0)
        step(b"/model set\r", b"Provider [")
        start = len(data)
        os.kill(pid, signal.SIGINT)
        wait(b"Configuration cancelled.", start)
        assert not config.exists() and b"Model [" not in data[start:]
        verify_composer()

        if kitty:
            step(b"/model set\r", b"Provider [")
            assert flags == 1
            step(b"\x1b[99;5u", b"Configuration cancelled.")
            assert not config.exists()
            verify_composer()

        start = wizard_to_key()
        cancel = b"\x1b[99;5u" if kitty else b"\x03"
        step(b"partial-test-key" + cancel * 2, b"Configuration cancelled.")
        assert b"partial-test-key" not in data[start:]
        assert not config.exists() and not secrets.exists()
        verify_composer()

        start = wizard_to_key()
        step(b"eof-test-key\x04", b"Input ended before configuration was complete.")
        assert b"eof-test-key" not in data[start:]
        assert not config.exists() and not secrets.exists()
        verify_composer()

        start = wizard_to_key()
        config.mkdir()
        step(b"unsaved-test-key\r", b"Error:")
        assert b"unsaved-test-key" not in data[start:] and not secrets.exists()
        config.rmdir()
        verify_composer()

        start = wizard_to_key()
        step(b"complete-test-key\r", b"API key saved")
        assert b"complete-test-key" not in data[start:]
        assert b"openai-api" in config.read_bytes()
        assert b"OPENAI_API_KEY=complete-test-key" in secrets.read_bytes()
        if kitty:
            wait(b"Press Enter to return", start)
            step(b"\r", b"Message Auxim...")
        verify_composer()

        saved = (config.read_bytes(), secrets.read_bytes())
        step(b"/model set\r", b"Provider [")
        step(b"\x04", b"Input ended before configuration was complete.")
        assert (config.read_bytes(), secrets.read_bytes()) == saved
        verify_composer()

        os.write(fd, b"/exit\r")
        deadline = time.monotonic() + 8
        while not reaped:
            pump()
            done, status = os.waitpid(pid, os.WNOHANG)
            if done:
                reaped = True
                assert os.waitstatus_to_exitcode(status) == 0
            assert time.monotonic() < deadline
        while stack and time.monotonic() < deadline:
            pump()
        assert not stack and flags == 0
        assert termios.tcgetattr(fd)[3] & termios.ECHO
        print(f"PASS configuration prompts (Kitty={kitty})", flush=True)
    finally:
        if not reaped:
            os.kill(pid, signal.SIGKILL)
            os.waitpid(pid, 0)
        os.close(fd)


with tempfile.TemporaryDirectory(prefix="auxim-config-pty-") as temp:
    for kitty in (False, True):
        run(Path(temp) / str(kitty), kitty)
