using System.Runtime.InteropServices;

namespace NetReact.DevRunner.Cli;

internal static class NetReactHelp
{
    public const string InitDescription = "Create a starter devrunner.yaml in the current directory.";
    public const string ValidateDescription = "Check devrunner.yaml and the configured service folders.";
    public const string StartDescription = "Start the services and supervise them in the terminal.";

    public static string Shortcuts(bool watching) => $"""
        Left / Right   Switch between tabs.
        Up / Down      Scroll one line.
        PgUp / PgDn    Scroll one page.
        Home / End     Jump to the oldest log or return to live output.
        Ctrl+D         Stop the selected service. Press again to start it.
        Ctrl+R         {(watching ? "Restart this .NET watcher." : "Restart all configured services.")}
        Ctrl+U         Show the server URLs in the NetReact tab.
        Ctrl+C         Stop all services and exit.
        """;

    public static string Version => typeof(NetReactHelp).Assembly.GetName().Version!.ToString(3);

    public static string Info(string directory) => $"""
        NetReact DevRunner {Version}
        Runtime: {RuntimeInformation.FrameworkDescription}
        OS: {RuntimeInformation.OSDescription}
        Config: {Path.Combine(directory, "devrunner.yaml")}
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
            "start" => Start,
            _ => ""
        };
        return text.Length != 0;
    }

    private static bool IsHelp(string option) => option is "-h" or "-?" or "--help";

    private static readonly string Overview = $"""
        NetReact DevRunner
        Launch and supervise ASP.NET Core, React, and other local services from one terminal.

        Usage:
          netreact <command> [options]
          netreact <command> --help

        Commands:
          init       {InitDescription}
          validate   {ValidateDescription}
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

        Creates a starter devrunner.yaml for an ASP.NET Core backend and a React frontend.
        Set each service's workingDirectory, command, and args to match your project.
        An existing devrunner.yaml is preserved.
        """;

    private const string Validate = """
        Usage: netreact validate

        Reads devrunner.yaml in the current folder and checks schema version, service commands,
        and working directories. Relative paths are resolved from the config folder.
        Exits with status 1 if a setting is invalid.
        """;

    private static readonly string Start = $"""
        Usage: netreact start [--watch] [--plain]

        Launches services from devrunner.yaml. .NET apps use dotnet run by default.

        Options:
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

