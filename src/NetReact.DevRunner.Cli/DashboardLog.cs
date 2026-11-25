using Spectre.Console;
using Spectre.Console.Rendering;

namespace NetReact.DevRunner.Cli;

internal sealed class DashboardLog
{
    private const int LineLimit = 2000;
    internal const int BoxWidthLimit = 86;
    internal const int MinimumBoxWidth = 12;
    internal const int BoxPaddingWidth = 4;
    private readonly Queue<Entry> _history = new();
    private readonly List<Entry> _rows = [];
    private string _filter = "";
    private int _width;
    private bool _compactBoxes;
    private int? _top;
    private RenderOptions? _renderOptions;

    public long NextId { get; private set; }
    public int Count => _rows.Count;
    public bool IsFollowing => _top is null;

    public void Append(string text, Style style, BoxPart boxPart = BoxPart.None)
    {
        var entry = new Entry(NextId++, text, style, boxPart);
        _history.Enqueue(entry);
        if (_width != 0)
        {
            Wrap(entry);
        }

        if (_history.Count > LineLimit)
        {
            var removed = _history.Dequeue();
            var firstRemaining = _rows.FindIndex(row => row.Id > removed.Id);
            var removeCount = firstRemaining < 0 ? _rows.Count : firstRemaining;
            _rows.RemoveRange(0, removeCount);
            if (_top is int top)
            {
                _top = Math.Max(0, top - removeCount);
            }
        }
    }

    public void Filter(string query)
    {
        if (query == _filter) return;
        _filter = query;
        _rows.Clear();
        if (_width > 0)
            foreach (var entry in _history) Wrap(entry);
        Follow();
    }

    public void Resize(int width, bool compactBoxes = false)
    {
        if (width == _width && compactBoxes == _compactBoxes)
        {
            return;
        }

        var anchor = _top is int top && top < _rows.Count ? _rows[top].Id : (long?)null;
        _width = width;
        _compactBoxes = compactBoxes;
        _renderOptions = new RenderOptions(AnsiConsole.Profile.Capabilities, new Size(width, int.MaxValue));
        _rows.Clear();
        foreach (var entry in _history)
        {
            Wrap(entry);
        }

        if (anchor is long id)
        {
            _top = Math.Max(0, _rows.FindIndex(row => row.Id >= id));
        }
    }

    public int VisibleStart(int height) => Math.Min(_top ?? LastTop(height), LastTop(height));

    public IEnumerable<Entry> VisibleRows(int height) => _rows.Skip(VisibleStart(height)).Take(height);

    public void Scroll(int amount, int height)
    {
        var last = LastTop(height);
        var target = Math.Clamp(VisibleStart(height) + amount, 0, last);
        _top = target == last ? null : target;
    }

    public void JumpTo(long id, int height)
    {
        var index = _rows.FindIndex(row => row.Id >= id);
        _top = index >= 0 && index < LastTop(height) ? index : null;
    }

    public void Follow() => _top = null;

    public void Clear()
    {
        _history.Clear();
        _rows.Clear();
        _filter = "";
        Follow();
    }

    private int LastTop(int height) => Math.Max(0, _rows.Count - height);

    private void Wrap(Entry entry)
    {
        if (_filter.Length > 0 && !entry.Text.Contains(_filter, StringComparison.OrdinalIgnoreCase)) return;

        var showBox = _width >= MinimumBoxWidth && _filter.Length == 0;
        if (entry.Part is BoxPart.Top or BoxPart.Bottom)
        {
            if (showBox && !_compactBoxes)
                _rows.Add(entry);
            return;
        }

        var width = showBox && entry.Part == BoxPart.Content ? Math.Min(_width, BoxWidthLimit) - BoxPaddingWidth : _width;
        if (new Segment(entry.Text).CellCount() <= width)
        {
            _rows.Add(entry);
            return;
        }

        IRenderable text = new Text(entry.Text, entry.Style) { Overflow = Overflow.Fold };
        var lines = Segment.SplitLines(text.Render(_renderOptions!, width));
        foreach (var line in lines)
        {
            _rows.Add(entry with { Text = string.Concat(line.Select(segment => segment.Text)).TrimEnd() });
        }
    }

    internal enum BoxPart { None, Top, Content, Bottom }

    internal readonly record struct Entry(long Id, string Text, Style Style, BoxPart Part);
}

