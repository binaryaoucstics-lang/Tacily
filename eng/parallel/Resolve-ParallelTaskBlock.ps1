#requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunPath,
    [Parameter(Mandatory)][string]$TaskId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$ResolutionNotes,
    [string]$ControllerRef
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$lock = $null; $reportPath = $null; $state = $null; $failed = $false
$report = @{ status='failed'; taskId=$TaskId; resolutionNotes=$ResolutionNotes; commands=@(); error=''; controllerRef=$null }
function Get-ResolutionContent {
    param($Root, $Baseline)
    $items = foreach ($path in @(Get-ParallelChangedFiles $Root $Baseline)) {
        $full = Assert-ParallelChildPath $Root (Join-Path $Root $path)
        if (Test-Path -LiteralPath $full -PathType Leaf) { "$path $((Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash)" }
        else { "$path DELETED" }
    }
    return $items -join "`n"
}
try {
    $RunPath = [IO.Path]::GetFullPath($RunPath); $lock = Enter-ParallelLock $RunPath
    $statePath = Join-Path $RunPath 'state.json'; $state = Read-ParallelJson $statePath
    if ([IO.Path]::GetFullPath($state.runPath) -ne $RunPath) { throw 'State runPath identity mismatch.' }
    $matches = @($state.tasks | Where-Object id -eq $TaskId)
    if ($matches.Count -ne 1) { throw "Unknown task: $TaskId" }; $task = $matches[0]
    if ($task.status -notin @('blocked','completed','needs_rework') -or $task.verifiedCommit -or $state.integration.status -ne 'pending') { throw 'Only an unintegrated, finished task block may be resolved.' }
    Assert-ParallelTaskWorktree $state $task
    if ($task.expectedHead -ne $state.baseline) { throw 'Task HEAD is not the original baseline.' }
    $attempt = Assert-ParallelChildPath $RunPath $task.attemptPath
    $processPath = Join-Path $attempt 'process.json'; $resultPath = Join-Path $attempt 'result.json'
    $process = Read-ParallelJson $processPath; $result = Read-ParallelJson $resultPath
    if ($process.status -ne 'exited' -or $process.exitCode -ne 0 -or -not $process.finishedAt) { throw 'CLI must have exited successfully with its original result.' }
    foreach ($id in @($process.codexPid,$process.workerPid)) {
        $live = if ($id) { Get-Process -Id $id -ErrorAction SilentlyContinue } else { $null }
        if ($live) {
            $liveStarted = $live.StartTime.ToUniversalTime()
            $recordStarted = ([DateTime]$process.startedAt).ToUniversalTime().AddSeconds(-5)
            $recordFinished = ([DateTime]$process.finishedAt).ToUniversalTime()
            if ($liveStarted -ge $recordStarted -and $liveStarted -le $recordFinished) { throw 'Worker or CLI is still running.' }
        }
    }
    if ($result.taskId -cne $TaskId -or $result.status -notin @('blocked','completed')) { throw 'Cannot resolve a failed or mismatched implementation result.' }
    if ($result.status -eq 'completed' -and -not @($result.permissionRequests).Count) { throw 'Completed task has no block to resolve; use normal review.' }
    $resolutionDirectory = Join-Path $attempt ('resolution-' + [guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($resolutionDirectory) | Out-Null
    $reportPath = Join-Path $resolutionDirectory 'report.json'
    $report.resultPath = $resultPath; $report.resultHash = (Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash
    $report.processPath = $processPath; $report.processHash = (Get-FileHash -LiteralPath $processPath -Algorithm SHA256).Hash
    $report.head = Get-ParallelHead $task.worktree; $report.contentSnapshot = Get-ResolutionContent $task.worktree $state.baseline
    $report.originalStatus = $result.status; $report.permissionRequests = @($result.permissionRequests)
    if ($ControllerRef) {
        if ($ControllerRef.StartsWith('-')) { throw 'Invalid controller ref.' }
        $sha = Invoke-ParallelGit $state.repoRoot @('rev-parse','--verify',"$ControllerRef^{commit}")
        Invoke-ParallelGit $state.repoRoot @('merge-base','--is-ancestor',$state.baseline,$sha) | Out-Null
        $paths = @((Invoke-ParallelGit $state.repoRoot @('diff','--name-only',$state.baseline,$sha,'--')) -split "`r?`n" | Where-Object { $_ })
        foreach ($path in $paths) {
            foreach ($other in $state.tasks) {
                if (Test-ParallelPathOwned $path $other.ownedPaths) { throw "Controller fix overlaps task ownership: $path" }
            }
        }
        $report.controllerRef = $sha
    }
    $i = 0
    foreach ($command in $task.validation) {
        $i++; $timeout = if ($command.ContainsKey('timeoutSeconds')) { [int]$command.timeoutSeconds } else { 1800 }
        $record = Invoke-ParallelCommand $command.filePath ([string[]]$command.arguments) $task.worktree (Join-Path $resolutionDirectory "validation-$i.log") $timeout
        $report.commands += $record
        if ($record.exitCode -ne 0 -or $record.timedOut) { throw "Controller validation $i failed; block retained: $($record.logPath)" }
    }
    Assert-ParallelTaskWorktree $state $task
    if ((Get-ResolutionContent $task.worktree $state.baseline) -cne $report.contentSnapshot) { throw 'Validation changed source; block retained.' }
    if ((Get-FileHash -LiteralPath $resultPath -Algorithm SHA256).Hash -ne $report.resultHash -or (Get-FileHash -LiteralPath $processPath -Algorithm SHA256).Hash -ne $report.processHash) { throw 'Original worker evidence changed.' }
    $report.status = 'passed'; Write-ParallelJson $reportPath $report
    $task.blockResolutionPath = $reportPath; $task.status = 'validation_resolved'; $task.error = ''
    Write-ParallelJson $statePath $state
    Write-Output "Controller resolved task block after validation: $reportPath"
} catch { $failed = $true; $report.error = $_.Exception.Message; Write-Error $report.error -ErrorAction Continue }
finally { if ($reportPath) { Write-ParallelJson $reportPath $report }; if ($lock) { $lock.Dispose() } }
if ($failed) { exit 1 }
