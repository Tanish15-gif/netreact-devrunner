using NetReact.DevRunner.Infrastructure;
using Spectre.Console;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public sealed class ValidateCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        const string configFileName = "devrunner.yaml";
        var currentDirectory = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(currentDirectory, configFileName);

        try
        {
            var config = YamlConfigFile.Read(configPath);
            var errors = YamlConfigFile.GetErrors(config, currentDirectory).ToArray();

            if (errors.Length == 0)
            {
                AnsiConsole.MarkupLine("[green]devrunner.yaml is valid.[/]");
                return 0;
            }

            foreach (var error in errors)
            {
                System.Console.Error.WriteLine(error);
            }

            return 1;
        }
        catch (FileNotFoundException)
        {
            System.Console.Error.WriteLine("devrunner.yaml was not found. Run netreact init first.");
            return 1;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Console.Error.WriteLine($"Could not read {configFileName}. {exception.Message}");
            return 1;
        }
    }
}