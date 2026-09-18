# CodexQuotaWidget

CodexQuotaWidget is a Windows tray widget for viewing the quota information exposed by the official local Codex App Server. It keeps its authentication and local data separate from Codex Desktop.

> **Development Status:** This project is currently under active development. The next release is planned to add a quota reset toggle, with multi-language support planned for a future update.

## Implemented features

- Displays remaining 5-hour and weekly Codex quota in a tray widget.
- Shows reset countdowns and local reset timestamps.
- Shows additional server-provided quota windows, including GPT reserve when available.
- Displays available reset credits and their expiry when the server provides them. This version never consumes reset credits.
- Native Windows tray icon with show/hide, manual refresh, diagnostics, and exit commands.
- Automatic refresh every 60, 120, or 300 seconds, with single-instance protection.
- Persists window position, basic theme, accent color, a small quota cache, and settings using atomic writes.
- Supports dark, light, and system themes. It also detects when Codex Desktop is unavailable and retains the latest data as stale.
- Can use the official browser login flow to create a widget-specific Codex authentication profile.

## Requirements

- Windows 11 x64.
- .NET 10 SDK `10.0.401` to build from source, or the self-contained Windows release to run it.
- Official Codex Desktop / Codex CLI installed locally. The widget only accepts an Authenticode-signed `codex.exe` published by OpenAI.
- A ChatGPT account authorized through the official browser flow on first use.

## Build

```powershell
dotnet restore .\src\CodexQuotaWidget\CodexQuotaWidget.csproj --locked-mode
dotnet build .\src\CodexQuotaWidget\CodexQuotaWidget.csproj -c Release --no-restore
dotnet run --project .\tests\CodexQuotaWidget.Checks\CodexQuotaWidget.Checks.csproj -c Release --no-restore
dotnet publish .\src\CodexQuotaWidget\CodexQuotaWidget.csproj -c Release -r win-x64 --self-contained true -o .\artifacts\CodexQuotaWidget-win-x64
```

## Use

Run `CodexQuotaWidget.exe`. The tray icon remains available after the panel is hidden.

1. Open the panel from the tray icon and expand its settings.
2. Select **Login / Rebind independent account** and complete the official browser flow yourself when first prompted.
3. Use **Locate official codex.exe** only if automatic discovery cannot find the locally installed official CLI.

The executable also supports read-only diagnostics:

```powershell
.\CodexQuotaWidget.exe --check
.\CodexQuotaWidget.exe --once
.\CodexQuotaWidget.exe --once --codex-path "C:\path\to\official\codex.exe"
```

`--check` and `--once` use the same read-only account and rate-limit requests as the widget. They do not spend reset credits or send model requests.

## Local data and privacy

By default, the widget keeps its data under `%LOCALAPPDATA%\CodexQuotaWidget`, with its independent Codex home at `%LOCALAPPDATA%\CodexQuotaWidget\codex-home`. It does not read, copy, or modify Codex Desktop's own authentication directory.

For a portable or managed deployment, set `CODEX_QUOTA_WIDGET_DATA` to choose a different data directory. Set `CODEX_QUOTA_WIDGET_CLI` to give a previously verified official CLI path. Neither value needs to be stored in source control.

The app deliberately accepts only four App Server methods: `initialize`, `account/read`, `account/rateLimits/read`, and `account/login/start`. It rejects reset, model, and other write-capable methods. Logs contain only fixed event identifiers and integer error codes; they do not record tokens, cookies, email addresses, login URLs, or raw App Server responses. Logs are capped at seven days and approximately 2 MB.

## Current status and roadmap

Version `1.0.0` is the first Windows V1 release. It focuses on live quota visibility, tray operation, safe local persistence, and independent authentication. Historical charts, token activity, CSV export, advanced notifications, deep visual customization, MSI packaging, a reset toggle, and multi-language UI are not part of this release.

## Compatibility note

Codex App Server is an evolving local interface. The widget validates required response fields, retains unknown quota windows when possible, and reports protocol errors instead of guessing. It is not an official OpenAI product and is not affiliated with or endorsed by OpenAI.
