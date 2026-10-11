#requires -Version 7.2
param([Parameter(Mandatory)][string]$RunPath, [Parameter(Mandatory)][string]$MainWorktree)
. (Join-Path $PSScriptRoot 'Common.ps1')
$lock = $null; $failed = $false
$promotionReportPath = $null
$promotionReport = @{ status = 'failed'; error = ''; head = $null; mainWorktree = $MainWorktree; startedAt = [DateTime]::UtcNow.ToString('o') }
try {
    $RunPath = [IO.Path]::GetFullPath($RunPath); $lock = Enter-ParallelLock $RunPath
    $promotionReportPath = Join-Path $RunPath ('promotion-' + [guid]::NewGuid().ToString('N') + '.json')
    $statePath = Join-Path $RunPath 'state.json'; $state = Read-ParallelJson $statePath
    if ([IO.Path]::GetFullPath($state.runPath) -ne $RunPath) { throw 'State runPath identity mismatch.' }
    if ($state.status -ne 'validated' -or $state.integration.validationStatus -ne 'passed') { throw 'Integration has not passed repository validation.' }
    $integration = $state.integration
    $root = Assert-ParallelChildPath $RunPath $integration.worktree
    $sha = Get-ParallelHead $root
    $promotionReport.head = $sha
    if ($sha -notmatch '^[0-9a-f]{40}$' -or $integration.head -ne $sha -or $integration.validationHead -ne $sha -or
        (Invoke-ParallelGit $root @('symbolic-ref', '--short', 'HEAD')) -ne $integration.branch) { throw 'Integration HEAD/branch differs from the validated SHA.' }
    Assert-ParallelClean $root
    $reportPath = Assert-ParallelChildPath $root $integration.reportPath
    $report = Read-ParallelJson $reportPath
    if ($report.status -ne 'passed' -or $report.head -ne $sha -or $report.verificationHead -ne $sha -or
        [IO.Path]::GetFullPath($report.repoRoot) -ne $root -or @($report.errors).Count) { throw 'Repository report does not pass at the exact integration SHA/worktree.' }
    $expectedArguments = @{
        sdk = @('--list-sdks')
        restore = @('restore', 'ScreenTranslator.sln', '--configfile', 'NuGet.Config')
        build = @('build', 'ScreenTranslator.sln', '-c', 'Release', '--no-restore')
        'dotnet-tests' = @('test', 'ScreenTranslator.sln', '-c', 'Release', '--no-build', '--logger', 'trx', '--results-directory', (Join-Path (Split-Path $reportPath -Parent) 'trx'))
        'node-tests' = @('--test') + @(Get-ChildItem -LiteralPath (Join-Path $root 'browser-extension/tests') -Filter '*.test.js' -Recurse -File | Sort-Object FullName | ForEach-Object FullName)
        'workflow-tests' = @('-NoProfile', '-File', (Join-Path $root 'eng/parallel/Test-ParallelWorkflow.ps1'))
    }
    foreach ($name in @('sdk','restore','build','dotnet-tests','node-tests','workflow-tests')) {
        $records = @($report.commands | Where-Object name -eq $name)
        if ($records.Count -ne 1 -or $null -eq $records[0].exitCode -or $records[0].exitCode -ne 0 -or $records[0].timedOut) { throw "Missing or failed report command: $name" }
        $arguments = @($records[0].arguments)
        if (($arguments | ConvertTo-Json -Compress) -cne ($expectedArguments[$name] | ConvertTo-Json -Compress)) { throw "Report command arguments do not match the complete required gate: $name" }
        switch ($name) {
            'sdk' { if ($arguments -notcontains '--list-sdks') { throw 'SDK report command mismatch.' } }
            'restore' { if ($arguments[0] -ne 'restore' -or $arguments -notcontains 'ScreenTranslator.sln' -or $arguments -notcontains 'NuGet.Config') { throw 'Restore report command mismatch.' } }
            'build' { if ($arguments[0] -ne 'build' -or $arguments -notcontains 'ScreenTranslator.sln' -or $arguments -notcontains 'Release' -or $arguments -notcontains '--no-restore') { throw 'Build report command mismatch.' } }
            'dotnet-tests' { if ($arguments[0] -ne 'test' -or $arguments -notcontains 'ScreenTranslator.sln' -or $arguments -notcontains 'Release' -or $arguments -notcontains 'trx' -or $arguments -notcontains '--no-build') { throw '.NET tests report command mismatch.' } }
            'node-tests' {
                $tests = @(Get-ChildItem -LiteralPath (Join-Path $root 'browser-extension/tests') -Filter '*.test.js' -Recurse -File | ForEach-Object FullName)
                if (-not $tests.Count -or $arguments[0] -ne '--test') { throw 'Node tests report command mismatch.' }
                foreach ($test in $tests) { if ($arguments -notcontains $test) { throw "Node test missing from report: $test" } }
            }
            'workflow-tests' { if ($arguments -notcontains (Join-Path $root 'eng/parallel/Test-ParallelWorkflow.ps1') -or $arguments -contains '-SelfOnly') { throw 'Full workflow tests report command mismatch.' } }
        }
        if (-not (Test-Path -LiteralPath (Assert-ParallelChildPath $root $records[0].logPath) -PathType Leaf)) { throw "Validation log missing: $name" }
    }
    $manifest = Read-ParallelJson (Join-Path $root 'eng/assets.json')
    foreach ($asset in $manifest.assets) {
        $records = @($report.assets | Where-Object path -eq $asset.path)
        if ($records.Count -ne 1 -or $records[0].status -ne 'passed' -or $records[0].sha256 -ne $asset.sha256) { throw "Asset was not verified: $($asset.path)" }
    }
    foreach ($task in $state.tasks) {
        if ($task.status -ne 'integrated' -or $task.verifiedCommit -notmatch '^[0-9a-f]{40}$') { throw "Task not integrated: $($task.id)" }
        Invoke-ParallelGit $root @('merge-base', '--is-ancestor', $task.verifiedCommit, $sha) | Out-Null
        if ($task.ContainsKey('blockResolutionPath')) {
            $resolution = Read-ParallelJson (Assert-ParallelChildPath $RunPath $task.blockResolutionPath)
            if ($resolution.status -ne 'passed') { throw 'Controller block resolution no longer passes.' }
            if ($resolution.controllerRef) {
                Invoke-ParallelGit $root @('merge-base', '--is-ancestor', $resolution.controllerRef, $sha) | Out-Null
            }
        }
    }
    $MainWorktree = [IO.Path]::GetFullPath($MainWorktree)
    $actual = [IO.Path]::GetFullPath((Invoke-ParallelGit $MainWorktree @('rev-parse', '--show-toplevel')))
    if ($actual -ne $MainWorktree -or (Invoke-ParallelGit $MainWorktree @('rev-parse', '--path-format=absolute', '--git-common-dir')) -ne (Invoke-ParallelGit $root @('rev-parse', '--path-format=absolute', '--git-common-dir'))) { throw 'Main worktree is not the same repository.' }
    Assert-ParallelClean $MainWorktree
    if ((Invoke-ParallelGit $MainWorktree @('symbolic-ref', '--short', 'HEAD')) -ne $state.mainBranch) { throw 'Main worktree has the wrong branch.' }
    if ((Get-ParallelHead $MainWorktree) -ne $state.mainHead) { throw 'Main HEAD changed since run preparation.' }
    Invoke-ParallelGit $MainWorktree @('merge-base', '--is-ancestor', $state.mainHead, $sha) | Out-Null
    Invoke-ParallelGit $MainWorktree @('merge', '--ff-only', $sha) | Out-Null
    if ((Get-ParallelHead $MainWorktree) -ne $sha) { throw 'Promotion did not reach the exact validated SHA.' }
    $state.status = 'promoted'; $state.promotedHead = $sha; $state.promotedAt = [DateTime]::UtcNow.ToString('o')
    Write-ParallelJson $statePath $state
    $promotionReport.status = 'passed'
    Write-Output "Main promoted to $sha"
} catch { $failed = $true; $promotionReport.error = $_.Exception.Message; Write-Error $_.Exception.Message -ErrorAction Continue }
finally {
    if ($promotionReportPath) { $promotionReport.finishedAt = [DateTime]::UtcNow.ToString('o'); Write-ParallelJson $promotionReportPath $promotionReport }
    if ($lock) { $lock.Dispose() }
}
if ($failed) { exit 1 }
