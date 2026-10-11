function Assert-Workflow {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}
function Assert-WorkflowThrows {
    param([scriptblock]$Action, [string]$Pattern = '.')
    $caught = $null
    try { & $Action | Out-Null } catch { $caught = $_.Exception.Message }
    Assert-Workflow ($null -ne $caught) 'Expected rejection, but operation succeeded.'
    Assert-Workflow ($caught -match $Pattern) "Unexpected rejection: $caught"
}
function Invoke-WorkflowTest {
    param([string]$Name, [scriptblock]$Action)
    try {
        & $Action | Out-Null
        $script:WorkflowResults.Add(@{name=$Name;outcome='passed';details=''})
        Write-Host "PASS $Name"
    } catch {
        $script:WorkflowResults.Add(@{name=$Name;outcome='failed';details=$_.ToString()})
        Write-Host "FAIL ${Name}: $_"
    }
}
function New-WorkflowPlan {
    param([string]$Baseline='HEAD', [string]$RunId='fixture-run')
    return @{schemaVersion=1;runId=$RunId;baseline=$Baseline;mainBranch='main';maxParallel=3;tasks=@(
        foreach ($id in @('alpha','beta','gamma')) {
            @{id=$id;title="Test $id";instructions='fixture:success';ownedPaths=@("outputs/$id.txt");dependsOn=@();
              acceptance=@('owned output exists');validation=@(@{filePath='pwsh';arguments=@('-NoProfile','-File','eng/parallel/tests/Assert-Fixture.ps1','-TaskId',$id);timeoutSeconds=10})}
        }
    )}
}
function New-WorkflowFixture {
    param([string]$Name,[switch]$StubRepository)
    $root = Join-Path $script:WorkflowRoot ([guid]::NewGuid().ToString('N').Substring(0,6) + ' 中文')
    $repo = Join-Path $root 'main'
    [IO.Directory]::CreateDirectory((Join-Path $repo 'eng/parallel/tests')) | Out-Null
    # Only repository-owned scripts are copied; no user configuration or credentials.
    Get-ChildItem -LiteralPath $script:WorkflowSource -File | Where-Object Extension -in '.ps1','.json' | ForEach-Object {
        Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $repo 'eng/parallel')
    }
    Copy-Item -LiteralPath (Join-Path $script:WorkflowSource 'tests/Assert-Fixture.ps1') -Destination (Join-Path $repo 'eng/parallel/tests')
    Copy-Item -LiteralPath (Join-Path $script:WorkflowSource 'tests/Write-FixtureReport.ps1') -Destination (Join-Path $repo 'eng/parallel/tests')
    if ($StubRepository) {
        Copy-Item -LiteralPath (Join-Path $script:WorkflowSource 'tests/Stub-Repository.ps1') -Destination (Join-Path $repo 'eng/parallel/Test-Repository.ps1')
    }
    # Tiny tracked inputs are committed BEFORE preparation so every checkout has
    # the same manifest and node test path. No downloads or SDK are required.
    Write-ParallelJson (Join-Path $repo 'eng/assets.json') @{version=1;assets=@()}
    [IO.Directory]::CreateDirectory((Join-Path $repo 'browser-extension/tests')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'browser-extension/tests/smoke.test.js'),"require('node:test')('fixture smoke', () => {});`n")
    [IO.File]::WriteAllText((Join-Path $repo '.gitignore'), ".parallel/`n")
    Invoke-ParallelGit $repo @('init','-b','main') | Out-Null
    Invoke-ParallelGit $repo @('config','core.longpaths','true') | Out-Null
    Invoke-ParallelGit $repo @('config','user.name','Workflow Fixture') | Out-Null
    Invoke-ParallelGit $repo @('config','user.email','workflow-fixture@example.invalid') | Out-Null
    Invoke-ParallelGit $repo @('config','commit.gpgsign','false') | Out-Null
    Invoke-ParallelGit $repo @('config','core.autocrlf','false') | Out-Null
    Invoke-ParallelGit $repo @('add','.') | Out-Null
    Invoke-ParallelGit $repo @('commit','-m','fixture baseline') | Out-Null
    $plan = New-WorkflowPlan (Get-ParallelHead $repo)
    $planPath = Join-Path $root 'plan.json'
    Write-ParallelJson $planPath $plan
    Write-ParallelJson (Join-Path $root 'fixture.json') @{name=$Name;repo=$repo}
    return @{root=$root;repo=$repo;plan=$plan;planPath=$planPath;runRoot=(Join-Path $root 'runs');runPath=(Join-Path $root 'runs/fixture-run')}
}
function Invoke-WorkflowScript {
    param([string]$Name,[string[]]$Arguments,[string]$WorkingDirectory=$script:WorkflowRoot,[int]$Timeout=60,
        [string]$ScriptRoot=$script:WorkflowSource)
    $log = Join-Path $script:WorkflowRoot ('command-' + [guid]::NewGuid().ToString('N') + '.log')
    return Invoke-ParallelCommand 'pwsh' (@('-NoProfile','-File',(Join-Path $ScriptRoot $Name)) + $Arguments) $WorkingDirectory $log $Timeout
}
function Assert-WorkflowSuccess {
    param($Result)
    Assert-Workflow ($Result.exitCode -eq 0 -and -not $Result.timedOut) "Command failed: $(Get-Content -LiteralPath $Result.logPath -Raw)"
}
function Read-WorkflowState { param($Fixture) return Read-ParallelJson (Join-Path $Fixture.runPath 'state.json') }
function Save-WorkflowState { param($Fixture,$State) Write-ParallelJson (Join-Path $Fixture.runPath 'state.json') $State }
function Start-WorkflowFixture {
    param($Fixture,[switch]$PrepareOnly,[int]$Timeout=15)
    $argsList = @('-PlanPath',$Fixture.planPath,'-RepoRoot',$Fixture.repo,'-RunRoot',$Fixture.runRoot,
        '-CodexPath',(Join-Path $script:WorkflowSource 'tests/Stub-Codex.ps1'),'-TimeoutSeconds',"$Timeout")
    if ($PrepareOnly) { $argsList += '-PrepareOnly' }
    return Invoke-WorkflowScript 'Invoke-ParallelDevelopment.ps1' $argsList $Fixture.repo 90
}
function Assert-WorkflowEvidence {
    param($Task)
    foreach ($name in @('process.json','events.jsonl','stderr.log')) {
        Assert-Workflow (Test-Path -LiteralPath (Join-Path $Task.attemptPath $name)) "Missing attempt evidence: $name"
    }
    $process = Read-ParallelJson (Join-Path $Task.attemptPath 'process.json')
    Assert-Workflow ($null -ne $process.finishedAt) 'Process did not finish.'
}
