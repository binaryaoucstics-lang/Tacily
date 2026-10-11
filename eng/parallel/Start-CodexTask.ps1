#requires -Version 7.2
param(
    [Parameter(Mandatory)][string]$Worktree,
    [Parameter(Mandatory)][string]$PromptPath,
    [Parameter(Mandatory)][string]$AttemptPath,
    [string]$CodexPath = 'codex',
    [int]$TimeoutSeconds = 1800
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$resultPath = Join-Path $Worktree '.parallel/result.json'
$processPath = Join-Path $AttemptPath 'process.json'
$process = $null; $outputStream = $null; $errorStream = $null
$exitCode = 1
$record = @{ status = 'starting'; workerPid = $PID; codexPid = $null; exitCode = $null;
    model = 'gpt-6.1-sol'; reasoningEffort = 'high'; serviceTier = 'fast'; requestServiceTier = 'priority';
    startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null; error = ''; arguments = @(); resultPath = $resultPath }
try {
    Assert-ParallelChildPath $Worktree $PromptPath | Out-Null
    [IO.Directory]::CreateDirectory($AttemptPath) | Out-Null
    # The output directory is prepared by the controller; only the assigned checkout is writable to the model.
    $arguments = @('--no-daemon', '--enable', 'prefer_mxc', '--ask-for-approval', 'on-request', 'exec', '--model', 'gpt-6.1-sol',
        '-c', 'model_reasoning_effort="high"', '-c', 'service_tier="fast"', '--sandbox', 'workspace-write',
        '-c', 'sandbox_workspace_write.writable_roots=[]', '-c', 'sandbox_workspace_write.network_access=false',
        '--cd', $Worktree, '--json', '--color', 'never', '--output-schema',
        (Join-Path $Worktree '.parallel/result.schema.json'), '--output-last-message', $resultPath, '-')
    $record.arguments = $arguments
    Write-ParallelJson $processPath $record
    $info = New-ParallelProcessInfo $CodexPath $arguments $Worktree
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $info
    $process.Start() | Out-Null
    $record.codexPid = $process.Id; $record.status = 'running'; Write-ParallelJson $processPath $record
    $outputStream = [IO.FileStream]::new((Join-Path $AttemptPath 'events.jsonl'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $errorStream = [IO.FileStream]::new((Join-Path $AttemptPath 'stderr.log'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::ReadWrite)
    $stdout = $process.StandardOutput.BaseStream.CopyToAsync($outputStream)
    $stderr = $process.StandardError.BaseStream.CopyToAsync($errorStream)
    $process.StandardInput.Write([IO.File]::ReadAllText($PromptPath, [Text.Encoding]::UTF8)); $process.StandardInput.Close()
    if (-not $process.WaitForExit($TimeoutSeconds * 1000)) {
        $process.Kill($true); $process.WaitForExit(); $exitCode = 124; $record.error = 'Codex task timed out; checkout preserved.'
    } else { $exitCode = $process.ExitCode }
    $stdout.GetAwaiter().GetResult(); $stderr.GetAwaiter().GetResult()
    $record.status = if ($exitCode -eq 0) { 'exited' } else { 'failed' }
} catch {
    $record.status = 'failed'; $record.error = $_.Exception.Message
    if ($process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
} finally {
    if ($outputStream) { $outputStream.Dispose() }; if ($errorStream) { $errorStream.Dispose() }
    if ($process) { $process.Dispose() }
    $record.exitCode = $exitCode; $record.finishedAt = [DateTime]::UtcNow.ToString('o')
    Write-ParallelJson $processPath $record
}
exit $exitCode
