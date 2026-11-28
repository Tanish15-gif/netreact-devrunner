using System.Text.Json;
using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class ServiceBuild
{
    public static ServiceConfig? Resolve(ServiceConfig service, string configDirectory)
    {
        if (DotNetLaunch.IsDotNet(service))
        {
            var arguments = new List<string?> { "build" };
            var runArguments = (service.Args ?? []).TakeWhile(argument => argument != "--").ToArray();
            for (var i = 0; i < runArguments.Length; i++)
            {
                if (runArguments[i] == "--project" && i + 1 < runArguments.Length)
                {
                    arguments.Add(runArguments[i + 1]);
                    break;
                }

                if (runArguments[i]?.StartsWith("--project=", StringComparison.Ordinal) == true)
                {
                    arguments.Add(runArguments[i]!["--project=".Length..]);
                    break;
                }
            }

            return new() { Command = service.Command, WorkingDirectory = service.WorkingDirectory, Args = arguments };
        }

        if (!string.Equals(service.Command, "npm", StringComparison.OrdinalIgnoreCase))
            return null;

        var manifest = Path.Combine(configDirectory, service.WorkingDirectory!, "package.json");
        try
        {
            using var package = JsonDocument.Parse(File.ReadAllText(manifest));
            if (package.RootElement.ValueKind == JsonValueKind.Object &&
                package.RootElement.TryGetProperty("scripts", out var scripts) &&
                scripts.ValueKind == JsonValueKind.Object &&
                scripts.TryGetProperty("build", out var build) &&
                build.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(build.GetString()))
            {
                return new() { Command = "npm", WorkingDirectory = service.WorkingDirectory, Args = ["run", "build"] };
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            // A missing, unreadable, or malformed manifest doesn't define a usable build script.
        }

        return null;
    }
}
