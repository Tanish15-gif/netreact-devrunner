# Contributing to NetReact

From the repository root, use the .NET 11 SDK to build and exercise the CLI:

```sh
dotnet restore NetReact.DevRunner.slnx
dotnet build NetReact.DevRunner.slnx -c Release --no-restore
dotnet run --project src/NetReact.DevRunner.Cli -c Release --no-build -- --help
```

The Windows, Linux, and macOS CI jobs build the solution, pack and install the tool, and exercise the configuration template with a temporary application. To try the CLI on your own application, follow the [README setup guide](README.md#set-up-your-own-app).

For a bug report, include the operating system, terminal, output of `netreact --info`, a minimal configuration, and what you expected. Remove private paths and credentials from logs.

For a pull request, explain the user-visible change and how you tested it. For a bug fix, include a minimal reproduction and the command that verifies the fix. For terminal changes, give the terminal dimensions and a screenshot if it illustrates the change. Keep feedback respectful and focused on the work.

Contributions are licensed under the project's MIT license. Preserve attribution for third-party code and assets. See the [code of conduct](CODE_OF_CONDUCT.md) and [private reporting guidance](SECURITY.md).
