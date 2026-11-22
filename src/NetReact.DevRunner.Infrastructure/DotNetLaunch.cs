using NetReact.DevRunner.Core;

namespace NetReact.DevRunner.Infrastructure;

public static class DotNetLaunch
{
    public static bool IsWatching(ServiceConfig service, bool watchRequested)
    {
        return watchRequested && IsDotNet(service)
            && service.Args?.FirstOrDefault() is null or "run" or "watch";
    }

    public static IEnumerable<string?> GetArguments(ServiceConfig service, bool watchRequested)
    {
        var arguments = service.Args ?? [];
        if (!IsDotNet(service))
        {
            return arguments;
        }

        if (arguments.FirstOrDefault() == "watch")
        {
            if (watchRequested)
            {
                return arguments.TakeWhile(argument => argument != "--").Contains("--non-interactive")
                    ? arguments
                    : ["watch", "--non-interactive", .. arguments.Skip(1)];
            }

            return UnwrapWatchRun(arguments);
        }

        if (arguments.Count == 0)
        {
            arguments = ["run"];
        }

        return watchRequested && arguments[0] == "run"
            ? ["watch", "--non-interactive", .. arguments]
            : arguments;
    }

    private static IEnumerable<string?> UnwrapWatchRun(List<string?> arguments)
    {
        var runIndex = 1;
        while (runIndex < arguments.Count && arguments[runIndex] == "--non-interactive")
        {
            runIndex++;
        }

        if (runIndex == arguments.Count || arguments[runIndex] == "--")
        {
            return ["run", .. arguments.Skip(runIndex)];
        }

        if (arguments[runIndex] == "run")
        {
            return arguments.Skip(runIndex);
        }

        throw new ArgumentException(
            "Custom dotnet watch arguments require netreact start --watch. To launch without watch, configure dotnet run.");
    }

    private static bool IsDotNet(ServiceConfig service)
    {
        var executable = Path.GetFileNameWithoutExtension(service.Command?.Replace('\\', '/'));
        return string.Equals(executable, "dotnet", StringComparison.OrdinalIgnoreCase);
    }
}
