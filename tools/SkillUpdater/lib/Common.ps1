# Shared updater primitives. No third-party code is executed here.

function Assert-UpdateChildPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path,
          [Parameter(Mandatory)] [string] $Root, [switch] $AllowRoot)
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or -not [IO.Path]::IsPathFullyQualified($Root)) {
        throw '[SKIP] Update paths must be absolute.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    if (-not $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase) -and
        -not ($AllowRoot -and $full.Equals($base, [StringComparison]::OrdinalIgnoreCase))) {
        throw '[SKIP] Path is outside its permitted update directory.'
    }
    $cursor = $full
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw '[SKIP] Update path contains a symbolic link or junction.'
            }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        $cursor = if ($null -ne $parent) { $parent.FullName } else { $null }
    }
    return $full
}

function Get-SkillFileManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Directory)
    $root = Assert-UpdateChildPath -Path $Directory -Root $Directory -AllowRoot
    if (-not [IO.Directory]::Exists($root)) { throw '[SKIP] Skill directory is missing.' }
    $files = @()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop)) {
            $null = Assert-UpdateChildPath -Path $item.FullName -Root $root
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = $item.FullName.Substring($root.Length + 1).Replace('\', '/')
            if ($item.Name -ieq 'SKILL.md' -and $relative -ine 'SKILL.md') {
                throw '[SKIP] Skill directory contains an additional nested Skill entry.'
            }
            $files += [pscustomobject]@{
                relative = $relative
                sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256 -ErrorAction Stop).Hash
                length = [long] $item.Length
            }
        }
    }
    return @($files | Sort-Object relative)
}

function Test-ManifestEqual {
    [CmdletBinding()]
    param([AllowNull()] [object[]] $Left, [AllowNull()] [object[]] $Right)
    $leftFiles = @($Left | Where-Object { $null -ne $_ })
    $rightFiles = @($Right | Where-Object { $null -ne $_ })
    if ($leftFiles.Count -ne $rightFiles.Count) { return $false }
    $map = @{}
    foreach ($entry in $leftFiles) {
        $relative = [string] $entry.relative
        if ([string]::IsNullOrWhiteSpace($relative) -or $map.ContainsKey($relative)) { return $false }
        $map[$relative] = $entry
    }
    $seen = @{}
    foreach ($entry in $rightFiles) {
        $relative = [string] $entry.relative
        if (-not $map.ContainsKey($relative) -or $seen.ContainsKey($relative)) { return $false }
        $seen[$relative] = $true
        if ([string] $entry.sha256 -ine [string] $map[$relative].sha256 -or
            [long] $entry.length -ne [long] $map[$relative].length) { return $false }
    }
    return $true
}

function Write-AtomicUpdateJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path,
          [Parameter(Mandatory)] [AllowNull()] [object] $Value)
    $full = [IO.Path]::GetFullPath($Path)
    $allowedRoot = $null
    foreach ($root in @((Join-Path (Split-Path -Parent $PSScriptRoot) 'config'), (Join-Path (Split-Path -Parent $PSScriptRoot) 'logs'), (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp'))) {
        if ($full.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { $allowedRoot = $root; break }
    }
    if ($null -eq $allowedRoot) { throw '[SKIP] JSON output is outside the updater D-drive directories.' }
    $full = Assert-UpdateChildPath -Path $Path -Root $allowedRoot
    $parent = [IO.Path]::GetDirectoryName($full)
    $null = [IO.Directory]::CreateDirectory($parent)
    $null = Assert-UpdateChildPath -Path $parent -Root $allowedRoot -AllowRoot
    $temp = Join-Path $parent ('.' + [IO.Path]::GetFileName($full) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    $null = Assert-UpdateChildPath -Path $temp -Root $allowedRoot
    $json = ConvertTo-Json -InputObject $Value -Depth 100 -Compress -ErrorAction Stop
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($json + "`n")
    try {
        $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ([IO.File]::Exists($full)) { [IO.File]::Replace($temp, $full, [NullString]::Value) }
        else { [IO.File]::Move($temp, $full) }
    }
    finally { if ([IO.File]::Exists($temp)) { [IO.File]::Delete($temp) } }
}

function Get-PublicGithubBytes {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Uri, [long] $MaxBytes = 4194304)
    $parsed = $null
    if (-not [Uri]::TryCreate($Uri, [UriKind]::Absolute, [ref] $parsed) -or
        $parsed.Scheme -cne 'https' -or $parsed.Host -notin @('api.github.com', 'raw.githubusercontent.com') -or
        -not $parsed.IsDefaultPort -or $parsed.UserInfo -or $parsed.Fragment -or
        $parsed.Query -match '(?i)(?:token|authorization|api[_-]?key)=') {
        throw '[SKIP] Public GitHub request has an unsupported URL or credential field.'
    }
    if ($MaxBytes -lt 1 -or $MaxBytes -gt 33554432) { throw '[SKIP] GitHub body limit is invalid.' }
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $client = [Net.Http.HttpClient]::new($handler)
    $client.Timeout = [TimeSpan]::FromSeconds(25)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('Codex-SkillUpdater/1.0')
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $parsed)
    $request.Headers.Accept.ParseAdd('application/vnd.github+json')
    $cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(25))
    $response = $null; $stream = $null; $memory = $null
    try {
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead,
            $cancellation.Token).GetAwaiter().GetResult()
        if ([int] $response.StatusCode -ge 300 -and [int] $response.StatusCode -lt 400) {
            throw '[SKIP] GitHub redirected this source; automatic source changes are forbidden.'
        }
        if ([int] $response.StatusCode -in @(404,410)) {
            throw '[SKIP] GitHub 原来源或必需文件已不可确认，保留当前版本。'
        }
        if (-not $response.IsSuccessStatusCode) {
            throw ('Public GitHub request failed (HTTP ' + [int] $response.StatusCode + ').')
        }
        if ($null -ne $response.Content.Headers.ContentLength -and
            $response.Content.Headers.ContentLength -gt $MaxBytes) { throw '[SKIP] GitHub body exceeds the download limit.' }
        $stream = $response.Content.ReadAsStreamAsync($cancellation.Token).GetAwaiter().GetResult()
        $memory = [IO.MemoryStream]::new()
        $buffer = [byte[]]::new(65536)
        while (($read = $stream.ReadAsync($buffer, 0, $buffer.Length, $cancellation.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($memory.Length + $read -gt $MaxBytes) { throw '[SKIP] GitHub body exceeds the download limit.' }
            $memory.Write($buffer, 0, $read)
        }
        return ,($memory.ToArray())
    }
    catch {
        if ($_.Exception.Message -match '^\[SKIP\]|^Public GitHub request failed') { throw }
        throw 'Public GitHub request failed or timed out; no local Skill was changed.'
    }
    finally {
        if ($null -ne $stream) { $stream.Dispose() }
        if ($null -ne $memory) { $memory.Dispose() }
        if ($null -ne $response) { $response.Dispose() }
        $request.Dispose(); $cancellation.Dispose(); $client.Dispose()
    }
}

function Get-GithubPublicJson {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Uri)
    $parsed = [Uri] $Uri
    if ($parsed.Host -cne 'api.github.com') { throw '[SKIP] GitHub JSON metadata must come from api.github.com.' }
    $bytes = Get-PublicGithubBytes -Uri $Uri -MaxBytes 16777216
    try { return [Text.UTF8Encoding]::new($false, $true).GetString($bytes) | ConvertFrom-Json -Depth 100 -ErrorAction Stop }
    catch { throw 'Public GitHub response is not valid UTF-8 JSON.' }
}

function Save-GithubBlob {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Repo,
          [Parameter(Mandatory)] [string] $Commit,
          [Parameter(Mandatory)] [string] $SourcePath,
          [Parameter(Mandatory)] [string] $BlobSha,
          [Parameter(Mandatory)] [string] $Destination, [long] $MaxBytes = 4194304)
    if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $Commit -notmatch '^[a-fA-F0-9]{40}$' -or $BlobSha -notmatch '^[a-fA-F0-9]{40}$' -or
        $SourcePath -match '(^/|//|[\\\x00-\x1f])|(^|/)\.\.?(/|$)' -or [string]::IsNullOrWhiteSpace($SourcePath)) {
        throw '[SKIP] GitHub blob identity or source path is invalid.'
    }
    $destinationPath = Assert-UpdateChildPath -Path $Destination -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp')
    $encodedPath = (($SourcePath -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $bytes = Get-PublicGithubBytes -Uri ('https://raw.githubusercontent.com/' + $Repo + '/' + $Commit + '/' + $encodedPath) -MaxBytes $MaxBytes
    $prefix = [Text.Encoding]::UTF8.GetBytes('blob ' + $bytes.Length + [char] 0)
    $hashInput = [byte[]]::new($prefix.Length + $bytes.Length)
    [Array]::Copy($prefix, 0, $hashInput, 0, $prefix.Length)
    [Array]::Copy($bytes, 0, $hashInput, $prefix.Length, $bytes.Length)
    $actual = [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($hashInput)).ToLowerInvariant()
    if ($actual -ine $BlobSha) { throw '[SKIP] Downloaded file does not match its pinned Git blob identity.' }
    $null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destinationPath))
    $null = Assert-UpdateChildPath -Path $destinationPath -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp')
    [IO.File]::WriteAllBytes($destinationPath, $bytes)
}

function Get-StandardPayloadMappings {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [object] $Profile, [Parameter(Mandatory)] [object] $Tree)
    if ($Tree.PSObject.Properties['truncated'] -and $Tree.truncated) { throw '[SKIP] GitHub payload tree is truncated.' }
    $recipe = [string] $Profile.recipe
    if ($recipe -notin @('humanizer', 'taste', 'career')) { throw '[SKIP] Unsupported standard Skill update recipe.' }
    $entries = if ($Tree.PSObject.Properties['tree']) { @($Tree.tree) } else { @($Tree) }
    $mappings = @(); $seen = @{}
    foreach ($entry in $entries) {
        if ([string] $entry.type -ne 'blob') { continue }
        $source = [string] $entry.path; $local = $null
        # Empty repository placeholders are not installed Skill content.
        if ($source -match '(^|/)\.gitkeep$' -and [long] $entry.size -eq 0 -and
            [string] $entry.mode -ceq '100644') { continue }
        switch ($recipe) {
            humanizer {
                if ($source -in @('SKILL.md', 'LICENSE', 'agents/openai.yaml')) { $local = $source }
                elseif ($source.StartsWith('agents/', [StringComparison]::Ordinal) -and $source -match '\.(?:exe|dll|ps1|sh|cmd|bat|js|py)$') {
                    throw '[SKIP] Humanizer agent payload introduced an executable helper.'
                }
            }
            taste {
                if ($source -ceq 'LICENSE') { $local = $source }
                elseif ($source.StartsWith('skills/taste-skill/', [StringComparison]::Ordinal)) {
                    $local = $source.Substring('skills/taste-skill/'.Length)
                    if ($local -cne 'SKILL.md' -and -not $local.StartsWith('references/', [StringComparison]::Ordinal)) {
                        throw '[SKIP] Taste Skill introduced an unsupported payload file.'
                    }
                }
            }
            career {
                if ($source -in @('SKILL.md', 'LICENSE')) { $local = $source }
                elseif ($source.StartsWith('references/', [StringComparison]::Ordinal)) { $local = $source }
            }
        }
        if ($null -eq $local) { continue }
        if ($source -match '[\\\x00-\x1f]|//' -or $local -match '(^/|[<>:"|?*])|(^|/)\.\.?(/|$)' -or
            $local -match '(^|/)[^/]*[ .](/|$)' -or
            $local -match '(^|/)(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.[^/]*)?(/|$)') {
            throw '[SKIP] Standard Skill payload contains an unsafe Windows path.'
        }
        if ($local -cne 'LICENSE' -and $local -notmatch '\.(?:md|txt|yaml|yml)$') {
            throw '[SKIP] Standard Skill introduced a non-text runtime or installer.'
        }
        if ([string] $entry.mode -cne '100644' -or [string] $entry.sha -notmatch '^[a-fA-F0-9]{40}$') {
            throw '[SKIP] Standard Skill payload has an unsupported file mode or blob identity.'
        }
        if ($seen.ContainsKey($local)) { throw '[SKIP] Standard Skill payload has duplicate destination paths.' }
        $seen[$local] = $true
        $mappings += [pscustomobject]@{ source = $source; local = $local; blobSha = [string] $entry.sha; size = [long] $entry.size }
    }
    if (-not $seen.ContainsKey('SKILL.md') -or -not $seen.ContainsKey('LICENSE')) {
        throw '[SKIP] Standard Skill payload is missing SKILL.md or LICENSE.'
    }
    return @($mappings | Sort-Object local)
}

function Test-SkillPayloadSafety {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Directory,
          [Parameter(Mandatory)] [string] $ExpectedName)
    $root = Assert-UpdateChildPath -Path $Directory -Root $Directory -AllowRoot
    $files = @(Get-SkillFileManifest -Directory $root)
    if ($ExpectedName -notmatch '^[a-z0-9]+(?:-[a-z0-9]+)*$') { throw '[SKIP] Expected Skill name is invalid.' }
    $skillPath = Join-Path $root 'SKILL.md'
    if (-not [IO.File]::Exists($skillPath)) { throw '[SKIP] Payload is missing SKILL.md.' }
    $utf8 = [Text.UTF8Encoding]::new($false, $true)
    try { $skill = $utf8.GetString([IO.File]::ReadAllBytes($skillPath)).TrimStart([char] 0xFEFF) }
    catch { throw '[SKIP] SKILL.md is not valid UTF-8 text.' }
    $front = [regex]::Match($skill, '\A---\r?\n(?<header>.*?)\r?\n---(?:\r?\n|$)', [Text.RegularExpressions.RegexOptions]::Singleline)
    if (-not $front.Success) { throw '[SKIP] SKILL.md frontmatter delimiters are missing.' }
    $header = $front.Groups['header'].Value
    $names = [regex]::Matches($header, '(?m)^name:\s*(?<value>[^\r\n]+)$')
    if ($names.Count -ne 1) { throw '[SKIP] Skill name is missing or ambiguous.' }
    $name = $names[0].Groups['value'].Value.Trim().Trim('"', "'")
    if ($name -cne $ExpectedName) { throw '[SKIP] Upstream changed the Skill name unexpectedly.' }
    $descriptions = [regex]::Matches($header, '(?m)^description:\s*(?<value>[^\r\n]*)$')
    if ($descriptions.Count -ne 1) { throw '[SKIP] Skill description is missing or ambiguous.' }
    $description = $descriptions[0].Groups['value'].Value.Trim()
    if ($description -match '^[|>][-+]?\s*$') {
        $offset = $descriptions[0].Index + $descriptions[0].Length
        $parts = @()
        foreach ($line in ($header.Substring($offset) -split '\r?\n')) {
            if (-not $line.Trim()) { continue }
            if ($line -notmatch '^\s+') { break }
            $parts += $line.Trim()
        }
        $description = $parts -join ' '
    }
    elseif ($description.StartsWith('"') -and $description.EndsWith('"')) {
        try { $description = ConvertFrom-Json -InputObject $description -ErrorAction Stop }
        catch { $description = $description.Trim('"') }
    }
    elseif ($description.StartsWith("'") -and $description.EndsWith("'")) {
        $description = $description.Substring(1, $description.Length - 2).Replace("''", "'")
    }
    if ([string]::IsNullOrWhiteSpace([string] $description)) { throw '[SKIP] Skill description is empty.' }
    $implicitAllowed = $true
    $agentPath = Join-Path $root 'agents\openai.yaml'
    if ([IO.File]::Exists($agentPath)) {
        $agentText = $utf8.GetString([IO.File]::ReadAllBytes($agentPath))
        $policies = [regex]::Matches($agentText, '(?m)^\s*allow_implicit_invocation:\s*(?<value>[^\r\n#]+)')
        if ($policies.Count -gt 1) { throw '[SKIP] Implicit invocation policy is ambiguous.' }
        if ($policies.Count -eq 1) {
            $value = $policies[0].Groups['value'].Value.Trim()
            if ($value -cnotin @('true', 'false')) { throw '[SKIP] Implicit invocation policy is unsupported.' }
            $implicitAllowed = $value -ceq 'true'
        }
    }
    $dangerPatterns = @(
        '(?i)\b(?:ignore|override|bypass|disregard)\b[^\r\n]{0,100}\b(?:higher[- ]priority|system instructions|previous instructions|safety rules)\b',
        '(?i)\.codex[/\\](?:auth\.json|config\.toml)|OPENAI_API_KEY|CODEX_API_KEY',
        '(?i)\b(?:exfiltrate|upload|send)\b[^\r\n]{0,80}\b(?:credentials|passwords|api keys|tokens|secrets)\b',
        '(?i)\b(?:Set-Content|Add-Content|Out-File|WriteAllText|write_text)\b[^\r\n]{0,100}(?:AGENTS\.md|config\.toml)',
        '(?i)(?:AGENTS\.md|config\.toml)[^\r\n]{0,100}\b(?:WriteAllText|write_text|write_bytes)\b',
        '(?i)(?:>>?|\b(?:cp|copy|Copy-Item|mv|Move-Item)\b)[^\r\n]{0,100}(?:AGENTS\.md|config\.toml)',
        '(?i)\b(?:curl|wget)\b[^\r\n]*\|\s*(?:sudo\s+)?(?:sh|bash|pwsh|powershell)\b',
        '(?i)\b(?:Invoke-Expression|iex)\b|\b(?:eval|exec)\s*\(',
        '(?i)\bnpm\s+(?:install|i)\b[^\r\n]*(?:--global|(?<!\S)-g(?!\S))',
        '(?i)\b(?:pip|pip3)\s+install\b|python\S*\s+-m\s+pip\s+install\b',
        '(?i)\b(?:subprocess\.(?:run|Popen|call)|os\.system|urllib\.request|requests\.(?:get|post|request))\b'
    )
    foreach ($file in $files) {
        $relative = [string] $file.relative
        if ($relative -match '(?i)(^|/)(?:AGENTS\.md|config\.toml|auth\.json|package\.json|pyproject\.toml|requirements[^/]*\.txt|(?:install|setup)\.[^/]+)$' -or
            $relative -match '(?i)\.(?:exe|dll|msi|com|scr|bat|cmd|ps1|sh|so|dylib|zip|tar|gz|whl|jar)$') {
            throw '[SKIP] Skill payload introduced configuration, an installer or an executable package.'
        }
        $path = Join-Path $root $relative.Replace('/', '\')
        if ($relative -eq 'LICENSE' -or $relative -match '\.(?:md|txt|yaml|yml|py|json|csv)$') {
            try { $text = $utf8.GetString([IO.File]::ReadAllBytes($path)) }
            catch { throw '[SKIP] Skill text payload is not valid UTF-8.' }
            foreach ($line in ($text -split '\r?\n')) {
                foreach ($pattern in $dangerPatterns) {
                    if ($line -match $pattern) { throw ('[SKIP] Potentially unsafe new instructions in ' + $relative + '.')}
                }
            }
            if ($relative -match '\.md$') {
                $refs = [regex]::Matches($text, '(?:references|scripts|assets|data)/[A-Za-z0-9_./-]+\.(?:md|py|json|csv|png|jpg|svg|txt)')
                foreach ($reference in $refs) {
                    $relativeReference = $reference.Value
                    if ($relativeReference -match '(^|/)\.\.?(/|$)') { throw '[SKIP] Payload reference escapes its Skill directory.' }
                    $referencePath = Join-Path $root $relativeReference.Replace('/', '\')
                    if (-not [IO.File]::Exists($referencePath)) { throw ('[SKIP] Required referenced file is missing: ' + $relativeReference) }
                    $null = Assert-UpdateChildPath -Path $referencePath -Root $root
                }
            }
        }
        else { throw '[SKIP] Skill payload contains an unsupported file type.' }
    }
    # Full YAML validation is deliberately left to the native Codex loader.
    return [pscustomobject]@{ HeaderName = $name; ImplicitAllowed = $implicitAllowed; Description = [string] $description }
}

function Remove-UpdateTempTree {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [string] $Path)
    $root = Assert-UpdateChildPath -Path $Path -Root (Join-Path (Split-Path -Parent $PSScriptRoot) 'temp')
    if (-not (Test-Path -LiteralPath $root)) { return }
    if (-not [IO.Directory]::Exists($root)) { throw 'Temporary cleanup target is not a directory.' }
    $files = @(); $directories = @(); $errors = @()
    $pending = [Collections.Generic.Stack[string]]::new(); $pending.Push($root)
    while ($pending.Count -gt 0) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop)) {
            $null = Assert-UpdateChildPath -Path $item.FullName -Root $root
            if ($item.PSIsContainer) { $directories += $item.FullName; $pending.Push($item.FullName) }
            else { $files += $item.FullName }
        }
    }
    foreach ($file in $files) { try { [IO.File]::Delete($file) } catch { $errors += 'file' } }
    foreach ($directory in @($directories | Sort-Object Length -Descending)) {
        try { [IO.Directory]::Delete($directory, $false) } catch { $errors += 'directory' }
    }
    try { [IO.Directory]::Delete($root, $false) } catch { $errors += 'root' }
    if ($errors.Count) { throw 'Temporary cleanup was incomplete; remaining files were preserved.' }
}
