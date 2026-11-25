using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class ServiceDiagnostics
{
    public static IEnumerable<string> Check(DevRunnerConfig config, string configDirectory)
    {
        foreach (var (name, settings) in config.Services!)
        {
            var service = settings!;
            var directory = Path.GetFullPath(service.WorkingDirectory!, configDirectory);
            var error = CheckService(service, directory);
            if (error is not null) yield return $"{name} ({directory}): {error}";
        }
    }

    private static string? CheckService(ServiceConfig service, string directory)
    {
        try
        {
            var command = service.Command!;
            if (OperatingSystem.IsWindows() && command.Equals("npm", StringComparison.OrdinalIgnoreCase))
                ServiceRunner.FindWindowsNpm();
            else if (!CanResolve(command, directory))
                return $"Cannot find executable '{command}'. Check command and PATH.";

            if (Path.GetFileNameWithoutExtension(command).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                using var sdk = Process.Start(new ProcessStartInfo(command, "--version")
                {
                    WorkingDirectory = directory,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                })!;
                sdk.BeginOutputReadLine();
                sdk.BeginErrorReadLine();
                if (!sdk.WaitForExit(10000))
                {
                    sdk.Kill(entireProcessTree: true);
                    return "Timed out checking the .NET SDK.";
                }
                if (sdk.ExitCode != 0) return "Install the .NET SDK required by this project or its global.json.";
            }

            var npmScript = service.Args switch
            {
                ["run", string named, ..] => named,
                ["start", ..] => "start",
                _ => null
            };
            if (command.Equals("npm", StringComparison.OrdinalIgnoreCase) && npmScript is not null)
            {
                var manifest = Path.Combine(directory, "package.json");
                if (!File.Exists(manifest)) return "Missing package.json. Set workingDirectory to the frontend folder.";
                using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                if (json.RootElement.ValueKind != JsonValueKind.Object
                    || !json.RootElement.TryGetProperty("scripts", out var scripts) || scripts.ValueKind != JsonValueKind.Object
                    || !scripts.TryGetProperty(npmScript, out var value) || value.ValueKind != JsonValueKind.String
                    || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    // npm start can run server.js when package.json does not define a start script.
                    if (service.Args is ["start", ..] && File.Exists(Path.Combine(directory, "server.js")))
                        return null;
                    return $"Add the '{npmScript}' script to package.json or change args.";
                }
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException
            or UnauthorizedAccessException or JsonException)
        {
            return ex.Message;
        }
    }

    private static bool CanResolve(string command, string directory)
    {
        var folders = Path.IsPathRooted(command)
            || command.Contains(Path.DirectorySeparatorChar)
            || command.Contains(Path.AltDirectorySeparatorChar)
            ? new[] { directory }
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var folder in folders.Where(Path.IsPathFullyQualified))
        {
            var path = Path.Combine(folder, command);
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(Path.HasExtension(path) ? path : path + ".exe")) return true;
            }
            else if (File.Exists(path) && (File.GetUnixFileMode(path) &
                (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return true;
        }
        return false;
    }
}
