<h1 align="center">
  <img src="docs/netreact-devrunner.png" alt="NetReact DevRunner" width="800">
</h1>

[![.NET 10 and 11](https://img.shields.io/badge/.NET-10%20%7C%2011-512BD4?style=flat-square)](https://dotnet.microsoft.com/download)
[![React](https://img.shields.io/badge/frontend-React-087EA4?style=flat-square)](https://react.dev/)
[![Angular](https://img.shields.io/badge/frontend-Angular-DD0031?style=flat-square)](https://angular.dev/)
[![NuGet 0.7.0](https://img.shields.io/badge/NuGet-0.7.0-004880?style=flat-square)](https://www.nuget.org/packages/NetReact.DevRunner/0.7.0)
[![License: MIT](https://img.shields.io/badge/license-MIT-237A57?style=flat-square)](LICENSE)

Run an ASP.NET Core API and a React, Angular, or Expo frontend from one terminal. Switch between their logs with the arrow keys, press **Ctrl+U** to see both URLs, or restart either service independently.

**New in 0.7.0.** Build an individual service or every service with a build command from the NetReact tab. NetReact asks before stopping a running service for a build. You can also scroll service logs with the mouse wheel and use the slimmer command prompt.

[Install](#install-netreact) · [Set up your app](#set-up-your-own-app) · [Angular](#angular-projects) · [YAML reference](#configuration-reference) · [Interactive CLI](#interactive-cli) · [Troubleshooting](#troubleshooting)

## Install NetReact

Install the **.NET 10 or 11 SDK** to run the tool. Building both targets from source requires the .NET 11 SDK. Install npm and the Node.js version required by your frontend. Your backend also needs its own compatible SDK and runtime.

```sh
dotnet --list-sdks
node --version
npm --version
```

Install [NetReact DevRunner 0.7.0](https://www.nuget.org/packages/NetReact.DevRunner/0.7.0) from any terminal.

```sh
dotnet tool install --global NetReact.DevRunner --version 0.7.0
netreact --version
```

If you already have NetReact installed, close active sessions and run:

```sh
dotnet tool update --global NetReact.DevRunner --version 0.7.0
```

The global tool exposes `netreact` across your projects. To build from the source checkout, see [Contributing](CONTRIBUTING.md).

## Set up your own app

### 1. Open your application root

Open the folder containing your backend and frontend in VS Code, then open its terminal. For example, the following app has an API in `Backend` and an npm project in `frontend`:

```text
Portfolio/
  Backend/
    Portfolio.Api.csproj
  frontend/
    package.json
```

### 2. Generate and check the configuration

```sh
netreact init
```

NetReact looks up to three folder levels deep for an ASP.NET Core web project and npm frontend projects. It detects a `dev` script, Angular workspaces with `angular.json` and a `start` script, and Expo projects with the `expo` dependency and a `start` script. If several frontends match, run `init` in an interactive terminal and pick one. If it prints **check this path**, set that service's `workingDirectory` yourself. If a configuration already exists, open it directly.

For this layout, the backend runs in `./Backend` and the frontend runs in `./frontend`. See the annotated [configuration reference](#configuration-reference) or copy the [YAML template](devrunner.example.yaml) into your application as `devrunner.yaml`.

### 3. Install frontend dependencies and check both services

From your app root, use the frontend path you chose in step 2:

```sh
npm install --prefix ./frontend
netreact doctor
```

Look for `Service checks passed.` The doctor checks the configuration, service directories, executable availability, .NET SDK selection, and configured npm script.

### 4. Start your session

```sh
netreact start
```

This runs `dotnet run` for the sample backend and the configured npm script for its frontend, such as `npm run dev` or `npm start`. To use .NET Hot Reload, start with `netreact start --watch`. To work on the backend alone, use `netreact start backend --watch`. A supported C# edit applies when you save.

### Angular projects

Run `netreact init` from your application root. NetReact detects Angular when a workspace contains `angular.json` and its `package.json` defines a `start` script. For a standard Angular CLI workspace where that script runs `ng serve`, the frontend entry under `services` looks like this:

```yaml
frontend:
  workingDirectory: ./frontend
  command: npm
  args: [start]
```

If your Angular workspace is in another folder, replace `./frontend` in both `workingDirectory` and the install command. Then install dependencies and start both services:

```sh
npm install --prefix ./frontend
netreact doctor
netreact start
```

Open the **Angular** tab for frontend logs. The **Services** box shows the detected URL, which is usually `http://localhost:4200/`. Use `args: [start, --, --port, "4300"]` to pass a custom port to Angular. Type `restart frontend` in the **NetReact** tab to restart Angular while the API keeps running.

### Expo projects

Run `netreact init` from your application root. NetReact detects Expo when a project's `package.json` includes the `expo` dependency and defines a `start` script. For a standard Expo project, the frontend entry under `services` looks like this:

```yaml
frontend:
  workingDirectory: ./frontend
  command: npm
  args: [start]
```

If your Expo project is in another folder, replace `./frontend` in both `workingDirectory` and the install command. Then install dependencies and start both services:

```sh
npm install --prefix ./frontend
netreact doctor
netreact start
```

Open the frontend service tab for Metro bundler logs, and check the **Services** box for the detected URL. Use `args: [start, --, --port, "8082"]` to pass a custom port to Expo, or type `restart frontend` in the **NetReact** tab to restart Expo while the API keeps running.

## Configuration reference

Each key under `services` is a name you choose. For example, `backend` is the name used in `netreact start backend --watch` and in the interactive command `restart backend`.

```yaml
# Place this file beside your backend and frontend folders as devrunner.yaml.
version: 1
services:
  backend:
    workingDirectory: ./Backend       # Folder containing the web .csproj
    command: dotnet
    args: [run]
    # If your API serves this endpoint, uncomment it to enable a readiness check.
    # readyUrl: http://localhost:5083/health

  frontend:
    workingDirectory: ./frontend      # Folder containing package.json
    command: npm
    args: [run, dev]
```

| Setting | Meaning |
| --- | --- |
| `version` | Required. Use `1` for the configuration schema. |
| `workingDirectory` | Required for each service. The CLI launches the executable in this folder. Relative paths start at the directory containing the YAML file. |
| `command` | Required. The executable to launch, such as `dotnet` or `npm`. |
| `args` | Optional list of arguments in order. For a specific project, use `[run, --project, "Store API.csproj"]`. The file name is one argument. |
| `readyUrl` | Optional HTTP or HTTPS endpoint. The first 2xx response changes the service status to **Ready**. |

**Choosing a working directory**

| Where the project lives | Value in YAML |
| --- | --- |
| The web `.csproj` is next to `devrunner.yaml` | `workingDirectory: .` |
| The web project is in `src/Store.Api/` | `workingDirectory: ./src/Store.Api` |
| The frontend manifest is in `Client App/package.json` | `workingDirectory: "./Client App"` |
| The config is in `config/dev.yaml` and the frontend is its sibling directory `frontend/` at the app root | `workingDirectory: ../frontend` |

Match the capitalization on disk so the same file works across platforms. When using `netreact start --config ./config/dev.yaml`, the CLI still resolves service paths from `config/`. Review configurations before running them because each command runs with your account's permissions.

## Interactive CLI

### .NET tab — read the backend log

Use **Left/Right** to select **.NET**. Look for `Now listening on` to find the API address. In this Portfolio session it is `http://localhost:5083`. Your app's launch settings determine its address.

[![.NET tab showing ASP.NET Core startup and a listening address on localhost port 5083](docs/screenshots/dotnet-tab.png)](https://github.com/user-attachments/assets/65d2fd9d-40b3-4dff-8ef7-83b8583e9697)

### React tab — open the frontend

Switch to **React** and open Vite's **Local** URL in your browser. This session uses `http://localhost:5173/`. The tab also shows frontend build errors and reload messages while you work.

[![React tab showing the npm dev script and Vite's local URL on port 5173](docs/screenshots/react-tab.png)](https://github.com/user-attachments/assets/86627c95-4111-417b-9a56-15b31b3944f6)

### NetReact tab — manage the session

The **Services** box sits below **Session activity** and shows live service states and listening URLs. Press **Ctrl+U** from a service tab to return to **NetReact**. A service shows **Fetching URL…** for the first eight seconds of a launch while NetReact looks for a listening address. URLs that appear later show up automatically.

Type `clear` or `cls` to clear the session activity while services keep running. Use `restart backend` to restart one service, `restart all` to restart every service, or `help` for all session commands. On a service tab, the footer shows **Ctrl+D** to stop or start that service.

Use `build backend`, `build frontend`, or `build all` in the **NetReact** tab. **Ctrl+B** builds the selected service, or all services from the NetReact tab. NetReact uses `dotnet build` for .NET and the `npm run build` script for React and Angular. If a build target is running, NetReact asks before stopping it. Build output appears in **Session activity**, and the stopped services can be started again with **Ctrl+D** on their tabs.

```text
Services
.NET              Running    http://localhost:5083
React             Running    http://localhost:5173/
```

### Shortcuts — navigate, search, and stop

Press **?** to open the shortcuts for the current tab. Use an empty prompt on the NetReact tab. Press **Esc** to close the panel.

[![Shortcuts panel showing tab navigation, log search, scrolling, and service controls](docs/screenshots/shortcuts.png)](https://github.com/user-attachments/assets/01bb691f-778b-4db5-9fa8-c943e51084ad)

| Key | Action |
| --- | --- |
| Left / Right | Switch tabs. |
| Mouse wheel / trackpad | Scroll the active tab in terminals with mouse reporting. Scroll back to the bottom or press End to resume live output. |
| Alt+drag | Select text directly in the VS Code terminal on Windows and Linux. |
| `select` | Pause the display and enable ordinary drag selection. Run it in the NetReact tab and press Esc to resume. |
| Up / Down or PgUp / PgDn | Scroll the current log by line or page. |
| Home / End | Jump to the oldest retained log or resume live output. |
| `/` then text | Filter the current tab's logs. Esc clears the search. |
| Ctrl+D | Stop the selected service. Press again to start it. |
| Ctrl+B | Build the selected service, or all buildable services from the NetReact tab. Use `build` in the NetReact tab if the terminal intercepts the shortcut. |
| Ctrl+R | On a .NET Watch tab, restart that watcher. On other tabs, restart all services in the session. |
| Ctrl+U | Return to NetReact to see service states and URLs. |
| Ctrl+C | Stop all services and exit. |

## Other ways to run

| Command or setting | When to use it |
| --- | --- |
| `netreact validate` | Check YAML and service folders before launching processes. |
| `netreact start --config ./configs/demo.yaml` | Use an alternate config. Service paths are relative to that file. `doctor` and `validate` also accept `--config`. |
| `netreact start --plain` | Keep labeled stdout and stderr in terminal scrollback. Redirecting input or output also selects this mode. |
| `netreact start --help` | See launch options and keyboard controls from the shell. |
| `netreact --info` | Show the tool version, runtime, OS, and default config path. |

**Exit behavior.** In the interactive CLI you can restart a service after it exits. In plain mode the first observed service exit stops the remaining services and supplies the session exit code. Ctrl+C requests shutdown and returns 130.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| `devrunner.yaml` cannot be found | Run `netreact --info` to see the expected path. Open the app root or pass `--config` to `start`. |
| npm reports a missing `package.json` or npm script | Check the frontend's `workingDirectory` and the `dev` or `start` script in its manifest. Run `netreact doctor` after editing. |
| A service reports a port in use | Check its log for the requested port. Close the process using it or pick a different port in the service's launch arguments. |
| PowerShell blocks `npm.ps1` | Use `npm.cmd` in the terminal, for example `npm.cmd install --prefix ./frontend`. Keep `command: npm` in YAML for the standard Windows Node installation. |
| Services shows No URL detected | Read the service tab for startup errors or a custom address message. NetReact recognizes the usual ASP.NET Core, Angular, and Vite listening messages. |
| Windows denies access during a tool update | Exit running NetReact sessions with Ctrl+C, wait for the shell prompt, then retry the update. |

## Contributing and license

For a bug report, include your OS, terminal, `netreact --info` output, and a minimal configuration. For build and verification instructions, see [Contributing](CONTRIBUTING.md). Project discussions follow the [code of conduct](CODE_OF_CONDUCT.md). Report security concerns through the [private reporting guide](SECURITY.md).

NetReact DevRunner uses the [MIT license](LICENSE). The package includes [notices for its bundled libraries](THIRD-PARTY-NOTICES.md).

## Supported Technologies

[![.NET documentation](https://img.shields.io/badge/.NET-512BD4?style=for-the-badge&logo=dotnet&logoColor=white)](https://learn.microsoft.com/en-us/dotnet/) [![React documentation](https://img.shields.io/badge/React-20232A?style=for-the-badge&logo=react&logoColor=61DAFB)](https://react.dev/learn) [![Angular documentation](https://img.shields.io/badge/Angular-DD0031?style=for-the-badge&logo=angular&logoColor=white)](https://angular.dev/overview)
