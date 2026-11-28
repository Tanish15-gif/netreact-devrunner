using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class ServiceRunner
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private static readonly Regex PortConflict = new(
        @"\bport\s+(?<port>[0-9]{1,5})\s+is already in use\b|"
        + @"failed to bind to address https?://\S+:(?<port>[0-9]{1,5})\b.*address already in use|"
        + @"EADDRINUSE: address already in use[^\r\n]*?:(?<port>[0-9]{1,5})\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
    { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task<int> RunAsync
    (
        IReadOnlyDictionary<string, ServiceConfig?> services,
        string configDirectory,
        bool watch,
        ChannelReader<ServiceRequest>? requests,
        Action<ServiceUpdate> report,
        CancellationToken cancellationToken,
        string? serviceHostAssembly = null
    )
    {
        var running = new Dictionary<string, RunningService>();
        using var requestsLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var host = serviceHostAssembly ?? System.Reflection.Assembly.GetEntryAssembly()!.Location;
        var build = Task.CompletedTask;
        string[] building = [];

        try
        {
            foreach (var name in services.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!Start(name) && requests is null)
                {
                    return 1;
                }
            }

            var pendingRequest = requests?.ReadAsync(requestsLifetime.Token).AsTask();
            while (true)
            {
                var pendingExit = running.Count == 0
                    ? null
                    : Task.WhenAny(running.Values.Select(service => service.ExitCode));

                if (pendingRequest is not null && (pendingExit is null
                    || await Task.WhenAny(pendingExit, pendingRequest).WaitAsync(cancellationToken) == pendingRequest))
                {
                    var request = await pendingRequest.WaitAsync(cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    await ApplyRequestAsync(request);
                    pendingRequest = requests!.ReadAsync(requestsLifetime.Token).AsTask();
                    continue;
                }

                if (pendingExit is null)
                {
                    return 0;
                }

                var firstExit = await pendingExit.WaitAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                var exited = running.Values.First(service => service.ExitCode == firstExit);
                var code = await firstExit;
                var outcome = code == 0 ? ServiceUpdateKind.Stopped : ServiceUpdateKind.Failure;
                if (requests is null)
                {
                    report(new(exited.Name, outcome,
                        $"Exited with code {code}. Stopping the other services."));
                    return code;
                }

                await StopAsync([exited], report);
                RemoveStopped(exited);
                report(new(exited.Name, outcome,
                    $"Exited with code {code}. Ctrl+D starts this service again."));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report(new(null, ServiceUpdateKind.Notice, "Stopping services..."));
            return 130;
        }
        finally
        {
            requestsLifetime.Cancel();
            try
            {
                await Task.WhenAll(build, StopAsync(running.Values.ToArray(), report));
            }
            finally
            {
                foreach (var service in running.Values)
                {
                    service.Dispose();
                }
            }
        }

        bool Start(string name)
        {
            report(new(name, ServiceUpdateKind.Starting, "Starting..."));
            try
            {
                var child = StartService(name, services[name]!, configDirectory, watch, report, host);
                running.Add(name, child);
                ReleaseProcess(child);
                report(new(name, ServiceUpdateKind.Started, $"Started (PID {child.Process.Id})."));
                if (services[name]!.ReadyUrl is string url)
                {
                    report(new(name, ServiceUpdateKind.WaitingForReady, $"Waiting for {url}"));
                    child.Probe = ProbeAsync(name, url, report, child.ProbeLifetime.Token);
                }
                return true;
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException or ArgumentException or IOException)
            {
                report(new(name, ServiceUpdateKind.Failure, $"Could not start. {exception.Message}"));
                return false;
            }
        }

        async Task ApplyRequestAsync(ServiceRequest request)
        {
            var targets = request.Service is null ? services.Keys.ToArray() : [request.Service];
            if (request.Action == ServiceAction.Build)
            {
                await StartBuildAsync(targets, request.ApprovedStops);
                return;
            }

            if (!build.IsCompleted && targets.Any(building.Contains))
            {
                report(new(null, ServiceUpdateKind.ErrorOutput, "Wait for the build to finish before starting or restarting its services."));
                return;
            }

            var selected = targets.Where(running.ContainsKey).Select(name => running[name]).ToArray();
            var startAfterStop = request.Action == ServiceAction.Restart
                || selected.All(service => service.Process.HasExited);

            await StopAsync(selected, report);
            foreach (var service in selected)
            {
                RemoveStopped(service);
            }

            if (startAfterStop)
            {
                foreach (var name in targets.Where(services.ContainsKey))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!running.ContainsKey(name))
                    {
                        Start(name);
                    }
                }
            }
        }

        async Task StartBuildAsync(string[] targets, IReadOnlyCollection<string>? approvedStops)
        {
            if (!build.IsCompleted)
            {
                report(new(null, ServiceUpdateKind.ErrorOutput, "A build is already running."));
                return;
            }
            await build;

            var commands = new Dictionary<string, ServiceConfig>();
            foreach (var name in targets)
            {
                if (services.TryGetValue(name, out var service) && ServiceBuild.Resolve(service!, configDirectory) is { } command)
                    commands.Add(name, command);
                else
                    report(new(null, ServiceUpdateKind.ErrorOutput, $"[{name}] No build command found. .NET uses dotnet build and npm projects need a build script."));
            }
            if (commands.Count == 0)
                return;

            var selected = commands.Keys.Where(running.ContainsKey).Select(name => running[name]).ToArray();
            var active = selected.Where(IsRunning).ToArray();
            if (active.Any(service => approvedStops?.Contains(service.Name) != true))
            {
                var names = string.Join(", ", active.Select(service => service.Name));
                report(new(null, ServiceUpdateKind.ErrorOutput, $"Stop {names} before building, then retry the build."));
                return;
            }

            await StopAsync(selected, report);
            if (selected.Any(IsRunning))
            {
                report(new(null, ServiceUpdateKind.ErrorOutput, "Build canceled because a service could not finish stopping."));
                return;
            }

            foreach (var service in selected)
                RemoveStopped(service);

            cancellationToken.ThrowIfCancellationRequested();
            building = commands.Keys.ToArray();
            build = RunBuildsAsync(commands, configDirectory, report, host, requestsLifetime.Token);
        }

        void RemoveStopped(RunningService service)
        {
            if (service.Process.HasExited)
            {
                running.Remove(service.Name);
                service.Dispose();
            }
        }
    }

    private static bool IsRunning(RunningService service) => !service.Process.HasExited || service.Group.IsActive();

    private static void ReleaseProcess(RunningService service)
    {
        service.Process.StandardInput.BaseStream.WriteByte(1);
        service.Process.StandardInput.Close();
        service.Process.BeginOutputReadLine();
        service.Process.BeginErrorReadLine();
    }

    private static async Task RunBuildsAsync(
        Dictionary<string, ServiceConfig> commands, string configDirectory, Action<ServiceUpdate> report,
        string host, CancellationToken cancellationToken)
    {
        try
        {
            foreach (var (name, config) in commands)
            {
                cancellationToken.ThrowIfCancellationRequested();
                report(new(null, ServiceUpdateKind.Notice, $"[{name}] Running {Path.GetFileName(config.Command)} {string.Join(' ', config.Args!)}"));
                RunningService? process = null;
                ServiceUpdate result;
                try
                {
                    process = StartService(name, config, configDirectory, false,
                        update => report(new(null, update.Kind, $"[{name}] {update.Message}")), host);
                    ReleaseProcess(process);
                    var code = await process.ExitCode.WaitAsync(cancellationToken);
                    await process.Process.WaitForExitAsync(cancellationToken);
                    result = code == 0
                        ? new(null, ServiceUpdateKind.Ready, $"[{name}] Build succeeded.")
                        : new(null, ServiceUpdateKind.Failure, $"[{name}] Build failed (exit code {code}).");
                }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or IOException)
                {
                    result = new(null, ServiceUpdateKind.Failure, $"[{name}] Could not build. {error.Message}");
                }
                finally
                {
                    if (process is not null)
                    {
                        using (process)
                            await StopAsync([process], update =>
                            {
                                if (update.Kind == ServiceUpdateKind.Failure)
                                    report(new(null, update.Kind, $"[{name}] {update.Message}"));
                            });
                    }
                }
                report(result);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            report(new(null, ServiceUpdateKind.Notice, "Build canceled."));
        }
    }

    private static RunningService StartService(
        string name,
        ServiceConfig service,
        string configDirectory,
        bool watch,
        Action<ServiceUpdate> report,
        string serviceHostAssembly)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = service.Command!,
            WorkingDirectory = Path.GetFullPath(service.WorkingDirectory!, configDirectory),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (OperatingSystem.IsWindows())
        {
            startInfo.CreateNewProcessGroup = true;
        }

        if (OperatingSystem.IsWindows() && string.Equals(service.Command, "npm", StringComparison.OrdinalIgnoreCase))
        {
            // The standard Windows Node installer provides npm as a batch file.
            // Start its JavaScript entry point directly to avoid shell parsing.
            var (node, npmCli) = FindWindowsNpm();
            startInfo.FileName = node;
            startInfo.ArgumentList.Add(npmCli);
        }

        foreach (var argument in DotNetLaunch.GetArguments(service, watch))
        {
            startInfo.ArgumentList.Add(argument!);
        }

        ServiceProcessGroup.Prepare(startInfo, serviceHostAssembly);
        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var exit = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var portConflictReported = 0;

        void ReportOutput(ServiceUpdateKind kind, string? line)
        {
            if (line is null) return;
            report(new(name, kind, line));
            if (Volatile.Read(ref portConflictReported) != 0) return;

            var conflict = PortConflict.Match(line);
            if (conflict.Success && Interlocked.Exchange(ref portConflictReported, 1) == 0)
            {
                var port = conflict.Groups["port"].Value;
                report(new(name, ServiceUpdateKind.ErrorOutput,
                    $"Port {port} is already in use. Stop the process using it or configure a different port for {name}."));
            }
        }

        process.Exited += (_, _) =>
        {
            try
            {
                exit.TrySetResult(process.ExitCode);
            }
            catch (InvalidOperationException)
            {
                // Cleanup may dispose the handle while an exit notification is pending.
                exit.TrySetCanceled();
            }
        };
        process.OutputDataReceived += (_, args) => ReportOutput(ServiceUpdateKind.Output, args.Data);
        process.ErrorDataReceived += (_, args) => ReportOutput(ServiceUpdateKind.ErrorOutput, args.Data);

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("The process did not start.");
            }

            return new RunningService(name, process, exit.Task, new ServiceProcessGroup(process));
        }
        catch
        {
            try { process.StandardInput.Close(); } catch (InvalidOperationException) { }
            process.Dispose();
            throw;
        }
    }

    internal static (string Node, string NpmCli) FindWindowsNpm()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory))
            {
                continue;
            }

            var node = Path.Combine(directory, "node.exe");
            var npmCli = Path.Combine(directory, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(node) && File.Exists(npmCli))
            {
                return (node, npmCli);
            }
        }

        throw new InvalidOperationException(
            "Could not locate npm alongside node.exe. Add your Node.js installation folder to PATH.");
    }

    private static async Task ProbeAsync(string name, string url, Action<ServiceUpdate> report, CancellationToken token)
    {
        try
        {
            while (true)
            {
                try
                {
                    using var response = await ProbeClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
                    if (response.IsSuccessStatusCode)
                    {
                        report(new(name, ServiceUpdateKind.Ready, $"Ready at {url}"));
                        return;
                    }
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
                await Task.Delay(500, token);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private static async Task StopAsync(IReadOnlyList<RunningService> running, Action<ServiceUpdate> report)
    {
        foreach (var service in running)
        {
            service.ProbeLifetime.Cancel();
            if (service.Group.IsActive() || !service.Process.HasExited)
            {
                report(new(service.Name, ServiceUpdateKind.Stopping, "Stopping..."));
                service.Group.RequestStop();
            }
        }
        await Task.WhenAll(running.Select(service => service.Probe));
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < ShutdownTimeout && running.Any(service => !service.Process.HasExited || service.Group.IsActive()))
            await Task.Delay(50);

        foreach (var service in running)
        {
            try
            {
                // Keep control of descendants even when their original parent has already exited.
                if (service.Group.IsActive()) service.Group.ForceStop();
                if (!service.Process.HasExited) service.Process.Kill(entireProcessTree: true);
                await service.Process.WaitForExitAsync().WaitAsync(ShutdownTimeout);
                report(new(service.Name, ServiceUpdateKind.Stopped, "Stopped."));
            }
            catch (Exception ex) when (ex is Win32Exception or TimeoutException or InvalidOperationException or AggregateException)
            {
                report(new(service.Name, ServiceUpdateKind.Failure, $"Could not finish shutdown. {ex.Message}"));
            }
        }
    }

    private sealed record RunningService(string Name, Process Process, Task<int> ExitCode, ServiceProcessGroup Group) : IDisposable
    {
        public CancellationTokenSource ProbeLifetime { get; } = new();
        public Task Probe { get; set; } = Task.CompletedTask;
        public void Dispose()
        {
            Group.Dispose();
            Process.Dispose();
            ProbeLifetime.Dispose();
        }
    }
}
