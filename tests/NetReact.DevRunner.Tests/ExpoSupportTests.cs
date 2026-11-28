using NetReact.DevRunner.Cli;
using NetReact.DevRunner.Cli.Commands;

namespace NetReact.DevRunner.Tests;

public sealed class ExpoDetectionTests
{
    [Fact]
    public void DetectFolders_ExpoInDependencies_UsesNpmStart()
    {
        var result = DetectFrontend("""
            {
              "dependencies": { "expo": "~54.0.0" },
              "scripts": { "start": "expo start" }
            }
            """);

        var frontend = Assert.Single(result.Frontends);
        Assert.Equal("start", frontend.Command);
        Assert.Equal("start", frontend.Arguments);
    }

    [Fact]
    public void DetectFolders_ExpoInDevDependencies_UsesNpmStart()
    {
        var result = DetectFrontend("""
            {
              "devDependencies": { "expo": "~54.0.0" },
              "scripts": { "start": "expo start" }
            }
            """);

        var frontend = Assert.Single(result.Frontends);
        Assert.Equal("start", frontend.Command);
        Assert.Equal("start", frontend.Arguments);
    }

    [Fact]
    public void DetectFolders_ExpoWithStartAndDev_PrefersStart()
    {
        var result = DetectFrontend("""
            {
              "dependencies": { "expo": "~54.0.0" },
              "scripts": {
                "start": "expo start",
                "dev": "vite"
              }
            }
            """);

        var frontend = Assert.Single(result.Frontends);
        Assert.Equal("start", frontend.Command);
        Assert.Equal("start", frontend.Arguments);
    }

    [Fact]
    public void DetectFolders_NonExpoDevScript_RemainsSupported()
    {
        var result = DetectFrontend("""
            {
              "dependencies": { "vite": "^6.0.0" },
              "scripts": { "dev": "vite" }
            }
            """);

        var frontend = Assert.Single(result.Frontends);
        Assert.Equal("run dev", frontend.Command);
        Assert.Equal("run, dev", frontend.Arguments);
    }

    [Fact]
    public void DetectFolders_NonExpoStartOnly_DoesNotDetectFrontend()
    {
        var result = DetectFrontend("""
            {
              "dependencies": { "react": "^19.0.0" },
              "scripts": { "start": "node server.js" }
            }
            """);

        Assert.Empty(result.Frontends);
    }

    [Fact]
    public void DetectFolders_ExpoWithoutStartButWithDev_UsesDevFallback()
    {
        var result = DetectFrontend("""
            {
              "dependencies": { "expo": "~54.0.0" },
              "scripts": { "dev": "vite" }
            }
            """);

        var frontend = Assert.Single(result.Frontends);
        Assert.Equal("run dev", frontend.Command);
        Assert.Equal("run, dev", frontend.Arguments);
    }

    private static (
        List<InitCommand.BackendProject> Backends,
        List<InitCommand.Frontend> Frontends
    ) DetectFrontend(string packageJson)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "NetReact.DevRunner.Tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(
                Path.Combine(root, "package.json"),
                packageJson);

            return InitCommand.DetectFolders(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

public sealed class ExpoUrlDetectionTests
{
    [Theory]
    [InlineData("Waiting on http://localhost:8081", "http://localhost:8081")]
    [InlineData("Web is waiting on http://localhost:8081", "http://localhost:8081")]
    [InlineData("Web is waiting on http://192.168.1.10:8081/", "http://192.168.1.10:8081/")]
    [InlineData("Local: http://localhost:5173/", "http://localhost:5173/")]
    [InlineData("Now listening on: https://localhost:5001", "https://localhost:5001")]
    [InlineData("open your browser on http://localhost:19006", "http://localhost:19006")]
    public void DetectListeningUrls_RecognizesSupportedMessages(
        string message,
        string expectedUrl)
    {
        var urls = ServiceDashboard.DetectListeningUrls(message);

        Assert.Contains(expectedUrl, urls);
    }

    [Fact]
    public void DetectListeningUrls_IgnoresNativeExpoUrl()
    {
        var urls = ServiceDashboard.DetectListeningUrls(
            "Metro waiting on exp://192.168.1.10:8081");

        Assert.Empty(urls);
    }

    [Fact]
    public void DetectListeningUrls_ReturnsMultipleHttpUrls()
    {
        var urls = ServiceDashboard.DetectListeningUrls(
            "Local: http://localhost:8081 Web is waiting on https://localhost:8081");

        Assert.Equal(
            new[] { "http://localhost:8081", "https://localhost:8081" },
            urls);
    }

    [Fact]
    public void DetectListeningUrls_EmptyMessage_ReturnsNoUrls()
    {
        Assert.Empty(ServiceDashboard.DetectListeningUrls(""));
    }
}