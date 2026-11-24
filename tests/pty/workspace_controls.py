"""Exercise alternate workspace controls in the actual CLI with an isolated session store."""
import fcntl
import json
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

from approval_grid import frame

REPO = Path(__file__).resolve().parents[2]
CLI = REPO / "src/Auxim.Cli/bin/Release/net10.0/auxim.dll"


def run(home):
    sessions = home / "sessions"
    sessions.mkdir()
    saved = {
        "first-id": "Initial session",
        "second-id": "Unicode 中文 session",
        "hidden-id": "Unrelated session",
    }
    for identifier, title in saved.items():
        (sessions / f"{identifier}.json").write_text(json.dumps({
            "id": identifier, "title": title,
            "createdAt": "2026-11-18T00:00:00Z", "updatedAt": "2026-11-18T00:00:00Z",
            "messages": [{"role": "user", "content": "Existing question"},
                         {"role": "assistant", "content": (
                             "Preview first line 中文🙂\n\n" +
                             "\n\n".join(f"Detail line {i:02}" for i in range(30)) +
                             "\n\n```text\npreview-code-final\n```"
                             if identifier == "second-id" else "Existing reply"
                         )}]
        }))
    pointer = home / "current_session"
    pointer.write_text("first-id")
    env = {k: v for k, v in os.environ.items() if not k.startswith("AUXIM_")}
    env.update(AUXIM_HOME=str(home), AUXIM_PROVIDER="local", TERM="xterm-256color")
    pid, fd = pty.fork()
    if pid == 0:
        os.execvpe("dotnet", ["dotnet", str(CLI)], env)
    fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, 80, 0, 0))
    data = bytearray()
    width = 80

    def drain(seconds=.15):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline and select.select([fd], [], [], .03)[0]:
            try:
                data.extend(os.read(fd, 65536))
            except OSError:
                break

    def send(keys):
        os.write(fd, keys.encode())
        drain(.2)

    def grid():
        return frame(bytes(data), width, 24)[0]

    def wait(check):
        deadline = time.monotonic() + 8
        while not check():
            assert time.monotonic() < deadline, "\n".join(grid())
            drain()

    def contains(text):
        return any(text in row for row in grid())

    def click(text):
        rows = grid()
        y, row = next((i + 1, row) for i, row in enumerate(rows) if text in row)
        x = row.index(text) + 1
        send(f"\x1b[<0;{x};{y}M\x1b[<0;{x};{y}m")

    def current():
        return pointer.read_text().strip()

    exited = False
    try:
        wait(lambda: contains("Message Auxim...") and b"\x1b[?2004h" in data)
        # Reports behind Esc (including a fragmented one) must not
        # reopen History. Fresh reports still navigate in both directions.
        up, down = "\x1b[<64;2;2M", "\x1b[<65;2;2M"
        send(up)
        wait(lambda: contains("AUXIM / History"))
        send(up * 12 + "\x1b" + down * 4 + "\x1b[<65;2;")
        wait(lambda: contains("AUXIM / Main"))
        send("2M")
        assert contains("AUXIM / Main")
        send(down)
        wait(lambda: contains("AUXIM / History"))
        send("\x1b")
        wait(lambda: contains("AUXIM / Main"))

        send("/session\r")
        wait(lambda: contains("AUXIM / Sessions") and contains("(current) Initial session"))
        send("\x1b")
        wait(lambda: contains("AUXIM / Main"))
        click("/session")
        assert "AUXIM / Main" in grid()[0] and "/session" in grid()[20]
        assert current() == "first-id"
        send("\r")
        wait(lambda: contains("AUXIM / Sessions"))
        send("second-id")
        wait(lambda: contains("second-id") and contains("[1/3]"))
        assert not contains("Unrelated session")
        send("\x15")
        wait(lambda: contains("[3/3]"))
        click("second-id")
        assert current() == "first-id"
        assert any(">" in row and "second-id" in row for row in grid())
        send("中文")
        wait(lambda: contains("[1/3]"))
        send("\x1b[C")
        wait(lambda: contains("AUXIM / Session Preview") and contains("Preview first line"))
        assert current() == "first-id"
        width = 32
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, width, 0, 0))
        os.kill(pid, signal.SIGWINCH)
        wait(lambda: contains("Session Preview") and contains("Enter switch"))
        assert contains("Preview first line")
        width = 80
        fcntl.ioctl(fd, termios.TIOCSWINSZ, struct.pack("HHHH", 24, width, 0, 0))
        os.kill(pid, signal.SIGWINCH)
        drain(.3)
        send(down + "\x1b[B" + "\x1b[6~" * 6)
        wait(lambda: contains("preview-code-final"))
        send("\x1bOQ")
        assert data.rfind(b"\x1b[?1000l") > data.rfind(b"\x1b[?1000h")
        send("\x1b[D")
        wait(lambda: contains("AUXIM / Sessions") and contains("[1/3]"))
        assert data.rfind(b"\x1b[?1000h") > data.rfind(b"\x1b[?1000l")
        assert any(">" in row and "second-id" in row for row in grid())
        send("\x1b[C")
        wait(lambda: contains("AUXIM / Session Preview"))
        send("\x1b")
        wait(lambda: contains("AUXIM / Sessions") and contains("[1/3]"))
        send("\x1b[C")
        wait(lambda: contains("AUXIM / Session Preview"))
        send("\r")
        wait(lambda: current() == "second-id" and contains("AUXIM / Main"))
        for removed in ("/resume", "/sessions", "/session list", "/session use first-id",
                        "/session search 中文", "/session show", "/session new"):
            send(removed + "\r")
            wait(lambda: contains("Failed") and contains("AUXIM / Main"))
            assert current() == "second-id" and len(list(sessions.glob("*.json"))) == 3
            send("\x1b")
            wait(lambda: not contains("Failed"))

        send("/session h\t")
        wait(lambda: "/session help" in grid()[20])
        assert not contains("/session · Done")
        send("\r")
        wait(lambda: contains("/session · Done"))
        compact = grid()
        assert "AUXIM / Main" in compact[0] and contains("Agent / Session")
        heading = next(i for i, row in enumerate(compact[:19]) if "/session · Done" in row)
        assert 18 - heading <= 8 and not contains("/session · Failed")
        send("/ses\t")
        wait(lambda: "/session" in grid()[20])
        assert contains("AUXIM / Main")
        send("\x01\x7f")
        send("/s\t")
        wait(lambda: contains("/sandbox"))
        click("/sandbox")
        wait(lambda: "/sandbox" in grid()[20])
        assert contains("AUXIM / Main")
        send("\x01\x7f")
        send("/help\r")
        wait(lambda: contains("F3 expand"))
        send("\x1b[6~")
        assert grid() != compact and contains("AUXIM / Main")
        send("\x1bOR")
        wait(lambda: contains("F3 shrink"))
        send("\x1b")
        wait(lambda: not contains("F3 shrink") and contains("Quick actions"))
        assert current() == "second-id"
        send("/session\r")
        wait(lambda: contains("AUXIM / Sessions"))
        send("first-id\r")
        wait(lambda: current() == "first-id")
        send("/session clear\r")
        wait(lambda: not pointer.exists() and contains("AUXIM / New Chat"))
        assert len(list(sessions.glob("*.json"))) == 3
        send("/exit\r")
        deadline = time.monotonic() + 8
        while not exited:
            drain(.05)
            done, status = os.waitpid(pid, os.WNOHANG)
            if done:
                exited = True
                assert os.waitstatus_to_exitcode(status) == 0
            assert time.monotonic() < deadline
        assert termios.tcgetattr(fd)[3] & termios.ECHO
        assert data.rfind(b"\x1b[?1000l") > data.rfind(b"\x1b[?1000h")
        assert b"\x1b[?1049l" in data
        def session_command(*args):
            result = subprocess.run(["dotnet", str(CLI), "session", *args], env=env,
                                    capture_output=True, text=True, timeout=10, check=True)
            assert not result.stderr, result.stderr
            return result.stdout

        assert "first-id" in session_command("list")
        assert "Existing reply" in session_command("show", "first-id")
        assert "second-id" in session_command("search", "Unicode")
        assert "first-id" in session_command("use", "first-id") and current() == "first-id"
        assert "Current session:" in session_command("new", "Script session") and current() != "first-id"
        print("PASS History wheel bursts, Session preview and persistence, result panels, completion and script commands", flush=True)
    finally:
        if not exited:
            os.kill(pid, signal.SIGKILL)
            os.waitpid(pid, 0)
        os.close(fd)


with tempfile.TemporaryDirectory(prefix="auxim-workspace-controls-") as path:
    run(Path(path))
