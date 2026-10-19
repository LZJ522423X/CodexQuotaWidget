$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repo 'artifacts\siwc-phase2-20261016\catalog-diagnostic-build\bin\CodexQuotaWidget\release_win-x64\CodexQuotaWidget.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Build the independent phase-two experiment first. No login or request was started.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo.ProductVersion
if ($version -notlike '1.13.2-experimental*') { throw 'Only the separately built phase-two experimental executable may be launched.' }
$runtime = Join-Path $repo 'artifacts\siwc-phase2-20261016\runtime'
New-Item -ItemType Directory -Path (Join-Path $runtime 'temp'),(Join-Path $runtime 'bundle') -Force | Out-Null
$info = [Diagnostics.ProcessStartInfo]::new($exe)
$info.UseShellExecute = $false
$info.WorkingDirectory = $repo
$info.ArgumentList.Add('--siwc-acceptance')
$info.Environment['DOTNET_ROOT'] = 'D:\codex\Tools\dotnet-10.0.401'
$info.Environment['DOTNET_CLI_HOME'] = 'D:\codex\Tools\dotnet-home'
$info.Environment['TEMP'] = Join-Path $runtime 'temp'
$info.Environment['TMP'] = Join-Path $runtime 'temp'
$info.Environment['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $runtime 'bundle'
$info.Environment['CODEX_QUOTA_WIDGET_DATA'] = 'D:\CodexQuotaWidgetData\siwc-acceptance\phase2-20261016'
$process = [Diagnostics.Process]::Start($info)
Write-Output "SIWC independent experiment window PID=$($process.Id). Browser/login/send require the user's clicks."
