using NetReact.DevRunner.Cli.Commands;
using Spectre.Console.Cli;
using NetReact.DevRunner.Cli;

if (args.Length >= 3 && args[0] == "start" && args[^2] == "--" && args[^1] == "watch")
{
    args = [.. args[..^2], "--watch"];
}

if (NetReactHelp.TryGetHelp(args, out var help))
{
    Console.WriteLine(help);
    return 0;
}

if (args is ["--info"])
{
    Console.WriteLine(NetReactHelp.Info(Directory.GetCurrentDirectory()));
    return 0;
}

var app = new CommandApp();

app.Configure(config =>
{
    config.SetApplicationName("netreact");
    config.SetApplicationVersion(NetReactHelp.Version);
    config.AddCommand<InitCommand>("init")
        .WithDescription(NetReactHelp.InitDescription);
    config.AddCommand<ValidateCommand>("validate")
        .WithDescription(NetReactHelp.ValidateDescription);
    config.AddCommand<StartCommand>("start")
        .WithDescription(NetReactHelp.StartDescription);
});

return await app.RunAsync(args);
