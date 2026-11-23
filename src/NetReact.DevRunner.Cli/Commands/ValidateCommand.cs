using NetReact.DevRunner.Infrastructure;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public class ValidateCommand : Command<ConfigSettings>
{
    protected virtual bool CheckTools => false;

    public override int Execute(CommandContext context, ConfigSettings settings, CancellationToken cancellationToken)
    {
        var configFileName = settings.Config;

        try
        {
            var configPath = Path.GetFullPath(configFileName);
            var currentDirectory = Path.GetDirectoryName(configPath)!;
            var config = YamlConfigFile.Read(configPath);
            var errors = YamlConfigFile.GetErrors(config, currentDirectory).ToArray();

            if (errors.Length == 0 && CheckTools)
            {
                errors = ServiceDiagnostics.Check(config!, currentDirectory).ToArray();
            }

            if (errors.Length == 0)
            {
                Console.WriteLine(CheckTools ? "Service checks passed." : $"{configFileName} is valid.");
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
            System.Console.Error.WriteLine($"{configFileName} was not found. Run netreact init or specify --config.");
            return 1;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            System.Console.Error.WriteLine($"Could not read {configFileName}. {exception.Message}");
            return 1;
        }
    }
}