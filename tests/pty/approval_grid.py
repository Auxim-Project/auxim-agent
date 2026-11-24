"""Replay the character cells used by Auxim's fixed alternate screen."""
import re
import unicodedata


def frame(data, width, height):
    rows = [[" "] * width for _ in range(height)]
    row = col = 0
    saved_cursor = (0, 0)
    for part in re.split(r"(\x1b\[[0-?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07|\x1b[78])",
                         data.decode("utf-8", errors="replace")):
        if part == "\x1b7":
            saved_cursor = (row, col)
            continue
        if part == "\x1b8":
            row, col = saved_cursor
            continue
        if part.startswith("\x1b["):
            args, command = part[2:-1], part[-1]
            if command in "Hf":
                y, x = (args.split(";") + ["", ""])[:2]
                row, col = int(y or 1) - 1, int(x or 1) - 1
            elif part in ("\x1b[2J", "\x1b[?1049h"):
                rows = [[" "] * width for _ in range(height)]
                row = col = 0
            elif part == "\x1b[2K":
                rows[row] = [" "] * width
            continue
        if part.startswith("\x1b]"):
            continue
        for ch in part:
            if ch == "\r":
                col = 0
            elif ch == "\n":
                row = min(row + 1, height - 1)
            elif ch.isprintable() and not unicodedata.combining(ch):
                if 0 <= col < width and 0 <= row < height:
                    rows[row][col] = ch
                col += 2 if unicodedata.east_asian_width(ch) in ("F", "W") else 1
    return ["".join(line).rstrip() for line in rows], (row + 1, col + 1)
