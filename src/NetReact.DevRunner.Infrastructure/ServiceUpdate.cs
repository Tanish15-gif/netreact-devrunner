namespace NetReact.DevRunner.Infrastructure;

public enum ServiceAction
{
    Toggle,
    Restart
}

public readonly record struct ServiceRequest(ServiceAction Action, string? Service);

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
