namespace Auxim.Cli.Interactive;

// Used only while the approval reader owns the foreground.
internal sealed class ApprovalPanel(InteractiveView view, IReadOnlyList<string> details)
{
    private readonly TranscriptViewport _viewport = new();
    private ScreenLayout? _layout;
    private int _pageHeight;
    private int _selected = -1;
    private int _drawnOffset = -1;
    private int _drawnSelection = -1;
    private bool _feedback;

    public static bool Fits((int Width, int Height) size) => size.Width >= 48 && size.Height >= 17;

    public void Scroll(int direction) => _viewport.ScrollClamped(direction * _pageHeight, _pageHeight);

    public void Draw(int selected)
    {
        _selected = selected;
        Paint(feedback: false);
    }

    public ScreenLayout DrawFeedback() => Paint(feedback: true);

    private ScreenLayout Paint(bool feedback)
    {
        var layout = view.ApprovalLayout();
        var layoutChanged = layout != _layout;
        if (layoutChanged)
        {
            _pageHeight = layout.ContentHeight - 8;
            _viewport.Reflow(details.SelectMany(line => TerminalTextLayout.Wrap(line, layout.Columns)).ToArray(),
                _pageHeight, followBottom: false);
        }
        if (!layoutChanged && _drawnOffset == _viewport.Offset && _feedback == feedback && _drawnSelection == _selected)
            return layout;

        _layout = layout;
        _drawnOffset = _viewport.Offset;
        _drawnSelection = _selected;
        _feedback = feedback;
        var rows = new List<string>(view.ApprovalContext(layout.Columns))
        {
            Ansi.Warning("SAFETY REVIEW · Tool approval required")
        };
        rows.AddRange(_viewport.GetVisibleLines(_pageHeight));
        while (rows.Count < 4 + _pageHeight) rows.Add("");
        rows.Add(Ansi.Muted($"PgUp/PgDn details   {_viewport.Offset + 1}–{Math.Min(_viewport.Offset + _pageHeight, _viewport.TotalLines)}/{_viewport.TotalLines}"));
        for (var i = 0; i < ApprovalRenderer.Options.Length; i++)
        {
            var label = $"{(i == _selected ? ">" : " ")} {i + 1}. {ApprovalRenderer.Options[i]}";
            if (i == 0) label += "   Enter confirms";
            rows.Add(i == _selected ? Ansi.Reverse(label) : label);
        }
        view.PaintApproval(rows, layout, feedback, layoutChanged);
        return layout;
    }
}
