using System.Text.Json;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class InitCommand : Command
{
    private const string ConfigFileName = "devrunner.yaml";

    private static string StarterConfig(
        string? backend,
        string backendArguments,
        string? frontend,
        string npmArguments) => $"""
        version: 1
        services:
          backend:
            workingDirectory: {backend ?? "./backend"}
            command: dotnet
            args: [{backendArguments}]
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
        var (backends, frontends) = DetectFolders(root);

        BackendProject? backend = backends.Count == 1 ? backends[0] : null;

        if (backends.Count > 1)
        {
            var candidates = backends
                .OrderBy(candidate => Path.GetRelativePath(root, candidate.Directory), StringComparer.Ordinal)
                .ThenBy(candidate => candidate.ProjectFile, StringComparer.Ordinal)
                .ToArray();

            if (Console.IsInputRedirected || Console.IsOutputRedirected
                || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                Console.Error.WriteLine(
                    "Multiple backend projects found. Run netreact init in an interactive terminal to select one:");

                foreach (var candidate in candidates)
                {
                    Console.Error.WriteLine(
                        $"  {Path.GetRelativePath(root, candidate.Directory)}\\{candidate.ProjectFile}");
                }

                return 1;
            }

            backend = AnsiConsole.Prompt(new SelectionPrompt<BackendProject>()
                .Title("Which backend project should NetReact start?")
                .UseConverter(candidate => Markup.Escape(
                    $"{Path.GetRelativePath(root, candidate.Directory)}\\{candidate.ProjectFile}"))
                .AddChoices(candidates));
        }

        var frontend = frontends.Count == 1 ? frontends[0] : null;

        if (frontends.Count > 1)
        {
            var candidates = frontends
                .OrderBy(candidate => candidate.Directory, StringComparer.Ordinal)
                .ToArray();

            if (Console.IsInputRedirected || Console.IsOutputRedirected
                || !AnsiConsole.Profile.Capabilities.Interactive)
            {
                Console.Error.WriteLine(
                    "Multiple frontend projects found. Run netreact init in an interactive terminal to select one:");

                foreach (var candidate in candidates)
                {
                    Console.Error.WriteLine(
                        $"  {Path.GetRelativePath(root, candidate.Directory)} (npm {candidate.Command})");
                }

                return 1;
            }

            frontend = AnsiConsole.Prompt(new SelectionPrompt<Frontend>()
                .Title("Which frontend should NetReact start?")
                .UseConverter(candidate => Markup.Escape(
                    $"{Path.GetRelativePath(root, candidate.Directory)} (npm {candidate.Command})"))
                .AddChoices(candidates));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var backendPath = RelativeYamlPath(root, backend?.Directory);
        var frontendPath = RelativeYamlPath(root, frontend?.Directory);

        // Always identify the selected project explicitly. This also avoids
        // ambiguity when another .csproj exists in the same directory.
        var backendArguments = backend is null
            ? "run"
            : $"run, --project, {JsonSerializer.Serialize(backend.ProjectFile)}";

        FileStream file;
        try
        {
            file = File.Open(ConfigFileName, FileMode.CreateNew, FileAccess.Write);
        }
        catch (IOException) when (File.Exists(ConfigFileName))
        {
            Console.Error.WriteLine($"{ConfigFileName} already exists.");
            return 1;
        }

        using (file)
        using (var writer = new StreamWriter(file))
        {
            writer.WriteLine(StarterConfig(
                backendPath,
                backendArguments,
                frontendPath,
                frontend?.Arguments ?? "run, dev"));
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {ConfigFileName}");

        Console.WriteLine(backend is null
            ? "Backend: ./backend (check this path)"
            : $"Backend: {backendPath} ({backend.ProjectFile})");

        Console.WriteLine(frontend is null
            ? "Frontend: ./frontend (check this path)"
            : $"Frontend: {frontendPath} (npm {frontend.Command})");

        Console.WriteLine("Run netreact doctor to check the service folders and commands.");
        return 0;
    }

    internal static (List<BackendProject> Backends, List<Frontend> Frontends) DetectFolders(string root)
    {
        var backends = new List<BackendProject>();
        var frontends = new List<Frontend>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint
                | FileAttributes.Hidden
                | FileAttributes.System
        };

        while (queue.TryDequeue(out var item))
        {
            foreach (var project in Directory.EnumerateFiles(item.Directory, "*.csproj", options))
            {
                try
                {
                    if (File.ReadLines(project).Any(line =>
                        line.Contains("Microsoft.NET.Sdk.Web", StringComparison.Ordinal)))
                    {
                        backends.Add(new BackendProject(
                            item.Directory,
                            Path.GetFileName(project)));
                    }
                }
                catch (Exception ex) when (
                    ex is IOException or UnauthorizedAccessException)
                {
                    // Ignore projects that cannot be read.
                }
            }

            var manifest = Path.Combine(item.Directory, "package.json");

            if (File.Exists(manifest))
            {
                try
                {
                    using var json = JsonDocument.Parse(File.ReadAllText(manifest));

                    if (json.RootElement.ValueKind == JsonValueKind.Object
                        && json.RootElement.TryGetProperty("scripts", out var scripts)
                        && scripts.ValueKind == JsonValueKind.Object)
                    {
                        if (File.Exists(Path.Combine(item.Directory, "angular.json"))
                            && HasScript(scripts, "start"))
                        {
                            frontends.Add(new Frontend(item.Directory, "start", "start"));
                        }
                        else if (HasDependency(json.RootElement, "expo")
                            && HasScript(scripts, "start"))
                        {
                            frontends.Add(new Frontend(item.Directory, "start", "start"));
                        }
                        else if (HasScript(scripts, "dev"))
                        {
                            frontends.Add(new Frontend(item.Directory, "run dev", "run, dev"));
                        }
                    }
                }
                catch (Exception ex) when (
                    ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    // Ignore invalid or inaccessible package manifests.
                }
            }

            if (item.Depth >= 3)
            {
                continue;
            }

            foreach (var child in Directory.EnumerateDirectories(item.Directory, "*", options))
            {
                var name = Path.GetFileName(child);

                if (name.StartsWith('.')
                    || name.ToLowerInvariant() is "node_modules" or "bin" or "obj"
                        or "dist" or "artifacts")
                {
                    continue;
                }

                queue.Enqueue((child, item.Depth + 1));
            }
        }

        return (backends, frontends);
    }

    private static bool HasDependency(JsonElement root, string name) =>
        HasPackage(root, "dependencies", name)
        || HasPackage(root, "devDependencies", name);

    private static bool HasPackage(JsonElement root, string section, string name) =>
        root.TryGetProperty(section, out var packages)
        && packages.ValueKind == JsonValueKind.Object
        && packages.TryGetProperty(name, out var version)
        && version.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(version.GetString());

    private static bool HasScript(JsonElement scripts, string name) =>
        scripts.TryGetProperty(name, out var script)
        && script.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(script.GetString());

    private static string? RelativeYamlPath(string root, string? directory)
    {
        if (directory is null)
        {
            return null;
        }

        var path = Path.GetRelativePath(root, directory)
            .Replace(Path.DirectorySeparatorChar, '/');

        return JsonSerializer.Serialize(path == "." ? path : "./" + path);
    }

    internal sealed record BackendProject(string Directory, string ProjectFile);

    internal sealed record Frontend(string Directory, string Command, string Arguments);
}
