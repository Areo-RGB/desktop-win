# Fluent Desktop — WinUI 3

Rewrite of [Areo-RGB/flutter-desktop](https://github.com/Areo-RGB/flutter-desktop) to **WinUI 3** (Windows App SDK) with a **C# backend**.
Layout and controls follow [microsoft/WinUI-Gallery](https://github.com/microsoft/WinUI-Gallery):
`TitleBar` + `NavigationView` (left pane, settings item, footer), Mica backdrop,
`SelectorBar` sub-tabs, `SettingsCard`/`SettingsExpander` (Community Toolkit),
`InfoBar` notifications, `ContentDialog` confirmations, `WebView2` for the MCPHub dashboard.

```
  FluentDesktop.Core/        net8.0 backend — runs the CLI tools (no UI references)
    Infrastructure/          ProcessRunner (run / stream / timeout / cmd.exe wrapping), PATH lookup, JSON store
    Dashboard/               dashboard_commands.json + streaming command sessions
    DesktopCommander/        npx @wonderwhy-er/desktop-commander remote + agent-browser verification
    Hub/                     scripts\hubctl.ps1, netstat/taskkill port tools, SSH banner test
    GitHub/                  gh repo list/create/edit/archive/delete, git init/remote/push
    DeviceMirror/            adb devices/props/screencap/tcpip/reboot, scrcpy launch
    YtDlp/                   yt-dlp metadata + downloads, local library, Cloudflare R2 (SigV4)
    SpielerPlus/             bundled Python pipeline (SpielerPlus/python, embedded, extracted to %LOCALAPPDATA%)
    Settings/                theme / backdrop preferences, folders (paths.json)
    Mcp/                     MCP servers synced to Claude/Codex/Gemini/Grok/OpenCode/Hermes/Pi/MiniMax (mcp_servers.json)
    Projects/                ~/Projects discovery (flutter/web/android/extension) and start commands
    Skills/                  Git-backed skills store (~/Projects/skills) linked into Pi/Claude/Codex/OpenCode
    Video/                   ffprobe/ffmpeg chapter metadata + HEVC re-encode, Training Hub R2 (r2.properties, rclone)
    Tools/                   host info and CLI tool versions
  FluentDesktop.Core.Tests/  xUnit tests for the backend (run on any OS)
  FluentDesktop/             WinUI 3 app (unpackaged, self-contained)
  ~/Projects/mcp-hub/mcphub_fork/  MCPHub fork — hub backend, API, dashboard, and reference implementation
  ~/Projects/mcp-hub/ssh-mcp/  SSH MCP stdio server exposed through MCPHub
scripts/hubctl.ps1           Hub control script (uses the shared mcp-hub project)
```

Settings live in `%APPDATA%\fluent_desktop\` with the same file names and JSON shape as the
Flutter app (`dashboard_commands.json`, `github_repos.json`, `desktop_commander.json`,
`yt_dlp_history.json`, `spielerplus_config.json`), so existing data carries over. The Projects,
MCP, Skills, Video and Tools pages port DesktopCC (Rust/GPUI) and share its files
(`paths.json`, `mcp_servers.json`, `projects_hidden.json`, `r2.properties`).

## Build & run (Windows 10 1809+ / 11)

Requirements: .NET 8 SDK. Visual Studio 2022 with the "WinUI application development"
workload is optional (for the XAML designer / debugging).

```powershell
dotnet test  FluentDesktop.Core.Tests
dotnet build FluentDesktop\FluentDesktop.csproj -c Release -p:Platform=x64 -r win-x64
.\FluentDesktop\bin\x64\Release\net8.0-windows10.0.22621.0\win-x64\FluentDesktop.exe
```

Or open `FluentDesktop.sln` in Visual Studio, select `x64`, and press F5.
CI (`.github/workflows/build.yml`) tests the backend, publishes a standalone single-file
`FluentDesktop.exe`, smoke-tests it (launch + visit every page) and uploads it as an artifact.
Push a `v*` tag (`git tag v1.0.0 && git push origin v1.0.0`) and `release.yml` attaches the exe
to a GitHub Release.

Standalone exe locally:

```powershell
dotnet publish FluentDesktop\FluentDesktop.csproj -c Release -r win-x64 -p:Platform=x64 `
  -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o publish
```

## MCPHub

The Hub page uses the local fork in `~\Projects\mcp-hub\mcphub_fork`, copied from the Flutter
project with its local changes. The Flutter copy remains available. See
[mcp-hub README](../mcp-hub/README.md) for provenance and development guidance.

Build the hub with Node.js and pnpm before starting it from the Hub page:

```powershell
pnpm --dir ~\Projects\mcp-hub\mcphub_fork install --frozen-lockfile
pnpm --dir ~\Projects\mcp-hub\mcphub_fork build
npm --prefix ~\Projects\mcp-hub\ssh-mcp ci
```

Local hub settings, credentials, and data are retained locally and excluded from
Git. The hub is a separate Node.js service; publishing the WinUI executable does
not bundle it. Use `FLUENT_DESKTOP_PROJECT_ROOT` for an installed app to locate
this checkout.

The **Hub → MCP Endpoints → SSH MCP Connection** fields configure SSH host,
port, username, private key, and password. Save and restart MCPHub to apply.
The initial settings use the Flutter project's previous local SSH connection.
See [SSH MCP setup and verification](../mcp-hub/README.md#ssh-mcp) for details.

## Environment overrides

| Variable | Purpose |
| --- | --- |
| `FLUENT_DESKTOP_PROJECT_ROOT` | Checkout containing `scripts\hubctl.ps1` (Hub scripts; default: this checkout, then `~\Projects\winui`, `~\Projects\desktop-win`, or `~\Projects\flutter-desktop`) |
| `MCPHUB_DIR` | Alternate MCPHub fork directory (default: `~\Projects\mcp-hub\mcphub_fork`; `MCP_HUB_ROOT` moves the whole hub project) |
| `FLUENT_DESKTOP_CONFIG_DIR` | Alternate config folder |
| `YT_DLP_LIBRARY_ROOT` | Local video library root (default `D:\video_manager`, else Downloads) |
| `YT_DLP_ENV_FILE` | `.env` with `R2_*` credentials (default `%USERPROFILE%\Desktop\auth\.env`) |
| `ADB`, `SCRCPY` | Explicit tool paths |
| `SPIELERPLUS_IMPORTER_DIR` | Alternate Python runtime folder |
