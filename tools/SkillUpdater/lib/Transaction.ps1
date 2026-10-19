# File-set transactions for known third-party user skills. Rollback data is temporary on D:.
function Assert-SkillTransactionTarget {
    param([string]$Directory, [string]$TestTargetRoot = '')
    $productionRoot = (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.codex\skills')
    $root = $productionRoot
    if ($TestTargetRoot) {
        [void](Assert-UpdateChildPath -Path $TestTargetRoot -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))
        $root = [IO.Path]::GetFullPath($TestTargetRoot).TrimEnd('\', '/')
    }
    $path = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $leaf = [IO.Path]::GetFileName($path)
    if ([IO.Path]::GetDirectoryName($path) -ine $root.TrimEnd('\', '/') -or
        $leaf -notin @('humanizer', 'design-taste-frontend', 'career-ops', 'ui-ux-pro-max')) {
        throw '事务目标不在已确认的四个第三方 Skill 目录中。'
    }
    if (-not (Test-Path -LiteralPath $path -PathType Container)) { throw '事务目标目录不存在。' }
    Assert-SkillTransactionTree -Directory $path
    $path
}

function Assert-SkillTransactionTree {
    param([string]$Directory)
    $cursor = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw '事务路径包含链接，已暂停。'
            }
        }
        $next = [IO.Path]::GetDirectoryName($cursor)
        if ($next -eq $cursor) { break }
        $cursor = $next
    }
    if (Test-Path -LiteralPath $Directory -PathType Container) {
        foreach ($item in @(Get-ChildItem -LiteralPath $Directory -Recurse -Force -ErrorAction Stop)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw '事务文件集合包含链接，已暂停。'
            }
        }
    }
}

function Get-SkillTransactionFilePath {
    param([string]$Directory, [string]$Relative)
    if ([string]::IsNullOrWhiteSpace($Relative) -or $Relative.Contains('\') -or
        $Relative -match '(^/|(^|/)\.{1,2}(/|$)|[:*?"<>|]|//)') {
        throw '事务清单存在无效相对路径。'
    }
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $path = [IO.Path]::GetFullPath((Join-Path $root $Relative.Replace('/', '\')))
    if (-not $path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '事务文件路径超出目标目录。'
    }
    $path
}

function ConvertTo-SkillTransactionManifest {
    param([object[]]$Manifest)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    @($Manifest | ForEach-Object {
        $relative = [string]$_.relative
        if (-not $names.Add($relative) -or [string]$_.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or
            [long]$_.length -lt 0) { throw '事务清单哈希、长度或路径重复异常。' }
        $null = Get-SkillTransactionFilePath -Directory (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp\manifest-check') -Relative $relative
        [pscustomobject]@{ relative = $relative; sha256 = ([string]$_.sha256).ToUpperInvariant(); length = [long]$_.length }
    } | Sort-Object relative)
}

function Write-SkillTransactionJournal {
    param([string]$Path, [object]$Journal)
    Write-AtomicUpdateJson -Path $Path -Value $Journal
    # Force the completed journal file to disk before any target mutation.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::Read)
    try { $stream.Flush($true) } finally { $stream.Dispose() }
}

function Test-SkillTransactionProfileEqual {
    param([object]$Left, [object]$Right)
    if ($null -eq $Left -or $null -eq $Right) { return $false }
    foreach ($key in @('name', 'recipe', 'repo', 'repoId', 'branch', 'commit', 'implicitAllowed', 'description')) {
        $leftValue = $Left.PSObject.Properties[$key]
        $rightValue = $Right.PSObject.Properties[$key]
        if (($null -eq $leftValue) -ne ($null -eq $rightValue)) { return $false }
        if ($null -ne $leftValue -and $leftValue.Value -cne $rightValue.Value) { return $false }
    }
    Test-ManifestEqual -Left @($Left.manifest) -Right @($Right.manifest)
}

function Get-SkillTransactionProfileConfig {
    param([object]$Journal)
    if (-not $Journal.configPath) { return $null }
    $configRoot = if ($Journal.testTargetRoot) { (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp') } else { (Join-Path (Split-Path -Parent $PSScriptRoot) 'config') }
    [void](Assert-UpdateChildPath -Path $Journal.configPath -Root $configRoot)
    if (-not (Test-Path -LiteralPath $Journal.configPath -PathType Leaf)) { throw '来源配置丢失，暂停自动恢复。' }
    $config = Get-Content -LiteralPath $Journal.configPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 40
    if ($config.schemaVersion -ne 1) { throw '来源配置结构异常，暂停自动恢复。' }
    $rows = @($config.profiles | Where-Object name -CEQ $Journal.oldProfile.name)
    if ($rows.Count -ne 1) { throw '来源配置同名记录缺失或重复，暂停自动恢复。' }
    if (-not (Test-SkillTransactionProfileEqual $rows[0] $Journal.oldProfile) -and
        -not (Test-SkillTransactionProfileEqual $rows[0] $Journal.newProfile)) {
        throw '来源配置已出现后续人工变化，暂停自动恢复。'
    }
    $config
}

function Copy-SkillTransactionFile {
    param(
        [string]$Source, [string]$Destination, [object]$SourceRecord,
        [object[]]$AllowedCurrent = @(), [string]$RollbackSource = ''
    )
    $parent = [IO.Path]::GetDirectoryName($Destination)
    Assert-SkillTransactionTree -Directory $parent
    [void][IO.Directory]::CreateDirectory($parent)
    $sourceStream = $null
    $destinationStream = $null
    $created = $false
    $deleteCreated = $false
    try {
        $sourceStream = [IO.File]::Open($Source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        $sourceHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($sourceStream))
        if ($sourceHash -ine $SourceRecord.sha256 -or $sourceStream.Length -ne [long]$SourceRecord.length) {
            throw '暂存或回滚文件在事务中发生变化。'
        }
        $sourceStream.Position = 0
        if (Test-Path -LiteralPath $Destination -PathType Leaf) {
            $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $currentHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($destinationStream))
            $allowed = @($AllowedCurrent | Where-Object { $_.sha256 -ieq $currentHash -and [long]$_.length -eq $destinationStream.Length })
            if ($allowed.Count -eq 0) { throw '目标文件出现未知或人工修改，已拒绝覆盖。' }
        }
        else {
            if ($AllowedCurrent.Count -gt 0) { throw '目标旧文件在事务中丢失。' }
            $destinationStream = [IO.File]::Open($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            $created = $true
        }
        try {
            $destinationStream.SetLength(0)
            $destinationStream.Position = 0
            $sourceStream.CopyTo($destinationStream)
            $destinationStream.Flush($true)
        }
        catch {
            if ($RollbackSource) {
                $rollbackStream = [IO.File]::Open($RollbackSource, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
                try {
                    $destinationStream.SetLength(0)
                    $destinationStream.Position = 0
                    $rollbackStream.CopyTo($destinationStream)
                    $destinationStream.Flush($true)
                } finally { $rollbackStream.Dispose() }
            }
            elseif ($created) { $deleteCreated = $true }
            throw
        }
    }
    finally {
        if ($null -ne $destinationStream) { $destinationStream.Dispose() }
        if ($null -ne $sourceStream) { $sourceStream.Dispose() }
        if ($deleteCreated -and (Test-Path -LiteralPath $Destination -PathType Leaf)) {
            [IO.File]::Delete($Destination)
        }
    }
}

function Repair-InterruptedSkillTransaction {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$JournalPath, [string]$TestTargetRoot = '')
    [void](Assert-UpdateChildPath -Path $JournalPath -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))
    if ([IO.Path]::GetFileName($JournalPath) -ine 'journal.json') { throw '未知事务日志名称，已拒绝恢复。' }
    $journal = Get-Content -LiteralPath $JournalPath -Raw -Encoding UTF8 | ConvertFrom-Json -Depth 40
    if ($journal.schemaVersion -ne 1) { throw '事务日志结构未知，保护副本保留。' }
    $transaction = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($JournalPath))
    if ($journal.transactionDirectory -ine $transaction -or $journal.backupDirectory -ine (Join-Path $transaction 'old')) {
        throw '事务日志目录与保护副本路径不符。'
    }
    if ([string]$journal.testTargetRoot -ine $TestTargetRoot) { throw '测试事务必须显式授权同一 D 盘测试根目录。' }
    $target = Assert-SkillTransactionTarget -Directory $journal.targetDirectory -TestTargetRoot $TestTargetRoot
    [void](Assert-UpdateChildPath -Path $journal.stageDirectory -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))
    Assert-SkillTransactionTree -Directory $transaction
    if ($journal.phase -in @('committed', 'rolledBack')) {
        Remove-UpdateTempTree -Path $transaction
        return [pscustomobject]@{ Status = 'Cleaned'; TargetDirectory = $target }
    }
    if ($journal.phase -notin @('prepared', 'replacing', 'written', 'verified', 'committing', 'rollingBack')) {
        throw '事务阶段未知，保护副本保留。'
    }
    $old = @(ConvertTo-SkillTransactionManifest -Manifest @($journal.expectedOld))
    $staged = @(ConvertTo-SkillTransactionManifest -Manifest @($journal.staged))
    if (-not (Test-ManifestEqual -Left $old -Right @(Get-SkillFileManifest -Directory $journal.backupDirectory))) {
        throw '临时回滚副本校验失败，暂停恢复并保留文件。'
    }
    $current = @(Get-SkillFileManifest -Directory $target)
    foreach ($file in $current) {
        $allowed = @(@($old) + @($staged) | Where-Object {
            $_.relative -ieq $file.relative -and $_.sha256 -ieq $file.sha256 -and [long]$_.length -eq [long]$file.length
        })
        if ($allowed.Count -eq 0) { throw '目标含事务清单外文件或后续人工修改；保护副本保留，暂停自动恢复。' }
    }
    $config = Get-SkillTransactionProfileConfig -Journal $journal
    $journal.phase = 'rollingBack'
    Write-SkillTransactionJournal -Path $JournalPath -Journal $journal
    # Restore supporting files before exposing the old SKILL.md entrance.
    foreach ($file in @($old | Sort-Object @{ Expression = { $_.relative -ieq 'SKILL.md' }; Ascending = $true }, relative)) {
        $destination = Get-SkillTransactionFilePath $target $file.relative
        $existing = @($current | Where-Object relative -IEQ $file.relative)
        Copy-SkillTransactionFile -Source (Get-SkillTransactionFilePath $journal.backupDirectory $file.relative) -Destination $destination -SourceRecord $file -AllowedCurrent $existing
    }
    foreach ($file in @($current | Where-Object { $_.relative -notin @($old.relative) })) {
        $path = Get-SkillTransactionFilePath $target $file.relative
        if ((Get-FileHash -LiteralPath $path).Hash -ine $file.sha256) { throw '回滚删除前目标发生变化，保护副本保留。' }
        [IO.File]::Delete($path)
    }
    if (-not (Test-ManifestEqual -Left $old -Right @(Get-SkillFileManifest -Directory $target))) {
        throw '恢复后文件集合仍不匹配，临时保护副本保留。'
    }
    if ($null -ne $config) {
        # Recheck immediately before editing the one profile row. Preserve other successes.
        $config = Get-SkillTransactionProfileConfig -Journal $journal
        $config.profiles = @($config.profiles | ForEach-Object {
            if ($_.name -ceq $journal.oldProfile.name) { $journal.oldProfile } else { $_ }
        })
        Write-AtomicUpdateJson -Path $journal.configPath -Value $config
    }
    $journal.phase = 'rolledBack'
    Write-SkillTransactionJournal -Path $JournalPath -Journal $journal
    Remove-UpdateTempTree -Path $transaction
    [pscustomobject]@{ Status = 'Recovered'; TargetDirectory = $target }
}

function Invoke-SkillTransactionalReplace {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$TargetDirectory,
        [Parameter(Mandatory)][string]$StageDirectory,
        [Parameter(Mandatory)][string]$TransactionDirectory,
        [Parameter(Mandatory)][object[]]$ExpectedOldManifest,
        [Parameter(Mandatory)][scriptblock]$Verify,
        [Parameter(Mandatory)][scriptblock]$OnCommit,
        [object]$OldProfile = $null, [object]$NewProfile = $null,
        [string]$ConfigPath = '', [string]$TestTargetRoot = ''
    )
    $target = Assert-SkillTransactionTarget -Directory $TargetDirectory -TestTargetRoot $TestTargetRoot
    [void](Assert-UpdateChildPath -Path $StageDirectory -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))
    [void](Assert-UpdateChildPath -Path $TransactionDirectory -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))
    $stage = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\', '/')
    $transaction = [IO.Path]::GetFullPath($TransactionDirectory).TrimEnd('\', '/')
    if ($stage -ieq $transaction -or $stage.StartsWith($transaction + '\', [StringComparison]::OrdinalIgnoreCase) -or
        $transaction.StartsWith($stage + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '暂存目录与事务保护目录必须相互独立。'
    }
    Assert-SkillTransactionTree -Directory $stage
    Assert-SkillTransactionTree -Directory $transaction
    if ((Test-Path -LiteralPath $transaction) -and @(Get-ChildItem -LiteralPath $transaction -Force).Count -gt 0) {
        throw '事务目录不为空，禁止覆盖既有保护副本。'
    }
    $old = @(ConvertTo-SkillTransactionManifest -Manifest $ExpectedOldManifest)
    $staged = @(ConvertTo-SkillTransactionManifest -Manifest @(Get-SkillFileManifest -Directory $stage))
    if ('SKILL.md' -notin @($old.relative) -or 'SKILL.md' -notin @($staged.relative)) {
        throw '目标或新版缺少 SKILL.md。'
    }
    if (-not (Test-ManifestEqual -Left $old -Right @(Get-SkillFileManifest -Directory $target))) {
        throw '当前 Skill 与可信安装基线不符，已拒绝更新。'
    }
    if ($ConfigPath) {
        $configRoot = if ($TestTargetRoot) { (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp') } else { (Join-Path (Split-Path -Parent $PSScriptRoot) 'config') }
        [void](Assert-UpdateChildPath -Path $ConfigPath -Root $configRoot)
        if ($null -eq $OldProfile -or $null -eq $NewProfile -or $OldProfile.name -cne $NewProfile.name -or
            $OldProfile.name -ine [IO.Path]::GetFileName($target) -or
            -not (Test-ManifestEqual -Left @($OldProfile.manifest) -Right $old) -or
            -not (Test-ManifestEqual -Left @($NewProfile.manifest) -Right $staged)) {
            throw '事务来源快照或新旧清单不匹配。'
        }
    }
    elseif ($null -ne $OldProfile -or $null -ne $NewProfile) { throw '来源快照必须提供 D 盘配置路径。' }

    [void][IO.Directory]::CreateDirectory($transaction)
    $backup = Join-Path $transaction 'old'
    [void][IO.Directory]::CreateDirectory($backup)
    foreach ($file in $old) {
        $source = Get-SkillTransactionFilePath $target $file.relative
        $destination = Get-SkillTransactionFilePath $backup $file.relative
        Copy-SkillTransactionFile -Source $source -Destination $destination -SourceRecord $file
    }
    if (-not (Test-ManifestEqual -Left $old -Right @(Get-SkillFileManifest -Directory $backup)) -or
        -not (Test-ManifestEqual -Left $old -Right @(Get-SkillFileManifest -Directory $target))) {
        throw '临时保护副本或目标校验失败；未开始替换。'
    }
    $journalPath = Join-Path $transaction 'journal.json'
    $journal = [pscustomobject]@{
        schemaVersion = 1; targetDirectory = $target; stageDirectory = $stage
        transactionDirectory = $transaction; backupDirectory = $backup
        expectedOld = $old; staged = $staged; phase = 'prepared'
        configPath = $ConfigPath; oldProfile = $OldProfile; newProfile = $NewProfile; testTargetRoot = $TestTargetRoot
    }
    if ($ConfigPath) {
        # Freeze snapshots; OnCommit is allowed to mutate the caller's profile object.
        $journal.oldProfile = $OldProfile | ConvertTo-Json -Depth 40 -Compress | ConvertFrom-Json -Depth 40
        $journal.newProfile = $NewProfile | ConvertTo-Json -Depth 40 -Compress | ConvertFrom-Json -Depth 40
    }
    if ($ConfigPath) { $null = Get-SkillTransactionProfileConfig -Journal $journal }
    Write-SkillTransactionJournal -Path $journalPath -Journal $journal
    try {
        $journal.phase = 'replacing'
        Write-SkillTransactionJournal -Path $journalPath -Journal $journal
        foreach ($file in @($staged | Sort-Object @{ Expression = { $_.relative -ieq 'SKILL.md' }; Ascending = $true }, relative)) {
            $oldFile = @($old | Where-Object relative -IEQ $file.relative)
            $rollbackSource = if ($oldFile.Count -eq 1) { Get-SkillTransactionFilePath $backup $file.relative } else { '' }
            Copy-SkillTransactionFile -Source (Get-SkillTransactionFilePath $stage $file.relative) -Destination (Get-SkillTransactionFilePath $target $file.relative) -SourceRecord $file -AllowedCurrent $oldFile -RollbackSource $rollbackSource
        }
        foreach ($file in @($old | Where-Object { $_.relative -notin @($staged.relative) })) {
            $path = Get-SkillTransactionFilePath $target $file.relative
            if ((Get-FileHash -LiteralPath $path).Hash -ine $file.sha256) { throw '旧文件在删除前出现人工修改。' }
            [IO.File]::Delete($path)
        }
        if (-not (Test-ManifestEqual -Left $staged -Right @(Get-SkillFileManifest -Directory $target))) {
            throw '替换后完整文件集合不匹配。'
        }
        $journal.phase = 'written'
        Write-SkillTransactionJournal -Path $journalPath -Journal $journal
        $verifyResult = @(& $Verify)
        if ($verifyResult.Count -gt 0 -and $verifyResult[-1] -is [bool] -and -not $verifyResult[-1]) {
            throw '替换后原生加载验证未通过。'
        }
        if (-not (Test-ManifestEqual -Left $staged -Right @(Get-SkillFileManifest -Directory $target))) {
            throw '验证期间目标文件发生变化，已停止提交。'
        }
        $journal.phase = 'verified'
        Write-SkillTransactionJournal -Path $journalPath -Journal $journal
        $journal.phase = 'committing'
        Write-SkillTransactionJournal -Path $journalPath -Journal $journal
        $null = & $OnCommit
        if ($ConfigPath) {
            $committedConfig = Get-SkillTransactionProfileConfig -Journal $journal
            $committedRow = @($committedConfig.profiles | Where-Object name -CEQ $journal.newProfile.name)
            if ($committedRow.Count -ne 1 -or -not (Test-SkillTransactionProfileEqual $committedRow[0] $journal.newProfile)) {
                throw '来源提交未得到确认。'
            }
        }
        if (-not (Test-ManifestEqual -Left $staged -Right @(Get-SkillFileManifest -Directory $target))) {
            throw '提交期间目标文件发生变化，保护副本保留。'
        }
        $journal.phase = 'committed'
        Write-SkillTransactionJournal -Path $journalPath -Journal $journal
    }
    catch {
        $failure = $_.Exception.Message
        try { $null = Repair-InterruptedSkillTransaction -JournalPath $journalPath -TestTargetRoot $TestTargetRoot }
        catch { throw "更新失败：$failure；安全回滚暂停：$($_.Exception.Message)；临时保护副本保留于 $backup。" }
        throw "更新失败并已恢复原 Skill：$failure"
    }
    # Once committed, a cleanup failure must not roll back a successful update/profile.
    try { Remove-UpdateTempTree -Path $transaction }
    catch { return [pscustomobject]@{ Status = 'Committed'; CleanupPending = $true; TargetDirectory = $target; JournalPath = $journalPath } }
    [pscustomobject]@{ Status = 'Committed'; CleanupPending = $false; TargetDirectory = $target; JournalPath = '' }
}
