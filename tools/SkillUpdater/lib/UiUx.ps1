# Single-Skill UI/UX adapter. Templates are read as data; no upstream code runs.

function Get-UiUxPayloadMappings {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)] [object] $Tree)

    if ($Tree.PSObject.Properties['truncated'] -and $Tree.truncated) {
        throw 'UI/UX GitHub tree is truncated; a complete payload cannot be verified.'
    }
    $entries = if ($Tree.PSObject.Properties['tree']) { @($Tree.tree) }
        elseif ($Tree.PSObject.Properties['entries']) { @($Tree.entries) }
        else { @($Tree) }
    $mappings = @()
    $seen = @{}
    foreach ($entry in $entries) {
        $source = [string] $entry.path
        if ([string] $entry.type -ne 'blob') { continue }
        # Upstream test fixtures and bytecode are not part of the installed single Skill.
        if ($source.StartsWith('src/ui-ux-pro-max/scripts/tests/', [StringComparison]::Ordinal) -or
            $source -match '(^|/)__pycache__(/|$)' -or $source -match '\.py[co]$') { continue }
        $local = $null
        if ($source -ceq 'LICENSE' -or $source -ceq 'skill.json') {
            $local = $source
        }
        elseif ($source.StartsWith('src/ui-ux-pro-max/data/', [StringComparison]::Ordinal) -or
                $source.StartsWith('src/ui-ux-pro-max/scripts/', [StringComparison]::Ordinal)) {
            $local = $source.Substring('src/ui-ux-pro-max/'.Length)
        }
        elseif ($source.StartsWith('.claude/skills/ui-ux-pro-max/references/', [StringComparison]::Ordinal)) {
            $local = $source.Substring('.claude/skills/ui-ux-pro-max/'.Length)
        }
        if ($null -eq $local) { continue }
        if ($source -match '[\\\x00-\x1f]' -or $local -match '(^|/)\.\.?(/|$)' -or
            $local -match '(^/|[<>:"|?*])' -or $local -match '(^|/)[^/]*[ .](/|$)' -or
            $local -match '(^|/)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.[^/]*)?(/|$)') {
            throw "UI/UX payload contains an unsafe Windows path: $source"
        }
        if ([string] $entry.mode -notin @('100644', '100755')) {
            throw "UI/UX payload contains an unsupported file mode: $source"
        }
        if ([string] $entry.sha -notmatch '^[a-fA-F0-9]{40}$') {
            throw "UI/UX payload has no valid Git blob identity: $source"
        }
        if ($seen.ContainsKey($local)) { throw "UI/UX payload maps two entries to $local" }
        $seen[$local] = $true
        $mappings += [pscustomobject]@{
            source = $source
            local = $local
            blobSha = [string] $entry.sha
            size = [long] $entry.size
        }
    }
    foreach ($required in @('LICENSE', 'skill.json', 'scripts/core.py', 'scripts/search.py',
            'scripts/design_system.py', 'scripts/reasoning_contract.py', 'scripts/validate_data.py',
            'references/pro-rules.md', 'references/quick-reference.md')) {
        if (-not $seen.ContainsKey($required)) { throw "UI/UX payload is missing $required" }
    }
    if (@($mappings | Where-Object { $_.local.StartsWith('data/', [StringComparison]::Ordinal) }).Count -eq 0) {
        throw 'UI/UX payload contains no searchable data.'
    }
    return @($mappings | Sort-Object local)
}

function New-UiUxCodexEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [string] $StageDirectory,
        [Parameter(Mandatory = $true)] [string] $DownloadRoot,
        [Parameter(Mandatory = $true)] [string] $TargetDirectory
    )

    $tempRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $PSScriptRoot) 'temp\'))
    $stageRoot = [IO.Path]::GetFullPath($StageDirectory).TrimEnd('\', '/')
    $downloadDirectory = [IO.Path]::GetFullPath($DownloadRoot).TrimEnd('\', '/')
    foreach ($directory in @($stageRoot, $downloadDirectory)) {
        if (-not $directory.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'UI/UX adapter input/output must be inside D:\Codex\SkillUpdater\temp\.'
        }
        if (-not [IO.Directory]::Exists($directory)) { throw "UI/UX adapter directory is missing: $directory" }
        $cursor = $directory
        while ($cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "UI/UX adapter cannot use a reparse-point path: $cursor"
            }
            $parent = [IO.Directory]::GetParent($cursor)
            $cursor = if ($null -ne $parent) { $parent.FullName } else { $null }
        }
    }
    $targetRoot = [IO.Path]::GetFullPath($TargetDirectory).TrimEnd('\', '/')
    if (-not [IO.Path]::IsPathRooted($TargetDirectory) -or
        [IO.Path]::GetFileName($targetRoot) -cne 'ui-ux-pro-max' -or $targetRoot -match '["\x00-\x1f]') {
        throw 'UI/UX target directory must be an absolute ui-ux-pro-max Skill directory.'
    }
    $templateRoot = Join-Path $downloadDirectory 'src\ui-ux-pro-max\templates'
    $codexPath = Join-Path $templateRoot 'platforms\codex.json'
    $basePath = Join-Path $templateRoot 'base\skill-content.md'
    $quickPath = Join-Path $templateRoot 'base\quick-reference.md'
    foreach ($path in @($codexPath, $basePath, $quickPath)) {
        if (-not [IO.File]::Exists($path)) { throw "UI/UX template is missing: $path" }
        $cursor = $path
        while ($cursor -and $cursor.StartsWith($downloadDirectory, [StringComparison]::OrdinalIgnoreCase)) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "UI/UX template cannot be a reparse point: $cursor"
            }
            $parent = [IO.Directory]::GetParent($cursor)
            $cursor = if ($null -ne $parent) { $parent.FullName } else { $null }
        }
    }
    $platform = [IO.File]::ReadAllText($codexPath) | ConvertFrom-Json -ErrorAction Stop
    if ($platform.platform -cne 'codex' -or $platform.installType -cne 'full' -or
        $platform.frontmatter.name -cne 'ui-ux-pro-max' -or $platform.title -cne 'ui-ux-pro-max' -or
        $platform.scriptPath -cne 'skills/ui-ux-pro-max/scripts/search.py' -or
        $platform.folderStructure.skillPath -cne 'skills/ui-ux-pro-max' -or
        $platform.folderStructure.filename -cne 'SKILL.md' -or
        $platform.sections.quickReference -ne $true -or
        $platform.description -isnot [string] -or [string]::IsNullOrWhiteSpace($platform.description) -or
        $platform.frontmatter.description -cne $platform.description) {
        throw 'Unsupported UI/UX Codex platform template; automatic adaptation is paused.'
    }
    $body = [IO.File]::ReadAllText($basePath)
    $quick = [IO.File]::ReadAllText($quickPath)
    foreach ($placeholder in @('{{TITLE}}', '{{DESCRIPTION}}', '{{QUICK_REFERENCE}}')) {
        if ([regex]::Matches($body, [regex]::Escape($placeholder)).Count -ne 1) {
            throw "Unsupported UI/UX template placeholder: $placeholder"
        }
    }
    $scriptPlaceholderCount = [regex]::Matches($body, [regex]::Escape('{{SCRIPT_PATH}}')).Count
    if ($scriptPlaceholderCount -eq 0 -or
        [regex]::Matches($body, [regex]::Escape('python3 --version || python --version')).Count -ne 1 -or
        [regex]::Matches($body, [regex]::Escape('python3 {{SCRIPT_PATH}}')).Count -ne $scriptPlaceholderCount) {
        throw 'Unsupported UI/UX Python command template; automatic adaptation is paused.'
    }
    $oldNote = '> **Note:** On Windows, use `python` instead of `python3` to run scripts (e.g., `python scripts/search.py` instead of `python3 scripts/search.py`).'
    $newNote = '> **Windows / Codex:** Commands below use the existing Python 3 interpreter and a quoted absolute script path, so they work from any project directory. `-B` avoids writing Python bytecode caches.'
    if ([regex]::Matches($body, [regex]::Escape($oldNote)).Count -ne 1) {
        throw 'Unsupported UI/UX Windows prerequisite template; automatic adaptation is paused.'
    }
    $scriptPath = '"' + ($targetRoot.Replace('\', '/')) + '/scripts/search.py"'
    $body = $body.Replace('{{TITLE}}', $platform.title).
        Replace('{{DESCRIPTION}}', $platform.description + "`n").
        Replace('{{QUICK_REFERENCE}}', $quick).
        Replace('{{SCRIPT_PATH}}', $scriptPath).
        Replace('```bash', '```powershell').
        Replace('python3 --version || python --version', 'python --version').
        Replace('python3 ' + $scriptPath, 'python -B ' + $scriptPath).
        Replace($oldNote, $newNote)
    if ($body -match '\{\{[^{}]+\}\}' -or $body -match '\$\{CLAUDE_PLUGIN_ROOT\}' -or
        $body -match '(?m)^python3\s+') {
        throw 'UI/UX adapted entry still contains unresolved or incompatible template content.'
    }
    $descriptionYaml = ConvertTo-Json -InputObject ([string] $platform.frontmatter.description) -Compress
    $content = '---' + "`n" + 'name: ui-ux-pro-max' + "`n" +
        'description: ' + $descriptionYaml + "`n" + '---' + "`n" + $body
    $outputPath = Join-Path $stageRoot 'SKILL.md'
    if (Test-Path -LiteralPath $outputPath) {
        if ((Get-Item -LiteralPath $outputPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'UI/UX staged entry must not be a reparse point.'
        }
    }
    [IO.File]::WriteAllText($outputPath, $content, (New-Object Text.UTF8Encoding $false))
    return $outputPath
}
