#requires -Version 7.2
param([Parameter(Mandatory)][string]$RunPath, [Parameter(Mandatory)][string]$TaskId,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$Instructions)
. (Join-Path $PSScriptRoot 'Get-ParallelStatus.ps1') -RunPath $RunPath
$RunPath = [IO.Path]::GetFullPath($RunPath)
$lock = Enter-ParallelLock $RunPath
try {
    if ([string]::IsNullOrWhiteSpace($Instructions)) { throw 'Repair instructions cannot be whitespace.' }
    $statePath = Join-Path $RunPath 'state.json'
    $state = Read-ParallelJson $statePath
    if (-not $state.runPath.Equals($RunPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run path identity mismatch.' }
    $matches = @($state.tasks | Where-Object id -eq $TaskId)
    if ($matches.Count -ne 1) { throw "Unknown task: $TaskId" }
    $task = $matches[0]
    if ($state.status -in @('integrated','validated','promoted') -or $state.integration.status -ne 'pending') {
        throw 'Integration has started; repair requires a new development run.'
    }
    $snapshot = Get-ParallelRuntimeSnapshot $state $task
    if ($snapshot.workerAlive -or $snapshot.codexAlive) { throw 'Task still has a live worker or CLI process; no requeue permitted.' }
    if ($task.ContainsKey('launchState') -and $task.launchState -eq 'starting' -and -not $snapshot.process) {
        throw 'Interrupted startup has no worker metadata; process identity is uncertain. Controller must establish that no worker launched before repair.'
    }
    if ($snapshot.status -notin @('failed','blocked','needs_rework','timed_out','completed','validation_resolved')) { throw "Task cannot be repaired from status $($snapshot.status)." }
    Assert-ParallelTaskWorktree $state $task
    if ($task.attemptPath) {
        $attempt = Assert-ParallelChildPath $RunPath $task.attemptPath
        $rawResult = Join-Path $task.worktree '.parallel/result.json'
        $savedResult = Join-Path $attempt 'result.json'
        if ((Test-Path -LiteralPath $rawResult) -and -not (Test-Path -LiteralPath $savedResult)) {
            Copy-Item -LiteralPath $rawResult -Destination $savedResult
        }
    }
    # Preserve both the state transition and instructions without changing attempt evidence or checkout.
    if (-not $task.ContainsKey('repairs')) { $task.repairs = @() }
    $repair = @{ at = [DateTime]::UtcNow.ToString('o'); attempt = $task.attempt;
        previousStatus = $snapshot.status; previousError = $snapshot.error; instructions = $Instructions }
    if ($task.ContainsKey('blockResolutionPath')) {
        # The prior proof belongs to the retained attempt, never the next outputs.
        $repair.previousBlockResolutionPath = $task.blockResolutionPath
        $task.Remove('blockResolutionPath')
    }
    $task.repairs = @($task.repairs) + @($repair)
    $task.instructions += "`n`nRepair instructions ($([DateTime]::UtcNow.ToString('o'))):`n$Instructions"
    $task.status = 'pending'; $task.workerPid = $null; $task.verifiedCommit = $null; $task.error = ''
    $state.status = 'prepared'
    Write-ParallelJson $statePath $state
    Write-Output "Requeued $TaskId; attempt $($task.attempt) retained. Resume with -RunPath to create a new attempt."
} finally { $lock.Dispose() }
