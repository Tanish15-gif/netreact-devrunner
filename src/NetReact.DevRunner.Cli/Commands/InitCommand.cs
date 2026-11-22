using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class InitCommand : Command
{
    private const string ConfigFileName = "devrunner.yaml";

    private const string StarterConfig = """
        version: 1
        services:
          backend:
            workingDirectory: ./backend
            command: dotnet
            args: [run]
          frontend:
            workingDirectory: ./frontend
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
            writer.WriteLine(StarterConfig);
        }

        AnsiConsole.MarkupLine($"[green]Created[/] {ConfigFileName}");
        return 0;
    }
}