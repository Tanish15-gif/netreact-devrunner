using NetReact.DevRunner.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace NetReact.DevRunner.Infrastructure;

public static class YamlConfigFile
{
    public static DevRunnerConfig? Read(string path)
    {
        using var reader = File.OpenText(path);

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithDuplicateKeyChecking()
            .Build();

        try
        {
            return deserializer.Deserialize<DevRunnerConfig>(reader);
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException($"Invalid YAML. {exception.Message}", exception);
        }
    }

    public static IEnumerable<string> GetErrors(DevRunnerConfig? config, string configDirectory)
    {
        if (config is null)
        {
            yield return "The configuration is empty.";
            yield break;
        }

        if (config.Version != 1)
        {
            yield return "Set version to 1.";
        }

        if (config.Services is not { Count: > 0 } services)
        {
            yield return "Define at least one service.";
            yield break;
        }

        foreach (var (name, service) in services)
        {
            if (service is null)
            {
                yield return $"Service '{name}' needs a configuration.";
                continue;
            }

            if (string.IsNullOrWhiteSpace(service.Command))
            {
                yield return $"Service '{name}' needs a command.";
            }

            if (service.Args?.Contains(null) == true)
            {
                yield return $"Service '{name}' has a null argument.";
            }

            if (string.IsNullOrWhiteSpace(service.WorkingDirectory))
            {
                yield return $"Service '{name}' needs a workingDirectory.";
            }
            else if (!Directory.Exists(Path.Combine(configDirectory, service.WorkingDirectory)))
            {
                yield return $"Service '{name}' cannot find folder '{service.WorkingDirectory}'.";
            }
        }
    }
}