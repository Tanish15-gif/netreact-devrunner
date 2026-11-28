using System.Runtime.InteropServices;

namespace NetReact.DevRunner.Cli;

internal static class NetReactHelp
{
    public const string InitDescription = "Create a starter devrunner.yaml in the current directory.";
    public const string ValidateDescription = "Check devrunner.yaml and the configured service folders.";
    public const string StartDescription = "Start the services and supervise them in the terminal.";

    public static string Shortcuts(bool watching) => $"""
        Left / Right   Switch between tabs.
        /              Search this tab's log. Esc returns to live output.
        Mouse wheel    Scroll the active tab's logs.
        Alt+drag       Select text in VS Code on Windows and Linux.
        select         Pause for drag selection. Esc returns to the live display.
        Up / Down      Scroll one line.
        PgUp / PgDn    Scroll one page.
        Home / End     Jump to the oldest log or return to live output.
        Ctrl+D         Stop the selected service. Press again to start it.
        Ctrl+B         Build the selected service (all on the NetReact tab).
        Ctrl+R         {(watching ? "Restart this .NET watcher." : "Restart all configured services.")}
        Ctrl+U         Return to NetReact to see service states and URLs.
        Ctrl+C         Stop all services and exit.
        """;

    public static string Version => typeof(NetReactHelp).Assembly.GetName().Version!.ToString(3);

    public static string Info(string directory, string? configPath = null) => $"""
        NetReact DevRunner {Version}
        Runtime: {RuntimeInformation.FrameworkDescription}
        OS: {RuntimeInformation.OSDescription}
        Config: {configPath ?? Path.Combine(directory, "devrunner.yaml")}
        """;

    public static bool TryGetHelp(string[] args, out string text)
    {
        var command = args switch
        {
            [] => "",
            [var option] when IsHelp(option) || option == "help" => "",
            [var name, var option] when IsHelp(option) => name,
            ["help", var name] => name,
            _ => null
        };

        text = command switch
        {
            "" => Overview,
            "init" => Init,
            "validate" => Validate,
            "doctor" => "Usage: netreact doctor [--config <path>]\nCheck each service executable, the .NET SDK, and configured npm scripts.",
            "start" => Start,
            _ => ""
        };
        return text.Length != 0;
    }

    private static bool IsHelp(string option) => option is "-h" or "-?" or "--help";

    private static readonly string Overview = $"""
        NetReact DevRunner
        Launch and supervise ASP.NET Core, React, Angular, and other local services from one terminal.

        Usage:
          netreact <command> [options]
          netreact <command> --help

        Commands:
          init       {InitDescription}
          validate   {ValidateDescription}
          doctor     Check service programs, SDK, and npm scripts.
          start      {StartDescription}
          help       Show general help or help for a command.

        Options:
          -h, --help  Show this help.
          --version   Show the installed NetReact version.
          --info      Show version, runtime, and config location.

        Examples:
          netreact init
          netreact validate
          netreact start          Run the backend with dotnet run.
          netreact start --watch  Run the backend with dotnet watch run.
          netreact start --help
        """;

    private const string Init = """
        Usage: netreact init

        Creates a starter devrunner.yaml for an ASP.NET Core backend and a React or Angular frontend.
        Searches up to three folders deep for a web project and a frontend with npm run dev
        or an Angular workspace with npm start. Prompts when there are multiple frontend projects.
        Check workingDirectory when a service is missing or several backends match.
        An existing devrunner.yaml is preserved.
        """;

    private const string Validate = """
        Usage: netreact validate [--config <path>]

        Reads devrunner.yaml in the current folder and checks schema version, service commands,
        and working directories. Relative paths are resolved from the config folder.
        Exits with status 1 if a setting is invalid.
        """;

    private static readonly string Start = $"""
        Usage: netreact start [SERVICE] [--config <path>] [--watch] [--plain]

        Launches services from devrunner.yaml. .NET apps use dotnet run by default.

        Options:
          SERVICE   Start one service by its YAML name, e.g. netreact start backend.
          --config  Read a different YAML file. Service paths are relative to that file.
          --watch   Run .NET apps with dotnet watch run.
                    netreact start -- watch is also supported.
          --plain   Stream labeled logs to terminal scrollback. Used automatically
                    with redirected input or output.

        Interactive controls:
        {Shortcuts(watching: false)}

        .NET Watch tab:
          Save compatible changes to apply Hot Reload automatically.
          Ctrl+R restarts only the selected .NET watcher on its tab.
          Use restart all in the NetReact tab to restart every service.
          Run dotnet watch --list in the .NET project folder to list watched files.
          Run dotnet watch --help at the shell for SDK watcher options.

        Press ? for shortcuts. Type help in the NetReact tab for session commands.
        """;
}

