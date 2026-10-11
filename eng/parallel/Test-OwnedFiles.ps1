#requires -Version 7.2
param([Parameter(Mandatory)][string]$PlanPath, [Parameter(Mandatory)][string]$TaskId)
. (Join-Path $PSScriptRoot 'Common.ps1')
$plan = Test-ParallelPlan (Read-ParallelJson $PlanPath)
$task = @($plan.tasks | Where-Object id -eq $TaskId)
if ($task.Count -ne 1) { throw 'Unknown task.' }
$repo = Invoke-ParallelGit (Get-Location).Path @('rev-parse', '--show-toplevel')
$count = 0
foreach ($owned in $task[0].ownedPaths) {
    $path = Assert-ParallelChildPath $repo (Join-Path $repo $owned)
    if (-not (Test-Path -LiteralPath $path)) { throw "Required output is missing: $owned" }
    $files = if (Test-Path -LiteralPath $path -PathType Container) { Get-ChildItem -LiteralPath $path -Recurse -File } else { Get-Item -LiteralPath $path }
    foreach ($file in $files) {
        if ($file.Extension -eq '.ps1') {
            $parseTokens = $null; $parseErrors = $null
            [Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$parseTokens, [ref]$parseErrors) | Out-Null
            if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
        } elseif ($file.Extension -eq '.json') { Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json | Out-Null }
        if ($file.Length -eq 0) { throw "Empty output: $($file.FullName)" }
        $count++
    }
}
if ($count -eq 0) { throw 'No task output files were verified.' }
Write-Output "Owned files verified: $count"
