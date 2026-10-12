Invoke-WorkflowTest 'three independent processes overlap and preserve schema results' {
    $f=New-WorkflowFixture 'concurrency'
    foreach ($t in $f.plan.tasks) { $t.instructions='fixture:success fixture:barrier' }
    Write-ParallelJson $f.planPath $f.plan
    Assert-WorkflowSuccess (Start-WorkflowFixture $f -PrepareOnly)
    $prepared=Read-WorkflowState $f
    Assert-Workflow (@($prepared.tasks.worktree | Sort-Object -Unique).Count -eq 3) 'Worktrees are not distinct.'
    Assert-Workflow (@($prepared.tasks.branch | Sort-Object -Unique).Count -eq 3) 'Branches are not distinct.'
    foreach ($t in $prepared.tasks) {
        Assert-Workflow ((Get-ParallelHead $t.worktree) -eq $f.plan.baseline) 'Baseline was not pinned.'
        Assert-Workflow ($t.attempt -eq 0 -and $null -eq $t.verifiedCommit) 'Prepare started a worker or commit.'
    }
    $info=New-ParallelProcessInfo 'pwsh' @('-NoProfile','-File',(Join-Path $script:WorkflowSource 'Invoke-ParallelDevelopment.ps1'),
        '-RunPath',$f.runPath,'-CodexPath',(Join-Path $script:WorkflowSource 'tests/Stub-Codex.ps1'),'-TimeoutSeconds','25') $f.repo
    $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
    $p=[Diagnostics.Process]::new(); $p.StartInfo=$info
    try {
        $p.Start() | Out-Null
        $stdout=$p.StandardOutput.ReadToEndAsync(); $stderr=$p.StandardError.ReadToEndAsync()
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        do {
            $ready=@($prepared.tasks | Where-Object { Test-Path -LiteralPath (Join-Path $_.worktree '.parallel/stub-window.json') })
            if ($ready.Count -eq 3) { break }
            if ($p.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'All three workers did not reach the barrier concurrently.' }
            Start-Sleep -Milliseconds 50
        } while ($true)
        $windows=@($prepared.tasks | ForEach-Object { Read-ParallelJson (Join-Path $_.worktree '.parallel/stub-window.json') })
        Assert-Workflow (@($windows.pid | Sort-Object -Unique).Count -eq 3) 'Worker PIDs are not independent.'
        Assert-Workflow (@($windows | Where-Object { $null -ne $_.finishedAt }).Count -eq 0) 'Worker finished before all peers started.'
        foreach ($t in $prepared.tasks) { [IO.File]::WriteAllText((Join-Path $t.worktree '.parallel/release-worker'),'release') }
        Assert-Workflow ($p.WaitForExit(45000)) 'Scheduler did not finish within deadline.'
        Assert-Workflow ($p.ExitCode -eq 0) 'Scheduler failed after barrier release.'
    } finally {
        if (-not $p.HasExited) { $p.Kill($true); $p.WaitForExit() }
        [IO.File]::WriteAllText((Join-Path $f.root 'scheduler.log'),$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult())
        $p.Dispose()
    }
    $state=Read-WorkflowState $f
    $windows=@($state.tasks | ForEach-Object { Read-ParallelJson (Join-Path $_.worktree '.parallel/stub-window.json') })
    $latestStart=($windows.startedAt | ForEach-Object { [DateTime]$_ } | Sort-Object | Select-Object -Last 1)
    $earliestEnd=($windows.finishedAt | ForEach-Object { [DateTime]$_ } | Sort-Object | Select-Object -First 1)
    Assert-Workflow ($latestStart -lt $earliestEnd) 'Three worker time windows do not overlap.'
    Write-ParallelJson (Join-Path $f.root 'concurrency-evidence.json') @{windows=$windows;latestStart=$latestStart;earliestEnd=$earliestEnd}
    foreach ($t in $state.tasks) {
        Assert-Workflow ($t.status -eq 'completed') "Unexpected worker status $($t.status)"
        Assert-WorkflowEvidence $t
        $r=Read-ParallelJson (Join-Path $t.attemptPath 'result.json')
        Assert-Workflow ($r.taskId -eq $t.id -and $r.status -eq 'completed') 'Result not retained with task identity.'
        Assert-Workflow ((Get-ParallelHead $t.worktree) -eq $state.baseline) 'Worker committed changes.'
        Assert-ParallelTaskWorktree $state $t
    }
    $before=Get-FileHash -LiteralPath (Join-Path $f.runPath 'state.json')
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Get-ParallelStatus.ps1' @('-RunPath',$f.runPath,'-Json'))
    Assert-Workflow ((Get-FileHash -LiteralPath (Join-Path $f.runPath 'state.json')).Hash -eq $before.Hash) 'Status mutated state.'
    $script:SuccessfulFixture=$f
}
Invoke-WorkflowTest 'refresh completion evidence after joining a child with stale liveness observation' {
    $f=New-WorkflowFixture 'shutdown-observation'
    $localScripts=Join-Path $f.repo 'eng/parallel'
    $statusPath=Join-Path $localScripts 'Get-ParallelStatus.ps1'
    $statusText=[IO.File]::ReadAllText($statusPath)
    Assert-Workflow ($statusText.Contains('return ($null -ne $candidate)')) 'PID observation fixture anchor missing.'
    # Simulate a stale PID observation while retaining the real child handle.
    # Only fixture scripts are changed; no real CLI or network is used.
    [IO.File]::WriteAllText($statusPath,$statusText.Replace('return ($null -ne $candidate)','return $false'),[Text.UTF8Encoding]::new($false))
    Invoke-ParallelGit $f.repo @('add','eng/parallel/Get-ParallelStatus.ps1') | Out-Null
    Invoke-ParallelGit $f.repo @('commit','-m','fixture stale liveness observation') | Out-Null
    $f.plan.baseline=Get-ParallelHead $f.repo
    Write-ParallelJson $f.planPath $f.plan
    $stubPath=Join-Path $f.repo '.parallel/delayed-stub.ps1'
    [IO.Directory]::CreateDirectory((Split-Path $stubPath -Parent)) | Out-Null
    $stubText=[IO.File]::ReadAllText((Join-Path $script:WorkflowSource 'tests/Stub-Codex.ps1'))
    Assert-Workflow ($stubText.Contains('Write-StubWindow $null')) 'Delayed stub anchor missing.'
    [IO.File]::WriteAllText($stubPath,$stubText.Replace('Write-StubWindow $null',"Write-StubWindow `$null`nStart-Sleep -Milliseconds 1200"),[Text.UTF8Encoding]::new($false))
    $result=Invoke-WorkflowScript 'Invoke-ParallelDevelopment.ps1' @('-PlanPath',$f.planPath,'-RepoRoot',$f.repo,'-RunRoot',$f.runRoot,'-CodexPath',$stubPath,'-TimeoutSeconds','15') $f.repo 90 $localScripts
    $state=Read-WorkflowState $f
    foreach ($task in $state.tasks) {
        Assert-WorkflowEvidence $task
        $process=Read-ParallelJson (Join-Path $task.attemptPath 'process.json')
        Assert-Workflow ($process.status -eq 'exited' -and $process.exitCode -eq 0) 'Fixture child did not actually finish successfully.'
        Assert-Workflow ($task.status -eq 'completed' -and $task.attempt -eq 1) 'Stale observation replaced valid completion evidence or caused a restart.'
    }
    Assert-WorkflowSuccess $result
}
foreach ($mode in @('failed','process-failure','missing-result','invalid-result','wrong-id','blocked','timeout')) {
    $testMode=$mode
    Invoke-WorkflowTest "runtime rejects $testMode and retains checkout/logs" {
        $f=New-WorkflowFixture $testMode
        $f.plan.tasks[0].instructions="fixture:$testMode"
        Write-ParallelJson $f.planPath $f.plan
        $null=Start-WorkflowFixture $f -Timeout 3
        $state=Read-WorkflowState $f; $task=$state.tasks[0]
        $expected=if ($testMode -eq 'blocked') { @('blocked') } elseif ($testMode -eq 'timeout') { @('timed_out') } else { @('failed','blocked','needs_rework') }
        Assert-Workflow ($task.status -in $expected) "Bad status for ${testMode}: $($task.status)"
        Assert-WorkflowEvidence $task
        Assert-Workflow (Test-Path -LiteralPath (Join-Path $task.worktree 'outputs/alpha.txt')) 'Failure discarded worker edits.'
        Assert-Workflow ((Get-ParallelHead $task.worktree) -eq $state.baseline) 'Failure committed changes.'
        if ($testMode -eq 'blocked') {
            $r=Read-ParallelJson (Join-Path $task.attemptPath 'result.json')
            Assert-Workflow ($r.permissionRequests.Count -gt 0) 'Permission request evidence lost.'
            $script:BlockedFixture=$f
        }
    }
}
Invoke-WorkflowTest 'repair retains old evidence and changes in a new attempt' {
    Assert-Workflow ($null -ne (Get-Variable BlockedFixture -Scope Script -ErrorAction SilentlyContinue)) 'Blocked fixture unavailable.'
    $f=$script:BlockedFixture; $state=Read-WorkflowState $f; $old=$state.tasks[0]
    $oldPath=$old.attemptPath
    $hashes=@{}; Get-ChildItem -LiteralPath $oldPath -File | ForEach-Object { $hashes[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash }
    $output=Join-Path $old.worktree 'outputs/alpha.txt'; $before=Get-Content -LiteralPath $output -Raw
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Repair-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Instructions','Fixture rework: keep evidence'))
    $state=Read-WorkflowState $f
    Assert-Workflow ($state.tasks[0].status -eq 'pending') 'Repair did not requeue.'
    Assert-Workflow ((Get-Content -LiteralPath $output -Raw) -eq $before) 'Repair changed existing output.'
    $null=Invoke-WorkflowScript 'Invoke-ParallelDevelopment.ps1' @('-RunPath',$f.runPath,'-CodexPath',(Join-Path $script:WorkflowSource 'tests/Stub-Codex.ps1'),'-TimeoutSeconds','5') $f.repo
    $state=Read-WorkflowState $f; $new=$state.tasks[0]
    Assert-Workflow ($new.attempt -eq $old.attempt+1 -and $new.attemptPath -ne $oldPath) 'Repair reused an attempt.'
    foreach ($name in $hashes.Keys) { Assert-Workflow ((Get-FileHash -LiteralPath (Join-Path $oldPath $name)).Hash -eq $hashes[$name]) "Old evidence overwritten: $name" }
    Assert-WorkflowEvidence $new
}
