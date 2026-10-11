#requires -Version 7.2
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Git emits UTF-8 paths even when a redirected PowerShell child defaults to an OEM code page.
# Keep native command decoding and script output consistent without changing the system code page.
$OutputEncoding = [Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)

function Write-ParallelJson {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)]$Value)
    $parent = Split-Path ([IO.Path]::GetFullPath($Path)) -Parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $temporary = Join-Path $parent ([IO.Path]::GetFileName($Path) + '.' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 40), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}

function Read-ParallelJson {
    param([Parameter(Mandatory)][string]$Path)
    return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable
}

function Invoke-ParallelGit {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string[]]$Arguments)
    $output = (& git -C $RepoRoot -c core.quotePath=false @Arguments 2>&1 | Out-String).TrimEnd()
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed in ${RepoRoot}: $output" }
    return $output
}

function Assert-ParallelClean {
    param([Parameter(Mandatory)][string]$RepoRoot)
    $status = Invoke-ParallelGit $RepoRoot @('status', '--porcelain=v1', '--untracked-files=all')
    if ($status) { throw "Worktree is not clean; existing changes are preserved: $RepoRoot`n$status" }
}

function Get-ParallelHead {
    param([Parameter(Mandatory)][string]$RepoRoot)
    return Invoke-ParallelGit $RepoRoot @('rev-parse', 'HEAD')
}

function Assert-ParallelChildPath {
    param([Parameter(Mandatory)][string]$Parent, [Parameter(Mandatory)][string]$Path)
    $prefix = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Path is outside its assigned root: $resolved" }
    return $resolved
}

function ConvertTo-ParallelOwnedPath {
    param([Parameter(Mandatory)][string]$Path)
    $normalized = $Path.Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($normalized) -or $normalized.StartsWith('/') -or
        $normalized -match '[:*?\[\]\x00-\x1f]' -or $normalized -match '(^|/)\.\.?(/|$)' -or
        $normalized -match '(^|/)(\.git|\.parallel|\.tools)(/|$)') { throw "Invalid owned path: $Path" }
    return $normalized
}

function Test-ParallelPathOwned {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string[]]$OwnedPaths)
    $normalized = $Path.Replace('\', '/')
    foreach ($owned in $OwnedPaths) {
        if ($normalized.Equals($owned, [StringComparison]::OrdinalIgnoreCase) -or
            ($owned.EndsWith('/') -and $normalized.StartsWith($owned, [StringComparison]::OrdinalIgnoreCase))) { return $true }
    }
    return $false
}

function Test-ParallelPlan {
    param([Parameter(Mandatory)][hashtable]$Plan)
    foreach ($key in @('schemaVersion', 'runId', 'baseline', 'mainBranch', 'maxParallel', 'tasks')) {
        if (-not $Plan.ContainsKey($key)) { throw "Missing plan field: $key" }
    }
    if ($Plan.schemaVersion -ne 1) { throw 'Only plan schemaVersion 1 is supported.' }
    if ($Plan.runId -notmatch '^[a-z][a-z0-9-]{2,63}$') { throw 'runId must be a lowercase slug (3-64 characters).' }
    if ([string]::IsNullOrWhiteSpace($Plan.baseline) -or $Plan.baseline.StartsWith('-')) { throw 'Invalid baseline.' }
    if ($Plan.mainBranch -notmatch '^[a-zA-Z0-9][a-zA-Z0-9/_-]*$') { throw 'Invalid main branch.' }
    if ($Plan.maxParallel -lt 1 -or $Plan.maxParallel -gt 5) { throw 'maxParallel must be between 1 and 5.' }
    if (@($Plan.tasks).Count -lt 3 -or @($Plan.tasks).Count -gt 5) { throw 'A plan needs 3-5 tasks.' }
    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $owners = [Collections.Generic.List[object]]::new()
    foreach ($task in $Plan.tasks) {
        foreach ($key in @('id', 'title', 'instructions', 'ownedPaths', 'dependsOn', 'acceptance', 'validation')) {
            if (-not $task.ContainsKey($key)) { throw "Task is missing field $key" }
        }
        if ($task.id -notmatch '^[a-z][a-z0-9-]{1,31}$' -or -not $ids.Add($task.id)) { throw 'Invalid or duplicate task id.' }
        if ([string]::IsNullOrWhiteSpace($task.title) -or [string]::IsNullOrWhiteSpace($task.instructions)) { throw 'Task title and instructions cannot be empty.' }
        if (@($task.ownedPaths).Count -eq 0 -or @($task.acceptance).Count -eq 0 -or @($task.validation).Count -eq 0) { throw 'Every task needs ownership, acceptance and validation.' }
        $task.ownedPaths = @($task.ownedPaths | ForEach-Object { ConvertTo-ParallelOwnedPath $_ })
        foreach ($path in $task.ownedPaths) {
            foreach ($owner in $owners) {
                $same = $path.TrimEnd('/').Equals($owner.path.TrimEnd('/'), [StringComparison]::OrdinalIgnoreCase)
                $nested = ($path.EndsWith('/') -and $owner.path.StartsWith($path, [StringComparison]::OrdinalIgnoreCase)) -or
                    ($owner.path.EndsWith('/') -and $path.StartsWith($owner.path, [StringComparison]::OrdinalIgnoreCase))
                if ($same -or $nested) { throw "Overlapping ownership: $path ($($task.id)) and $($owner.path) ($($owner.id))" }
            }
            $owners.Add(@{ path = $path; id = $task.id })
        }
        foreach ($validation in $task.validation) {
            if (-not $validation.ContainsKey('filePath') -or [string]::IsNullOrWhiteSpace($validation.filePath) -or
                -not $validation.ContainsKey('arguments')) { throw 'Validation requires filePath and an arguments array.' }
        }
    }
    $resolved = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    while ($resolved.Count -lt $ids.Count) {
        $before = $resolved.Count
        foreach ($task in $Plan.tasks) {
            $ready = $true
            foreach ($dependency in $task.dependsOn) {
                if (-not $ids.Contains($dependency) -or $dependency -eq $task.id) { throw "Invalid dependency in $($task.id): $dependency" }
                if (-not $resolved.Contains($dependency)) { $ready = $false }
            }
            if ($ready) { $resolved.Add($task.id) | Out-Null }
        }
        if ($before -eq $resolved.Count) { throw 'Task dependencies contain a cycle.' }
    }
    return $Plan
}

function Enter-ParallelLock {
    param([Parameter(Mandatory)][string]$RunPath)
    return [IO.File]::Open((Join-Path $RunPath 'controller.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}

function Get-ParallelChangedFiles {
    param([Parameter(Mandatory)][string]$RepoRoot, [Parameter(Mandatory)][string]$Baseline)
    $tracked = Invoke-ParallelGit $RepoRoot @('diff', '--name-only', $Baseline, '--')
    $untracked = Invoke-ParallelGit $RepoRoot @('ls-files', '--others', '--exclude-standard')
    return @(@($tracked -split "`r?`n") + @($untracked -split "`r?`n") | Where-Object { $_ } | Sort-Object -Unique)
}

function Assert-ParallelTaskWorktree {
    param([Parameter(Mandatory)][hashtable]$State, [Parameter(Mandatory)][hashtable]$Task)
    $expected = Assert-ParallelChildPath $State.runPath $Task.worktree
    $actual = [IO.Path]::GetFullPath((Invoke-ParallelGit $expected @('rev-parse', '--show-toplevel')))
    if (-not $actual.Equals($expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Task worktree identity mismatch.' }
    $branch = Invoke-ParallelGit $expected @('symbolic-ref', '--short', 'HEAD')
    if ($branch -ne $Task.branch -or (Get-ParallelHead $expected) -ne $Task.expectedHead) { throw "Task branch or HEAD changed outside the controller: $($Task.id)" }
    foreach ($path in @(Get-ParallelChangedFiles $expected $State.baseline)) {
        if (-not (Test-ParallelPathOwned $path $Task.ownedPaths)) { throw "Out-of-scope change in $($Task.id): $path" }
    }
}

function Resolve-ParallelExecutable {
    param([Parameter(Mandatory)][string]$Name)
    $command = Get-Command $Name -ErrorAction Stop | Select-Object -First 1
    if ($command.CommandType -notin @('Application', 'ExternalScript')) { throw "An executable or script is required: $Name" }
    $resolved = $command.Source
    # Prefer npm's matching native CLI to a PowerShell wrapper that transcodes JSONL/stdin.
    # Arbitrary custom .ps1 executables (including test doubles) retain their supplied behavior.
    if ([IO.Path]::GetFileName($resolved) -eq 'codex.ps1') {
        $packageRoot = Join-Path (Split-Path $resolved -Parent) 'node_modules/@openai/codex/node_modules/@openai'
        foreach ($candidate in @('codex-win32-x64/vendor/x86_64-pc-windows-msvc/bin/codex.exe',
            'codex-win32-arm64/vendor/aarch64-pc-windows-msvc/bin/codex.exe')) {
            $native = Join-Path $packageRoot $candidate
            if (Test-Path -LiteralPath $native -PathType Leaf) { return $native }
        }
    }
    return $resolved
}

function New-ParallelProcessInfo {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory)
    $info = [Diagnostics.ProcessStartInfo]::new()
    $resolved = Resolve-ParallelExecutable $FilePath
    if ($resolved.EndsWith('.ps1', [StringComparison]::OrdinalIgnoreCase)) {
        $info.FileName = Resolve-ParallelExecutable 'pwsh'
        foreach ($argument in @('-NoProfile', '-File', $resolved)) { $info.ArgumentList.Add($argument) }
    } elseif ($resolved.EndsWith('.cmd', [StringComparison]::OrdinalIgnoreCase) -or $resolved.EndsWith('.bat', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Use an .exe or .ps1 executable; cmd/bat argument interpolation is not supported.'
    } else { $info.FileName = $resolved }
    foreach ($argument in $Arguments) { $info.ArgumentList.Add($argument) }
    $info.WorkingDirectory = [IO.Path]::GetFullPath($WorkingDirectory)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    return $info
}

function Invoke-ParallelCommand {
    param([Parameter(Mandatory)][string]$FilePath, [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$WorkingDirectory, [Parameter(Mandatory)][string]$LogPath,
        [int]$TimeoutSeconds = 1800)
    $info = New-ParallelProcessInfo $FilePath $Arguments $WorkingDirectory
    $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    $started = [DateTime]::UtcNow
    try {
        $process.Start() | Out-Null
        $stdout = $process.StandardOutput.ReadToEndAsync(); $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = -not $process.WaitForExit($TimeoutSeconds * 1000)
        if ($timedOut) { $process.Kill($true); $process.WaitForExit() }
        [IO.Directory]::CreateDirectory((Split-Path $LogPath -Parent)) | Out-Null
        [IO.File]::WriteAllText($LogPath, $stdout.GetAwaiter().GetResult() + "`nSTDERR:`n" + $stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        return @{ filePath = $FilePath; arguments = $Arguments; exitCode = $(if ($timedOut) { 124 } else { $process.ExitCode });
            timedOut = $timedOut; logPath = $LogPath; startedAt = $started.ToString('o'); finishedAt = [DateTime]::UtcNow.ToString('o') }
    } finally { $process.Dispose() }
}
