#requires -Version 7.2
# Explicit controller replanning after main advances; validation is always invalidated.
param(
    [Parameter(Mandatory)][string]$RunPath,
    [Parameter(Mandatory)][string]$MainWorktree,
    [string]$ControllerRef
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$lock = $null
try {
    $RunPath = [IO.Path]::GetFullPath($RunPath)
    $MainWorktree = [IO.Path]::GetFullPath($MainWorktree)
    $lock = Enter-ParallelLock $RunPath
    $statePath = Join-Path $RunPath 'state.json'
    $state = Read-ParallelJson $statePath
    if ([IO.Path]::GetFullPath($state.runPath) -ne $RunPath) { throw 'Run path identity mismatch.' }
    if ($state.status -eq 'promoted') { throw 'A promoted run cannot be replanned.' }
    $actualMain = [IO.Path]::GetFullPath((Invoke-ParallelGit $MainWorktree @('rev-parse', '--show-toplevel')))
    if ($actualMain -ne $MainWorktree -or (Invoke-ParallelGit $MainWorktree @('symbolic-ref', '--short', 'HEAD')) -ne $state.mainBranch) { throw 'Expected the main checkout and branch.' }
    $common = [IO.Path]::GetFullPath((Invoke-ParallelGit $MainWorktree @('rev-parse', '--path-format=absolute', '--git-common-dir')))
    $controllerCommon = [IO.Path]::GetFullPath((Invoke-ParallelGit $state.repoRoot @('rev-parse', '--path-format=absolute', '--git-common-dir')))
    if ($common -ne $controllerCommon) { throw 'Main checkout belongs to a different repository.' }
    Assert-ParallelClean $MainWorktree
    $newMain = Get-ParallelHead $MainWorktree
    Invoke-ParallelGit $MainWorktree @('merge-base', '--is-ancestor', $state.mainHead, $newMain) | Out-Null
    foreach ($task in $state.tasks) {
        if ($task.status -notin @('reviewed', 'integrated') -or $task.verifiedCommit -notmatch '^[0-9a-f]{40}$') { throw "Task is not reviewed: $($task.id)" }
        Assert-ParallelTaskWorktree $state $task
        Assert-ParallelClean $task.worktree
    }
    $sharedCommit = $null
    if ($ControllerRef) {
        if ($ControllerRef.StartsWith('-')) { throw 'Invalid controller ref.' }
        Assert-ParallelClean $state.repoRoot
        $sharedCommit = Invoke-ParallelGit $state.repoRoot @('rev-parse', '--verify', "$ControllerRef^{commit}")
        Invoke-ParallelGit $state.repoRoot @('merge-base', '--is-ancestor', $state.baseline, $sharedCommit) | Out-Null
        # Shared controller changes must not replace any independently owned task output.
        foreach ($path in @((Invoke-ParallelGit $state.repoRoot @('diff', '--name-only', $state.baseline, $sharedCommit, '--')) -split "`r?`n" | Where-Object { $_ })) {
            foreach ($task in $state.tasks) {
                if (Test-ParallelPathOwned $path $task.ownedPaths) { throw "Controller ref overlaps task $($task.id): $path" }
            }
        }
    }
    $integration = $state.integration
    $root = Assert-ParallelChildPath $RunPath $integration.worktree
    if ($integration.status -eq 'pending') {
        if (Test-Path -LiteralPath $root) { throw 'Integration directory already exists; preserved.' }
        Invoke-ParallelGit $state.repoRoot @('worktree', 'add', '-b', $integration.branch, $root, $state.baseline) | Out-Null
        $integration.head = Get-ParallelHead $root
        $integration.status = 'integrating'
        Write-ParallelJson $statePath $state
    }
    if ((Invoke-ParallelGit $root @('symbolic-ref', '--short', 'HEAD')) -ne $integration.branch -or (Get-ParallelHead $root) -ne $integration.head) { throw 'Integration branch or HEAD drift; review required.' }
    Assert-ParallelClean $root
    $before = $integration.head
    # Record the invalidation before any merge, so a conflict cannot leave a passed gate.
    $integration.validationStatus = 'pending'; $integration.validationHead = $null; $integration.reportPath = $null
    $state.status = 'integrated'
    Write-ParallelJson $statePath $state
    if ($sharedCommit) {
        Invoke-ParallelGit $root @('merge', '--no-ff', '--no-edit', $sharedCommit) | Out-Null
        $integration.head = Get-ParallelHead $root
        Write-ParallelJson $statePath $state
    }
    Invoke-ParallelGit $root @('merge', '--no-ff', '--no-edit', $newMain) | Out-Null
    $integration.head = Get-ParallelHead $root
    Assert-ParallelClean $root
    if ((Get-ParallelHead $MainWorktree) -ne $newMain) { throw 'Main moved during replanning; integration retained, main untouched. Run Sync again after review.' }
    if (-not $state.ContainsKey('reconciliations')) { $state.reconciliations = @() }
    $state.reconciliations += @(@{ at = [DateTime]::UtcNow.ToString('o'); previousMainHead = $state.mainHead;
        mainHead = $newMain; previousIntegrationHead = $before; integrationHead = $integration.head; controllerCommit = $sharedCommit })
    $state.mainHead = $newMain
    $state.status = 'integrated'
    Write-ParallelJson $statePath $state
    Write-Output "Replanned against main $newMain. Main unchanged; complete repository validation is required."
} catch {
    if ($null -ne $state -and $lock) {
        $state.reconciliationError = $_.Exception.Message
        Write-ParallelJson (Join-Path $RunPath 'state.json') $state
    }
    throw
} finally { if ($lock) { $lock.Dispose() } }
