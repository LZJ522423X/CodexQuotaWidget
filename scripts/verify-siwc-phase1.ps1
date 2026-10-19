param([string]$EvidenceRoot = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $projectRoot.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Verification requires the D: project checkout.' }
if (-not $EvidenceRoot) { $EvidenceRoot = Join-Path $projectRoot ('artifacts\siwc-phase1-verify-' + (Get-Date -Format 'yyyyMMdd-HHmmss')) }
$EvidenceRoot = [IO.Path]::GetFullPath($EvidenceRoot)
if (-not $EvidenceRoot.StartsWith((Join-Path $projectRoot 'artifacts\siwc-phase1-'), [StringComparison]::OrdinalIgnoreCase)) { throw 'Only isolated SIWC artifact directories are allowed.' }
$sdk = Split-Path -Parent (Get-Command dotnet.exe -ErrorAction Stop).Source
if (-not (Test-Path -LiteralPath (Join-Path $sdk 'dotnet.exe'))) { throw 'The configured .NET SDK was not found. No SDK is installed automatically.' }
New-Item -ItemType Directory -Path $EvidenceRoot,(Join-Path $EvidenceRoot 'temp'),(Join-Path $EvidenceRoot 'empty-feed') -Force | Out-Null
$envNames = @('DOTNET_ROOT','DOTNET_CLI_HOME','NUGET_PACKAGES','TEMP','TMP','DOTNET_BUNDLE_EXTRACT_BASE_DIR','DOTNET_GENERATE_ASPNET_CERTIFICATE')
$saved = @{}; foreach ($name in $envNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
$results = [Collections.Generic.List[object]]::new()
$coreLock = Join-Path $projectRoot 'src\CodexQuotaWidget.Core\packages.lock.json'
$coreLockBefore = [IO.File]::ReadAllBytes($coreLock)
[IO.File]::WriteAllBytes((Join-Path $EvidenceRoot 'core-lock-before.json'), $coreLockBefore)
try {
    $env:DOTNET_ROOT = $sdk; $env:DOTNET_CLI_HOME = (Join-Path  'sdk-home'); $env:NUGET_PACKAGES = (Join-Path  'nuget-packages')
    $env:TEMP = Join-Path $EvidenceRoot 'temp'; $env:TMP = $env:TEMP; $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $EvidenceRoot 'runtime-cache'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $dotnet = Join-Path $sdk 'dotnet.exe'; $build = Join-Path $EvidenceRoot 'build'
    foreach ($project in @('tests\CodexQuotaWidget.Checks\CodexQuotaWidget.Checks.csproj','src\CodexQuotaWidget\CodexQuotaWidget.csproj',
        'src\CodexQuotaWidget.Executor\CodexQuotaWidget.Executor.csproj','src\CodexQuotaWidget.StartupWatcher\CodexQuotaWidget.StartupWatcher.csproj')) {
        $log = Join-Path $EvidenceRoot ('build-' + [IO.Path]::GetFileNameWithoutExtension($project) + '.log')
        & $dotnet build (Join-Path $projectRoot $project) -c Release --artifacts-path $build '-p:Version=1.13.0-experimental' ('-p:RestoreSources=' + (Join-Path $EvidenceRoot 'empty-feed')) 2>&1 | Out-File -LiteralPath $log -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw "Release build failed: $log" }
        $results.Add([pscustomobject]@{ Test=$project; Kind='ReleaseBuild'; ExitCode=0; Log=$log })
    }
    $assembly = Join-Path $build 'bin\CodexQuotaWidget.Checks\release\CodexQuotaWidget.Checks.dll'
    foreach ($flag in @('--siwc-phase1-check','--workflow-loop-check','--workflow-checkpoint-check','--workflow-persistence-check','--workflow-stability-check',
        '--workflow-gpt-connection-check','--workflow-gpt-session-binding-check','--workflow-codex-binding-check','--workflow-project-files-check',
        '--workflow-project-validator-check','--capsule-placement-check','--follow-codex-close-check','--startup-watch-loop-check')) {
        $log = Join-Path $EvidenceRoot ($flag.TrimStart('-') + '.log')
        & $dotnet $assembly $flag 2>&1 | Out-File -LiteralPath $log -Encoding utf8
        if ($LASTEXITCODE -ne 0) { throw "Offline test failed: $log" }
        $results.Add([pscustomobject]@{ Test=$flag; Kind='OfflineFixture'; ExitCode=0; Log=$log })
    }
    $widget = Join-Path $build 'bin\CodexQuotaWidget\release_win-x64\CodexQuotaWidget.exe'
    foreach ($flag in @('--workflow-check','--shell-layout-check')) {
        $log = Join-Path $EvidenceRoot ($flag.TrimStart('-') + '.log'); $err = $log + '.err'
        $process = Start-Process -FilePath $widget -ArgumentList $flag -WorkingDirectory $projectRoot -WindowStyle Hidden -Wait -PassThru -RedirectStandardOutput $log -RedirectStandardError $err
        if ($process.ExitCode -ne 0) { throw "WPF fixture failed: $log" }
        $results.Add([pscustomobject]@{ Test=$flag; Kind='RealWpfWithOfflineProviders'; ExitCode=0; Log=$log })
    }
    [pscustomobject]@{ Passed=$true; AccountAuthorizations=0; RealModelCalls=0; DeploymentChanged=$false; Results=$results } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $EvidenceRoot 'verification.json') -Encoding utf8
    Write-Output "Offline verification passed. Evidence: $EvidenceRoot"
}
finally {
    # The SDK toggles empty RID entries when this dependency-free Core is built for console and WPF.
    # Restore only this known generated change; preserve any concurrent substantive dependency edit.
    if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($coreLock)) -ne [Convert]::ToBase64String($coreLockBefore)) {
        $beforeLock = [Text.Encoding]::UTF8.GetString($coreLockBefore) | ConvertFrom-Json
        $afterLock = Get-Content -LiteralPath $coreLock -Raw | ConvertFrom-Json
        $beforeNonempty = @($beforeLock.dependencies.PSObject.Properties | Where-Object { @($_.Value.PSObject.Properties).Count -gt 0 })
        $afterNonempty = @($afterLock.dependencies.PSObject.Properties | Where-Object { @($_.Value.PSObject.Properties).Count -gt 0 })
        if ($beforeLock.version -eq $afterLock.version -and $beforeNonempty.Count -eq 0 -and $afterNonempty.Count -eq 0) { [IO.File]::WriteAllBytes($coreLock, $coreLockBefore) }
        else { Write-Warning 'Core lock contains a substantive change; it was preserved for inspection.' }
    }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name, $saved[$name], 'Process') }
}
