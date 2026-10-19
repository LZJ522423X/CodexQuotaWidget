# Native Codex skill-loader validation. Runtime data stays in the updater's D: temp tree.
# This module deliberately never calls thread/turn, config-write, install, or login APIs.
function Get-CodexSkillCatalog {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)] [string] $CodexExe,
        [Parameter(Mandatory)] [string] $Workspace,
        [Parameter(Mandatory)] [string[]] $ExtraRoots,
        [Parameter(Mandatory)] [string] $SandboxRoot
    )

    $skills = [System.Collections.Generic.List[object]]::new()
    $errors = [System.Collections.Generic.List[object]]::new()
    $process = $null
    $stderrTask = $null
    $started = $false
    try {
        $tempBase = [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'temp')).TrimEnd('\', '/')
        $sandbox = [System.IO.Path]::GetFullPath($SandboxRoot).TrimEnd('\', '/')
        if (-not $sandbox.StartsWith($tempBase + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            throw '原生加载器临时目录必须位于 D:\Codex\SkillUpdater\temp 内。'
        }
        if (-not [System.IO.Path]::IsPathFullyQualified($SandboxRoot)) {
            throw '原生加载器临时目录必须为绝对路径。'
        }
        # Check all existing ancestors before creating anything; a reparse point can escape D:.
        $ancestor = $sandbox
        while ($ancestor) {
            if (Test-Path -LiteralPath $ancestor) {
                $item = Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop
                if (-not $item.PSIsContainer -or (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw '原生加载器临时路径包含非目录或链接，已拒绝使用。'
                }
            }
            $parent = [System.IO.Path]::GetDirectoryName($ancestor)
            if ($parent -eq $ancestor) { break }
            $ancestor = $parent
        }
        $exe = [System.IO.Path]::GetFullPath($CodexExe)
        if (-not [System.IO.Path]::IsPathFullyQualified($CodexExe) -or
            -not (Test-Path -LiteralPath $exe -PathType Leaf) -or
            [System.IO.Path]::GetExtension($exe) -ne '.exe') {
            throw '未找到有效的 Codex 原生可执行文件。'
        }
        $cwd = [System.IO.Path]::GetFullPath($Workspace)
        if (-not [System.IO.Path]::IsPathFullyQualified($Workspace) -or
            -not (Test-Path -LiteralPath $cwd -PathType Container)) {
            throw 'Skill 验证工作目录不存在或不是绝对路径。'
        }
        $roots = @($ExtraRoots | ForEach-Object {
            if (-not [System.IO.Path]::IsPathFullyQualified($_)) {
                throw 'Skill 额外发现路径必须是绝对路径。'
            }
            $root = [System.IO.Path]::GetFullPath($_)
            if (-not (Test-Path -LiteralPath $root -PathType Container)) {
                throw 'Skill 额外发现路径不存在。'
            }
            $root
        } | Select-Object -Unique)
        if ($roots.Count -eq 0) { throw '未提供 Skill 额外发现路径。' }

        [void][System.IO.Directory]::CreateDirectory($sandbox)
        $cliHome = Join-Path $sandbox 'cli-home'
        $cliTemp = Join-Path $sandbox 'cli-temp'
        foreach ($directory in @($cliHome, $cliTemp)) {
            if (Test-Path -LiteralPath $directory) {
                $directoryItem = Get-Item -LiteralPath $directory -Force -ErrorAction Stop
                if (-not $directoryItem.PSIsContainer -or
                    (($directoryItem.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0)) {
                    throw '原生加载器隔离目录包含链接或非目录。'
                }
            }
            [void][System.IO.Directory]::CreateDirectory($directory)
        }
        # Refuse a reused CLI home that might contain auth/config. No secrets are copied.
        if ((Test-Path -LiteralPath (Join-Path $cliHome 'auth.json')) -or
            (Test-Path -LiteralPath (Join-Path $cliHome 'config.toml'))) {
            throw '原生加载器隔离目录含认证或配置文件，已拒绝使用。'
        }

        $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $exe
        [void]$startInfo.ArgumentList.Add('app-server')
        [void]$startInfo.ArgumentList.Add('--listen')
        [void]$startInfo.ArgumentList.Add('stdio://')
        $startInfo.WorkingDirectory = $sandbox
        $startInfo.UseShellExecute = $false
        $startInfo.CreateNoWindow = $true
        $startInfo.RedirectStandardInput = $true
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $utf8 = [System.Text.UTF8Encoding]::new($false)
        $startInfo.StandardInputEncoding = $utf8
        $startInfo.StandardOutputEncoding = $utf8
        $startInfo.StandardErrorEncoding = $utf8
        $startInfo.Environment['CODEX_HOME'] = $cliHome
        $startInfo.Environment['TEMP'] = $cliTemp
        $startInfo.Environment['TMP'] = $cliTemp
        # Changes apply solely to this child; do not expose parent API credentials to it.
        foreach ($key in @('OPENAI_API_KEY', 'CODEX_API_KEY', 'OPENAI_ACCESS_TOKEN', 'CODEX_ACCESS_TOKEN')) {
            [void]$startInfo.Environment.Remove($key)
        }
        $process = [System.Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) { throw '无法启动 Codex 原生加载器。' }
        $started = $true
        $process.StandardInput.AutoFlush = $true
        # Consume stderr continuously to prevent pipe backpressure. Never return or log it.
        $stderrTask = $process.StandardError.ReadToEndAsync()

        $sendRequest = {
            param([int]$RequestId, [string]$Method, [object]$Params)
            $request = @{ id = $RequestId; method = $Method; params = $Params }
            $process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 12 -Compress))
            $timer = [System.Diagnostics.Stopwatch]::StartNew()
            while ($timer.ElapsedMilliseconds -lt 25000) {
                $remaining = [Math]::Max(1, 25000 - [int]$timer.ElapsedMilliseconds)
                $lineTask = $process.StandardOutput.ReadLineAsync()
                if (-not $lineTask.Wait($remaining)) { throw "Codex 原生加载器请求超时：$Method。" }
                $line = $lineTask.GetAwaiter().GetResult()
                if ($null -eq $line) { throw 'Codex 原生加载器提前退出。' }
                if ([string]::IsNullOrWhiteSpace($line)) { continue }
                try { $message = $line | ConvertFrom-Json -AsHashtable -Depth 30 -ErrorAction Stop }
                catch { throw 'Codex 原生加载器返回无效 JSON。' }
                if ($message.ContainsKey('id') -and $message['id'] -eq $RequestId) {
                    if ($message.ContainsKey('error')) {
                        # Avoid exposing raw error data, which can contain caller data or secrets.
                        $rpcCode = $message['error']['code']
                        throw "Codex 原生加载器拒绝请求 $Method（错误码 $rpcCode）。"
                    }
                    if (-not $message.ContainsKey('result')) { throw 'Codex 原生加载器响应缺少结果。' }
                    return $message['result']
                }
                if ($message.ContainsKey('method') -and $message.ContainsKey('id')) {
                    # This read-only client never approves server-initiated requests.
                    $denial = @{ id = $message['id']; error = @{ code = -32601; message = 'Read-only skill validation client' } }
                    $process.StandardInput.WriteLine(($denial | ConvertTo-Json -Depth 8 -Compress))
                }
                # Notifications and unrelated responses do not reset the per-request timeout.
            }
            throw "Codex 原生加载器请求超时：$Method。"
        }

        $initialize = @{
            clientInfo = @{ name = 'codex_skill_updater_validation'; title = 'Skill Updater Validation'; version = '1.0.0' }
        }
        $null = & $sendRequest 1 'initialize' $initialize
        $process.StandardInput.WriteLine('{"method":"initialized","params":{}}')
        # The installed 0.159 alpha supports process-local roots, while the newer
        # perCwdExtraUserRoots list field is silently ignored by that version.
        # This API changes discovery roots solely in this short-lived process.
        $null = & $sendRequest 2 'skills/extraRoots/set' @{ extraRoots = $roots }
        $listParams = @{
            cwds = @($cwd)
            forceReload = $true
        }
        $result = & $sendRequest 3 'skills/list' $listParams
        if ($null -eq $result -or -not $result.ContainsKey('data')) {
            throw 'Codex 原生加载器 Skill 清单格式异常。'
        }
        foreach ($entry in @($result['data'])) {
            foreach ($skill in @($entry['skills'])) {
                if ($null -ne $skill) { $skills.Add([pscustomobject]$skill) }
            }
            foreach ($loadError in @($entry['errors'])) {
                if ($null -ne $loadError) { $errors.Add([pscustomobject]$loadError) }
            }
        }
    }
    catch {
        $errors.Add([pscustomobject]@{ path = ''; message = $_.Exception.Message; type = 'NativeValidation' })
    }
    finally {
        if ($null -ne $process) {
            if ($started) {
                try { $process.StandardInput.Close() } catch { }
                try {
                    if (-not $process.WaitForExit(2000)) {
                        $process.Kill($true) # Only the Process instance started above and its children.
                        [void]$process.WaitForExit(2000)
                    }
                } catch {
                    $errors.Add([pscustomobject]@{ path = ''; message = '原生验证子进程清理未完成。'; type = 'NativeCleanup' })
                }
            }
            if ($null -ne $stderrTask) {
                try { [void]$stderrTask.Wait(1000) } catch { }
            }
            $process.Dispose()
        }
    }
    [pscustomobject]@{
        Skills = @($skills.ToArray())
        Errors = @($errors.ToArray())
    }
}
