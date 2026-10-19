# Read-only discovery and user-selected plans. Reuses the updater's existing source and file validators.
function Get-SkillUpdateSnapshot {
    param($Settings, $Provenance, [string]$ProvenancePath)
    $items = [Collections.Generic.List[object]]::new()
    foreach ($entry in @(Get-InstalledSkillEntries -Settings $Settings)) {
        $item = [pscustomobject]@{Name=$entry.name;Path=$entry.path;Status='Unavailable';CurrentVersion=$null;LatestVersion=$null;
            Reason='由 Codex/插件管理，本工具不提供更新检查。';Repo=$null;RepoId=$null;Branch=$null;FromCommit=$null;ToCommit=$null}
        $profiles = @($Provenance.profiles | Where-Object { $_.target -ieq (Split-Path $entry.path -Parent) })
        if ($entry.kind -eq 'managed') { $items.Add($item); continue }
        if ($entry.kind -eq 'local') { $item.Reason='本地自建 Skill，没有远程更新来源。';$items.Add($item);continue }
        if ($profiles.Count -ne 1) { $item.Status='UnknownSource';$item.Reason='未登记可核验的原始安装来源。';$items.Add($item);continue }
        $profile=$profiles[0]
        try {
            if ($profile.name -cne $entry.name -or $profile.name -notin @('humanizer','design-taste-frontend','career-ops','ui-ux-pro-max') -or
                $profile.target -ine (Join-Path $Settings.userSkills $profile.name) -or
                $profile.commit -cnotmatch '^[0-9a-f]{40}$' -or [string]$profile.repoId -notmatch '^\d+$') {
                $item.Status='UnknownSource';$item.Reason='来源登记无效或不在现有受支持范围。';$items.Add($item);continue
            }
            $item.Repo=[string]$profile.repo;$item.RepoId=[string]$profile.repoId;$item.Branch=[string]$profile.branch
            $item.FromCommit=[string]$profile.commit
            if (-not (Test-ManifestEqual -Left @(Get-SkillFileManifest -Directory $profile.target) -Right @($profile.manifest))) {
                $item.Reason='本地内容与来源记录不一致，保留人工修改，不允许覆盖。';$items.Add($item);continue
            }
            $item.CurrentVersion='提交 '+$profile.commit.Substring(0,12)
            $head=Get-UpstreamHead -Profile $profile
            $item.ToCommit=$head;$item.LatestVersion='提交 '+$head.Substring(0,12)
            $item.Status=if($head -ceq $profile.commit){'UpToDate'}else{'Available'}
            $item.Reason=if($item.Status -eq 'Available'){'来源身份已核验，上游提交有变化；安装前仍须验证候选内容。'}else{'与已核验的上游提交一致。'}
        } catch {
            $item.Status=if($_.Exception.Message.StartsWith('[SKIP]')){'UnknownSource'}else{'Unavailable'}
            $item.Reason=if($item.Status -eq 'UnknownSource'){'原仓库身份、分支或来源无法继续核验。'}else{'来源检查失败或超时；未将失败视为已是最新。'}
        }
        $items.Add($item)
    }
    return [pscustomobject]@{SchemaVersion=1;CheckId=[guid]::NewGuid().ToString();CheckedAt=[DateTimeOffset]::UtcNow.ToString('o');
        ProvenanceHash=(Get-FileHash -LiteralPath $ProvenancePath -Algorithm SHA256).Hash;Items=$items.ToArray()}
}

function Read-ApprovedSkillPlan {
    param([string]$Path,$Settings,$Provenance,[string]$ProvenancePath)
    $null=Assert-UpdateChildPath -Path $Path -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp')
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt 256KB) {throw '更新选择记录无效，未执行。'}
    $plan=Get-Content -LiteralPath $Path -Raw -Encoding utf8|ConvertFrom-Json
    $id=[guid]::Empty;$at=[DateTimeOffset]::MinValue
    if ($plan.SchemaVersion -ne 1 -or $plan.Confirmed -isnot [bool] -or $plan.Confirmed -ne $true -or -not [guid]::TryParse([string]$plan.CheckId,[ref]$id) -or
        $id -eq [guid]::Empty -or -not [DateTimeOffset]::TryParse([string]$plan.CheckedAt,[ref]$at) -or
        $at -gt [DateTimeOffset]::UtcNow.AddMinutes(1) -or $at -lt [DateTimeOffset]::UtcNow.AddMinutes(-30) -or
        $plan.ProvenanceHash -cne (Get-FileHash -LiteralPath $ProvenancePath -Algorithm SHA256).Hash) {throw '检查结果已过期或来源登记已变化，请重新检查；未执行。'}
    $rows=@{}
    foreach($item in @($plan.Items)) {
        $profiles=@($Provenance.profiles|Where-Object name -CEQ $item.Name)
        if ($profiles.Count -ne 1 -or $rows.ContainsKey([string]$item.Name) -or $item.Status -cne 'Available') {throw '选择包含未确认的 Skill，未执行。'}
        $p=$profiles[0]
        if ($p.name -notin @('humanizer','design-taste-frontend','career-ops','ui-ux-pro-max') -or
            $p.target -ine (Join-Path $Settings.userSkills $p.name) -or $item.Path -ine (Join-Path $p.target 'SKILL.md') -or
            $item.Repo -cne $p.repo -or [string]$item.RepoId -cne [string]$p.repoId -or $item.Branch -cne $p.branch -or
            $item.FromCommit -cne $p.commit -or $item.ToCommit -cnotmatch '^[0-9a-f]{40}$' -or $item.ToCommit -ceq $p.commit -or
            -not (Test-ManifestEqual -Left @(Get-SkillFileManifest -Directory $p.target) -Right @($p.manifest))) {throw '选择的来源、版本或本地内容已变化，请重新检查；未执行。'}
        $rows[[string]$item.Name]=$item
    }
    if ($rows.Count -lt 1 -or $rows.Count -gt 4) {throw '未选择可更新的 Skill；未执行。'}
    return $rows
}
