namespace NetReact.DevRunner.Core;

public sealed class DevRunnerConfig
{
    public int Version { get; set; }
    public Dictionary<string, ServiceConfig?>? Services { get; set; }
}

public sealed class ServiceConfig
{
    public string? WorkingDirectory { get; set; }
    public string? ReadyUrl { get; set; }
    public string? Command { get; set; }
    public List<string?>? Args { get; set; }
}