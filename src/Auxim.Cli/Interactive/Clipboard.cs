using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Auxim.Cli.Interactive;

internal readonly record struct ClipboardResult(bool Success, string Status, string? TerminalSequence = null);

internal interface IClipboard
{
    Task<ClipboardResult> CopyAsync(string text);
}

internal sealed class Clipboard : IClipboard
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public Task<ClipboardResult> CopyAsync(string text)
    {
        // Keep process launch, pipe writes and platform clipboard calls off the UI owner.
        var osc52 = Environment.GetEnvironmentVariable("AUXIM_CLIPBOARD") == "osc52";
        var canWriteTerminal = Ansi.ControlSequencesEnabled;
        return Task.Run(async () =>
        {
            try
            {
                if (osc52)
                {
                    if (!canWriteTerminal || Encoding.UTF8.GetByteCount(text) > 64 * 1024)
                        return new(false, "OSC 52 unavailable or selection too large.");
                    return new(true, "Copy sent to terminal (unconfirmed).",
                        "\u001b]52;c;" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\a");
                }
                if (OperatingSystem.IsWindows()) return CopyWindows(text);
                if (OperatingSystem.IsMacOS()) return await CopyProcessAsync("pbcopy", [], text, Timeout);
                if (OperatingSystem.IsLinux())
                {
                    ClipboardResult? failure = null;
                    foreach (var (program, args) in LinuxBackends(
                        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"), Environment.GetEnvironmentVariable("DISPLAY")))
                    {
                        var result = await CopyProcessAsync(program, args, text, Timeout);
                        if (result.Success) return result;
                        if (!result.Status.EndsWith("not found.", StringComparison.Ordinal)) failure ??= result;
                    }
                    return failure ?? new(false, "No clipboard backend (wl-copy, xclip or xsel).");
                }
                return new(false, "No clipboard backend on this platform.");
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception
                or DllNotFoundException or EntryPointNotFoundException or ExternalException)
            {
                return new(false, "Clipboard unavailable.");
            }
        });
    }

    private static IEnumerable<(string Program, string[] Args)> LinuxBackends(string? wayland, string? display)
    {
        if (!string.IsNullOrEmpty(wayland)) yield return ("wl-copy", ["--type", "text/plain;charset=utf-8"]);
        if (!string.IsNullOrEmpty(display))
        {
            yield return ("xclip", ["-selection", "clipboard", "-in"]);
            yield return ("xsel", ["--clipboard", "--input"]);
        }
    }

    internal static async Task<ClipboardResult> CopyProcessAsync(string program, IReadOnlyList<string> arguments,
        string text, TimeSpan timeout)
    {
        var executable = ResolveExecutable(program);
        if (executable is null) return new(false, $"{program} not found.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        try { process.Start(); }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 2)
        { return new(false, $"{program} not found."); }
        catch (System.ComponentModel.Win32Exception) { return new(false, $"{program} could not start."); }

        // Clipboard tools are quiet. Keep both streams private and bounded by
        // the OS pipe capacity. A noisy or stuck backend hits the same deadline.
        using var deadline = new CancellationTokenSource(timeout);
        using var stdout = process.StandardOutput;
        using var stderr = process.StandardError;
        try
        {
            var stdin = process.StandardInput;
            await stdin.WriteAsync(text.AsMemory(), deadline.Token);
            await stdin.FlushAsync(deadline.Token);
            stdin.Close();
            while (!process.HasExited) await Task.Delay(25, deadline.Token);
            return process.ExitCode == 0 ? new(true, "Copied.") : new(false, $"{program} rejected the copy.");
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            return new(false, $"{program} timed out.");
        }
        catch (IOException) { return new(false, $"{program} could not receive the selection."); }
        finally { deadline.Cancel(); }
    }

    private static string? ResolveExecutable(string program)
    {
        if (Path.IsPathFullyQualified(program)) return File.Exists(program) ? program : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified)
            .Select(directory => Path.Combine(directory, program))
            .FirstOrDefault(File.Exists);
    }

    private static ClipboardResult CopyWindows(string text)
    {
        // OpenClipboard needs an owning window for EmptyClipboard/SetClipboardData.
        var window = CreateWindowEx(0, "STATIC", "", 0, 0, 0, 0, 0, new IntPtr(-3), 0, 0, 0);
        if (window == 0) return new(false, "Clipboard window unavailable.");
        try
        {
            if (!OpenClipboard(window)) return new(false, "Clipboard is busy.");
            try
            {
                var bytes = Encoding.Unicode.GetBytes(text.ReplaceLineEndings("\r\n") + '\0');
                var memory = GlobalAlloc(2, (nuint)bytes.Length);
                if (memory == 0) return new(false, "Clipboard memory unavailable.");
                try
                {
                    var address = GlobalLock(memory);
                    if (address == 0) return new(false, "Clipboard memory unavailable.");
                    try { Marshal.Copy(bytes, 0, address, bytes.Length); }
                    finally { GlobalUnlock(memory); }
                    if (!EmptyClipboard() || SetClipboardData(13, memory) == 0)
                        return new(false, "Windows clipboard rejected the copy.");
                    memory = 0; // Ownership transferred to the system.
                    return new(true, "Copied.");
                }
                finally { if (memory != 0) GlobalFree(memory); }
            }
            finally { CloseClipboard(); }
        }
        finally { DestroyWindow(window); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", ExactSpelling = true)]
    private static extern nint CreateWindowEx(uint exStyle, string className, string name, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")] private static extern bool OpenClipboard(nint owner);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint format, nint data);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint memory);
}
