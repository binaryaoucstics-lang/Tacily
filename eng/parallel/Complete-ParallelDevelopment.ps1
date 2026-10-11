#requires -Version 7.2
param([Parameter(Mandatory)][string]$RunPath, [string]$DotnetPath, [string]$AssetSource)
. (Join-Path $PSScriptRoot 'Common.ps1')
$lock = $null; $state = $null; $failed = $false
try {
    $RunPath = [IO.Path]::GetFullPath($RunPath); $lock = Enter-ParallelLock $RunPath
    $statePath = Join-Path $RunPath 'state.json'; $state = Read-ParallelJson $statePath
    if ([IO.Path]::GetFullPath($state.runPath) -ne $RunPath) { throw 'State runPath identity mismatch.' }
    foreach ($task in $state.tasks) {
        if ($task.status -notin @('reviewed', 'integrated') -or $task.verifiedCommit -notmatch '^[0-9a-f]{40}$' -or $task.expectedHead -ne $task.verifiedCommit) { throw "Task $($task.id) has no fixed reviewed commit." }
        Assert-ParallelTaskWorktree $state $task; Assert-ParallelClean $task.worktree
        Invoke-ParallelGit $task.worktree @('merge-base', '--is-ancestor', $state.baseline, $task.verifiedCommit) | Out-Null
        if (-not $task.ContainsKey('reviewReportPath')) { throw "Task $($task.id) has no supervisor review evidence." }
        $reviewPath = Assert-ParallelChildPath $RunPath $task.reviewReportPath
        $review = Read-ParallelJson $reviewPath
        if ($review.status -ne 'passed' -or -not $review.approved -or $review.taskId -ne $task.id -or $review.commit -ne $task.verifiedCommit) { throw "Task $($task.id) review does not approve its recorded SHA." }
        $validations = @($review.commands | Where-Object { $_.ContainsKey('kind') -and $_.kind -eq 'validation' })
        if ($validations.Count -ne @($task.validation).Count) { throw "Task $($task.id) review is missing supervisor validation results." }
        foreach ($validation in $validations) { if ($null -eq $validation.exitCode -or $validation.exitCode -ne 0 -or $validation.timedOut) { throw "Task $($task.id) supervisor validation failed." } }
    }
    $integration = $state.integration
    $root = Assert-ParallelChildPath $RunPath $integration.worktree
    if ($integration.status -eq 'pending') {
        if (Test-Path -LiteralPath $root) { throw 'Integration directory already exists; refusing to overwrite.' }
        Invoke-ParallelGit $state.repoRoot @('worktree', 'add', '-b', $integration.branch, $root, $state.baseline) | Out-Null
        $integration.head = Get-ParallelHead $root; $integration.status = 'integrating'
        Write-ParallelJson $statePath $state
    }
    if ((Invoke-ParallelGit $root @('symbolic-ref', '--short', 'HEAD')) -ne $integration.branch -or
        (Get-ParallelHead $root) -ne $integration.head) { throw 'Integration branch/HEAD drift; manual review required.' }
    Assert-ParallelClean $root
    foreach ($task in $state.tasks) {
        if ($task.status -eq 'integrated') {
            Invoke-ParallelGit $root @('merge-base', '--is-ancestor', $task.verifiedCommit, 'HEAD') | Out-Null
            continue
        }
        Invoke-ParallelGit $root @('merge', '--no-ff', '--no-edit', $task.verifiedCommit) | Out-Null
        $task.status = 'integrated'; $integration.head = Get-ParallelHead $root
        Write-ParallelJson $statePath $state
    }
    $state.status = 'integrated'; $integration.status = 'integrated'; $integration.validationStatus = 'running'
    $results = Join-Path $root ('.parallel/validation-' + [guid]::NewGuid().ToString('N'))
    $integration.reportPath = Join-Path $results 'report.json'; $integration.validationHead = $null
    Write-ParallelJson $statePath $state
    $arguments = @('-NoProfile', '-File', (Join-Path $root 'eng/parallel/Test-Repository.ps1'), '-RepoRoot', $root, '-ResultDirectory', $results)
    if ($DotnetPath) { $arguments += @('-DotnetPath', $DotnetPath) }; if ($AssetSource) { $arguments += @('-AssetSource', $AssetSource) }
    $command = Invoke-ParallelCommand 'pwsh' $arguments $root (Join-Path $results 'repository.log') 7200
    $report = Read-ParallelJson $integration.reportPath
    if ($command.exitCode -ne 0 -or $report.status -ne 'passed' -or $report.head -ne $integration.head -or (Get-ParallelHead $root) -ne $integration.head) { throw "Repository validation failed; evidence: $results" }
    foreach ($name in @('sdk','restore','build','dotnet-tests','node-tests','workflow-tests')) {
        $records = @($report.commands | Where-Object name -eq $name)
        if ($records.Count -ne 1 -or $null -eq $records[0].exitCode -or $records[0].exitCode -ne 0 -or $records[0].timedOut) { throw "Repository validation missing/failed command: $name; evidence: $results" }
    }
    Assert-ParallelClean $root
    $integration.validationHead = $report.head; $integration.validationStatus = 'passed'; $state.status = 'validated'
    Write-ParallelJson $statePath $state
    Write-Output "Integration validated: $($integration.head)"
} catch {
    $failed = $true
    if ($state) {
        $state.error = $_.Exception.Message
        if ($state.integration.validationStatus -eq 'running') { $state.integration.validationStatus = 'failed' }
        Write-ParallelJson $statePath $state
    }
    Write-Error $_.Exception.Message -ErrorAction Continue
} finally { if ($lock) { $lock.Dispose() } }
if ($failed) { exit 1 }
