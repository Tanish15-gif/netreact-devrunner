using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NetReact.DevRunner.Core;
using NetReact.DevRunner.Infrastructure;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace NetReact.DevRunner.Cli;

internal sealed class ServiceDashboard
{
    private static readonly TimeSpan UrlDiscoveryWindow = TimeSpan.FromSeconds(8);
    private static readonly Regex TerminalEscape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-_])",
        RegexOptions.NonBacktracking);
    private static readonly Regex ListeningUrl = new(
        @"(?:Now listening on:|Local:|open your browser on)\s+(https?://\S+)", RegexOptions.NonBacktracking);
    private static readonly Style TitleStyle = new(new Color(111, 224, 208), decoration: Decoration.Bold);
    private static readonly Style ServiceNameStyle = new(decoration: Decoration.Bold);
    private static readonly Style MutedStyle = new(new Color(156, 171, 182));
    private static readonly Style ErrorStyle = new(Color.Yellow);
    private static readonly Style UrlStyle = new(new Color(145, 217, 250));
    private static readonly Style SuccessStyle = new(new Color(131, 216, 162));
    private static readonly Style FailureStyle = new(new Color(250, 156, 147));
    private static readonly Style PendingStyle = new(new Color(239, 203, 131));
    private static readonly Style FrameStyle = new(new Color(77, 112, 121));
    private static readonly string[] FooterHints = ["Ctrl+U services", "Ctrl+C quit", "/ search"];

    private readonly Lock _sync = new();
    private readonly Page[] _pages;
    private readonly Page _commands = new(null, "NetReact");
    private readonly DashboardLog _shortcuts = CreateShortcutLog(watching: false);
    private readonly DashboardLog _watchShortcuts = CreateShortcutLog(watching: true);
    private readonly TimeProvider _clock;
    private readonly string _configPath;
    private readonly string _configDirectory;
    private string? _search;
    private readonly StringBuilder _input = new();
    private int _selected;
    private int _revision;
    private bool _canReadKeys = true;
    private bool _showShortcuts;

    public ServiceDashboard(
        IReadOnlyDictionary<string, ServiceConfig?> services, string configPath, bool watch, TimeProvider? clock = null)
    {
        _configPath = configPath;
        _configDirectory = Path.GetDirectoryName(configPath)!;
        _clock = clock ?? TimeProvider.System;
        _pages = services.Select(service => new Page(
                service.Key, GetTitle(service.Key, service.Value!, _configDirectory), DotNetLaunch.IsWatching(service.Value!, watch)))
            .Append(_commands).ToArray();
        _selected = _pages.Length - 1;
    }

    private Page Current => _pages[_selected];
    private DashboardLog ShortcutLog => Current.Watching ? _watchShortcuts : _shortcuts;

    public async Task<int> RunAsync(
        Func<ChannelReader<ServiceRequest>, Action<ServiceUpdate>, Task<int>> launch,
        CancellationTokenSource shutdown)
    {
        var requests = Channel.CreateBounded<ServiceRequest>(new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
        Task<int>? run = null;
        bool? previousControlMode = OperatingSystem.IsWindows() ? Console.TreatControlCAsInput : null;
        try
        {
            if (previousControlMode.HasValue)
            {
                Console.TreatControlCAsInput = true;
            }

            return await AnsiConsole.Live(Render(GetSize()))
                .Overflow(VerticalOverflow.Crop)
                .StartAsync(async context =>
                {
                    run = launch(requests.Reader, Publish);
                    var lastView = (-1, new ScreenSize(), 0);
                    while (!run.IsCompleted)
                    {
                        var size = GetSize();
                        ReadKeys(requests.Writer, shutdown, size);
                        var view = (Volatile.Read(ref _revision), size, CountFetchingUrls());
                        if (view != lastView)
                        {
                            context.UpdateTarget(Render(size));
                            lastView = view;
                        }

                        await Task.WhenAny(run, Task.Delay(70));
                    }

                    context.UpdateTarget(Render(GetSize()));
                    return await run;
                });
        }
        finally
        {
            try
            {
                if (run is { IsCompleted: false })
                {
                    shutdown.Cancel();
                    await run;
                }
            }
            finally
            {
                requests.Writer.TryComplete();
                if (previousControlMode is bool controlMode)
                {
                    Console.TreatControlCAsInput = controlMode;
                }
            }
        }
    }

    private void ReadKeys(ChannelWriter<ServiceRequest> requests, CancellationTokenSource shutdown, ScreenSize size)
    {
        if (!_canReadKeys || shutdown.IsCancellationRequested)
        {
            return;
        }

        try
        {
            for (var i = 0; i < 48 && Console.KeyAvailable; i++)
            {
                var key = Console.ReadKey(intercept: true);
                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                if (key.KeyChar == '\u0003' || (control && key.Key == ConsoleKey.C))
                {
                    shutdown.Cancel();
                    return;
                }

                lock (_sync)
                {
                    HandleKey(key, control, requests, size);
                    Interlocked.Increment(ref _revision);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            _canReadKeys = false;
            Interlocked.Increment(ref _revision);
        }
    }

    private void HandleKey(ConsoleKeyInfo key, bool control, ChannelWriter<ServiceRequest> requests, ScreenSize size)
    {
        if (_search is not null)
        {
            if (key.Key == ConsoleKey.Escape)
            {
                _search = null;
                Current.Log.Filter("");
                Current.Log.Follow();
            }
            else if (key.Key == ConsoleKey.Backspace && _search.Length > 0)
                Current.Log.Filter(_search = _search[..^1]);
            else if (!Scroll(Current.Log, key.Key, size) && !char.IsControl(key.KeyChar) && _search.Length < 128)
                Current.Log.Filter(_search += key.KeyChar);
            return;
        }
        if (!_showShortcuts && key.KeyChar == '/' && (Current != _commands || _input.Length == 0))
        {
            _search = "";
            return;
        }

        if (_showShortcuts)
        {
            if (key.Key == ConsoleKey.Escape || key.KeyChar == '?')
            {
                _showShortcuts = false;
            }
            else
            {
                Scroll(ShortcutLog, key.Key, size);
            }
            return;
        }

        if (key.KeyChar == '?' && (Current != _commands || _input.Length == 0))
        {
            OpenShortcuts(size);
            return;
        }

        if (key.KeyChar == '\u0004' || (control && key.Key == ConsoleKey.D))
        {
            if (Current.Service is string name)
            {
                Request(requests, new(ServiceAction.Toggle, name));
            }
            return;
        }

        if (key.KeyChar == '\u0012' || (control && key.Key == ConsoleKey.R))
        {
            Request(requests, new(ServiceAction.Restart, Current.Watching ? Current.Service : null));
            return;
        }

        if (key.KeyChar == '\u0015' || (control && key.Key == ConsoleKey.U))
        {
            _selected = _pages.Length - 1;
            return;
        }

        switch (key.Key)
        {
            case ConsoleKey.LeftArrow:
                _selected = (_selected + _pages.Length - 1) % _pages.Length;
                return;
            case ConsoleKey.RightArrow:
                _selected = (_selected + 1) % _pages.Length;
                return;
        }

        if (Scroll(Current.Log, key.Key, size) || Current != _commands)
        {
            return;
        }

        if (key.Key == ConsoleKey.Enter)
        {
            var command = _input.ToString().Trim();
            _input.Clear();
            if (command.Length != 0)
            {
                Execute(command, requests, size);
            }
        }
        else if (key.Key == ConsoleKey.Backspace && _input.Length != 0)
        {
            _input.Length--;
        }
        else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && _input.Length < 512)
        {
            _input.Append(key.KeyChar);
        }
    }

    private void OpenShortcuts(ScreenSize size)
    {
        _showShortcuts = true;
        ShortcutLog.Resize(size.ContentWidth);
        ShortcutLog.JumpTo(0, Math.Max(1, GetLogHeight(size)));
    }

    private static DashboardLog CreateShortcutLog(bool watching)
    {
        var log = new DashboardLog();
        foreach (var line in NetReactHelp.Shortcuts(watching).ReplaceLineEndings("\n").Split('\n'))
        {
            log.Append(line, Style.Plain);
        }

        return log;
    }

    private bool Scroll(DashboardLog log, ConsoleKey key, ScreenSize size)
    {
        if (key == ConsoleKey.End)
        {
            log.Follow();
            return true;
        }

        var height = Math.Max(1, GetLogHeight(size));
        int? delta = key switch
        {
            ConsoleKey.UpArrow => -1,
            ConsoleKey.DownArrow => 1,
            ConsoleKey.PageUp => -height,
            ConsoleKey.PageDown => height,
            ConsoleKey.Home => int.MinValue,
            _ => null
        };
        if (delta is not int amount)
        {
            return false;
        }

        log.Resize(size.ContentWidth, compactBoxes: log == _commands.Log && height < 5);
        log.Scroll(amount == int.MinValue ? -log.Count : amount, height);
        return true;
    }

    private void Execute(string command, ChannelWriter<ServiceRequest> requests, ScreenSize size)
    {
        var words = command.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 0 && words[0] == "netreact")
        {
            words = words[1..];
        }

        if (words is ["clear"] or ["cls"])
        {
            _commands.Log.Clear();
            return;
        }
        var start = _commands.Log.NextId;
        _commands.Log.Append($"> {command}", TitleStyle);
        if (words is ["help", "shortcuts"] or ["shortcuts"])
        {
            OpenShortcuts(size);
        }
        else if (words is [] or ["help"])
        {
            _commands.Log.Append("Session commands", TitleStyle);
            Write("""
                  status               Show service status.
                  restart NAME         Restart one service, e.g. restart backend.
                  restart all          Restart every service.
                  validate             Check devrunner.yaml.
                  clear, cls           Clear session activity.
                  help [command]       Show command help.

                Press ? for keyboard shortcuts.
                """);
        }
        else if (NetReactHelp.TryGetHelp(words, out var help))
        {
            WriteHelp(help);
        }
        else
        {
            switch (words)
            {
                case ["--version"]:
                    Write(NetReactHelp.Version);
                    break;
                case ["--info"]:
                    Write(NetReactHelp.Info(_configDirectory, _configPath));
                    break;
                case ["status"]:
                    _commands.Log.Append("Service status", TitleStyle, DashboardLog.BoxPart.Top);
                    foreach (var page in _pages.Take(_pages.Length - 1))
                        _commands.Log.Append($"{Clean(page.Service!)}: {StatusLabel(page.Status)}",
                            StatusStyle(page.Status), DashboardLog.BoxPart.Content);
                    _commands.Log.Append("", FrameStyle, DashboardLog.BoxPart.Bottom);
                    break;
                case ["restart"] or ["restart", "all"]:
                    Request(requests, new(ServiceAction.Restart, null));
                    break;
                case ["restart", .. var target]:
                    var name = string.Join(' ', target);
                    var service = _pages[..^1].FirstOrDefault(page =>
                        string.Equals(page.Service, name, StringComparison.Ordinal)
                        || string.Equals(page.Title, name, StringComparison.OrdinalIgnoreCase));
                    if (service?.Service is string matched)
                    {
                        Request(requests, new(ServiceAction.Restart, matched));
                    }
                    else
                    {
                        Write($"Unknown service '{name}'. Type status to list services.");
                    }
                    break;
                case ["validate"]:
                    ValidateConfig();
                    break;
                default:
                    Write("Unknown session command. Type help for the available commands.", ErrorStyle);
                    break;
            }
        }

        var height = Math.Max(1, GetLogHeight(size));
        _commands.Log.Resize(size.ContentWidth, compactBoxes: height < 5);
        _commands.Log.JumpTo(start, height);
    }

    private void Request(ChannelWriter<ServiceRequest> writer, ServiceRequest request)
    {
        var target = request.Service ?? "all services";
        var action = request.Action == ServiceAction.Restart ? "Restart" : "Toggle";
        if (writer.TryWrite(request))
            Write($"{action} requested: {target}.", MutedStyle);
        else
            Write("Another request is in progress.", ErrorStyle);
    }

    private void ValidateConfig()
    {
        try
        {
            var config = YamlConfigFile.Read(_configPath);
            var errors = YamlConfigFile.GetErrors(config, _configDirectory).ToArray();
            Write(errors.Length == 0 ? $"{Path.GetFileName(_configPath)} is valid." : string.Join('\n', errors),
                errors.Length == 0 ? SuccessStyle : ErrorStyle);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Write($"Could not read {_configPath}. {exception.Message}");
        }
    }

    private void Write(string message, Style? style = null)
    {
        foreach (var line in message.ReplaceLineEndings("\n").Split('\n'))
        {
            _commands.Log.Append(line, style ?? Style.Plain);
        }
    }

    private void WriteHelp(string help)
    {
        foreach (var line in help.ReplaceLineEndings("\n").Split('\n'))
        {
            var heading = line == "NetReact DevRunner" || (line.EndsWith(':') && !line.StartsWith(' '));
            _commands.Log.Append(line, heading ? TitleStyle : Style.Plain);
        }
    }

    private void Publish(ServiceUpdate update)
    {
        var line = Clean(update.Message);
        if (line.Length == 0 || (line.StartsWith("dotnet watch", StringComparison.Ordinal)
            && (line.Contains("Ctrl+R", StringComparison.OrdinalIgnoreCase)
                || line.Contains("Ctrl + R", StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        lock (_sync)
        {
            var page = update.Service is null ? _commands : Array.Find(_pages, page => page.Service == update.Service);
            if (page is null)
            {
                return;
            }

            if (update.Kind is ServiceUpdateKind.Starting or ServiceUpdateKind.Started or
                ServiceUpdateKind.Stopping or ServiceUpdateKind.Stopped or ServiceUpdateKind.Failure or
                ServiceUpdateKind.Ready or ServiceUpdateKind.WaitingForReady)
            {
                page.Status = update.Kind;
                if (update.Kind == ServiceUpdateKind.Starting)
                {
                    page.Urls.Clear();
                    page.UrlLookupStarted = _clock.GetTimestamp();
                }
                else if (update.Kind is ServiceUpdateKind.Started or ServiceUpdateKind.WaitingForReady or ServiceUpdateKind.Ready)
                    page.UrlLookupStarted ??= _clock.GetTimestamp();
            }

            foreach (Match address in ListeningUrl.Matches(line))
                page.Urls.Add(address.Groups[1].Value);
            if (update.Kind == ServiceUpdateKind.Ready && line.StartsWith("Ready at ", StringComparison.Ordinal))
                page.Urls.Add(line["Ready at ".Length..]);

            var style = update.Kind is ServiceUpdateKind.ErrorOutput or ServiceUpdateKind.Failure
                ? ErrorStyle : Style.Plain;
            page.Log.Append(ShortenStartupPath(line), style);
            Interlocked.Increment(ref _revision);
        }
    }

    private string ShortenStartupPath(string line)
    {
        const string launchSettings = "Using launch settings from ";
        const string contentRoot = "Content root path: ";
        var start = line.IndexOf(launchSettings, StringComparison.Ordinal);
        if (start >= 0)
            start += launchSettings.Length;
        else
        {
            start = line.IndexOf(contentRoot, StringComparison.Ordinal);
            if (start >= 0)
                start += contentRoot.Length;
        }

        if (start < 0 || !Path.IsPathFullyQualified(line.AsSpan(start)))
            return line;

        var prefix = Path.EndsInDirectorySeparator(_configDirectory)
            ? _configDirectory : _configDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var path = line.AsSpan(start).TrimEnd('.');
        if (path.Equals(_configDirectory, comparison) || path.Equals(prefix, comparison))
            return line[..start] + ".";
        if (line.AsSpan(start).StartsWith(prefix, comparison))
            return line[..start] + line[(start + prefix.Length)..];

        return line;
    }

    private int CountFetchingUrls()
    {
        lock (_sync)
            return _pages.Count(IsFetchingUrl);
    }

    private bool IsFetchingUrl(Page page) => page.IsActive && !page.HasLiveUrls &&
        page.UrlLookupStarted is long started && _clock.GetElapsedTime(started) < UrlDiscoveryWindow;

    private IRenderable Render(ScreenSize size)
    {
        lock (_sync)
        {
            var log = _showShortcuts ? ShortcutLog : Current.Log;
            var logHeight = GetLogHeight(size);
            log.Resize(size.ContentWidth, compactBoxes: log == _commands.Log && logHeight < 5);
            var rows = new List<IRenderable> { RenderHeader(size) };
            var serviceRows = ServiceRows(size);
            var showPanel = ShowServicePanel(size, serviceRows);
            if (showPanel)
                rows.Add(Line(size.Margin + "Session activity", TitleStyle));
            else if (Current != _commands && serviceRows > 0)
                rows.Add(RenderServiceRow(Current, size.ContentWidth, size.Margin));

            var lines = log.VisibleRows(logHeight).ToArray();
            var frameEntries = string.IsNullOrEmpty(_search) && size.ContentWidth >= DashboardLog.MinimumBoxWidth;
            rows.AddRange(lines.Select(entry => frameEntries ? RenderLogEntry(entry, size) : Line(size.Margin + entry.Text, entry.Style)));
            AddBlankRows(rows, logHeight - lines.Length);

            if (showPanel)
            {
                var hidden = _pages.Length - 1 - serviceRows;
                var label = hidden > 0 ? $"Services  +{hidden} more" : "Services";
                rows.Add(Line(" ", Style.Plain));
                rows.Add(Framed(size, label, RenderServices(size, serviceRows)));
            }
            else if (Current == _commands)
            {
                foreach (var page in _pages.Take(serviceRows))
                    rows.Add(RenderServiceRow(page, size.ContentWidth, size.Margin));
            }

            if (ShowPrompt(size))
            {
                rows.Add(RenderPrompt(size));
            }

            if (size.HasFooter)
            {
                rows.Add(RenderFooter(size, log));
            }

            return new Rows(rows);
        }
    }

    private static IRenderable RenderLogEntry(DashboardLog.Entry entry, ScreenSize size)
    {
        if (entry.Part == DashboardLog.BoxPart.None)
            return Line(size.Margin + entry.Text, entry.Style);

        var width = PanelWidth(size);
        var row = new Paragraph { Overflow = Overflow.Crop };
        row.Append(size.Margin);
        switch (entry.Part)
        {
            case DashboardLog.BoxPart.Top:
                var title = Segment.Truncate(new Segment(entry.Text), width - DashboardLog.BoxPaddingWidth);
                var remaining = width - DashboardLog.BoxPaddingWidth - (title?.CellCount() ?? 0);
                return row.Append("+ ", FrameStyle).Append(title?.Text ?? "", entry.Style)
                    .Append(" " + new string('-', remaining) + "+", FrameStyle);
            case DashboardLog.BoxPart.Bottom:
                return row.Append("+" + new string('-', width - 2) + "+", FrameStyle);
            default:
                var padding = Math.Max(0, width - DashboardLog.BoxPaddingWidth - new Segment(entry.Text).CellCount());
                return row.Append("| ", FrameStyle).Append(entry.Text, entry.Style)
                    .Append(new string(' ', padding) + " |", FrameStyle);
        }
    }

    private IRenderable RenderHeader(ScreenSize size)
    {
        if (_showShortcuts)
        {
            var title = Current.Watching ? ".NET Watch shortcuts" : "Shortcuts";
            return Decorated($"{size.Margin}[bold #6fe0d0]{title}[/]");
        }
        var labels = _pages.Select(page => page == Current ? $"[{page.Title}]" : $" {page.Title} ").ToArray();
        var brand = $"NetReact {NetReactHelp.Version}   ";
        var tabText = string.Join(' ', labels);
        if (new Segment(tabText).CellCount() > size.ContentWidth)
        {
            return Line($"{size.Margin}< {Current.Title} >  {_selected + 1}/{_pages.Length}", TitleStyle);
        }

        var tabs = string.Join(' ', labels.Select((label, i) => i == _selected
            ? $"[bold #081c1a on #6fe0d0]{Markup.Escape(label)}[/]"
            : $"[#9cabb6]{Markup.Escape(label)}[/]"));
        var header = new Segment(brand + tabText).CellCount() <= size.ContentWidth
            ? $"[bold]NetReact[/] [#9cabb6]{NetReactHelp.Version}[/]   {tabs}"
            : tabs;
        return Decorated(size.Margin + header);
    }

    private IRenderable RenderServiceRow(Page page, int width, string margin = "")
    {
        var status = StatusLabel(page.Status);
        return new Paragraph { Overflow = Overflow.Crop }
            .Append(margin)
            .Append(ServiceNameColumn(page, width), ServiceNameStyle)
            .Append("  ")
            .Append(width >= 60 ? status.PadRight(9) : status, StatusStyle(page.Status))
            .Append("  ")
            .Append(ServiceAddress(page), page.HasLiveUrls ? UrlStyle : MutedStyle);
    }

    private static string ServiceNameColumn(Page page, int availableWidth)
    {
        var width = availableWidth >= 60 ? 16 : Math.Max(4, (availableWidth - 20) / 2);
        var name = Segment.Truncate(new Segment(page.Title), width) ?? new Segment("");
        var padding = availableWidth >= 60 ? width - name.CellCount() : 0;
        return name.Text + new string(' ', Math.Max(0, padding));
    }

    private string ServiceAddress(Page page)
    {
        if (page.HasLiveUrls)
            return page.Urls.First();
        if (IsFetchingUrl(page))
            return "Fetching URL...";
        return page.IsActive ? "No URL detected" : "";
    }

    private IEnumerable<IRenderable> RenderServices(ScreenSize size, int serviceRows)
    {
        var extraRows = ExtraAddressRows(size, serviceRows);
        var width = PanelWidth(size) - DashboardLog.BoxPaddingWidth;
        foreach (var page in _pages.Take(serviceRows))
        {
            yield return RenderServiceRow(page, width);
            if (!page.HasLiveUrls)
                continue;

            var indent = width >= 60 ? new string(' ', 29) : "  ";
            foreach (var address in page.Urls.Skip(1).Take(extraRows))
            {
                yield return Line(indent + address, UrlStyle);
                extraRows--;
            }
        }
    }

    private static IRenderable Framed(ScreenSize size, string title, IEnumerable<IRenderable> contents)
    {
        var panel = new Panel(new Rows(contents))
        {
            Border = BoxBorder.Ascii,
            BorderStyle = FrameStyle,
            Header = new PanelHeader($"[bold #6fe0d0] {Markup.Escape(title)} [/]"),
            Width = PanelWidth(size),
            Padding = new Padding(1, 0)
        };
        return new Padder(panel, new Padding(size.Inset, 0));
    }

    private static int PanelWidth(ScreenSize size) => Math.Min(DashboardLog.BoxWidthLimit, size.ContentWidth);

    private IRenderable RenderPrompt(ScreenSize size)
    {
        if (_search is not null)
            return Line($"{size.Margin}/ {_search}", TitleStyle);
        if (PromptHeight(size) == 3)
            return Framed(size, "Command", [RenderCommandInput(PanelWidth(size) - DashboardLog.BoxPaddingWidth)]);

        return RenderCommandInput(PanelWidth(size) - 2,
            $"{size.Margin}[#4d7079]|[/]", "[#4d7079]|[/]");
    }

    private IRenderable RenderCommandInput(int width, string leftEdge = "", string rightEdge = "")
    {
        var empty = _input.Length == 0;
        var input = empty ? "Type help for commands" : _input.ToString();
        var marker = AnsiConsole.Profile.Capabilities.Unicode ? "›" : ">";
        var prefix = width >= 4 ? $" {marker} " : marker;
        var room = Math.Max(0, width - new Segment(prefix).CellCount() - 1);
        var value = room == 0 ? "" : empty
            ? Segment.Truncate(new Segment(input), room)?.Text ?? ""
            : Segment.SplitOverflow(new Segment(input), Overflow.Fold, room)[^1].Text;
        var fill = new string(' ', Math.Max(0, room - new Segment(value).CellCount()));
        const string surface = "#1d292f";
        var textColor = empty || _showShortcuts ? "#9aabb7" : "#eaf0f4";
        var caret = _showShortcuts ? $"[on {surface}] [/]" : "[on #6fe0d0] [/]";
        return Decorated($"{leftEdge}[bold #6fe0d0 on {surface}]{prefix}[/]"
            + (empty ? caret : "")
            + $"[{textColor} on {surface}]{Markup.Escape(value)}[/]"
            + (empty ? "" : caret)
            + $"[on {surface}]{fill}[/]" + rightEdge);
    }

    private IRenderable RenderFooter(ScreenSize size, DashboardLog log)
    {
        if (!_canReadKeys)
        {
            return Line($"{size.Margin}Ctrl+C to exit", MutedStyle);
        }

        if (_search is not null)
            return Line($"{size.Margin}{log.Count} matching rows   Esc close", MutedStyle);
        if (_showShortcuts)
            return Line(size.Margin + "Esc close   PgUp/PgDn scroll", MutedStyle);
        var first = Current.Service is null ? "? shortcuts"
            : Current.IsActive ? "Ctrl+D stop" : "Ctrl+D start";
        var state = !log.IsFollowing ? "Paused  |  End live"
            : Current.Service is null ? "" : Current.Watching ? "Watch"
            : ServiceRows(size) > 0 ? "" : StatusLabel(Current.Status);
        var left = first;
        AddHint("< > tabs");
        AddHint(Current.Service is null ? "Ctrl+D on service tab" : "? shortcuts");
        foreach (var hint in FooterHints)
            AddHint(hint);

        var gap = size.ContentWidth - left.Length - state.Length;
        if (state.Length == 0 || gap < 2)
        {
            return Line(size.Margin + left, MutedStyle);
        }

        return Line(size.Margin + left + new string(' ', gap) + state, MutedStyle);

        void AddHint(string hint)
        {
            var stateSpace = state.Length > 0 ? state.Length + 2 : 0;
            if (left.Length + hint.Length + stateSpace + 3 <= size.ContentWidth)
                left += $"   {hint}";
        }
    }

    private static void AddBlankRows(List<IRenderable> rows, int count)
    {
        for (var i = 0; i < count; i++)
        {
            rows.Add(Line(" ", Style.Plain));
        }
    }

    private int BodyHeight(ScreenSize size) =>
        size.Height - 1 - (size.HasFooter ? 1 : 0) - PromptHeight(size);

    private int ServiceRows(ScreenSize size)
    {
        if (_showShortcuts || _search is not null)
            return 0;
        if (Current != _commands)
            return BodyHeight(size) >= 6 ? 1 : 0;

        var reserve = CanFrameServices(size) ? 6 : 2;
        return Math.Min(Math.Min(_pages.Length - 1, 5), Math.Max(0, BodyHeight(size) - reserve));
    }

    private bool ShowServicePanel(ScreenSize size, int serviceRows) =>
        Current == _commands && CanFrameServices(size) && serviceRows > 0;

    private bool CanFrameServices(ScreenSize size) => BodyHeight(size) >= 8 && size.Width >= 44;

    private int ExtraAddressRows(ScreenSize size, int serviceRows)
    {
        var available = Math.Max(0, BodyHeight(size) - serviceRows - 6);
        if (available == 0)
            return 0;

        var addresses = _pages.Take(serviceRows).Where(page => page.HasLiveUrls).Sum(page => page.Urls.Count - 1);
        return Math.Min(available, addresses);
    }

    private int GetLogHeight(ScreenSize size)
    {
        var serviceRows = ServiceRows(size);
        var panelRows = ShowServicePanel(size, serviceRows) ? 4 + ExtraAddressRows(size, serviceRows) : 0;
        return Math.Max(0, BodyHeight(size) - serviceRows - panelRows);
    }

    private bool ShowPrompt(ScreenSize size) =>
        (Current == _commands || _search is not null) && size.Height >= (_showShortcuts ? 7 : 3);

    private int PromptHeight(ScreenSize size)
    {
        if (!ShowPrompt(size))
            return 0;
        return !_showShortcuts && _search is null && size.Height >= 10 && size.ContentWidth >= 28 ? 3 : 1;
    }

    private static ScreenSize GetSize() => new(
        Math.Max(4, AnsiConsole.Profile.Width - 1), Math.Max(1, AnsiConsole.Profile.Height - 1));

    private static string GetTitle(string name, ServiceConfig service, string configDirectory) => name.ToLowerInvariant() switch
    {
        "backend" or "dotnet" => ".NET",
        "angular" => "Angular",
        "frontend" when string.Equals(service.Command, "npm", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Path.Combine(configDirectory, service.WorkingDirectory!, "angular.json")) => "Angular",
        "frontend" or "react" => "React",
        _ => Clean(name)
    };

    private static string StatusLabel(ServiceUpdateKind kind) => kind switch
    {
        ServiceUpdateKind.Ready => "Ready",
        ServiceUpdateKind.WaitingForReady => "Starting",
        ServiceUpdateKind.Started => "Running",
        ServiceUpdateKind.Stopping => "Stopping",
        ServiceUpdateKind.Stopped => "Stopped",
        ServiceUpdateKind.Failure => "Failed",
        _ => "Starting"
    };

    private static Style StatusStyle(ServiceUpdateKind kind) => kind switch
    {
        ServiceUpdateKind.Ready or ServiceUpdateKind.Started => SuccessStyle,
        ServiceUpdateKind.Failure => FailureStyle,
        ServiceUpdateKind.Stopped => MutedStyle,
        _ => PendingStyle
    };

    private static Text Line(string text, Style style) =>
        new(text.Length == 0 ? " " : text, style) { Overflow = Overflow.Crop };

    private static Markup Decorated(string markup) => new(markup) { Overflow = Overflow.Crop };

    private static string Clean(string text)
    {
        var shortText = text[..Math.Min(text.Length, 4096)];
        return string.Concat(TerminalEscape.Replace(shortText, "").Replace("\t", "    ")
            .Where(character => !char.IsControl(character))).TrimEnd();
    }

    private sealed class Page(string? service, string title, bool watching = false)
    {
        public string? Service { get; } = service;
        public string Title { get; } = title;
        public bool Watching { get; } = watching;
        public ServiceUpdateKind Status { get; set; }
        public long? UrlLookupStarted { get; set; }
        public HashSet<string> Urls { get; } = new(StringComparer.Ordinal);
        public bool IsActive => Status is ServiceUpdateKind.Starting or ServiceUpdateKind.Started
            or ServiceUpdateKind.WaitingForReady or ServiceUpdateKind.Ready;
        public bool HasLiveUrls => IsActive && Urls.Count > 0;
        public DashboardLog Log { get; } = new();
    }

    private readonly record struct ScreenSize(int Width, int Height)
    {
        public int Inset => Width >= 40 ? 2 : 0;
        public string Margin => Inset == 0 ? "" : "  ";
        public int ContentWidth => Width - (Inset * 2);
        public bool HasFooter => Height >= 4;
    }
}
