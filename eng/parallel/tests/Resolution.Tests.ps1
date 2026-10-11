function New-ResolutionFixture {
    param([string]$Name)
    $fixture = New-WorkflowFixture $Name
    $fixture.plan.tasks[0].instructions = 'fixture:blocked'
    Write-ParallelJson $fixture.planPath $fixture.plan
    $null = Start-WorkflowFixture $fixture
    return $fixture
}

Invoke-WorkflowTest 'controller resolution preserves blocked result and still requires normal review' {
    $f = New-ResolutionFixture 'resolve-block'
    $task = (Read-WorkflowState $f).tasks[0]
    $originalResult = (Get-FileHash -LiteralPath (Join-Path $task.attemptPath 'result.json')).Hash
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Resolve-ParallelTaskBlock.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-ResolutionNotes','Fixture supervisor resolved validation in its authorized environment.'))
    $state = Read-WorkflowState $f
    Assert-Workflow ($state.tasks[0].status -eq 'validation_resolved' -and -not $state.tasks[0].verifiedCommit) 'Resolution committed or skipped review.'
    Assert-Workflow ((Get-FileHash -LiteralPath (Join-Path $task.attemptPath 'result.json')).Hash -eq $originalResult) 'Original blocked result changed.'
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve'))
    Assert-Workflow ((Read-WorkflowState $f).tasks[0].status -eq 'reviewed') 'Resolved task was not normally reviewed.'
}

Invoke-WorkflowTest 'resolved validation can be repaired without reusing or deleting its proof' {
    $f = New-ResolutionFixture 'resolve-then-repair'; $task = (Read-WorkflowState $f).tasks[0]
    $resultPath = Join-Path $task.attemptPath 'result.json'
    $originalResult = (Get-FileHash -LiteralPath $resultPath).Hash
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Resolve-ParallelTaskBlock.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-ResolutionNotes','Original acceptance passed; code still requires review.'))
    $resolved = (Read-WorkflowState $f).tasks[0]
    $proofPath = $resolved.blockResolutionPath
    $proofHash = (Get-FileHash -LiteralPath $proofPath).Hash
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Repair-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Instructions','Fix independently reviewed code defect.'))
    $requeued = (Read-WorkflowState $f).tasks[0]
    Assert-Workflow ($requeued.status -eq 'pending' -and -not $requeued.ContainsKey('blockResolutionPath')) 'Repair reused the old resolution.'
    Assert-Workflow ($requeued.repairs[-1].previousStatus -eq 'validation_resolved' -and $requeued.repairs[-1].previousBlockResolutionPath -eq $proofPath) 'Resolution history was lost.'
    Assert-Workflow ((Get-FileHash -LiteralPath $proofPath).Hash -eq $proofHash -and (Get-FileHash -LiteralPath $resultPath).Hash -eq $originalResult) 'Original resolution or blocked evidence changed.'
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq (Read-WorkflowState $f).baseline) 'Repair committed task code.'
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve')) 'status=pending'
}

Invoke-WorkflowTest 'failed controller validation retains block and cannot commit' {
    $f = New-ResolutionFixture 'resolve-failing-validation'; $task = (Read-WorkflowState $f).tasks[0]
    [IO.File]::WriteAllText((Join-Path $task.worktree 'outputs/alpha.txt'),'validation-failure')
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Resolve-ParallelTaskBlock.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-ResolutionNotes','Attempt validation.')) 'validation.*failed'
    Assert-Workflow ((Read-WorkflowState $f).tasks[0].status -eq 'blocked') 'Failed resolution removed block.'
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq (Read-WorkflowState $f).baseline) 'Failed resolution committed.'
}

Invoke-WorkflowTest 'source changes after resolution invalidate supervisor proof' {
    $f = New-ResolutionFixture 'resolve-stale'; $task = (Read-WorkflowState $f).tasks[0]
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Resolve-ParallelTaskBlock.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-ResolutionNotes','Fixture validation completed.'))
    [IO.File]::AppendAllText((Join-Path $task.worktree 'outputs/alpha.txt'),' later source change')
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve')) 'resolution.*(stale|match)'
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq (Read-WorkflowState $f).baseline) 'Stale resolution committed.'
}

Invoke-WorkflowTest 'controller resolution rejects fixes that overlap worker ownership' {
    $f = New-ResolutionFixture 'resolve-overlap'
    [IO.Directory]::CreateDirectory((Join-Path $f.repo 'outputs')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $f.repo 'outputs/alpha.txt'),'controller replacement')
    Invoke-ParallelGit $f.repo @('add','outputs/alpha.txt') | Out-Null
    Invoke-ParallelGit $f.repo @('commit','-m','overlapping controller change') | Out-Null
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Resolve-ParallelTaskBlock.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-ResolutionNotes','Attempt overlapping fix.','-ControllerRef',(Get-ParallelHead $f.repo))) 'overlap'
    Assert-Workflow ((Read-WorkflowState $f).tasks[0].status -eq 'blocked') 'Ownership conflict was resolved.'
}

Invoke-WorkflowTest 'main promotion requires a resolved controller fix to be integrated' {
    $f = New-WorkflowPromotionFixture 'resolution-controller-promotion'; $state = Read-WorkflowState $f
    $controller = Join-Path $f.root 'controller'
    Invoke-ParallelGit $f.repo @('worktree','add','-b','codex/controller-fix',$controller,$state.baseline) | Out-Null
    [IO.File]::WriteAllText((Join-Path $controller 'controller-fix.txt'),'verified shared repair')
    Invoke-ParallelGit $controller @('add','controller-fix.txt') | Out-Null
    Invoke-ParallelGit $controller @('commit','-m','shared controller repair') | Out-Null
    $reportPath = Join-Path $state.tasks[0].attemptPath 'controller-resolution.json'
    Write-ParallelJson $reportPath @{status='passed';controllerRef=(Get-ParallelHead $controller)}
    $state.tasks[0].blockResolutionPath = $reportPath; Save-WorkflowState $f $state
    $before = Get-ParallelHead $f.repo
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Update-ParallelMain.ps1' @('-RunPath',$f.runPath,'-MainWorktree',$f.repo)) 'merge-base.*ancestor'
    Assert-Workflow ((Get-ParallelHead $f.repo) -eq $before) 'Main promoted without the referenced controller repair.'
}
