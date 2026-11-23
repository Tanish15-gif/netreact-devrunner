using System.ComponentModel;
using System.Runtime.InteropServices;
using NetReact.DevRunner.Core;
using NetReact.DevRunner.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class StartCommand : AsyncCommand<StartCommand.Settings>
{
    public sealed class Settings : ConfigSettings
    {
        [CommandArgument(0, "[SERVICE]")]
        [Description("Start only this configured service.")]
        public string? Service { get; init; }

        [CommandOption("--watch")]
        [Description("Run .NET apps with dotnet watch run.")]
        public bool Watch { get; init; }

        [CommandOption("--plain")]
        [Description("Stream labeled logs without the live console.")]
        public bool Plain { get; init; }
    }

    public override async Task<int> ExecuteAsync(
        CommandContext context, Settings settings, CancellationToken cancellationToken)
    {
        var configFileName = settings.Config;
        string configPath;
        string configDirectory;

        DevRunnerConfig? config;
        try
        {
            configPath = Path.GetFullPath(configFileName);
            configDirectory = Path.GetDirectoryName(configPath)!;
            config = YamlConfigFile.Read(configPath);
        }
        catch (FileNotFoundException)
        {
            Console.Error.WriteLine($"{configFileName} was not found. Run netreact init or specify --config.");
            return 1;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"Could not read {configFileName}. {exception.Message}");
            return 1;
        }

        if (settings.Service is string selected && config?.Services is { Count: > 0 } available)
        {
            if (!available.TryGetValue(selected, out var service))
            {
                Console.Error.WriteLine($"Unknown service '{selected}'. Available: {string.Join(", ", available.Keys)}.");
                return 1;
            }
            config.Services = new() { [selected] = service };
        }

        var errors = YamlConfigFile.GetErrors(config, configDirectory).ToArray();
        if (errors.Length != 0)
        {
            foreach (var error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return 1;
        }

        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var terminate = OperatingSystem.IsWindows() ? null
            : PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
            {
                signal.Cancel = true;
                shutdown.Cancel();
            });
        ConsoleCancelEventHandler onCancel = (_, args) =>
        {
            args.Cancel = true;
            try
            {
                shutdown.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // A console signal can arrive just as the command finishes.
            }
        };
        Console.CancelKeyPress += onCancel;

        try
        {
            var services = config!.Services!;
            var capabilities = AnsiConsole.Profile.Capabilities;
            if (!settings.Plain && capabilities.Ansi && capabilities.Interactive
                && !Console.IsInputRedirected && !Console.IsOutputRedirected)
            {
                var dashboard = new ServiceDashboard(services, configPath, settings.Watch);
                return await dashboard.RunAsync(
                    (requests, report) => ServiceRunner.RunAsync(
                        services, configDirectory, settings.Watch, requests, report, shutdown.Token),
                    shutdown);
            }

            Console.WriteLine("Press Ctrl+C to stop all services.");
            return await ServiceRunner.RunAsync(
                services,
                configDirectory,
                settings.Watch,
                requests: null,
                WritePlain,
                shutdown.Token);
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static void WritePlain(ServiceUpdate update)
    {
        if (update.Service is null)
        {
            Console.WriteLine(update.Message);
            return;
        }

        switch (update.Kind)
        {
            case ServiceUpdateKind.Output:
                Console.WriteLine($"[{update.Service} stdout] {update.Message}");
                break;
            case ServiceUpdateKind.ErrorOutput:
                Console.Error.WriteLine($"[{update.Service} stderr] {update.Message}");
                break;
            case ServiceUpdateKind.Failure:
                Console.Error.WriteLine($"[{update.Service}] {update.Message}");
                break;
            default:
                Console.WriteLine($"[{update.Service}] {update.Message}");
                break;
        }
    }
}
