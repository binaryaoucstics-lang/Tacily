#requires -Version 7.2
param([Parameter(Mandatory)][string]$RunPath, [switch]$Json)
. (Join-Path $PSScriptRoot 'Common.ps1')

function Test-ParallelPidAlive {
    param($ProcessId)
    if ($null -eq $ProcessId -or [int]$ProcessId -le 0) { return $false }
    $candidate = Get-Process -Id ([int]$ProcessId) -ErrorAction SilentlyContinue
    return ($null -ne $candidate)
}

function Get-ParallelRuntimeSnapshot {
    param([hashtable]$State, [hashtable]$Task)
    $metadata = $null; $result = $null; $readError = ''
    if ($Task.attemptPath) {
        $attempt = Assert-ParallelChildPath $State.runPath $Task.attemptPath
        $processFile = Join-Path $attempt 'process.json'
        if (Test-Path -LiteralPath $processFile) {
            try { $metadata = Read-ParallelJson $processFile } catch { $readError = "Unreadable process metadata: $($_.Exception.Message)" }
        }
        $resultFile = Join-Path $attempt 'result.json'
        if (-not (Test-Path -LiteralPath $resultFile) -and $Task.status -eq 'running') {
            $resultFile = Join-Path (Assert-ParallelChildPath $State.runPath $Task.worktree) '.parallel/result.json'
        }
        if (Test-Path -LiteralPath $resultFile) {
            try {
                $resultText = [IO.File]::ReadAllText($resultFile, [Text.Encoding]::UTF8)
                if (-not (Test-Json -Json $resultText -SchemaFile (Join-Path $PSScriptRoot 'result.schema.json') -ErrorAction Stop)) {
                    throw 'CLI result does not satisfy result.schema.json.'
                }
                $result = $resultText | ConvertFrom-Json -AsHashtable
            } catch { $readError += " Invalid CLI result: $($_.Exception.Message)" }
        }
    }
    $workerAlive = Test-ParallelPidAlive $Task.workerPid
    $codexAlive = $false
    if ($metadata) {
        if ($metadata.ContainsKey('workerPid')) { $workerAlive = $workerAlive -or (Test-ParallelPidAlive $metadata.workerPid) }
        if ($metadata.ContainsKey('codexPid')) { $codexAlive = Test-ParallelPidAlive $metadata.codexPid }
    }
    $status = $Task.status; $errorText = $Task.error
    if ($Task.status -eq 'running' -and -not $workerAlive -and -not $codexAlive) {
        $status = 'failed'; $errorText = 'Worker disappeared without successful CLI completion; checkout and logs retained.'
        if ($metadata -and $metadata.ContainsKey('exitCode') -and $null -ne $metadata.exitCode) {
            if ($metadata.exitCode -eq 124) { $status = 'timed_out'; $errorText = 'CLI timed out; checkout and logs retained.' }
            elseif ($metadata.exitCode -eq 0 -and $metadata.ContainsKey('status') -and $metadata.status -eq 'exited') {
                $valid = $null -ne $result
                foreach ($key in @('taskId','status','summary','changedFiles','tests','risks','permissionRequests')) {
                    if (-not $result -or -not $result.ContainsKey($key)) { $valid = $false }
                }
                if ($valid -and $result.taskId -eq $Task.id -and $result.status -in @('completed','failed','blocked')) {
                    $status = $result.status; $errorText = if ($status -eq 'completed') { '' } else { [string]$result.summary }
                    if (@($result.permissionRequests).Count -gt 0) { $status = 'blocked'; $errorText = 'CLI requires permission: ' + ($result.permissionRequests -join '; ') }
                } else { $errorText = 'Missing, invalid or mismatched CLI result; completion is not assumed.' }
            } else {
                $errorText = "CLI exited with code $($metadata.exitCode)."
                if ($metadata.ContainsKey('error') -and $metadata.error) { $errorText += ' ' + $metadata.error }
                # Approval errors remain visible even when the CLI cannot emit its schema result.
                $stderrFile = Join-Path $Task.attemptPath 'stderr.log'
                if (Test-Path -LiteralPath $stderrFile) {
                    $stderrText = [IO.File]::ReadAllText($stderrFile)
                    if ($stderrText) { $errorText += " See stderr.log: $stderrText" }
                    if ($stderrText -match '(?i)approval|permission denied|access is denied|sandbox.*(fail|denied)|unauthorized') {
                        $status = 'blocked'
                    }
                }
                if ($result -and $result.ContainsKey('permissionRequests') -and @($result.permissionRequests).Count -gt 0) {
                    $status = 'blocked'; $errorText += ' ' + ($result.permissionRequests -join '; ')
                }
                if ($result -and $result.taskId -eq $Task.id -and $result.status -eq 'blocked') {
                    $status = 'blocked'; $errorText += ' ' + $result.summary
                }
            }
        }
        if (-not $metadata -and $Task.ContainsKey('launchState') -and $Task.launchState -eq 'starting') {
            $status = 'blocked'; $errorText = 'Interrupted startup has no worker metadata; worker identity is uncertain. No automatic retry is permitted.'
        }
        if ($readError) { $errorText += ' ' + $readError }
    }
    return @{ id = $Task.id; status = $status; recordedStatus = $Task.status; attempt = $Task.attempt;
        attemptPath = $Task.attemptPath; workerPid = $Task.workerPid; workerAlive = $workerAlive;
        codexAlive = $codexAlive; process = $metadata; result = $result; error = $errorText; readError = $readError }
}

# Dot sourcing exposes the read-only snapshot implementation to the two mutating commands.
if ($MyInvocation.InvocationName -ne '.') {
    $RunPath = [IO.Path]::GetFullPath($RunPath)
    $state = Read-ParallelJson (Join-Path $RunPath 'state.json')
    if (-not $state.runPath.Equals($RunPath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Run path identity mismatch.' }
    $snapshots = @($state.tasks | ForEach-Object { Get-ParallelRuntimeSnapshot $state $_ })
    $report = @{ schemaVersion = $state.schemaVersion; runId = $state.runId; runPath = $state.runPath;
        status = $state.status; baseline = $state.baseline; integration = $state.integration; tasks = $snapshots }
    if ($Json) { $report | ConvertTo-Json -Depth 40 }
    else {
        Write-Output "Run $($state.runId): $($state.status)"
        $snapshots | Select-Object id,status,attempt,workerPid,workerAlive,codexAlive,error | Format-Table -Wrap
    }
}
