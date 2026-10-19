# Offline fixtures only. Does not invoke updater.ps1, a model, a download, or an installed Skill update.
#requires -Version 7.0
$ErrorActionPreference='Stop'
$updaterRoot=Join-Path (Split-Path -Parent $PSScriptRoot) 'tools\SkillUpdater'
. (Join-Path $updaterRoot 'lib\Common.ps1')
. (Join-Path $updaterRoot 'lib\Selection.ps1')
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $updaterRoot 'updater.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Updater parse failed'}
$definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Get-InstalledSkillEntries'},$false)
. ([ScriptBlock]::Create($definition.Extent.Text))
$fixture=Join-Path (Join-Path $updaterRoot 'temp') ('selection-test-'+[guid]::NewGuid().ToString('N'))
$checks=0
function Check($ok,[string]$why){if(-not $ok){throw $why};$script:checks++}
function Reject([scriptblock]$action,[string]$why){$failed=$false;try{& $action|Out-Null}catch{$failed=$true};Check $failed $why}
function Get-UpstreamHead {param($Profile);$script:headCalls++;switch($script:mode){'latest'{return $Profile.commit};'offline'{throw 'network timed out'};'source'{throw '[SKIP] source changed'};default{return ('b'*40)}}}
try {
    $settings=[pscustomobject]@{userSkills=(Join-Path $fixture 'skills');pluginCache=(Join-Path $fixture 'plugins')}
    foreach($name in @('humanizer','skill-router-lite','new-unregistered','.system\managed')) {
        $dir=Join-Path $settings.userSkills $name;New-Item -ItemType Directory -Path $dir -Force|Out-Null
        Set-Content -LiteralPath (Join-Path $dir 'SKILL.md') -Value "---`nname: fixture`ndescription: offline`n---`nfixture"
    }
    $profile=[pscustomobject]@{name='humanizer';target=(Join-Path $settings.userSkills 'humanizer');commit=('a'*40);repo='blader/humanizer';repoId='123';branch='main';manifest=@(Get-SkillFileManifest (Join-Path $settings.userSkills 'humanizer'))}
    $provenance=[pscustomobject]@{profiles=@($profile)};$source=Join-Path $fixture 'provenance.json'
    $provenance|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $source
    $before=(Get-FileHash -LiteralPath (Join-Path $profile.target 'SKILL.md')).Hash
    $mode='available';$headCalls=0
    $snapshot=Get-SkillUpdateSnapshot $settings $provenance $source
    Check ($snapshot.Items.Count -eq 4 -and $headCalls -eq 1) 'discover all installed categories, query only registered source'
    Check (@($snapshot.Items|Where-Object Status -eq Available).Count -eq 1) 'available source'
    Check (@($snapshot.Items|Where-Object Status -eq UnknownSource).Count -eq 1) 'unknown is not latest'
    Check (@($snapshot.Items|Where-Object Status -eq Unavailable).Count -eq 2) 'managed and local are not guessed latest'
    foreach($mode in @('latest','offline','source')) {
        $expected=@{latest='UpToDate';offline='Unavailable';source='UnknownSource'}[$mode]
        $scan=Get-SkillUpdateSnapshot $settings $provenance $source
        Check (($scan.Items|Where-Object Name -eq humanizer).Status -eq $expected) ('distinct '+$mode+' outcome')
        Check ($scan.Items.Count -eq 4) 'failure continues other inventory entries'
    }
    Add-Content -LiteralPath (Join-Path $profile.target 'SKILL.md') -Value 'manual edit'
    $scan=Get-SkillUpdateSnapshot $settings $provenance $source;$changed=$scan.Items|Where-Object Name -eq humanizer
    Check ($changed.Status -eq 'Unavailable' -and $null -eq $changed.CurrentVersion) 'modified local version is not guessed or installable'
    Set-Content -LiteralPath (Join-Path $profile.target 'SKILL.md') -Value "---`nname: fixture`ndescription: offline`n---`nfixture"
    $plan=[pscustomobject]@{SchemaVersion=1;CheckId=$snapshot.CheckId;CheckedAt=$snapshot.CheckedAt;ProvenanceHash=$snapshot.ProvenanceHash;Confirmed=$true;Items=@($snapshot.Items|Where-Object Status -eq Available)}
    $planPath=Join-Path $fixture 'approved.json'
    function SavePlan {$plan|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $planPath}
    SavePlan;$rows=Read-ApprovedSkillPlan $planPath $settings $provenance $source
    Check ($rows.Count -eq 1 -and $rows['humanizer'].ToCommit -eq ('b'*40)) 'selected fixed commit only'
    $plan.Confirmed=$false;SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'confirmation required';$plan.Confirmed=$true
    $plan.Confirmed='true';SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'confirmation must be a boolean';$plan.Confirmed=$true
    $at=$plan.CheckedAt;$plan.CheckedAt=[DateTimeOffset]::UtcNow.AddHours(-1).ToString('o');SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'expired plan';$plan.CheckedAt=$at
    $hash=$plan.ProvenanceHash;$plan.ProvenanceHash='wrong';SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'changed config';$plan.ProvenanceHash=$hash
    foreach($field in @('Name','Repo','RepoId','Branch','FromCommit','ToCommit','Path','Status')) {
        $old=$plan.Items[0].$field;$plan.Items[0].$field='changed';SavePlan
        Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} ('reject '+$field+' change')
        $plan.Items[0].$field=$old
    }
    $item=$plan.Items[0];$plan.Items=@($item,$item);SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'duplicate selection'
    $plan.Items=@();SavePlan;Reject {Read-ApprovedSkillPlan $planPath $settings $provenance $source} 'empty selection'
    Check ((Get-FileHash -LiteralPath (Join-Path $profile.target 'SKILL.md')).Hash -eq $before) 'fixture check and approval never change Skill content'
    $loop=$ast.Find({param($n)$n -is [Management.Automation.Language.ForEachStatementAst] -and $n.Variable.VariablePath.UserPath -ceq 'profile'},$false)
    function Assert-NativeRecognized {param($EntryPath,$ExpectedName,$Catalog)}
    function Add-RunEvent {param($Action,$Data)}
    function Save-UpstreamPackage {throw 'package download must never happen in this test'}
    $native=$null;$blockedTargets=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $approvedRows=@{humanizer=[pscustomobject]@{ToCommit=('c'*40)}};$mode='available'
    $provenance.profiles=@($profile,[pscustomobject]@{name='career-ops';target='not-authorized-to-touch'})
    $result=[pscustomobject]@{updated=0;latest=0;skipped=0;failed=0;items=@()}
    . ([scriptblock]::Create($loop.Extent.Text))
    Check ($result.items.Count -eq 1 -and $result.items[0].name -ceq 'humanizer') 'production loop never processes unselected Skills'
    Check ($result.updated -eq 0 -and $result.skipped -eq 1 -and $result.failed -eq 0) 'changed upstream commit is blocked before downloading'
    $checkBranch=$ast.Find({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$CheckOnly'},$false)
    Check ($null -ne $checkBranch -and $checkBranch.Extent.Text -notmatch 'Save-Upstream|Repair-Interrupted|Invoke-SkillTransactional|Remove-UpdateTemp') 'check-only branch contains no package, recovery, cleanup or replacement calls'
    Write-Output "PASS Skill selection OFFLINE checks: $checks; real network/download/install: 0"
} finally {
    $full=Assert-UpdateChildPath -Path $fixture -Root (Join-Path $updaterRoot 'temp')
    if([IO.Directory]::Exists($full)){[IO.Directory]::Delete($full,$true)}
}
