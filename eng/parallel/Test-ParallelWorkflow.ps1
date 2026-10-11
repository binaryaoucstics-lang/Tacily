#requires -Version 7.2
param([switch]$SelfOnly)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'tests/Workflow.Helpers.ps1')
$script:WorkflowSource = $PSScriptRoot
$script:WorkflowRoot = Join-Path (Split-Path $PSScriptRoot -Parent | Split-Path -Parent) ('.parallel/wf/测试 ' + [guid]::NewGuid().ToString('N').Substring(0,8))
[IO.Directory]::CreateDirectory($script:WorkflowRoot) | Out-Null
$script:WorkflowResults = [Collections.Generic.List[object]]::new()
try {
    . (Join-Path $PSScriptRoot 'tests/Common.Tests.ps1')
    if (-not $SelfOnly) {
        $required = @('Invoke-ParallelDevelopment.ps1','Get-ParallelStatus.ps1','Repair-ParallelTask.ps1',
            'Review-ParallelTask.ps1','Complete-ParallelDevelopment.ps1','Update-ParallelMain.ps1')
        Invoke-WorkflowTest 'production modules available' {
            foreach ($name in $required) { Assert-Workflow (Test-Path -LiteralPath (Join-Path $script:WorkflowSource $name)) "Missing production module: $name (use -SelfOnly during development)" }
        }
        if (@($script:WorkflowResults | Where-Object outcome -eq 'failed').Count -eq 0) {
            . (Join-Path $PSScriptRoot 'tests/Runtime.Tests.ps1')
            . (Join-Path $PSScriptRoot 'tests/Gates.Tests.ps1')
            . (Join-Path $PSScriptRoot 'tests/Resolution.Tests.ps1')
        }
    }
} catch {
    $script:WorkflowResults.Add(@{name='suite setup';outcome='failed';details=$_.ToString()})
} finally {
    $failed = @($script:WorkflowResults | Where-Object outcome -eq 'failed').Count
    Write-ParallelJson (Join-Path $script:WorkflowRoot 'summary.json') @{
        selfOnly=[bool]$SelfOnly; passed=$script:WorkflowResults.Count-$failed; failed=$failed
        tests=@($script:WorkflowResults.ToArray()); diagnostics=$script:WorkflowRoot
    }
    Write-Host "Workflow tests: $($script:WorkflowResults.Count-$failed) passed, $failed failed."
    Write-Host "Retained diagnostics: $script:WorkflowRoot"
}
if ($failed) { exit 1 }
exit 0
