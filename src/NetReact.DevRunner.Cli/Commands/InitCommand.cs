using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class InitCommand : Command
{
    private const string ConfigFileName = "devrunner.yaml";

    private static string StarterConfig(string? backend, string? frontend) => $"""
        version: 1
        services:
          backend:
            workingDirectory: {backend ?? "./backend"}
            command: dotnet
            args: [run]
          frontend:
            workingDirectory: {frontend ?? "./frontend"}
            command: npm
            args: [run, dev]
        """;

    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
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
            var (backend, frontend) = DetectFolders(Directory.GetCurrentDirectory());
            writer.WriteLine(StarterConfig(backend, frontend));
            Console.WriteLine($"Backend: {backend ?? "./backend (check this path)"}");
            Console.WriteLine($"Frontend: {frontend ?? "./frontend (check this path)"}");
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {ConfigFileName}");
        Console.WriteLine("Run netreact doctor to check the service folders and commands.");
        return 0;
    }

    private static (string? Backend, string? Frontend) DetectFolders(string root)
    {
        var backends = new List<string>();
        var frontends = new List<string>();
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
                        && json.RootElement.TryGetProperty("scripts", out var scripts) && scripts.ValueKind == JsonValueKind.Object
                        && scripts.TryGetProperty("dev", out var script) && script.ValueKind == JsonValueKind.String)
                        frontends.Add(item.Directory);
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

        string? UniquePath(List<string> matches)
        {
            if (matches.Count != 1) return null;
            var path = Path.GetRelativePath(root, matches[0]).Replace(Path.DirectorySeparatorChar, '/');
            if (path != ".") path = "./" + path;
            return JsonSerializer.Serialize(path);
        }
        return (UniquePath(backends), UniquePath(frontends));
    }
}