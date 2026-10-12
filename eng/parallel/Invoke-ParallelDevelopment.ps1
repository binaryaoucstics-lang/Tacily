#requires -Version 7.2
[CmdletBinding(DefaultParameterSetName = 'Prepare')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Prepare')][string]$PlanPath,
    [string]$RepoRoot = (Join-Path $PSScriptRoot '../..'),
    [Parameter(ParameterSetName = 'Prepare')][string]$RunRoot,
    [string]$CodexPath = 'codex',
    [ValidateRange(1,2147483)][int]$TimeoutSeconds = 1800,
    [switch]$PrepareOnly,
    [Parameter(Mandatory, ParameterSetName = 'Resume')][string]$RunPath
)
$requestedRunPath = $RunPath
. (Join-Path $PSScriptRoot 'Get-ParallelStatus.ps1') -RunPath $RepoRoot
$RunPath = $requestedRunPath

function Write-ParallelTaskInputs {
    param([hashtable]$State, [hashtable]$Task, [hashtable]$Plan)
    $directory = Join-Path $Task.worktree '.parallel'
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    # Refuse to put prompts/configuration into tracked source or visible untracked source.
    $ignored = Invoke-ParallelGit $Task.worktree @('check-ignore', '.parallel/task.json')
    if (-not $ignored) { throw '.parallel must be ignored by the pinned baseline.' }
    Write-ParallelJson (Join-Path $directory 'task.json') $Task
    Write-ParallelJson (Join-Path $directory 'plan.json') $Plan
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'result.schema.json') -Destination (Join-Path $directory 'result.schema.json')
    $prompt = @"
You are an independent development Agent for task $($Task.id): $($Task.title).
Read AGENTS.md and eng/parallel/CONTRACT.md first. Your checkout is $($Task.worktree).
All tasks started from the pinned clean commit $($State.baseline).
ONLY writable source ownership: $($Task.ownedPaths -join ', ').
Do not create subagents, use other worktrees, commit, merge, push, mutate Git branches/index/history,
or write outside your checkout. Preserve existing edits. Stay in workspace-write sandbox;
never use dangerous or sandbox bypass flags. Do not read secrets or user config.
If ownership or permissions prevent progress, report blocked and specific permissionRequests.
Dependencies are review gates only: no other task's code is shared. Follow the published baseline contract.

$($Task.instructions)

Acceptance:
$($Task.acceptance -join "`n")

Controller-reviewed validation commands (filePath and arguments, not shell strings):
$($Task.validation | ConvertTo-Json -Depth 10)

Return JSON conforming to .parallel/result.schema.json with taskId=$($Task.id).
Report actual test outcomes and concrete risks. Leave source changes uncommitted for controller review.
"@
    [IO.File]::WriteAllText((Join-Path $directory 'agent-prompt.txt'), $prompt, [Text.UTF8Encoding]::new($false))
}

function Save-ParallelRuntimeState {
    param([hashtable]$State)
    Write-ParallelJson (Join-Path $State.runPath 'state.json') $State
}

$lock = $null
$children = @{}
$state = $null
try {
    if ($PSCmdlet.ParameterSetName -eq 'Prepare') {
        $RepoRoot = [IO.Path]::GetFullPath((Invoke-ParallelGit $RepoRoot @('rev-parse','--show-toplevel')))
        Assert-ParallelClean $RepoRoot
        $plan = Test-ParallelPlan (Read-ParallelJson ([IO.Path]::GetFullPath($PlanPath)))
        foreach ($task in $plan.tasks) {
            if ($task.id -eq 'integration') { throw 'Task id integration collides with the reserved integration worktree.' }
            foreach ($validation in $task.validation) {
                if ($validation.arguments -isnot [array]) { throw 'Validation arguments must be a JSON array.' }
            }
        }
        $baseline = Invoke-ParallelGit $RepoRoot @('rev-parse','--verify',"$($plan.baseline)^{commit}")
        $mainHead = Invoke-ParallelGit $RepoRoot @('rev-parse','--verify',"refs/heads/$($plan.mainBranch)^{commit}")
        $plan.baseline = $baseline
        if (-not $RunRoot) { $RunRoot = Join-Path (Split-Path $RepoRoot -Parent) 'codex-parallel-runs' }
        $RunRoot = [IO.Path]::GetFullPath($RunRoot)
        $RunPath = Assert-ParallelChildPath $RunRoot (Join-Path $RunRoot $plan.runId)
        if (Test-Path -LiteralPath $RunPath) { throw "Run directory already exists; use -RunPath for recovery: $RunPath" }
        $branches = @("codex/integration/$($plan.runId)") + @($plan.tasks | ForEach-Object { "codex/parallel/$($plan.runId)/$($_.id)" })
        $existingBranches = @( (Invoke-ParallelGit $RepoRoot @('for-each-ref','--format=%(refname:short)','refs/heads/')) -split "`r?`n" )
        foreach ($branch in $branches) {
            Invoke-ParallelGit $RepoRoot @('check-ref-format',"refs/heads/$branch") | Out-Null
            if ($existingBranches -contains $branch) { throw "Branch collision: $branch" }
        }
        [IO.Directory]::CreateDirectory($RunRoot) | Out-Null
        # Directory.CreateDirectory accepts existing paths; New-Item fails on concurrent preparation.
        New-Item -ItemType Directory -Path $RunPath -ErrorAction Stop | Out-Null
        $lock = Enter-ParallelLock $RunPath
        $state = @{ schemaVersion = 1; runId = $plan.runId; repoRoot = $RepoRoot; runPath = $RunPath;
            baseline = $baseline; mainBranch = $plan.mainBranch; mainHead = $mainHead; status = 'preparing';
            createdAt = [DateTime]::UtcNow.ToString('o'); preparationComplete = $false; maxParallel = [int]$plan.maxParallel; tasks = @();
            integration = @{ branch = "codex/integration/$($plan.runId)"; worktree = (Join-Path $RunPath 'worktrees/integration');
                status = 'pending'; head = $null; validationHead = $null; validationStatus = 'pending'; reportPath = $null } }
        foreach ($entry in $plan.tasks) {
            $task = @{}
            foreach ($key in @('id','title','instructions','ownedPaths','dependsOn','acceptance','validation')) { $task[$key] = $entry[$key] }
            $task.branch = "codex/parallel/$($plan.runId)/$($task.id)"
            $task.worktree = Join-Path $RunPath "worktrees/$($task.id)"
            $task.expectedHead = $baseline; $task.status = 'pending'; $task.attempt = 0; $task.attemptPath = $null
            $task.workerPid = $null; $task.verifiedCommit = $null; $task.error = ''
            $state.tasks += @($task)
        }
        Write-ParallelJson (Join-Path $RunPath 'plan.json') $plan
        Save-ParallelRuntimeState $state
        foreach ($task in $state.tasks) {
            Invoke-ParallelGit $RepoRoot @('worktree','add','-b',$task.branch,$task.worktree,$baseline) | Write-Output
            Write-ParallelTaskInputs $state $task $plan
        }
        # Complete-ParallelDevelopment creates this checkout after all tasks are reviewed.
        # Keep the integration branch/path reserved in state without creating either here.
        $state.preparationComplete = $true; $state.status = 'prepared'; Save-ParallelRuntimeState $state
    } else {
        $RunPath = [IO.Path]::GetFullPath($RunPath)
        $lock = Enter-ParallelLock $RunPath
        $state = Read-ParallelJson (Join-Path $RunPath 'state.json')
        if (-not $state.runPath.Equals($RunPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run path identity mismatch.' }
        $plan = Test-ParallelPlan (Read-ParallelJson (Join-Path $RunPath 'plan.json'))
        if ($plan.baseline -ne $state.baseline -or $plan.runId -ne $state.runId) { throw 'Pinned plan/state mismatch.' }
        if ($state.ContainsKey('preparationComplete') -and -not $state.preparationComplete) {
            throw 'Preparation was interrupted; existing branches, directories and logs are retained for controller diagnosis.'
        }
        if ($state.status -in @('preparing','integrated','validated','promoted') -or $state.integration.status -ne 'pending') {
            throw 'Preparation is incomplete or integration has begun; scheduler cannot resume this run.'
        }
        Assert-ParallelClean $state.repoRoot
    }
    if ($PrepareOnly) { Write-Output "Prepared run: $RunPath"; return }
    $workerScript = Join-Path $PSScriptRoot 'Start-CodexTask.ps1'
    $pwsh = Resolve-ParallelExecutable 'pwsh'
    $CodexPath = Resolve-ParallelExecutable $CodexPath
    $state.status = 'running'; Save-ParallelRuntimeState $state
    while ($true) {
        foreach ($task in @($state.tasks | Where-Object status -eq 'running')) {
            $snapshot = Get-ParallelRuntimeSnapshot $state $task
            if ($snapshot.workerAlive -or $snapshot.codexAlive) { continue }
            if ($children.ContainsKey($task.id)) {
                $child = $children[$task.id]
                $child.process.WaitForExit()
                [IO.File]::WriteAllText((Join-Path $task.attemptPath 'worker.stdout.log'), $child.stdout.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
                [IO.File]::WriteAllText((Join-Path $task.attemptPath 'worker.stderr.log'), $child.stderr.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
                $child.process.Dispose(); $children.Remove($task.id)
            }
            $rawResult = Join-Path $task.worktree '.parallel/result.json'
            if (Test-Path -LiteralPath $rawResult) {
                $copyPath = Join-Path $task.attemptPath 'result.json'
                if (-not (Test-Path -LiteralPath $copyPath)) { Copy-Item -LiteralPath $rawResult -Destination $copyPath }
            }
            # The child join can complete metadata and result publication after
            # the first PID observation. Classify only the refreshed evidence.
            $snapshot = Get-ParallelRuntimeSnapshot $state $task
            $task.status = $snapshot.status; $task.error = $snapshot.error; $task.workerPid = $null
            Save-ParallelRuntimeState $state
        }
        $running = @($state.tasks | Where-Object status -eq 'running').Count
        foreach ($task in @($state.tasks | Where-Object status -eq 'pending')) {
            if ($running -ge [int]$plan.maxParallel) { break }
            $ready = $true
            foreach ($dependency in @($task.dependsOn)) {
                $predecessors = @($state.tasks | Where-Object id -eq $dependency)
                if ($predecessors.Count -ne 1 -or $predecessors[0].status -notin @('reviewed','integrated')) { $ready = $false }
            }
            if (-not $ready) { continue }
            try {
                $snapshot = Get-ParallelRuntimeSnapshot $state $task
                if ($snapshot.workerAlive -or $snapshot.codexAlive) { throw 'Existing worker/CLI is alive; refusing duplicate launch.' }
                Assert-ParallelTaskWorktree $state $task
                $taskRoot = Assert-ParallelChildPath $RunPath (Join-Path $RunPath "tasks/$($task.id)")
                $nextAttempt = [int]$task.attempt + 1
                $attemptPath = Assert-ParallelChildPath $taskRoot (Join-Path $taskRoot "attempt-$nextAttempt")
                if (Test-Path -LiteralPath $attemptPath) { throw "Attempt collision: $attemptPath" }
                # Preserve raw output from the previous attempt before the CLI opens its output path.
                $rawResult = Join-Path $task.worktree '.parallel/result.json'
                if (Test-Path -LiteralPath $rawResult) {
                    if (-not $task.attemptPath) { throw 'Unexpected pre-existing CLI result without attempt provenance.' }
                    $archive = Join-Path (Assert-ParallelChildPath $RunPath $task.attemptPath) ("raw-result-" + [guid]::NewGuid().ToString('N') + '.json')
                    Move-Item -LiteralPath $rawResult -Destination $archive
                }
                New-Item -ItemType Directory -Path $attemptPath -ErrorAction Stop | Out-Null
                $task.attempt = $nextAttempt; $task.attemptPath = $attemptPath
                Write-ParallelTaskInputs $state $task $plan
                # Save intent before starting. Recovery will fail this attempt if startup is interrupted.
                $task.status = 'running'; $task.workerPid = $null; $task.error = ''; $task.launchState = 'starting'
                Save-ParallelRuntimeState $state
                $info = New-ParallelProcessInfo $pwsh @('-NoProfile','-File',$workerScript,
                    '-Worktree',$task.worktree,'-PromptPath',(Join-Path $task.worktree '.parallel/agent-prompt.txt'),
                    '-AttemptPath',$attemptPath,'-CodexPath',$CodexPath,'-TimeoutSeconds',([string]$TimeoutSeconds)) $task.worktree
                $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
                $info.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
                $info.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
                $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
                $process.Start() | Out-Null
                $children[$task.id] = @{ process = $process; stdout = $process.StandardOutput.ReadToEndAsync(); stderr = $process.StandardError.ReadToEndAsync() }
                $task.workerPid = $process.Id
                $task.workerStartedAt = $process.StartTime.ToUniversalTime().ToString('o')
                $task.launchState = 'started'
                Save-ParallelRuntimeState $state
                $running++
                Write-Output "Started $($task.id), attempt $nextAttempt, worker PID $($task.workerPid)"
            } catch {
                if (-not $children.ContainsKey($task.id) -and $task.ContainsKey('launchState') -and $task.launchState -eq 'starting') {
                    $task.launchState = 'not_started'
                }
                $task.status = 'blocked'; $task.error = $_.Exception.Message
                Save-ParallelRuntimeState $state
            }
        }
        if (@($state.tasks | Where-Object status -eq 'running').Count -eq 0) { break }
        Start-Sleep -Milliseconds 200
    }
    $failures = @($state.tasks | Where-Object { $_.status -in @('failed','blocked','needs_rework','timed_out') })
    $state.status = if ($failures.Count -gt 0) { 'failed' } else { 'awaiting_review' }
    Save-ParallelRuntimeState $state
    Write-Output "Run $($state.runId): $($state.status). Pending dependencies require predecessor review, then -RunPath."
    if ($failures.Count -gt 0) { throw "$($failures.Count) task(s) failed or blocked; inspect state.json and attempt logs." }
} catch {
    if ($state -and $lock) {
        $state.controllerError = $_.Exception.Message
        if ($state.status -eq 'preparing') { $state.status = 'failed' }
        if ($state.status -eq 'running' -and @($state.tasks | Where-Object status -eq 'running').Count -eq 0) { $state.status = 'failed' }
        Save-ParallelRuntimeState $state
    }
    throw
} finally {
    # Never kill workers on controller interruption. They write process.json and retain all evidence.
    foreach ($child in $children.Values) { $child.process.Dispose() }
    if ($lock) { $lock.Dispose() }
}
