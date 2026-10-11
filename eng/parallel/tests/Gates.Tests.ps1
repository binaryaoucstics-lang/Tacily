function Assert-WorkflowRejected {
    param($Result,[string]$Reason='.')
    Assert-Workflow ($Result.exitCode -ne 0 -and -not $Result.timedOut) 'Unsafe gate succeeded or hung.'
    $log=Get-Content -LiteralPath $Result.logPath -Raw
    Assert-Workflow ($log -match $Reason) "Gate rejected for an unexpected reason: $log"
}
Invoke-WorkflowTest 'review without approval validates but never commits' {
    Assert-Workflow ($null -ne (Get-Variable SuccessfulFixture -Scope Script -ErrorAction SilentlyContinue)) 'Successful runtime fixture unavailable.'
    $f=$script:SuccessfulFixture; $state=Read-WorkflowState $f; $task=$state.tasks[0]
    $before=Get-ParallelHead $task.worktree
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha'))
    $state=Read-WorkflowState $f
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq $before -and $null -eq $state.tasks[0].verifiedCommit) 'Review without approval committed.'
    Assert-Workflow (Test-Path -LiteralPath (Join-Path $task.worktree 'outputs/alpha.txt')) 'Review discarded edits.'
    Assert-Workflow (@(Get-ChildItem -LiteralPath (Join-Path $f.runPath 'tasks/alpha') -Recurse -File | Where-Object { $_.Name -match 'review|diff' }).Count -gt 0) 'No real diff/review artifact retained.'
}
Invoke-WorkflowTest 'validation failure prevents approved commit' {
    $f=New-WorkflowFixture 'validation-failure'; $f.plan.tasks[0].instructions='fixture:validation-failure'
    Write-ParallelJson $f.planPath $f.plan
    Assert-WorkflowSuccess (Start-WorkflowFixture $f)
    $state=Read-WorkflowState $f; $task=$state.tasks[0]; $head=Get-ParallelHead $task.worktree
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve')) 'validat|acceptance'
    $state=Read-WorkflowState $f
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq $head -and $null -eq $state.tasks[0].verifiedCommit) 'Failed validation committed.'
    Assert-Workflow (Test-Path -LiteralPath (Join-Path $task.worktree 'outputs/alpha.txt')) 'Failed validation discarded edits.'
}
Invoke-WorkflowTest 'permission blocked result cannot be approved' {
    $f=New-WorkflowFixture 'review-permission'; $f.plan.tasks[0].instructions='fixture:blocked'
    Write-ParallelJson $f.planPath $f.plan; $null=Start-WorkflowFixture $f
    $state=Read-WorkflowState $f; $task=$state.tasks[0]; $head=Get-ParallelHead $task.worktree
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve')) 'blocked|permission|complet|status'
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq $head) 'Permission blocked task committed.'
    Assert-Workflow ((Read-ParallelJson (Join-Path $task.attemptPath 'result.json')).permissionRequests.Count -gt 0) 'Approval denial evidence lost.'
}
Invoke-WorkflowTest 'out-of-ownership edit cannot be approved' {
    $f=New-WorkflowFixture 'review-ownership'; Assert-WorkflowSuccess (Start-WorkflowFixture $f)
    $state=Read-WorkflowState $f; $task=$state.tasks[0]
    [IO.File]::WriteAllText((Join-Path $task.worktree 'unowned.txt'),'preserve this')
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve')) 'scope|own'
    Assert-Workflow ((Get-ParallelHead $task.worktree) -eq $state.baseline) 'Unowned edit committed.'
    Assert-Workflow (Test-Path -LiteralPath (Join-Path $task.worktree 'unowned.txt')) 'Unowned edit discarded.'
}
Invoke-WorkflowTest 'approved review creates only verified task commit; complete rejects unreviewed peers' {
    $f=$script:SuccessfulFixture; $state=Read-WorkflowState $f; $main=Get-ParallelHead $f.repo
    $untracked=Invoke-ParallelGit $state.tasks[0].worktree @('ls-files','--others','--exclude-standard')
    Assert-Workflow ($untracked -match 'outputs/alpha.txt') 'Positive review fixture must start with a new untracked output.'
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId','alpha','-Approve'))
    $state=Read-WorkflowState $f; $task=$state.tasks[0]
    Assert-Workflow ($task.status -eq 'reviewed' -and $task.verifiedCommit -eq (Get-ParallelHead $task.worktree)) 'Verified commit not recorded.'
    Assert-Workflow ($task.expectedHead -eq $task.verifiedCommit -and $task.verifiedCommit -ne $state.baseline) 'Expected HEAD not advanced.'
    $files=@((Invoke-ParallelGit $task.worktree @('diff','--name-only',$state.baseline,'HEAD')) -split "`r?`n" | Where-Object { $_ })
    Assert-Workflow ($files.Count -eq 1 -and $files[0] -eq 'outputs/alpha.txt') 'Review committed unrelated files.'
    Assert-ParallelClean $task.worktree
    Assert-Workflow ((Invoke-ParallelGit $task.worktree @('show','HEAD:outputs/alpha.txt')) -match 'retained output') 'Approved commit did not preserve the actual output.'
    Assert-WorkflowRejected (Invoke-WorkflowScript 'Complete-ParallelDevelopment.ps1' @('-RunPath',$f.runPath)) 'review'
    Assert-Workflow ((Get-ParallelHead $f.repo) -eq $main) 'Complete moved main.'
}

function New-WorkflowPromotionFixture {
    param([string]$Name)
    $f=New-WorkflowFixture $Name -StubRepository
    Assert-WorkflowSuccess (Start-WorkflowFixture $f)
    $state=Read-WorkflowState $f
    Assert-Workflow (-not (Test-Path -LiteralPath $state.integration.worktree)) 'Runtime should leave integration creation to Complete.'
    foreach ($t in $state.tasks) {
        Assert-WorkflowSuccess (Invoke-WorkflowScript 'Review-ParallelTask.ps1' @('-RunPath',$f.runPath,'-TaskId',$t.id,'-Approve'))
    }
    # Invoke the REAL Complete copied into the temporary repository, with ONLY
    # Test-Repository replaced before baseline creation. Both script-relative
    # and integration-relative validation paths therefore use the fixture stub.
    $fixtureScripts=Join-Path $f.repo 'eng/parallel'
    $stub=Join-Path $fixtureScripts 'Test-Repository.ps1'
    Assert-Workflow ((Get-FileHash -LiteralPath $stub).Hash -eq (Get-FileHash -LiteralPath (Join-Path $script:WorkflowSource 'tests/Stub-Repository.ps1')).Hash) 'Fixture validation stub was not installed.'
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Complete-ParallelDevelopment.ps1' @('-RunPath',$f.runPath) $f.repo 90 -ScriptRoot $fixtureScripts)
    $state=Read-WorkflowState $f
    $report=Read-ParallelJson $state.integration.reportPath
    Assert-Workflow ($report.synthetic -eq $true) 'Complete must use only synthetic fixture validation.'
    Assert-ParallelChildPath (Join-Path $state.integration.worktree '.parallel') $state.integration.reportPath | Out-Null
    Assert-Workflow ($report.head -eq (Get-ParallelHead $state.integration.worktree) -and $report.errors.Count -eq 0) 'Fixture report HEAD/errors invalid.'
    Assert-Workflow ($report.commands.Count -eq 6) 'Synthetic report must contain all six required commands.'
    foreach ($command in $report.commands) {
        Assert-ParallelChildPath (Join-Path $state.integration.worktree '.parallel') $command.logPath | Out-Null
        Assert-Workflow (Test-Path -LiteralPath $command.logPath -PathType Leaf) 'Synthetic command log missing.'
        Assert-Workflow ($command.exitCode -eq 0 -and -not $command.timedOut) 'Synthetic command result invalid.'
    }
    return $f
}
Invoke-WorkflowTest 'approved tasks integrate sequentially and clean main promotes with complete fixture evidence' {
    $f=New-WorkflowPromotionFixture 'sequential-integration'; $state=Read-WorkflowState $f
    $integration=$state.integration.worktree; $head=Get-ParallelHead $integration
    Assert-Workflow ((Get-ParallelHead $f.repo) -eq $state.mainHead) 'Complete moved main before promotion.'
    Assert-Workflow ($state.integration.validationHead -eq $head -and $state.integration.validationStatus -eq 'passed') 'Complete did not record validation.'
    $merges=@((Invoke-ParallelGit $integration @('rev-list','--first-parent','--merges',"$($state.baseline)..HEAD")) -split "`r?`n" | Where-Object { $_ })
    Assert-Workflow ($merges.Count -eq 3) 'Complete must create three sequential task merges.'
    foreach ($t in $state.tasks) {
        Assert-Workflow ($t.status -eq 'integrated' -and $t.verifiedCommit -ne $state.baseline) 'Task was not genuinely reviewed/integrated.'
        Invoke-ParallelGit $integration @('merge-base','--is-ancestor',$t.verifiedCommit,$head) | Out-Null
        $parents=(Invoke-ParallelGit $integration @('rev-list','--first-parent','--parents',"$($state.baseline)..HEAD")) -split "`r?`n"
        Assert-Workflow (@($parents | Where-Object { ($_ -split ' ')[-1] -eq $t.verifiedCommit }).Count -eq 1) 'Task SHA was not merged exactly once.'
        Assert-Workflow ((Get-Content -LiteralPath (Join-Path $integration "outputs/$($t.id).txt") -Raw) -match 'retained output') 'Integration lost approved output.'
    }
    Assert-WorkflowSuccess (Invoke-WorkflowScript 'Update-ParallelMain.ps1' @('-RunPath',$f.runPath,'-MainWorktree',$f.repo))
    Assert-Workflow ((Get-ParallelHead $f.repo) -eq $head) 'Successful promotion did not fast-forward to the validated integration SHA.'
    Assert-ParallelClean $f.repo
}
foreach ($case in @('dirty-main','moved-main','changed-integration-head')) {
    $promotionCase=$case
    Invoke-WorkflowTest "promotion rejects $promotionCase without moving main" {
        $f=New-WorkflowPromotionFixture $promotionCase; $state=Read-WorkflowState $f
        switch ($promotionCase) {
            'dirty-main' { [IO.File]::WriteAllText((Join-Path $f.repo 'user changes.txt'),'preserve user edit') }
            'moved-main' {
                [IO.File]::WriteAllText((Join-Path $f.repo 'main-moved.txt'),'new main history')
                Invoke-ParallelGit $f.repo @('add','main-moved.txt') | Out-Null
                Invoke-ParallelGit $f.repo @('commit','-m','main moved after preparation') | Out-Null
            }
            'changed-integration-head' {
                [IO.File]::WriteAllText((Join-Path $state.integration.worktree 'later.txt'),'after validation')
                Invoke-ParallelGit $state.integration.worktree @('add','later.txt') | Out-Null
                Invoke-ParallelGit $state.integration.worktree @('commit','-m','integration changed after validation') | Out-Null
            }
        }
        $before=Get-ParallelHead $f.repo
        $reason=switch ($promotionCase) { 'dirty-main' {'clean|dirty|existing changes'} 'moved-main' {'main.*(HEAD|mov|chang)|HEAD.*(main|chang)|mainHead'} default {'integration.*(HEAD|chang)|HEAD.*(validat|chang)|validation.*HEAD'} }
        Assert-WorkflowRejected (Invoke-WorkflowScript 'Update-ParallelMain.ps1' @('-RunPath',$f.runPath,'-MainWorktree',$f.repo)) $reason
        Assert-Workflow ((Get-ParallelHead $f.repo) -eq $before) 'Rejected promotion moved main.'
        if ($promotionCase -eq 'dirty-main') { Assert-Workflow ((Get-Content -LiteralPath (Join-Path $f.repo 'user changes.txt') -Raw) -eq 'preserve user edit') 'Dirty main edit lost.' }
    }
}
