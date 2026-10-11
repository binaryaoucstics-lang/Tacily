# Synthetic evidence for gate CONTROL FLOW only; no build or tests are run here.
function Write-WorkflowFixtureReport {
    param([Parameter(Mandatory)][string]$RepoRoot,[string]$ResultDirectory)
    if (-not $ResultDirectory) { $ResultDirectory=Join-Path $RepoRoot '.parallel/fixture-validation' }
    Assert-ParallelChildPath (Join-Path $RepoRoot '.parallel') $ResultDirectory | Out-Null
    [IO.Directory]::CreateDirectory($ResultDirectory) | Out-Null
    $definitions=@(
        @{name='sdk';filePath='dotnet';arguments=@('--list-sdks')}
        @{name='restore';filePath='dotnet';arguments=@('restore','ScreenTranslator.sln','--configfile','NuGet.Config')}
        @{name='build';filePath='dotnet';arguments=@('build','ScreenTranslator.sln','-c','Release','--no-restore')}
        @{name='dotnet-tests';filePath='dotnet';arguments=@('test','ScreenTranslator.sln','-c','Release','--no-build','--logger','trx','--results-directory',(Join-Path $ResultDirectory 'trx'))}
        @{name='node-tests';filePath='node';arguments=@('--test',(Join-Path $RepoRoot 'browser-extension/tests/smoke.test.js'))}
        @{name='workflow-tests';filePath='pwsh';arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'eng/parallel/Test-ParallelWorkflow.ps1'))}
    )
    $now=[DateTime]::UtcNow.ToString('o')
    $commands=@(foreach ($entry in $definitions) {
        $log=Join-Path $ResultDirectory ($entry.name+'.log')
        [IO.File]::WriteAllText($log,"SYNTHETIC fixture evidence: $($entry.name); not executed.`n")
        $entry + @{exitCode=0;timedOut=$false;logPath=$log;startedAt=$now;finishedAt=$now;status='passed';outcome='passed'}
    })
    $manifest=Read-ParallelJson (Join-Path $RepoRoot 'eng/assets.json')
    if (@($manifest.assets).Count -ne 0) { throw 'Synthetic fixture expects its committed empty asset manifest.' }
    $head=Get-ParallelHead $RepoRoot
    $reportPath=Join-Path $ResultDirectory 'report.json'
    Write-ParallelJson $reportPath @{schemaVersion=1;repoRoot=$RepoRoot;head=$head;validationHead=$head;verificationHead=$head;
        status='passed';success=$true;errors=@();assets=@();commands=$commands;startedAt=$now;finishedAt=$now;synthetic=$true}
    return $reportPath
}
