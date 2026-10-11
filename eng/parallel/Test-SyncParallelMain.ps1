#requires -Version 7.2
# Isolated Git regression for controller replanning. No Codex, network or user repo writes.
. (Join-Path $PSScriptRoot 'Common.ps1')
$fixtureRoot = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) ('.parallel/sync-test-' + [guid]::NewGuid().ToString('N').Substring(0,8))
$repo = Join-Path $fixtureRoot 'main 中文'
$run = Join-Path $fixtureRoot 'run'
[IO.Directory]::CreateDirectory($repo) | Out-Null
[IO.Directory]::CreateDirectory($run) | Out-Null
function Assert-SyncTest([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Invoke-SyncTest {
    param([string]$Name)
    return Invoke-ParallelCommand 'pwsh' @('-NoProfile', '-File', (Join-Path $PSScriptRoot 'Sync-ParallelMain.ps1'),
        '-RunPath', $run, '-MainWorktree', $repo) $repo (Join-Path $fixtureRoot "$Name.log") 30
}
Invoke-ParallelGit $repo @('init', '-b', 'main') | Out-Null
Invoke-ParallelGit $repo @('config', 'core.longpaths', 'true') | Out-Null
Invoke-ParallelGit $repo @('config', 'user.name', 'Replanning Fixture') | Out-Null
Invoke-ParallelGit $repo @('config', 'user.email', 'fixture@example.invalid') | Out-Null
Invoke-ParallelGit $repo @('config', 'commit.gpgsign', 'false') | Out-Null
[IO.File]::WriteAllText((Join-Path $repo 'base.txt'), 'fixture baseline')
Invoke-ParallelGit $repo @('add', '--', 'base.txt') | Out-Null
Invoke-ParallelGit $repo @('commit', '-m', 'fixture baseline') | Out-Null
$baseline = Get-ParallelHead $repo
$state = @{ schemaVersion=1;runId='sync-fixture';repoRoot=$repo;runPath=$run;baseline=$baseline;
    mainBranch='main';mainHead=$baseline;status='awaiting_review';createdAt=[DateTime]::UtcNow.ToString('o');tasks=@();
    integration=@{branch='codex/integration/sync-fixture';worktree=(Join-Path $run 'integration');status='pending';
        head=$null;validationHead='old-verification';validationStatus='passed';reportPath='old-report'} }
foreach ($id in @('alpha','beta','gamma')) {
    $root = Join-Path $run $id
    $branch = "codex/parallel/sync-fixture/$id"
    Invoke-ParallelGit $repo @('worktree', 'add', '-b', $branch, $root, $baseline) | Out-Null
    [IO.File]::WriteAllText((Join-Path $root "$id.txt"), "verified $id")
    Invoke-ParallelGit $root @('add', '--', "$id.txt") | Out-Null
    Invoke-ParallelGit $root @('commit', '-m', "fixture $id") | Out-Null
    $sha = Get-ParallelHead $root
    $state.tasks += @(@{id=$id;worktree=$root;branch=$branch;expectedHead=$sha;verifiedCommit=$sha;
        status='reviewed';ownedPaths=@("$id.txt")})
}
Write-ParallelJson (Join-Path $run 'state.json') $state
[IO.File]::WriteAllText((Join-Path $repo 'advanced-main.txt'), 'preserve advanced main')
Invoke-ParallelGit $repo @('add', '--', 'advanced-main.txt') | Out-Null
Invoke-ParallelGit $repo @('commit', '-m', 'advance main during development') | Out-Null
$advanced = Get-ParallelHead $repo
$dirtyFile = Assert-ParallelChildPath $fixtureRoot (Join-Path $repo 'user changes.txt')
[IO.File]::WriteAllText($dirtyFile, 'preserve dirty main')
$result = Invoke-SyncTest 'dirty-main'
Assert-SyncTest ($result.exitCode -ne 0) 'Replanning accepted a dirty main.'
Assert-SyncTest ((Get-ParallelHead $repo) -eq $advanced -and (Get-Content -LiteralPath $dirtyFile -Raw) -eq 'preserve dirty main') 'Dirty main was modified.'
Assert-SyncTest (-not (Test-Path -LiteralPath $state.integration.worktree)) 'Dirty main unexpectedly created integration.'
Remove-Item -LiteralPath $dirtyFile
$result = Invoke-SyncTest 'advanced-main'
Assert-SyncTest ($result.exitCode -eq 0) "Replanning failed: $(Get-Content -LiteralPath $result.logPath -Raw)"
$after = Read-ParallelJson (Join-Path $run 'state.json')
Assert-SyncTest ($after.mainHead -eq $advanced -and (Get-ParallelHead $repo) -eq $advanced) 'Main pin or checkout changed incorrectly.'
Assert-SyncTest ($after.integration.validationStatus -eq 'pending' -and $null -eq $after.integration.validationHead -and $null -eq $after.integration.reportPath) 'Old verification survived a replan.'
Assert-SyncTest (@($after.reconciliations).Count -eq 1 -and $after.reconciliations[0].previousMainHead -eq $baseline) 'Replanning audit is missing.'
Invoke-ParallelGit $after.integration.worktree @('merge-base', '--is-ancestor', $advanced, 'HEAD') | Out-Null
Assert-SyncTest ((Get-Content -LiteralPath (Join-Path $after.integration.worktree 'advanced-main.txt') -Raw) -eq 'preserve advanced main') 'Integration lost advanced main content.'
foreach ($task in $after.tasks) { Assert-ParallelTaskWorktree $after $task; Assert-ParallelClean $task.worktree }
[IO.File]::WriteAllText((Join-Path $after.integration.worktree 'unexpected.txt'), 'retained integration change')
Invoke-ParallelGit $after.integration.worktree @('add', '--', 'unexpected.txt') | Out-Null
Invoke-ParallelGit $after.integration.worktree @('commit', '-m', 'integration drift fixture') | Out-Null
$result = Invoke-SyncTest 'integration-drift'
Assert-SyncTest ($result.exitCode -ne 0) 'Replanning accepted integration HEAD drift.'
Assert-SyncTest ((Get-ParallelHead $repo) -eq $advanced) 'Rejected integration drift moved main.'
Write-Output "Replanning tests: 3 passed. Retained diagnostics: $fixtureRoot"
