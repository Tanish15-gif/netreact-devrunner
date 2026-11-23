using System.ComponentModel;
using Spectre.Console.Cli;

namespace NetReact.DevRunner.Cli.Commands;

public class ConfigSettings : CommandSettings
{
    [CommandOption("--config <PATH>")]
    [Description("Path to the services YAML file.")]
    public string Config { get; init; } = "devrunner.yaml";
}
