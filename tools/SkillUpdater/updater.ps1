#requires -Version 7.0
[CmdletBinding()]
param([switch]$NoDialog, [switch]$CheckOnly, [string]$ApprovedPlan)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$base = [IO.Path]::GetFullPath($PSScriptRoot)
if (-not $base.StartsWith('D:\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Skill 更新器必须部署在 D 盘普通目录。' }
foreach ($module in @('Common','Codex','UiUx','Transaction')) { . (Join-Path $base ('lib\'+$module+'.ps1')) }
. (Join-Path $base 'lib\Selection.ps1')

function Add-RunEvent {
    param([string]$Action, $Data)
    @{ time=(Get-Date).ToString('o'); action=$Action; data=$Data } | ConvertTo-Json -Depth 14 -Compress |
        Add-Content -LiteralPath $script:eventLog -Encoding utf8NoBOM
}
function Find-CodexExecutable {
    param($Settings)
    $found = Get-Command codex.exe -ErrorAction SilentlyContinue
    if ($found -and (Test-Path -LiteralPath $found.Source -PathType Leaf)) { return $found.Source }
    if (Test-Path -LiteralPath $Settings.codexExe -PathType Leaf) { return $Settings.codexExe }
    $bin = Join-Path $env:LOCALAPPDATA 'OpenAI\Codex\bin'
    if (Test-Path -LiteralPath $bin) {
        $found = Get-ChildItem -LiteralPath $bin -Directory | Sort-Object LastWriteTime -Descending |
            ForEach-Object { Join-Path $_.FullName 'codex.exe' } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
        if ($found) { return $found }
    }
    throw '找不到本机 Codex 加载器；为保护现有版本，本次不进行更新。'
}
function Get-InstalledSkillEntries {
    param($Settings)
    $entries = [Collections.Generic.List[object]]::new()
    foreach ($child in @(Get-ChildItem -LiteralPath $Settings.userSkills -Directory -Force)) {
        if ($child.Name -eq '.system') {
            foreach ($systemEntry in @(Get-ChildItem -LiteralPath $child.FullName -Filter SKILL.md -File -Recurse -Force)) {
                $entries.Add([pscustomobject]@{name=(Split-Path (Split-Path $systemEntry.FullName -Parent) -Leaf);path=$systemEntry.FullName;kind='managed'})
            }
        } elseif (Test-Path -LiteralPath (Join-Path $child.FullName 'SKILL.md') -PathType Leaf) {
            $kind = if ($child.Name -eq 'skill-router-lite') { 'local' } else { 'unknown' }
            $entries.Add([pscustomobject]@{name=$child.Name;path=(Join-Path $child.FullName 'SKILL.md');kind=$kind})
        }
    }
    if (Test-Path -LiteralPath $Settings.pluginCache) {
        foreach ($managedEntry in @(Get-ChildItem -LiteralPath $Settings.pluginCache -Filter SKILL.md -File -Recurse -Force)) {
            $entries.Add([pscustomobject]@{name=(Split-Path (Split-Path $managedEntry.FullName -Parent) -Leaf);path=$managedEntry.FullName;kind='managed'})
        }
    }
    return $entries.ToArray()
}
function Assert-NativeRecognized {
    param([string]$EntryPath, [string]$ExpectedName, $Catalog)
    if (@($Catalog.Errors | Where-Object { [string]::IsNullOrWhiteSpace([string]$_.path) }).Count) {
        throw 'Codex 原生验证或验证进程清理失败；未接受新版本。'
    }
    $full = [IO.Path]::GetFullPath($EntryPath)
    $matches = @($Catalog.Skills | Where-Object { [IO.Path]::GetFullPath([string]$_.path) -ieq $full -and $_.name -ceq $ExpectedName -and $_.enabled -eq $true })
    if ($matches.Count -ne 1) { throw "Codex 未正确识别 $ExpectedName；未接受新版本。" }
    if (@($Catalog.Errors | Where-Object { [string]$_.path -ieq $full }).Count) { throw "Codex 解析 $ExpectedName 出错。" }
    return $matches[0]
}
function Protect-CoreState {
    param($Settings)
    $paths = @((Join-Path $Settings.codexHome 'config.toml'),(Join-Path $Settings.codexHome 'AGENTS.md'))
    $paths += @(Get-ChildItem -LiteralPath (Join-Path $Settings.userSkills '.system') -File -Recurse -Force | ForEach-Object FullName)
    $paths += @(Get-ChildItem -LiteralPath $Settings.pluginCache -Filter SKILL.md -File -Recurse -Force | ForEach-Object FullName)
    $paths += @(Get-ChildItem -LiteralPath (Join-Path $Settings.userSkills 'skill-router-lite') -File -Recurse -Force | ForEach-Object FullName)
    return @($paths | Sort-Object -Unique | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | ForEach-Object {
        [pscustomobject]@{path=$_;sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash}
    })
}
function Test-CoreState {
    param($Baseline)
    foreach ($record in $Baseline) {
        if (-not (Test-Path -LiteralPath $record.path -PathType Leaf) -or (Get-FileHash -LiteralPath $record.path -Algorithm SHA256).Hash -ne $record.sha256) {
            throw "受保护文件发生变化：$($record.path)。更新器已停止写入。"
        }
    }
}
function Get-UpstreamHead {
    param($Profile)
    if ($Profile.repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Profile.branch -notmatch '^[A-Za-z0-9_.-]+$') { throw '[SKIP] 来源记录异常。' }
    $repo = Get-GithubPublicJson -Uri ('https://api.github.com/repos/'+$Profile.repo)
    if ([string]$repo.id -ne [string]$Profile.repoId -or $repo.full_name -ine $Profile.repo -or $repo.archived -or $repo.disabled -or $repo.default_branch -cne $Profile.branch) {
        throw '[SKIP] 仓库身份、来源或默认分支发生变化，需要确认。'
    }
    $head = Get-GithubPublicJson -Uri ('https://api.github.com/repos/'+$Profile.repo+'/commits/'+$Profile.branch)
    if ($head.sha -notmatch '^[0-9a-f]{40}$') { throw '[SKIP] 上游提交信息异常。' }
    return [string]$head.sha
}
function Save-UpstreamPackage {
    param($Profile,[string]$Commit,[string]$PackageDirectory,[string]$DownloadRoot)
    $tree = Get-GithubPublicJson -Uri ('https://api.github.com/repos/'+$Profile.repo+'/git/trees/'+$Commit+'?recursive=1')
    if ($tree.truncated) { throw '上游文件列表不完整，未覆盖旧版。' }
    $maps = if ($Profile.recipe -eq 'uiux') { @(Get-UiUxPayloadMappings -Tree $tree) } else { @(Get-StandardPayloadMappings -Profile $Profile -Tree $tree) }
    if (-not $maps.Count -or $maps.Count -gt 300) { throw '候选文件数量异常。' }
    $total = 0L
    foreach ($map in $maps) {
        $total += [long]$map.size
        if ($total -gt 40MB) { throw '候选内容超过大小限制。' }
        $destination = Join-Path $PackageDirectory ([string]$map.local).Replace('/','\')
        Save-GithubBlob -Repo $Profile.repo -Commit $Commit -SourcePath $map.source -BlobSha $map.blobSha -Destination $destination -MaxBytes 4MB
    }
    if ($Profile.recipe -eq 'uiux') {
        foreach ($relative in @('src/ui-ux-pro-max/templates/platforms/codex.json','src/ui-ux-pro-max/templates/base/skill-content.md','src/ui-ux-pro-max/templates/base/quick-reference.md')) {
            $entry = @($tree.tree | Where-Object { $_.path -ceq $relative -and $_.type -eq 'blob' })
            if ($entry.Count -ne 1 -or $entry[0].mode -ne '100644') { throw 'Codex 适配模板缺失或异常。' }
            Save-GithubBlob -Repo $Profile.repo -Commit $Commit -SourcePath $relative -BlobSha $entry[0].sha -Destination (Join-Path $DownloadRoot $relative.Replace('/','\')) -MaxBytes 1MB
        }
        New-UiUxCodexEntry -StageDirectory $PackageDirectory -DownloadRoot $DownloadRoot -TargetDirectory $Profile.target | Out-Null
    }
}
function Update-ProvenanceRow {
    param([string]$ConfigPath,$NewProfile)
    $current = Get-Content -LiteralPath $ConfigPath -Raw -Encoding utf8 | ConvertFrom-Json
    $rows = @($current.profiles)
    $index = -1
    for ($i=0;$i -lt $rows.Count;$i++) { if ($rows[$i].name -ceq $NewProfile.name) { $index=$i;break } }
    if ($index -lt 0) { throw '原有来源登记消失；未添加新 Skill。' }
    $rows[$index] = $NewProfile
    $current.profiles = $rows
    Write-AtomicUpdateJson -Path $ConfigPath -Value $current
}
function Show-UpdateResult {
    param($Result)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('Codex Skill 更新完成')
    $lines.Add('')
    $lines.Add('成功更新：'+$Result.updated)
    $lines.Add('已是最新：'+$Result.latest)
    $lines.Add('跳过：'+$Result.skipped)
    $lines.Add('失败：'+$Result.failed)
    foreach ($item in @($Result.items | Where-Object { $_.status -in @('skipped','failed') })) { $lines.Add($item.name+'：'+$item.reason) }
    if (-not $Result.tempClean) { $lines.Add('临时恢复资料尚需保留，详情见 D 盘日志。') }
    $message = $lines -join [Environment]::NewLine
    if ($NoDialog) { Write-Output $message; return }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [Windows.Forms.MessageBox]::Show($message,'更新 Codex Skills',[Windows.Forms.MessageBoxButtons]::OK,[Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    } catch { Write-Output $message }
}

if ($CheckOnly) {
    if ($ApprovedPlan) { throw '只读检查不能同时执行安装。' }
    $checkMutex=[Threading.Mutex]::new($false,'Local\CodexSkillUpdater-OneClick');$checkOwns=$false
    try {
        try {$checkOwns=$checkMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$checkOwns=$true}
        if(-not $checkOwns){throw '更新器正在运行，本次无法检查。'}
        $settings=Get-Content -LiteralPath (Join-Path $base 'config\settings.json') -Raw -Encoding utf8|ConvertFrom-Json
        $sourcePath=Join-Path $base 'config\provenance.json'
        $provenance=Get-Content -LiteralPath $sourcePath -Raw -Encoding utf8|ConvertFrom-Json
        Get-SkillUpdateSnapshot -Settings $settings -Provenance $provenance -ProvenancePath $sourcePath|ConvertTo-Json -Depth 12 -Compress
    } finally {if($checkOwns){$checkMutex.ReleaseMutex()};$checkMutex.Dispose()}
    exit 0 # No downloads, native-loader processes, transaction recovery, log cleanup or Skill writes.
}

$approvedRows=$null
$mutex = [Threading.Mutex]::new($false,'Local\CodexSkillUpdater-OneClick')
$ownsMutex = $false
$result = [pscustomobject]@{started=(Get-Date).ToString('o');completed=$null;updated=0;latest=0;skipped=0;failed=0;managed=0;local=0;totalSkills=0;nativeVerified=$false;tempClean=$false;items=@();runId=$null}
$runDir = $null
try {
    try { $ownsMutex=$mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex=$true }
    if (-not $ownsMutex) { throw '另一次更新正在运行，请等待其完成。' }
    Assert-UpdateChildPath -Path $base -Root $base -AllowRoot | Out-Null
    $settingsPath = Join-Path $base 'config\settings.json'
    $provenancePath = Join-Path $base 'config\provenance.json'
    $settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding utf8 | ConvertFrom-Json
    $provenance = Get-Content -LiteralPath $provenancePath -Raw -Encoding utf8 | ConvertFrom-Json
    if ($ApprovedPlan) {
        $approvedRows=Read-ApprovedSkillPlan -Path $ApprovedPlan -Settings $settings -Provenance $provenance -ProvenancePath $provenancePath
        # Do not recover an unrelated old transaction when only a selected update was authorized.
        if (@(Get-ChildItem -LiteralPath (Join-Path $base 'temp') -Filter journal.json -File -Recurse).Count) {throw '存在未完成事务，先保留当前版本，不执行选择更新。'}
        $claim=[IO.FileStream]::new($ApprovedPlan+'.claim',[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try {$claim.WriteByte(1);$claim.Flush($true)}finally{$claim.Dispose()}
    }
    $codexExe = Find-CodexExecutable -Settings $settings
    $runId = (Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,6)
    $result.runId=$runId
    $logDir=Join-Path $base ('logs\'+$runId); New-Item -ItemType Directory -Path $logDir | Out-Null
    $script:eventLog=Join-Path $logDir 'events.jsonl'
    $runDir=Join-Path $base ('temp\'+$runId); New-Item -ItemType Directory -Path $runDir | Out-Null
    Add-RunEvent 'run_started' @{root=$base;thirdPartyProfiles=@($provenance.profiles).Count}

    # 只恢复有持久事务日志的临时副本，不触碰未知目录或任何历史项目备份。
    $blockedTargets=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $recoveryPending=$false
    foreach ($oldRun in @(Get-ChildItem -LiteralPath (Join-Path $base 'temp') -Directory | Where-Object { $_.FullName -ine $runDir -and $_.Name -match '^\d{8}-\d{6}-[0-9a-f]{6}$' })) {
        if ($ApprovedPlan) { continue }
        try {
            Assert-UpdateChildPath -Path $oldRun.FullName -Root (Join-Path $base 'temp') | Out-Null
            $journals=@(Get-ChildItem -LiteralPath $oldRun.FullName -Filter journal.json -File -Recurse)
            foreach ($journal in $journals) {
                try {
                    Repair-InterruptedSkillTransaction -JournalPath $journal.FullName | Out-Null
                    Add-RunEvent 'interrupted_update_recovered' @{journal=$journal.FullName}
                } catch {
                    $recoveryPending=$true
                    $target=$null
                    try {
                        $record=Get-Content -LiteralPath $journal.FullName -Raw -Encoding utf8 | ConvertFrom-Json
                        if ($record.targetDirectory -in @($provenance.profiles.target)) { $target=[string]$record.targetDirectory }
                    } catch { }
                    if ($target) { [void]$blockedTargets.Add($target) }
                    else { foreach ($p in @($provenance.profiles)) { [void]$blockedTargets.Add([string]$p.target) } }
                    $result.failed++
                    $result.items += [pscustomobject]@{name='未完成事务';status='failed';reason='无法安全恢复，保留 D 盘临时副本；相关 Skill 本次不覆盖。'}
                    Add-RunEvent 'recovery_deferred' @{journal=$journal.FullName;target=$target;reason=$_.Exception.Message}
                }
            }
            if (-not @(Get-ChildItem -LiteralPath $oldRun.FullName -Filter journal.json -File -Recurse).Count) { Remove-UpdateTempTree -Path $oldRun.FullName }
        } catch {
            $recoveryPending=$true
            $result.failed++
            $result.items += [pscustomobject]@{name='临时目录';status='failed';reason='旧临时目录无法安全检查或清理，已保留。'}
            Add-RunEvent 'old_temp_preserved' @{path=$oldRun.FullName;reason=$_.Exception.Message}
        }
    }
    $coreState=Protect-CoreState -Settings $settings
    $inventory=Get-InstalledSkillEntries -Settings $settings
    $result.totalSkills=@($inventory).Count
    $result.managed=@($inventory|Where-Object kind -eq 'managed').Count
    $native=Get-CodexSkillCatalog -CodexExe $codexExe -Workspace $base -ExtraRoots @($settings.userSkills) -SandboxRoot (Join-Path $runDir 'native-before')
    Assert-NativeRecognized -EntryPath (Join-Path $settings.userSkills 'skill-router-lite\SKILL.md') -ExpectedName 'skill-router-lite' -Catalog $native | Out-Null
    $result.local=1
    Add-RunEvent 'inventory_verified' @{total=$result.totalSkills;managed=$result.managed;local=$result.local;routerPreserved=$true}

    foreach ($profile in @($provenance.profiles)) {
        if ($approvedRows -and -not $approvedRows.ContainsKey([string]$profile.name)) {continue}
        $item=[pscustomobject]@{name=$profile.name;status='failed';reason='';fromCommit=$profile.commit;toCommit=$null;boundaryChanged=$false}
        try {
            if ($blockedTargets.Contains([string]$profile.target)) { throw '[SKIP] 上次更新尚未安全恢复，保留当前内容。' }
            if ($profile.name -notin @('humanizer','design-taste-frontend','career-ops','ui-ux-pro-max') -or $profile.target -ine (Join-Path $settings.userSkills $profile.name)) { throw '[SKIP] 当前来源登记不属于允许更新的已有第三方 Skill。' }
            if (-not (Test-Path -LiteralPath $profile.target -PathType Container)) { throw '[SKIP] 原 Skill 已不存在；不会自动安装。' }
            Assert-NativeRecognized -EntryPath (Join-Path $profile.target 'SKILL.md') -ExpectedName $profile.name -Catalog $native | Out-Null
            $currentManifest=@(Get-SkillFileManifest -Directory $profile.target)
            if (-not (Test-ManifestEqual -Left $currentManifest -Right @($profile.manifest))) { throw '[SKIP] 本地文件被修改或新增，避免覆盖人工内容。' }
            Add-RunEvent 'local_content_verified' @{name=$profile.name;files=$currentManifest.Count;repo=$profile.repo;installedCommit=$profile.commit}
            $head=Get-UpstreamHead -Profile $profile
            if ($approvedRows -and $head -cne $approvedRows[[string]$profile.name].ToCommit) {throw '[SKIP] 上游版本与已选择的版本不同，请重新检查后选择。'}
            $item.toCommit=$head
            Add-RunEvent 'upstream_checked' @{name=$profile.name;commit=$head;updateAvailable=($head -ne $profile.commit)}
            if ($head -eq $profile.commit) { $item.status='latest';$item.reason='已是最新';$result.latest++;continue }
            $packageRoot=Join-Path $runDir ($profile.name+'\package')
            $package=Join-Path $packageRoot $profile.name
            New-Item -ItemType Directory -Path $package -Force | Out-Null
            Add-RunEvent 'download_started' @{name=$profile.name;commit=$head;destination=$package}
            Save-UpstreamPackage -Profile $profile -Commit $head -PackageDirectory $package -DownloadRoot (Join-Path $runDir ($profile.name+'\source'))
            Test-SkillPayloadSafety -Directory $package -ExpectedName $profile.name | Out-Null
            $stageNative=Get-CodexSkillCatalog -CodexExe $codexExe -Workspace $base -ExtraRoots @($packageRoot) -SandboxRoot (Join-Path $runDir ($profile.name+'\native-stage'))
            $stageSkill=Assert-NativeRecognized -EntryPath (Join-Path $package 'SKILL.md') -ExpectedName $profile.name -Catalog $stageNative
            $newProfile=$profile|ConvertTo-Json -Depth 15|ConvertFrom-Json
            $newProfile.commit=$head
            $newProfile.manifest=@(Get-SkillFileManifest -Directory $package)
            $newProfile.description=$stageSkill.description
            $newProfile.implicitAllowed=(Test-SkillPayloadSafety -Directory $package -ExpectedName $profile.name).ImplicitAllowed
            $item.boundaryChanged=($newProfile.description -cne $profile.description -or $newProfile.implicitAllowed -ne $profile.implicitAllowed -or $head -ne $profile.commit)
            Add-RunEvent 'candidate_validated' @{name=$profile.name;files=$newProfile.manifest.Count;nativeRecognized=$true;boundaryRecheckRequired=$item.boundaryChanged}
            $verification={
                $post=Get-CodexSkillCatalog -CodexExe $codexExe -Workspace $base -ExtraRoots @($settings.userSkills) -SandboxRoot (Join-Path $runDir ($profile.name+'\native-post'))
                Assert-NativeRecognized -EntryPath (Join-Path $profile.target 'SKILL.md') -ExpectedName $profile.name -Catalog $post | Out-Null
                Test-CoreState -Baseline $coreState
            }
            $commitAction={ Update-ProvenanceRow -ConfigPath $provenancePath -NewProfile $newProfile }
            Add-RunEvent 'transaction_started' @{name=$profile.name;target=$profile.target;temporaryProtection=(Join-Path $runDir ($profile.name+'\transaction'))}
            Invoke-SkillTransactionalReplace -TargetDirectory $profile.target -StageDirectory $package -TransactionDirectory (Join-Path $runDir ($profile.name+'\transaction')) -ExpectedOldManifest $currentManifest -Verify $verification -OnCommit $commitAction -OldProfile $profile -NewProfile $newProfile -ConfigPath $provenancePath | Out-Null
            $item.status='updated';$item.reason='更新和原生识别验证通过';$result.updated++
        } catch {
            $message=$_.Exception.Message
            if ($message.StartsWith('[SKIP]')) { $item.status='skipped';$item.reason=$message.Substring(6).Trim();$result.skipped++ }
            else { $item.status='failed';$item.reason=$message;$result.failed++ }
        } finally {
            $result.items+= $item
            Add-RunEvent 'skill_result' $item
        }
    }
    foreach ($entry in @($inventory|Where-Object kind -eq 'unknown')) {
        if ($approvedRows) { continue }
        if ($entry.name -in @($provenance.profiles.name)) { continue }
        $result.items += [pscustomobject]@{name=$entry.name;status='skipped';reason='没有可核验的 GitHub 安装来源，不进行网络更新。'}
        $result.skipped++
    }
    $finalNative=Get-CodexSkillCatalog -CodexExe $codexExe -Workspace $base -ExtraRoots @($settings.userSkills) -SandboxRoot (Join-Path $runDir 'native-final')
    foreach ($entry in @($inventory|Where-Object kind -ne 'managed')) { Assert-NativeRecognized -EntryPath $entry.path -ExpectedName $entry.name -Catalog $finalNative | Out-Null }
    Test-CoreState -Baseline $coreState
    $result.totalSkills=@(Get-InstalledSkillEntries -Settings $settings).Count
    $result.nativeVerified=$true
    Add-RunEvent 'final_scan_verified' @{total=$result.totalSkills;protectedFilesUnchanged=$true;routerUnchanged=$true}
} catch {
    $result.failed++
    $result.items += [pscustomobject]@{name='更新器';status='failed';reason=$_.Exception.Message}
    if ($script:eventLog) { Add-RunEvent 'run_error' @{reason=$_.Exception.Message} }
} finally {
    if ($runDir -and (Test-Path -LiteralPath $runDir)) {
        try {
            $pending=@(Get-ChildItem -LiteralPath $runDir -Filter journal.json -File -Recurse)
            if ($pending.Count) { throw '存在尚未恢复的事务，保留 D 盘临时保护副本。' }
            Remove-UpdateTempTree -Path $runDir
            $result.tempClean=(-not (Test-Path -LiteralPath $runDir) -and -not $recoveryPending)
        } catch { $result.tempClean=$false; if ($script:eventLog) { Add-RunEvent 'cleanup_deferred' @{reason=$_.Exception.Message} } }
    }
    $result.completed=(Get-Date).ToString('o')
    if ($script:eventLog) {
        Write-AtomicUpdateJson -Path (Join-Path (Split-Path $script:eventLog -Parent) 'result.json') -Value $result
        Write-AtomicUpdateJson -Path (Join-Path $base 'logs\latest-result.json') -Value $result
        Add-RunEvent 'run_finished' @{updated=$result.updated;latest=$result.latest;skipped=$result.skipped;failed=$result.failed;tempClean=$result.tempClean}
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}

# 自动限制日志；仅删除本工具创建且有 result.json 的已完成运行记录。
if ($ownsMutex -and $settings) {
    try {
        $completedLogs=@(Get-ChildItem -LiteralPath (Join-Path $base 'logs') -Directory | Where-Object { $_.Name -match '^\d{8}-\d{6}-[0-9a-f]{6}$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'result.json')) } | Sort-Object Name -Descending)
        foreach ($oldLog in @($completedLogs | Select-Object -Skip ([int]$settings.logRetention))) {
            Assert-UpdateChildPath -Path $oldLog.FullName -Root (Join-Path $base 'logs') | Out-Null
            foreach ($file in @(Get-ChildItem -LiteralPath $oldLog.FullName -File)) { [IO.File]::Delete($file.FullName) }
            [IO.Directory]::Delete($oldLog.FullName,$false)
        }
    } catch { }
}
Show-UpdateResult -Result $result
if ($result.failed -gt 0 -or -not $result.tempClean) { exit 1 }
exit 0
