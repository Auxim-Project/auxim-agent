using System.Text;
using Auxim.Core.Runtime;

namespace Auxim.Cli.Interactive;

// A bounded, per-turn projection for Main. Session messages remain in History.
internal sealed class MainRunActivity
{
    private const int MaxEvents = 12;
    private const int MaxPreviewCharacters = 512;
    private readonly Queue<string> _events = new();
    private readonly StringBuilder _preview = new();
    private bool _newSegment;
    private string? _approvalTool;

    public string Stage { get; private set; } = "Starting";
    public int EventCount { get; private set; }
    public string? Latest => _events.LastOrDefault();
    public IEnumerable<string> Events => _events;
    public string Preview => _preview.ToString();

    public void Append(string text)
    {
        Stage = "Receiving model output";
        if (_newSegment) { _preview.Clear(); _newSegment = false; }
        _preview.Append(text);
        var excess = _preview.Length - MaxPreviewCharacters;
        if (excess <= 0) return;
        if (char.IsLowSurrogate(_preview[excess])) excess++;
        _preview.Remove(0, excess);
    }

    public void Observe(RuntimeEvent value)
    {
        string? activity = null;
        switch (value)
        {
            case RuntimeRunStartedEvent:
                Stage = "Waiting for model";
                break;
            case RuntimeToolStartedEvent started:
                Stage = "Running tool";
                activity = $"Started {started.ToolName} · {started.ResourceAccesses.Count} resources";
                _newSegment = true;
                break;
            case RuntimeToolCompletedEvent completed:
                Stage = "Waiting for model";
                activity = $"{completed.ToolName}: {completed.Outcome} · {completed.OutputLength} chars";
                break;
            case RuntimeApprovalRequestedEvent requested:
                Stage = "Awaiting approval";
                _approvalTool = requested.Request.ToolName;
                activity = $"Approval requested for {_approvalTool}";
                _newSegment = true;
                break;
            case RuntimeApprovalResolvedEvent resolved:
                Stage = "Continuing";
                var decision = resolved.Approved ? resolved.Remembered ? "allowed and remembered" : "allowed once" : "denied";
                activity = string.IsNullOrEmpty(_approvalTool) ? $"Approval {decision}" : $"Approval {decision} for {_approvalTool}";
                _approvalTool = null;
                break;
            case RuntimeRunCompletedEvent:
                Stage = "Finishing";
                break;
            case RuntimeRunFailedEvent:
                Stage = "Run failed";
                break;
        }

        if (activity is null) return;
        EventCount++;
        _events.Enqueue(activity);
        if (_events.Count > MaxEvents) _events.Dequeue();
    }
}
