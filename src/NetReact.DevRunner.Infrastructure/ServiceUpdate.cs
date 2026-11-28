namespace NetReact.DevRunner.Infrastructure;

public enum ServiceAction
{
    Toggle,
    Restart,
    Build
}

public readonly record struct ServiceRequest(ServiceAction Action, string? Service, IReadOnlyCollection<string>? ApprovedStops = null);

public enum ServiceUpdateKind
{
    Notice,
    Starting,
    Started,
    WaitingForReady,
    Ready,
    Output,
    ErrorOutput,
    Stopping,
    Stopped,
    Failure
}

public readonly record struct ServiceUpdate(string? Service, ServiceUpdateKind Kind, string Message);
