$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$version = '1.13.9'
$output = Join-Path $repo 'artifacts\release'
$package = Join-Path $output ('CodexQuotaWidget-' + $version + '-win-x64')
if (Test-Path -LiteralPath $package) { throw 'Package directory already exists. Use a fresh source checkout to avoid mixing outputs.' }
$dotnet = (Get-Command dotnet.exe -ErrorAction Stop).Source
New-Item -ItemType Directory -Path $package -Force | Out-Null
$projects = @(
    @{ Source='src\CodexQuotaWidget\CodexQuotaWidget.csproj'; Target=$package },
    @{ Source='src\CodexQuotaWidget.Executor\CodexQuotaWidget.Executor.csproj'; Target=(Join-Path $package 'executor') },
    @{ Source='src\CodexQuotaWidget.StartupWatcher\CodexQuotaWidget.StartupWatcher.csproj'; Target=(Join-Path $package 'startup-watcher') }
)
foreach ($project in $projects) {
    & $dotnet publish (Join-Path $repo $project.Source) -c Release -r win-x64 --self-contained true -o $project.Target --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release publish failed.' }
}
foreach ($name in @('README.md','CHANGELOG.md','LICENSE','THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $package
}
$zip = $package + '.zip'
Compress-Archive -LiteralPath $package -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $([IO.Path]::GetFileName($zip))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Built $zip"
Write-Output "SHA256 $hash"
