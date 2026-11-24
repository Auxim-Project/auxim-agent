using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Auxim.Core.Runtime;
using Microsoft.Win32.SafeHandles;

namespace Auxim.Cli.Interactive;

internal sealed class InteractiveHistory
{
    private const int MaxEntries = 200;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
    private readonly List<InputSubmission> _records = [];
    private readonly List<string> _entries = [];
    private readonly List<InputSubmission> _pending = [];
    private readonly string _legacyPath;
    private readonly string _path;
    private bool _readOnly;
    public IReadOnlyList<string> Entries => _entries;
    public InputSubmissionKind? KindAt(int index) => index >= 0 && index < _records.Count ? _records[index].Kind : null;

    internal InteractiveHistory(string legacyPath)
    {
        _legacyPath = legacyPath;
        _path = legacyPath + ".cli-v1.jsonl";
        try
        {
            if (!ReadHistory(_records, out var versioned)) PreserveDamagedHistory();
            else if (!versioned && File.Exists(legacyPath))
                Console.Error.WriteLine("Legacy input history retained unchanged. New history uses a private .cli-v1.jsonl sidecar. Old entries are recalled as messages; review the original privately for credential remnants before manually removing it.");
        }
        catch (DecoderFallbackException) { PreserveDamagedHistory(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            Console.Error.WriteLine("Input history unavailable; existing data was not overwritten. History saving disabled.");
        }
        _entries.AddRange(_records.Select(record => record.Text));
    }

    public static InteractiveHistory Load(IAuximRuntime runtime) => new(runtime.GetApplicationPaths().HistoryPath);

    private bool ReadHistory(List<InputSubmission> records, out bool versioned)
    {
        Secure(_legacyPath);
        Secure(_path);
        using var versionedStream = OpenSnapshot(_path);
        versioned = versionedStream is not null;
        using var legacyStream = versioned ? null : OpenSnapshot(_legacyPath);
        var stream = versionedStream ?? legacyStream;
        if (stream is null) return true;

        using var reader = versioned ? new StreamReader(stream, StrictUtf8, detectEncodingFromByteOrderMarks: false)
            : new StreamReader(stream);
        var valid = true;
        while (reader.ReadLine() is { } line)
        {
            if (versioned)
            {
                if (!TryDecode(line, out var record) || Sensitive(record!))
                {
                    valid = false;
                    continue;
                }
                AppendBounded(records, record!);
            }
            else if (!Sensitive(InputSubmission.Fresh(line)))
            {
                // Legacy origin is unknowable. Recall legacy entries as messages.
                AppendBounded(records, new(line, InputSubmissionKind.AgentMessage));
            }
        }
        return valid;
    }

    private static FileStream? OpenSnapshot(string path)
    {
        // The open handle stays valid across atomic replacement. Do not
        // mistake a read failure for a missing file and fall back to legacy.
        try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private void PreserveDamagedHistory()
    {
        _readOnly = true;
        _pending.Clear();
        Console.Error.WriteLine("Input history contains damaged, unsupported or sensitive records; original preserved and history saving disabled. Review privately before repairing it.");
    }

    public void Add(InputSubmission input)
    {
        if (string.IsNullOrWhiteSpace(input.Text) || Sensitive(input)) return;
        // The local recall list can have a stale tail. Deduplicate against the
        // actual disk tail during Save, independently of the local list.
        if (!_readOnly) Append(_pending, input);
        if (!Append(_records, input)) return;
        _entries.Add(input.Text);
        if (_entries.Count > MaxEntries) _entries.RemoveAt(0);
    }

    private static bool Append(List<InputSubmission> records, InputSubmission input)
    {
        if (records.Count > 0 && records[^1] == input) return false;
        AppendBounded(records, input);
        return true;
    }

    private static void AppendBounded(List<InputSubmission> records, InputSubmission input)
    {
        records.Add(input);
        if (records.Count > MaxEntries) records.RemoveAt(0);
    }

    // Conservative filtering: no raw secret is passed to a persistence writer.
    internal static bool Sensitive(InputSubmission input)
    {
        var text = input.Text;
        var tokens = InteractiveCommandParser.Tokenize(text.TrimStart().TrimStart('/'));
        if (tokens.Count >= 2 && tokens[0].Equals("auth", StringComparison.OrdinalIgnoreCase)
            && tokens[1].Equals("set-api-key", StringComparison.OrdinalIgnoreCase)) return true;
        if (input.Kind == InputSubmissionKind.AgentMessage) return false;
        return Regex.IsMatch(text, @"(?ix)(?:api[-_]?key|access[-_]?token|refresh[-_]?token|token|secret|password|passwd|authorization|credential|private[-_]?key|\bkey)[""']?\s*(?:=|:|\s+)|\bbearer\s+|https?://[^\s/]+@|(?:^|\s)(?:-u|--user|--proxy-user|--pass(?:word)?|--oauth2-bearer)(?:\s|=)",
            RegexOptions.CultureInvariant);
    }

    internal static string Encode(InputSubmission record) => JsonSerializer.Serialize(new
        { version = 1, kind = record.Kind.ToString(), text = record.Text });

    internal static bool TryDecode(string line, out InputSubmission? record)
    {
        record = null;
        try
        {
            using var doc = JsonDocument.Parse(line, new JsonDocumentOptions { AllowDuplicateProperties = false });
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 1) return false;
            var kindText = root.GetProperty("kind").GetString();
            if (!Enum.TryParse<InputSubmissionKind>(kindText, out var kind) || !Enum.IsDefined(kind)
                || kind.ToString() != kindText) return false;
            var text = root.GetProperty("text").GetString();
            if (text is null) return false;
            record = new(text, kind); return true;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        { return false; }
    }

    public void Save()
    {
        if (_readOnly || _pending.Count == 0) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            using var historyLock = AcquireLock(_path + ".lock");
            var merged = new List<InputSubmission>();
            if (!ReadHistory(merged, out _))
            {
                PreserveDamagedHistory();
                return;
            }

            foreach (var record in _pending) Append(merged, record);
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (var stream = new FileStream(temporary, PrivateFile(FileMode.CreateNew, FileAccess.Write)))
            using (var writer = new StreamWriter(stream))
                foreach (var record in merged) writer.WriteLine(Encode(record));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
            _pending.Clear();
        }
        catch (DecoderFallbackException) { PreserveDamagedHistory(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Input history could not be saved; previous history retained."); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static FileStream AcquireLock(string path)
    {
        Secure(path);
        var start = Stopwatch.GetTimestamp();
        var options = PrivateFile(FileMode.OpenOrCreate, FileAccess.ReadWrite);
        while (true)
        {
            try
            {
                // The separate lock inode must survive replacement of the
                // data file and must never be unlinked by cooperating writers.
                var stream = new FileStream(path, options);
                try
                {
                    if ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
                        && Flock(stream.SafeFileHandle, LockExclusive | LockNonBlocking) != 0)
                        throw new IOException("Could not lock input history.");
                    return stream;
                }
                catch
                {
                    stream.Dispose();
                    throw;
                }
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(start) < LockTimeout)
            {
                Thread.Sleep(20);
            }
        }
    }

    // Confirm the OS lock even if .NET's Unix file-sharing locks are disabled.
    [DllImport("libc", EntryPoint = "flock")]
    private static extern int Flock(SafeFileHandle file, int operation);

    private static FileStreamOptions PrivateFile(FileMode mode, FileAccess access)
    {
        var options = new FileStreamOptions { Mode = mode, Access = access, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return options;
    }

    private static void Secure(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Refusing linked history.");
        if (File.Exists(path) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
