using System.Text.Json;
using System.Text.RegularExpressions;
using Auxim.Core.Runtime;

namespace Auxim.Cli.Interactive;

internal sealed class InteractiveHistory
{
    private const int MaxEntries = 200;
    private readonly List<InputSubmission> _records = [];
    private readonly List<string> _entries = [];
    private readonly string _path;
    private bool _readOnly;
    public IReadOnlyList<string> Entries => _entries;
    public InputSubmissionKind? KindAt(int index) => index >= 0 && index < _records.Count ? _records[index].Kind : null;

    internal InteractiveHistory(string legacyPath)
    {
        _path = legacyPath + ".cli-v1.jsonl";
        try
        {
            Secure(legacyPath); Secure(_path);
            var versioned = File.Exists(_path);
            foreach (var line in File.Exists(versioned ? _path : legacyPath)
                ? File.ReadLines(versioned ? _path : legacyPath) : [])
            {
                InputSubmission record;
                if (versioned)
                {
                    if (!TryDecode(line, out record!))
                    {
                        _readOnly = true; // Do not overwrite damaged/unknown-version data.
                        record = new(line, InputSubmissionKind.AgentMessage);
                    }
                }
                else record = new(line, InputSubmissionKind.AgentMessage); // Legacy origin is unknowable.
                if (!Sensitive(record) && (versioned || !Sensitive(InputSubmission.Fresh(line)))) Append(record);
                else if (versioned) _readOnly = true;
            }
            if (!versioned && File.Exists(legacyPath))
                Console.Error.WriteLine("Legacy input history retained unchanged. New history uses a private .cli-v1.jsonl sidecar. Old entries are recalled as messages; review the original privately for credential remnants before manually removing it.");
            if (_readOnly) Console.Error.WriteLine("Input history contains damaged, unsupported or sensitive records; original preserved and history saving disabled. Review privately before repairing it.");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            _readOnly = true;
            Console.Error.WriteLine("Input history unavailable; existing data was not overwritten. History saving disabled.");
        }
    }

    public static InteractiveHistory Load(IAuximRuntime runtime) => new(runtime.GetApplicationPaths().HistoryPath);

    public void Add(InputSubmission input)
    {
        if (string.IsNullOrWhiteSpace(input.Text) || Sensitive(input)) return;
        if (_records.Count > 0 && _records[^1] == input) return;
        Append(input);
    }

    private void Append(InputSubmission input)
    {
        _records.Add(input); _entries.Add(input.Text);
        if (_records.Count > MaxEntries) { _records.RemoveAt(0); _entries.RemoveAt(0); }
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
            using var doc = JsonDocument.Parse(line);
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
        if (_readOnly) return;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            Secure(_path); // Existing file permissions need explicit tightening too.
            temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            using (var writer = new StreamWriter(stream))
                foreach (var record in _records) writer.WriteLine(Encode(record));
            File.Move(temporary, _path, overwrite: true);
            temporary = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { Console.Error.WriteLine("Input history could not be saved; previous history retained."); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); } catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Secure(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("Refusing linked history.");
        if (File.Exists(path) && !OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
