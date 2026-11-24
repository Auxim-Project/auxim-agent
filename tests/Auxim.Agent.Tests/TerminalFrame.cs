using System.Text.RegularExpressions;

namespace Auxim.Agent.Tests;

internal static class TerminalFrame
{
    // Keep cursor and erase commands so geometry assertions still inspect the frame.
    internal static string WithoutStyles(string frame) => Regex.Replace(frame, "\u001b\\[[0-9;]*m", "");
}
