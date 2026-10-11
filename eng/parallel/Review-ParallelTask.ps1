#requires -Version 7.2
param([Parameter(Mandatory)][string]$RunPath, [Parameter(Mandatory)][string]$TaskId, [switch]$Approve)
. (Join-Path $PSScriptRoot 'Common.ps1')
$lock = $null; $reportPath = $null; $state = $null; $task = $null
$report = @{ taskId = $TaskId; status = 'failed'; approved = [bool]$Approve; commands = @(); error = ''; commit = $null }
function Get-ReviewSnapshot {
    param($Root, $Baseline, [switch]$ContentOnly)
    $items = foreach ($path in @(Get-ParallelChangedFiles $Root $Baseline)) {
        $full = Assert-ParallelChildPath $Root (Join-Path $Root $path)
        if (Test-Path -LiteralPath $full -PathType Leaf) { "$path $((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash)" }
        else { "$path DELETED" }
    }
    if ($ContentOnly) { return ($items -join "`n") }
    return ($items -join "`n") + "`nTRACKED DIFF:`n" + (Invoke-ParallelGit $Root @('diff', '--binary', $Baseline, '--'))
}
try {
    $RunPath = [IO.Path]::GetFullPath($RunPath)
    $lock = Enter-ParallelLock $RunPath
    $statePath = Join-Path $RunPath 'state.json'; $state = Read-ParallelJson $statePath
    if ([IO.Path]::GetFullPath($state.runPath) -ne $RunPath) { throw 'State runPath identity mismatch.' }
    $matches = @($state.tasks | Where-Object id -eq $TaskId)
    if ($matches.Count -ne 1) { throw "Unknown task: $TaskId" }; $task = $matches[0]
    $attempt = Assert-ParallelChildPath $RunPath $task.attemptPath
    $reviewDir = Join-Path $attempt ('review-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($reviewDir) | Out-Null
    $reportPath = Join-Path $reviewDir 'report.json'
    if ($task.status -notin @('completed', 'needs_rework')) { throw "Task must be completed before review; status=$($task.status)." }
    Assert-ParallelTaskWorktree $state $task
    if ($task.expectedHead -ne $state.baseline) { throw 'Unreviewed task HEAD must equal the pinned baseline.' }
    $process = Read-ParallelJson (Join-Path $attempt 'process.json')
    if ($process.status -ne 'exited' -or $null -eq $process.exitCode -or $process.exitCode -ne 0 -or -not $process.finishedAt) { throw 'CLI did not finish successfully.' }
    $result = Read-ParallelJson (Join-Path $attempt 'result.json')
    foreach ($key in @('taskId', 'status', 'summary', 'changedFiles', 'tests', 'risks', 'permissionRequests')) {
        if (-not $result.ContainsKey($key)) { throw "Task result missing field: $key" }
    }
    if ($result.taskId -cne $TaskId -or $result.status -ne 'completed') { throw 'Task result id/status does not match completed task.' }
    if (@($result.permissionRequests).Count) { throw 'Task has unresolved permission requests.' }
    $files = @(Get-ParallelChangedFiles $task.worktree $state.baseline)
    if (-not $files.Count) { throw 'Task has no changes to review or commit.' }
    foreach ($path in @($result.changedFiles)) {
        if (-not (Test-ParallelPathOwned $path $task.ownedPaths)) { throw "Result declares an unowned path: $path" }
    }
    $report.changedFiles = $files; $report.head = Get-ParallelHead $task.worktree
    $diffPath = Join-Path $reviewDir 'review.diff'
    $diff = Invoke-ParallelGit $task.worktree @('diff', '--binary', $state.baseline, '--')
    [IO.File]::WriteAllText($diffPath, $diff + "`n", [Text.UTF8Encoding]::new($false))
    foreach ($path in @(Invoke-ParallelGit $task.worktree @('ls-files', '--others', '--exclude-standard') ) -split "`r?`n") {
        if (-not $path) { continue }
        $info = New-ParallelProcessInfo 'git' @('diff', '--no-index', '--binary', '--', '/dev/null', $path) $task.worktree
        $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
        $p = [Diagnostics.Process]::new(); $p.StartInfo = $info
        try {
            $p.Start() | Out-Null; $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync(); $p.WaitForExit()
            if ($p.ExitCode -notin @(0,1)) { throw "Cannot review untracked ${path}: $($err.GetAwaiter().GetResult())" }
            [IO.File]::AppendAllText($diffPath, $out.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        } finally { $p.Dispose() }
        $check = Invoke-ParallelCommand 'git' @('diff', '--no-index', '--check', '--', '/dev/null', $path) $task.worktree (Join-Path $reviewDir ('untracked-check-' + [guid]::NewGuid().ToString('N') + '.log'))
        $report.commands += $check
        $check.kind = 'diff-check'
        # --no-index implies --exit-code: 1 means a clean addition, 2/3 signal check errors.
        if ($check.exitCode -notin @(0,1) -or $check.timedOut) { throw "Untracked diff --check failed: $path; see $($check.logPath)" }
    }
    $report.diffPath = $diffPath
    Invoke-ParallelGit $task.worktree @('diff', '--check', $state.baseline, '--') | Out-Null
    $snapshot = Get-ReviewSnapshot $task.worktree $state.baseline
    $contentSnapshot = Get-ReviewSnapshot $task.worktree $state.baseline -ContentOnly
    $i = 0
    foreach ($command in $task.validation) {
        $i++; $timeout = if ($command.ContainsKey('timeoutSeconds')) { [int]$command.timeoutSeconds } else { 1800 }
        $record = Invoke-ParallelCommand $command.filePath ([string[]]$command.arguments) $task.worktree (Join-Path $reviewDir "validation-$i.log") $timeout
        $record.kind = 'validation'
        $report.commands += $record
        if ($record.exitCode -ne 0 -or $record.timedOut) { throw "Supervisor validation $i failed; see $($record.logPath)." }
    }
    Assert-ParallelTaskWorktree $state $task
    if ((Get-ReviewSnapshot $task.worktree $state.baseline) -cne $snapshot) { throw 'Validation modified task outputs; review again.' }
    Invoke-ParallelGit $task.worktree @('diff', '--check', $state.baseline, '--') | Out-Null
    if ($Approve) {
        # New files must enter the index before --only can commit them. Stage only the
        # captured, validated paths (including deletions), leaving other index entries intact.
        $report.stagedFiles = $files
        Invoke-ParallelGit $task.worktree (@('--literal-pathspecs', 'add', '--') + $files) | Out-Null
        Assert-ParallelTaskWorktree $state $task
        # Staging moves additions from untracked to tracked; compare content independently
        # of that classification, then verify that the index matches the working files.
        if ((Get-ReviewSnapshot $task.worktree $state.baseline -ContentOnly) -cne $contentSnapshot) { throw 'Task outputs changed during staging; preserved for review.' }
        Invoke-ParallelGit $task.worktree (@('--literal-pathspecs', 'diff', '--exit-code', '--') + $files) | Out-Null
        Invoke-ParallelGit $task.worktree (@('--literal-pathspecs', 'diff', '--cached', '--check', '--') + $files) | Out-Null
        # --only commits these exact paths while preserving unrelated staged changes.
        Invoke-ParallelGit $task.worktree (@('--literal-pathspecs', 'commit', '--only', '-m', "Parallel task $TaskId ($($state.runId))", '--') + $files) | Out-Null
        $sha = Get-ParallelHead $task.worktree
        $task.verifiedCommit = $sha; $task.expectedHead = $sha; $task.status = 'reviewed'; $task.error = ''
        $task.reviewReportPath = $reportPath; $report.commit = $sha
        Write-ParallelJson $statePath $state
    }
    $report.status = 'passed'
    Write-Output "Review diff: $diffPath"
    Get-Content -LiteralPath $diffPath
    if (-not $Approve) { Write-Output 'Validation passed. Explicit -Approve is required to create a task commit.' }
} catch {
    $report.error = $_.Exception.Message
    if ($task -and $task.status -in @('completed','needs_rework')) {
        $task.status = 'needs_rework'; $task.error = $report.error
        Write-ParallelJson $statePath $state
    }
    Write-Error $report.error -ErrorAction Continue
} finally {
    if ($reportPath) { Write-ParallelJson $reportPath $report }
    if ($lock) { $lock.Dispose() }
}
if ($report.status -ne 'passed') { exit 1 }
