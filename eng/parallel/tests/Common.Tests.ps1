Invoke-WorkflowTest 'valid plan and dependency graph' {
    $plan = New-WorkflowPlan
    $plan.tasks[2].dependsOn=@('alpha','beta')
    Assert-Workflow ((Test-ParallelPlan $plan).tasks.Count -eq 3) 'Valid plan rejected.'
}
Invoke-WorkflowTest 'plan count and duplicate IDs rejected' {
    foreach ($count in @(2,6)) {
        $plan=New-WorkflowPlan; $plan.tasks=@(1..$count | ForEach-Object { $plan.tasks[0] })
        Assert-WorkflowThrows { Test-ParallelPlan $plan } '3-5'
    }
    $plan=New-WorkflowPlan; $plan.tasks[1].id='alpha'
    Assert-WorkflowThrows { Test-ParallelPlan $plan } 'duplicate'
}
Invoke-WorkflowTest 'ownership overlap and directory boundaries' {
    foreach ($pair in @(@('Outputs/','outputs/alpha.txt'),@('outputs/alpha.txt','OUTPUTS/alpha.txt'),@('outputs/','outputs/sub/'))) {
        $plan=New-WorkflowPlan; $plan.tasks[0].ownedPaths=@($pair[0]); $plan.tasks[1].ownedPaths=@($pair[1])
        Assert-WorkflowThrows { Test-ParallelPlan $plan } 'Overlapping'
    }
    Assert-Workflow (Test-ParallelPathOwned '目录 space/file.txt' @('目录 space/')) 'Unicode directory ownership failed.'
    Assert-Workflow (-not (Test-ParallelPathOwned 'outputs-extra/file.txt' @('outputs/'))) 'Sibling prefix falsely owned.'
    Assert-Workflow (-not (Test-ParallelPathOwned 'outputs/a.txt.extra' @('outputs/a.txt'))) 'Exact file prefix falsely owned.'
}
Invoke-WorkflowTest 'escape and reserved ownership rejected' {
    foreach ($path in @('../outside','a/../outside','/absolute','C:/absolute','a/*.txt','.git/config','.parallel/result.json','.tools/sdk','a/./b')) {
        $plan=New-WorkflowPlan; $plan.tasks[0].ownedPaths=@($path)
        Assert-WorkflowThrows { Test-ParallelPlan $plan } 'Invalid owned'
    }
    Assert-WorkflowThrows { Assert-ParallelChildPath $script:WorkflowRoot $script:WorkflowRoot } 'outside'
    Assert-WorkflowThrows { Assert-ParallelChildPath $script:WorkflowRoot ($script:WorkflowRoot+'-sibling/file') } 'outside'
}
Invoke-WorkflowTest 'cycle unknown and self dependencies rejected' {
    $plan=New-WorkflowPlan; $plan.tasks[0].dependsOn=@('beta'); $plan.tasks[1].dependsOn=@('alpha')
    Assert-WorkflowThrows { Test-ParallelPlan $plan } 'cycle'
    foreach ($dependency in @('missing','alpha')) {
        $plan=New-WorkflowPlan; $plan.tasks[0].dependsOn=@($dependency)
        Assert-WorkflowThrows { Test-ParallelPlan $plan } 'Invalid dependency'
    }
}
Invoke-WorkflowTest 'JSON unicode roundtrip and exclusive controller lock' {
    $path=Join-Path $script:WorkflowRoot '空间 中文.json'
    Write-ParallelJson $path @{text='中文 space';values=@(1,2,3)}
    Assert-Workflow ((Read-ParallelJson $path).text -eq '中文 space') 'JSON encoding changed.'
    $lock=Enter-ParallelLock $script:WorkflowRoot
    try { Assert-WorkflowThrows { $other=Enter-ParallelLock $script:WorkflowRoot; $other.Dispose() } }
    finally { $lock.Dispose() }
    $lock=Enter-ParallelLock $script:WorkflowRoot; $lock.Dispose()
}
Invoke-WorkflowTest 'git fixture clean dirty and ownership HEAD guards' {
    $f=New-WorkflowFixture 'common-git'
    Assert-ParallelClean $f.repo
    $head=Get-ParallelHead $f.repo
    $run=Join-Path $f.root 'run'; [IO.Directory]::CreateDirectory($run) | Out-Null
    $wt=Join-Path $run 'worker 中文'
    Invoke-ParallelGit $f.repo @('worktree','add','-b','fixture-worker',$wt,$head) | Out-Null
    $task=@{id='alpha';worktree=$wt;branch='fixture-worker';expectedHead=$head;ownedPaths=@('outputs/')}
    $state=@{runPath=$run;baseline=$head}
    Assert-ParallelTaskWorktree $state $task
    [IO.Directory]::CreateDirectory((Join-Path $wt 'outputs')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $wt 'outputs/中文 file.txt'),'change')
    Assert-Workflow (@(Get-ParallelChangedFiles $wt $head) -contains 'outputs/中文 file.txt') 'Untracked unicode output omitted.'
    Assert-ParallelTaskWorktree $state $task
    Assert-WorkflowThrows { Assert-ParallelClean $wt } 'not clean'
    [IO.File]::WriteAllText((Join-Path $wt 'outside.txt'),'preserve')
    Assert-WorkflowThrows { Assert-ParallelTaskWorktree $state $task } 'Out-of-scope'
    $task.expectedHead='bad-head'
    Assert-WorkflowThrows { Assert-ParallelTaskWorktree $state $task } 'HEAD changed'
}
Invoke-WorkflowTest 'process literal argument transmission and finite timeout evidence' {
    $scriptPath=Join-Path $script:WorkflowRoot 'echo 中文.ps1'
    [IO.File]::WriteAllText($scriptPath,'param([string]$Value) [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); [Console]::Out.WriteLine($Value); [Console]::Error.WriteLine("stderr evidence")')
    $literal='space 中文 $() ; & literal'
    $r=Invoke-ParallelCommand $scriptPath @('-Value',$literal) $script:WorkflowRoot (Join-Path $script:WorkflowRoot 'echo.log') 10
    Assert-WorkflowSuccess $r
    $text=Get-Content -LiteralPath $r.logPath -Raw
    Assert-Workflow ($text.Contains($literal) -and $text.Contains('stderr evidence')) 'Arguments or stderr corrupted.'
    [IO.File]::WriteAllText($scriptPath,'[Console]::Out.WriteLine("before timeout"); Start-Sleep -Seconds 30')
    $r=Invoke-ParallelCommand $scriptPath @('unused') $script:WorkflowRoot (Join-Path $script:WorkflowRoot 'timeout.log') 2
    Assert-Workflow ($r.timedOut -and $r.exitCode -eq 124) 'Timeout was not enforced.'
    Assert-Workflow ((Get-Content -LiteralPath $r.logPath -Raw).Contains('before timeout')) 'Timeout evidence missing.'
}
foreach ($mode in @('success','blocked','timeout')) {
    $workerMode=$mode
    Invoke-WorkflowTest "immutable Start-CodexTask captures $workerMode evidence" {
        $f=New-WorkflowFixture "worker-$workerMode"
        $own=$f.repo; $local=Join-Path $own '.parallel'
        [IO.Directory]::CreateDirectory($local) | Out-Null
        Write-ParallelJson (Join-Path $local 'task.json') $f.plan.tasks[0]
        Copy-Item -LiteralPath (Join-Path $script:WorkflowSource 'result.schema.json') -Destination $local
        $prompt=Join-Path $local 'prompt.txt'
        [IO.File]::WriteAllText($prompt,"fixture:$workerMode 中文 stdin")
        $attempt=Join-Path $f.root 'attempt-1'
        $r=Invoke-WorkflowScript 'Start-CodexTask.ps1' @('-Worktree',$own,'-PromptPath',$prompt,'-AttemptPath',$attempt,
            '-CodexPath',(Join-Path $script:WorkflowSource 'tests/Stub-Codex.ps1'),'-TimeoutSeconds','3') $own 15
        $record=Read-ParallelJson (Join-Path $attempt 'process.json')
        Assert-WorkflowEvidence @{attemptPath=$attempt}
        Assert-Workflow (@($record.arguments) -contains 'sandbox_workspace_write.writable_roots=[]') 'Extra writable roots were not cleared.'
        Assert-Workflow (@($record.arguments) -contains 'sandbox_workspace_write.network_access=false') 'Network was not disabled.'
        Assert-Workflow (-not (@($record.arguments) -contains '--dangerously-bypass-approvals-and-sandbox')) 'Unsafe CLI argument.'
        Assert-Workflow ((Get-Content -LiteralPath (Join-Path $attempt 'events.jsonl') -Raw).Contains('fixture.started')) 'JSON event log missing.'
        Assert-Workflow ((Get-Content -LiteralPath (Join-Path $attempt 'stderr.log') -Raw).Contains('fixture mode=')) 'stderr missing.'
        Assert-Workflow (Test-Path -LiteralPath (Join-Path $own 'outputs/alpha.txt')) 'Worker output was lost.'
        if ($workerMode -eq 'timeout') {
            Assert-Workflow ($r.exitCode -eq 124 -and $record.exitCode -eq 124) 'Worker timeout exit/evidence incorrect.'
        } else {
            Assert-WorkflowSuccess $r
            $json=Get-Content -LiteralPath (Join-Path $local 'result.json') -Raw
            Assert-Workflow (Test-Json -Json $json -SchemaFile (Join-Path $local 'result.schema.json')) 'Stub output schema invalid.'
            $result=$json | ConvertFrom-Json -AsHashtable
            Assert-Workflow ($result.taskId -eq 'alpha') 'Worker result task identity incorrect.'
            if ($workerMode -eq 'blocked') { Assert-Workflow ($result.status -eq 'blocked' -and $result.permissionRequests.Count -eq 1) 'Permission denial missing.' }
        }
    }
}
