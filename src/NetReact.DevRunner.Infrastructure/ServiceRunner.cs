using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class ServiceRunner
{
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(3);
    private const uint CtrlBreakEvent = 1;

    public static async Task<int> RunAsync
    (
        IReadOnlyDictionary<string, ServiceConfig?> services,
        string configDirectory,
        bool watch,
        ChannelReader<ServiceRequest>? requests,
        Action<ServiceUpdate> report,
        CancellationToken cancellationToken
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
                if (requests is null)
                {
                    report(new(exited.Name, ServiceUpdateKind.Failure,
                        $"Exited with code {code}. Stopping the other services."));
                    return 1;
                }

                await StopAsync([exited], allowGracePeriod: false, report);
                RemoveStopped(exited);
                report(new(exited.Name, ServiceUpdateKind.Failure,
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
                await StopAsync(running.Values.ToArray(), cancellationToken.IsCancellationRequested, report);
            }
            finally
            {
                foreach (var service in running.Values)
                {
                    service.Process.Dispose();
                }
            }
        }

        bool Start(string name)
        {
            report(new(name, ServiceUpdateKind.Starting, "Starting..."));
            try
            {
                var child = StartService(name, services[name]!, configDirectory, watch, report);
                running.Add(name, child);
                child.Process.StandardInput.Close();
                child.Process.BeginOutputReadLine();
                child.Process.BeginErrorReadLine();
                report(new(name, ServiceUpdateKind.Started, $"Started (PID {child.Process.Id})."));
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

            await StopAsync(selected, OperatingSystem.IsWindows(), report);
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
                service.Process.Dispose();
            }
        }
    }


    private static RunningService StartService(
        string name,
        ServiceConfig service,
        string configDirectory,
        bool watch,
        Action<ServiceUpdate> report)
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

            return new RunningService(name, process, exit.Task);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private static (string Node, string NpmCli) FindWindowsNpm()
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

    private static async Task StopAsync(
        IReadOnlyList<RunningService> running,
        bool allowGracePeriod,
        Action<ServiceUpdate> report)
    {
        foreach (var service in running.Where(service => !service.Process.HasExited))
        {
            report(new(service.Name, ServiceUpdateKind.Stopping, "Stopping..."));
        }

        if (allowGracePeriod)
        {
            if (OperatingSystem.IsWindows())
            {
                foreach (var service in running.Where(service => !service.Process.HasExited))
                {
                    // A targeted break event reaches the service group, including console children.
                    GenerateConsoleCtrlEvent(CtrlBreakEvent, (uint)service.Process.Id);
                }
            }

            try
            {
                // Console-attached applications may also receive Ctrl+C.
                await Task.WhenAll(running.Select(service => service.ExitCode)).WaitAsync(ShutdownTimeout);
            }
            catch (TimeoutException)
            {
                // Force a stop for services that are still running.
            }
        }

        foreach (var service in running)
        {
            try
            {
                if (!service.Process.HasExited)
                {
                    service.Process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException) when (service.Process.HasExited)
            {
                // The process exited between the check and the kill.
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or AggregateException)
            {
                report(new(service.Name, ServiceUpdateKind.Failure,
                    $"Could not stop the process tree. {exception.Message}"));
            }
        }

        using var drainTimeout = new CancellationTokenSource(ShutdownTimeout);
        try
        {
            await Task.WhenAll(running.Select(service => service.Process.WaitForExitAsync(drainTimeout.Token)));
        }
        catch (OperationCanceledException) when (drainTimeout.IsCancellationRequested)
        {
            report(new(null, ServiceUpdateKind.Notice, "Timed out waiting for services or their output streams."));
        }

        foreach (var service in running.Where(service => service.Process.HasExited))
        {
            report(new(service.Name, ServiceUpdateKind.Stopped, "Stopped."));
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint controlEvent, uint processGroupId);

    private sealed record RunningService(string Name, Process Process, Task<int> ExitCode);
}
