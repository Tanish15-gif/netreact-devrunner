using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Channels;
using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class ServiceRunner
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
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
                await StopAsync(running.Values.ToArray(), report);
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
                var child = StartService(name, services[name]!, configDirectory, watch, report,
                    serviceHostAssembly ?? System.Reflection.Assembly.GetEntryAssembly()!.Location);
                running.Add(name, child);
                child.Process.StandardInput.BaseStream.WriteByte(1);
                child.Process.StandardInput.Close();
                child.Process.BeginOutputReadLine();
                child.Process.BeginErrorReadLine();
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

        void RemoveStopped(RunningService service)
        {
            if (service.Process.HasExited)
            {
                running.Remove(service.Name);
                service.Dispose();
            }
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
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is string line)
            {
                report(new(name, ServiceUpdateKind.Output, line));
            }
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is string line)
            {
                report(new(name, ServiceUpdateKind.ErrorOutput, line));
            }
        };

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
