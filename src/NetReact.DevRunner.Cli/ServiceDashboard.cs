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
    private static readonly Regex TerminalEscape = new(
        @"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-_])",
        RegexOptions.NonBacktracking);
    private static readonly Regex ListeningUrl = new(
        @"(?:Now listening on:|Local:)\s+(https?://\S+)", RegexOptions.NonBacktracking);
    private static readonly Style TitleStyle = new(new Color(111, 224, 208), decoration: Decoration.Bold);
    private static readonly Style MutedStyle = new(new Color(156, 171, 182));
    private static readonly Style ErrorStyle = new(Color.Yellow);

    private readonly Lock _sync = new();
    private readonly Page[] _pages;
    private readonly Page _commands = new(null, "NetReact");
    private readonly DashboardLog _shortcuts = CreateShortcutLog(watching: false);
    private readonly DashboardLog _watchShortcuts = CreateShortcutLog(watching: true);
    private readonly string _configDirectory;
    private readonly StringBuilder _input = new();
    private int _selected;
    private int _revision;
    private bool _canReadKeys = true;
    private bool _showShortcuts;

    public ServiceDashboard(
        IReadOnlyDictionary<string, ServiceConfig?> services, string configDirectory, bool watch)
    {
        _configDirectory = configDirectory;
        _pages = services.Select(service => new Page(
                service.Key, GetTitle(service.Key), DotNetLaunch.IsWatching(service.Value!, watch)))
            .Append(_commands).ToArray();
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
                    var lastView = (-1, new ScreenSize());
                    while (!run.IsCompleted)
                    {
                        var size = GetSize();
                        ReadKeys(requests.Writer, shutdown, size);
                        var view = (Volatile.Read(ref _revision), size);
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
            Execute("urls", requests, size);
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

        log.Resize(size.ContentWidth);
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
                  urls                 Show server URLs.
                  restart NAME         Restart one service, e.g. restart backend.
                  restart all          Restart every service.
                  validate             Check devrunner.yaml.
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
                    Write(NetReactHelp.Info(_configDirectory));
                    break;
                case ["urls"]:
                    foreach (var page in _pages[..^1])
                    {
                        var address = page.Status == ServiceUpdateKind.Started
                            ? page.Url ?? "waiting for a listening URL"
                            : StatusLabel(page.Status);
                        Write($"{page.Title}: {address}");
                    }
                    break;
                case ["status"]:
                    foreach (var page in _pages[..^1])
                    {
                        Write($"{page.Title}: {StatusLabel(page.Status)}");
                    }
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
                    Write("Unknown session command. Type help for the available commands.");
                    break;
            }
        }

        _commands.Log.Resize(size.ContentWidth);
        _commands.Log.JumpTo(start, Math.Max(1, GetLogHeight(size)));
    }

    private void Request(ChannelWriter<ServiceRequest> writer, ServiceRequest request)
    {
        var target = request.Service ?? "all services";
        var action = request.Action == ServiceAction.Restart ? "Restart" : "Toggle";
        Write(writer.TryWrite(request) ? $"{action} requested: {target}." : "Another request is in progress.");
    }

    private void ValidateConfig()
    {
        try
        {
            var config = YamlConfigFile.Read(Path.Combine(_configDirectory, "devrunner.yaml"));
            var errors = YamlConfigFile.GetErrors(config, _configDirectory).ToArray();
            Write(errors.Length == 0 ? "devrunner.yaml is valid." : string.Join('\n', errors));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Write($"Could not read devrunner.yaml. {exception.Message}");
        }
    }

    private void Write(string message)
    {
        foreach (var line in message.ReplaceLineEndings("\n").Split('\n'))
        {
            _commands.Log.Append(line, Style.Plain);
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
                ServiceUpdateKind.Stopping or ServiceUpdateKind.Stopped or ServiceUpdateKind.Failure)
            {
                page.Status = update.Kind;
                if (update.Kind == ServiceUpdateKind.Starting)
                {
                    page.Url = null;
                }
            }

            var address = ListeningUrl.Match(line);
            if (address.Success)
            {
                page.Url = address.Groups[1].Value;
            }

            var style = update.Kind is ServiceUpdateKind.ErrorOutput or ServiceUpdateKind.Failure
                ? ErrorStyle : Style.Plain;
            page.Log.Append(line, style);
            Interlocked.Increment(ref _revision);
        }
    }

    private IRenderable Render(ScreenSize size)
    {
        lock (_sync)
        {
            var log = _showShortcuts ? ShortcutLog : Current.Log;
            log.Resize(size.ContentWidth);
            var rows = new List<IRenderable> { RenderHeader(size) };
            if (size.HasSpacer)
            {
                rows.Add(Line(" ", Style.Plain));
            }

            var logHeight = GetLogHeight(size);
            var lines = log.VisibleRows(logHeight).ToArray();
            var blankRows = logHeight - lines.Length;
            var hasPrompt = ShowPrompt(size);
            var alignToPrompt = hasPrompt && !_showShortcuts;
            if (alignToPrompt)
            {
                AddBlankRows(rows, blankRows);
            }

            rows.AddRange(lines.Select(entry => Line(size.Margin + entry.Text, entry.Style)));
            if (!alignToPrompt)
            {
                AddBlankRows(rows, blankRows);
            }

            if (hasPrompt)
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

    private IRenderable RenderHeader(ScreenSize size)
    {
        if (_showShortcuts)
        {
            var title = Current.Watching ? ".NET Watch shortcuts" : "Shortcuts";
            return Decorated($"{size.Margin}[bold #6fe0d0]{title}[/]");
        }

        var labels = _pages.Select(page => page == Current ? $"[{page.Title}]" : page.Title).ToArray();
        var brand = $"NetReact {NetReactHelp.Version}   ";
        var tabText = string.Join("  ", labels);
        if (new Segment(tabText).CellCount() > size.ContentWidth)
        {
            return Line($"{size.Margin}< {Current.Title} >", TitleStyle);
        }

        var tabs = string.Join("  ", labels.Select((label, i) => i == _selected
            ? $"[bold #6fe0d0]{Markup.Escape(label)}[/]"
            : $"[#9cabb6]{Markup.Escape(label)}[/]"));
        var header = new Segment(brand + tabText).CellCount() <= size.ContentWidth
            ? $"[bold]NetReact[/] [#9cabb6]{NetReactHelp.Version}[/]   {tabs}"
            : tabs;
        return Decorated(size.Margin + header);
    }

    private IRenderable RenderPrompt(ScreenSize size)
    {
        var empty = _input.Length == 0;
        var input = empty ? "Type help for commands" : _input.ToString();
        const string prefix = " > ";
        var room = Math.Max(1, size.ContentWidth - prefix.Length - 1);
        var value = empty
            ? Segment.Truncate(new Segment(input), room)?.Text ?? ""
            : Segment.SplitOverflow(new Segment(input), Overflow.Fold, room)[^1].Text;
        var fill = new string(' ', Math.Max(0, room - new Segment(value).CellCount()));
        var textColor = empty || _showShortcuts ? "#b2c1ca" : "#eaf0f4";
        var caret = _showShortcuts ? "#28343b" : "#6fe0d0";
        return Decorated($"{size.Margin}[#6fe0d0 on #28343b]{prefix}[/]"
            + $"[{textColor} on #28343b]{Markup.Escape(value)}[/]"
            + $"[on {caret}] [/]"
            + $"[on #28343b]{fill}[/]");
    }

    private IRenderable RenderFooter(ScreenSize size, DashboardLog log)
    {
        if (!_canReadKeys)
        {
            return Line($"{size.Margin}Ctrl+C to exit", MutedStyle);
        }

        var first = _showShortcuts ? "Esc close" : "? shortcuts";
        var detail = _showShortcuts ? "PgUp/PgDn scroll" : "< > tabs";
        var state = _showShortcuts ? "" : !log.IsFollowing ? "Paused  |  End live"
            : Current.Service is null ? "" : (Current.Watching ? "Watch  |  " : "") + StatusLabel(Current.Status);
        var left = $"{first}   {detail}";
        if (left.Length + state.Length + 2 > size.ContentWidth)
        {
            left = first;
        }

        var gap = size.ContentWidth - left.Length - state.Length;
        if (state.Length == 0 || gap < 2)
        {
            return Line(size.Margin + left, MutedStyle);
        }

        return Line(size.Margin + left + new string(' ', gap) + state, MutedStyle);
    }

    private static void AddBlankRows(List<IRenderable> rows, int count)
    {
        for (var i = 0; i < count; i++)
        {
            rows.Add(Line(" ", Style.Plain));
        }
    }

    private int GetLogHeight(ScreenSize size) => Math.Max(0,
        size.Height - 1 - (size.HasSpacer ? 1 : 0) - (size.HasFooter ? 1 : 0) - (ShowPrompt(size) ? 1 : 0));

    private bool ShowPrompt(ScreenSize size) => Current == _commands && size.Height >= (_showShortcuts ? 7 : 3);

    private static ScreenSize GetSize() => new(
        Math.Max(4, AnsiConsole.Profile.Width - 1), Math.Max(1, AnsiConsole.Profile.Height - 1));

    private static string GetTitle(string name) => name.ToLowerInvariant() switch
    {
        "backend" or "dotnet" => ".NET",
        "frontend" or "react" => "React",
        _ => Clean(name)
    };

    private static string StatusLabel(ServiceUpdateKind kind) => kind switch
    {
        ServiceUpdateKind.Started => "Running",
        ServiceUpdateKind.Stopping => "Stopping",
        ServiceUpdateKind.Stopped => "Stopped",
        ServiceUpdateKind.Failure => "Failed",
        _ => "Starting"
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
        public string? Url { get; set; }
        public DashboardLog Log { get; } = new();
    }

    private readonly record struct ScreenSize(int Width, int Height)
    {
        private int Inset => Width >= 40 ? 2 : 0;
        public string Margin => Inset == 0 ? "" : "  ";
        public int ContentWidth => Width - (Inset * 2);
        public bool HasSpacer => Height >= 10;
        public bool HasFooter => Height >= 4;
    }
}
