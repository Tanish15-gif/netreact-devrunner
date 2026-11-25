using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class InitCommand : Command
{
    private const string ConfigFileName = "devrunner.yaml";

    private static string StarterConfig(string? backend, string? frontend, string npmArguments) => $"""
        version: 1
        services:
          backend:
            workingDirectory: {backend ?? "./backend"}
            command: dotnet
            args: [run]
          frontend:
            workingDirectory: {frontend ?? "./frontend"}
            command: npm
            args: [{npmArguments}]
        """;

    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        if (File.Exists(ConfigFileName))
        {
            Console.Error.WriteLine($"{ConfigFileName} already exists.");
            return 1;
        }

        var root = Directory.GetCurrentDirectory();
        var (backend, frontends) = DetectFolders(root);
        var frontend = frontends.Count == 1 ? frontends[0] : null;
        if (frontends.Count > 1)
        {
            var candidates = frontends.OrderBy(candidate => candidate.Directory, StringComparer.Ordinal).ToArray();
            if (Console.IsInputRedirected || Console.IsOutputRedirected || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                Console.Error.WriteLine("Multiple frontend projects found. Run netreact init in an interactive terminal to select one:");
                foreach (var candidate in candidates)
                {
                    Console.Error.WriteLine($"  {Path.GetRelativePath(root, candidate.Directory)} (npm {candidate.Command})");
                }
                return 1;
            }

            frontend = AnsiConsole.Prompt(new SelectionPrompt<Frontend>()
                .Title("Which frontend should NetReact start?")
                .UseConverter(candidate => Markup.Escape($"{Path.GetRelativePath(root, candidate.Directory)} (npm {candidate.Command})"))
                .AddChoices(candidates));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var backendPath = RelativeYamlPath(root, backend);
        var frontendPath = RelativeYamlPath(root, frontend?.Directory);
        FileStream file;
        try
        {
            file = File.Open(ConfigFileName, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(ConfigFileName))
        {
            System.Console.Error.WriteLine($"{ConfigFileName} already exists.");
            return 1;
        }

        using (file)
        using (var writer = new StreamWriter(file))
        {
            writer.WriteLine(StarterConfig(backendPath, frontendPath, frontend?.Arguments ?? "run, dev"));
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {ConfigFileName}");
        Console.WriteLine($"Backend: {backendPath ?? "./backend (check this path)"}");
        Console.WriteLine(frontend is null
            ? "Frontend: ./frontend (check this path)"
            : $"Frontend: {frontendPath} (npm {frontend.Command})");
        Console.WriteLine("Run netreact doctor to check the service folders and commands.");
        return 0;
    }

    private static (string? Backend, List<Frontend> Frontends) DetectFolders(string root)
    {
        var backends = new List<string>();
        var frontends = new List<Frontend>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var options = new EnumerationOptions { IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.Hidden | FileAttributes.System };
        while (queue.TryDequeue(out var item))
        {
            foreach (var project in Directory.EnumerateFiles(item.Directory, "*.csproj", options))
            {
                try
                {
                    if (File.ReadLines(project).Any(line => line.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal)))
                        backends.Add(item.Directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            var manifest = Path.Combine(item.Directory, "package.json");
            if (File.Exists(manifest))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));
                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object)
                    {
                        if (File.Exists(Path.Combine(item.Directory, "angular.json")) && HasScript(scripts, "start"))
                        {
                            frontends.Add(new(item.Directory, "start", "start"));
                        }
                        else if (HasScript(scripts, "dev"))
                        {
                            frontends.Add(new(item.Directory, "run dev", "run, dev"));
                        }
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            }

            if (item.Depth >= 3) continue;
            foreach (var child in Directory.EnumerateDirectories(item.Directory, "*", options))
            {
                var name = Path.GetFileName(child);
                if (name.StartsWith('.') || name.ToLowerInvariant() is "node_modules" or "bin" or "obj" or "dist" or "artifacts")
                    continue;
                queue.Enqueue((child, item.Depth + 1));
            }
        }

        return (backends.Count == 1 ? backends[0] : null, frontends);
    }

    private static bool HasScript(JsonElement scripts, string name) =>
        scripts.TryGetProperty(name, out var script) && script.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(script.GetString());

    private static string? RelativeYamlPath(string root, string? directory)
    {
        if (directory is null) return null;
        var path = Path.GetRelativePath(root, directory).Replace(Path.DirectorySeparatorChar, '/');
        return JsonSerializer.Serialize(path == "." ? path : "./" + path);
    }

    private sealed record Frontend(string Directory, string Command, string Arguments);
}
