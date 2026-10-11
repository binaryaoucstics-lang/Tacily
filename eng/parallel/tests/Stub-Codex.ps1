#requires -Version 7.2
# Deliberately has no named parameters: receive CLI switches exactly as supplied.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
[Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
$cli = @($args)
function Get-CliValue([string[]]$Names) {
    for ($i=0; $i -lt $cli.Count-1; $i++) { if ($cli[$i] -in $Names) { return [string]$cli[$i+1] } }
    throw "Required CLI argument missing: $Names"
}
$own = [IO.Path]::GetFullPath((Get-CliValue @('--cd')))
if ($own -ne [IO.Path]::GetFullPath((Get-Location).Path)) { throw 'Worker cwd mismatch.' }
$resultPath = [IO.Path]::GetFullPath((Get-CliValue @('--output-last-message','-o')))
$schemaPath = Get-CliValue @('--output-schema')
$prefix = $own.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $resultPath.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Result escaped checkout.' }
$schema = Get-Content -LiteralPath $schemaPath -Raw
$task = Get-Content -LiteralPath (Join-Path $own '.parallel/task.json') -Raw | ConvertFrom-Json -AsHashtable
$promptText = [Console]::In.ReadToEnd()
$mode = 'success'
foreach ($candidate in @('timeout','process-failure','missing-result','invalid-result','wrong-id','blocked','failed','validation-failure')) {
    if ($promptText.Contains("fixture:$candidate")) { $mode=$candidate; break }
}
$start = [DateTime]::UtcNow.ToString('o')
$evidencePath = Join-Path $own '.parallel/stub-window.json'
function Write-StubWindow($FinishedAt) {
    # Publish atomically so polling never observes half of a JSON sentinel.
    $temporary=$evidencePath+'.tmp'
    [IO.File]::WriteAllText($temporary,(@{pid=$PID;startedAt=$start;finishedAt=$FinishedAt;mode=$mode} | ConvertTo-Json),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $evidencePath -Force
}
Write-StubWindow $null
[Console]::Out.WriteLine('{"type":"fixture.started"}')
[Console]::Error.WriteLine("fixture mode=$mode task=$($task.id)")
[IO.Directory]::CreateDirectory((Join-Path $own 'outputs')) | Out-Null
$changed = "outputs/$($task.id).txt"
[IO.File]::WriteAllText((Join-Path $own $changed), "$mode 中文 retained output")
# Each process writes only its own checkout. The controller polls these read-only
# sentinels to prove all three started before releasing the barrier.
$release = Join-Path $own '.parallel/release-worker'
if ($promptText.Contains('fixture:barrier')) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (-not (Test-Path -LiteralPath $release)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw 'Fixture barrier deadline exceeded.' }
        Start-Sleep -Milliseconds 50
    }
}
if ($mode -eq 'timeout') { Start-Sleep -Seconds 30 }
if ($mode -eq 'process-failure') { exit 7 }
$result = @{taskId=$task.id;status='completed';summary="Fixture $mode";changedFiles=@($changed);
    tests=@(@{command='fixture write';outcome='passed';details='local only'});risks=@();permissionRequests=@()}
if ($mode -eq 'blocked') { $result.status='blocked'; $result.permissionRequests=@('Fixture approval unavailable: workspace permission required') }
if ($mode -eq 'failed') { $result.status='failed' }
if ($mode -eq 'wrong-id') { $result.taskId='wrong-task' }
if ($mode -eq 'invalid-result') { [IO.File]::WriteAllText($resultPath,'{invalid') }
elseif ($mode -ne 'missing-result') {
    $json = $result | ConvertTo-Json -Depth 10
    if (-not (Test-Json -Json $json -Schema $schema)) { throw 'Fixture result does not satisfy schema.' }
    [IO.File]::WriteAllText($resultPath,$json,[Text.UTF8Encoding]::new($false))
}
Write-StubWindow ([DateTime]::UtcNow.ToString('o'))
[Console]::Out.WriteLine('{"type":"fixture.finished"}')
exit 0
