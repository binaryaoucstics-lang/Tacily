#requires -Version 7.2
param(
    [string]$RepoRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$ResultDirectory,
    [string]$DotnetPath,
    [string]$AssetSource
)
. (Join-Path $PSScriptRoot 'Common.ps1')
$RepoRoot = [IO.Path]::GetFullPath($RepoRoot)
if (-not $ResultDirectory) { $ResultDirectory = Join-Path $RepoRoot ('.parallel/repository-' + [guid]::NewGuid().ToString('N')) }
$ResultDirectory = Assert-ParallelChildPath $RepoRoot $ResultDirectory
$report = @{ schemaVersion = 1; status = 'failed'; repoRoot = $RepoRoot; head = $null; verificationHead = $null;
    startedAt = [DateTime]::UtcNow.ToString('o'); finishedAt = $null; commands = @(); assets = @(); errors = @() }
$savedEnvironment = @{}
$safeResults = $false
function Assert-RepositoryDestination {
    param([string]$Path)
    $full = Assert-ParallelChildPath $RepoRoot $Path
    $cursor = $full
    while ($cursor -and $cursor -ne $RepoRoot) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Destination contains a reparse point: $cursor" }
        $cursor = Split-Path $cursor -Parent
    }
    return $full
}
function Invoke-RepositoryStep {
    param([string]$Name, [string]$FilePath, [string[]]$Arguments)
    $log = Join-Path $ResultDirectory "$Name.log"
    try {
        if (-not $FilePath) { throw "Executable unavailable for $Name." }
        $record = Invoke-ParallelCommand $FilePath $Arguments $RepoRoot $log 3600
        $record.name = $Name
        $report.commands += $record
        if ($record.exitCode -ne 0 -or $record.timedOut) { $report.errors += "${Name} failed: $log" }
    } catch {
        [IO.File]::WriteAllText($log, $_.Exception.Message)
        $report.commands += @{ name = $Name; filePath = $FilePath; arguments = $Arguments; exitCode = 1; timedOut = $false; logPath = $log; error = $_.Exception.Message }
        $report.errors += "${Name}: $($_.Exception.Message)"
    }
}
try {
    if ($env:PARALLEL_REPOSITORY_VALIDATION -eq '1') { throw 'Recursive Test-Repository invocation is forbidden; workflow fixtures must not call this gate.' }
    Assert-RepositoryDestination $ResultDirectory | Out-Null
    $relative = [IO.Path]::GetRelativePath($RepoRoot, $ResultDirectory).Replace('\','/')
    Invoke-ParallelGit $RepoRoot @('check-ignore', '--quiet', '--', "$relative/report.json") | Out-Null
    if (Invoke-ParallelGit $RepoRoot @('ls-files', '--', "$relative/report.json")) { throw 'ResultDirectory contains a tracked report; refusing to overwrite.' }
    $safeResults = $true
    [IO.Directory]::CreateDirectory($ResultDirectory) | Out-Null
    $report.head = Get-ParallelHead $RepoRoot; $report.verificationHead = $report.head
    foreach ($name in @('DOTNET_CLI_HOME','NUGET_PACKAGES','NUGET_HTTP_CACHE_PATH','NUGET_PLUGINS_CACHE_PATH','NUGET_SCRATCH','DOTNET_MULTILEVEL_LOOKUP','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_CLI_TELEMETRY_OPTOUT','PARALLEL_REPOSITORY_VALIDATION')) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    }
    foreach ($entry in @{ DOTNET_CLI_HOME='dotnet-home'; NUGET_PACKAGES='nuget/packages'; NUGET_HTTP_CACHE_PATH='nuget/http'; NUGET_PLUGINS_CACHE_PATH='nuget/plugins'; NUGET_SCRATCH='nuget/scratch' }.GetEnumerator()) {
        $directory = Join-Path $ResultDirectory $entry.Value
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [Environment]::SetEnvironmentVariable($entry.Key, $directory, 'Process')
    }
    $env:DOTNET_MULTILEVEL_LOOKUP = '0'; $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:PARALLEL_REPOSITORY_VALIDATION = '1'
    $dotnet = $null
    try {
        $candidate = if ($DotnetPath) { $DotnetPath } elseif (Test-Path -LiteralPath (Join-Path $RepoRoot '.tools/dotnet/dotnet.exe')) { Join-Path $RepoRoot '.tools/dotnet/dotnet.exe' } else { 'dotnet' }
        $dotnet = Resolve-ParallelExecutable $candidate
        Invoke-RepositoryStep 'sdk' $dotnet @('--list-sdks')
        if ($report.commands[-1].exitCode -ne 0 -or (Get-Content -LiteralPath (Join-Path $ResultDirectory 'sdk.log') -Raw) -notmatch '(?m)^8\.0\.\d+[^\r\n]*\[') { throw 'An existing .NET 8 SDK is required; no SDK will be installed.' }
    } catch {
        $report.errors += $_.Exception.Message; $dotnet = $null
        if (-not @($report.commands | Where-Object name -eq 'sdk').Count) { Invoke-RepositoryStep 'sdk' '' @('--list-sdks') }
    }
    try {
        $manifest = Read-ParallelJson (Join-Path $RepoRoot 'eng/assets.json')
        if ($manifest.version -ne 1 -or -not @($manifest.assets).Count) { throw 'Invalid asset manifest.' }
        foreach ($asset in $manifest.assets) {
            $record = @{ path = $asset.path; sha256 = $asset.sha256; status = 'failed'; error = '' }
            try {
                $path = ConvertTo-ParallelOwnedPath $asset.path
                if ($path -notmatch '^(src/ScreenTranslator\.App/Assets/Fonts/[^/]+\.(otf|ttf)|models/[^\r\n]+\.onnx)$' -or $asset.sha256 -notmatch '^[0-9a-fA-F]{64}$' -or $asset.bytes -le 0) { throw "Invalid pinned asset: $path" }
                $target = Assert-RepositoryDestination (Join-Path $RepoRoot $path)
                Invoke-ParallelGit $RepoRoot @('check-ignore', '--quiet', '--', $path) | Out-Null
                if (Invoke-ParallelGit $RepoRoot @('ls-files', '--', $path)) { throw "Asset is tracked; refusing writes: $path" }
                if (Test-Path -LiteralPath $target) {
                    if ((Get-Item -LiteralPath $target).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Existing asset mismatch; preserved: $path" }
                } else {
                    [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
                    $temporary = $target + '.download-' + [guid]::NewGuid().ToString('N')
                    # Failed downloads stay ignored as diagnostic evidence. Never replace an existing asset.
                    if ($AssetSource) {
                        $source = Assert-ParallelChildPath $AssetSource (Join-Path $AssetSource $path)
                        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Read-only asset cache missing: $path" }
                        Copy-Item -LiteralPath $source -Destination $temporary
                    } else {
                        if ([uri]$asset.url -and ([uri]$asset.url).Scheme -ne 'https') { throw "Asset URL must use HTTPS: $path" }
                        Invoke-WebRequest -Uri $asset.url -OutFile $temporary -TimeoutSec 120
                    }
                    if ((Get-Item -LiteralPath $temporary).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $asset.sha256) { throw "Downloaded/cache asset checksum mismatch: $path" }
                    [IO.File]::Move($temporary, $target, $false)
                }
                $record.status = 'passed'
            } catch { $record.error = $_.Exception.Message; $report.errors += $record.error }
            $report.assets += $record
        }
    } catch { $report.errors += "Assets: $($_.Exception.Message)" }
    Invoke-RepositoryStep 'restore' $dotnet @('restore', 'ScreenTranslator.sln', '--configfile', 'NuGet.Config')
    Invoke-RepositoryStep 'build' $dotnet @('build', 'ScreenTranslator.sln', '-c', 'Release', '--no-restore')
    Invoke-RepositoryStep 'dotnet-tests' $dotnet @('test', 'ScreenTranslator.sln', '-c', 'Release', '--no-build', '--logger', 'trx', '--results-directory', (Join-Path $ResultDirectory 'trx'))
    $node = $null; $nodeFiles = @()
    try {
        $node = Resolve-ParallelExecutable 'node'
        $nodeFiles = @(Get-ChildItem -LiteralPath (Join-Path $RepoRoot 'browser-extension/tests') -Filter '*.test.js' -File -Recurse | Sort-Object FullName | ForEach-Object FullName)
        if (-not $nodeFiles.Count) { throw 'No browser extension tests found.' }
    } catch { $report.errors += $_.Exception.Message; $node = $null }
    Invoke-RepositoryStep 'node-tests' $node (@('--test') + $nodeFiles)
    Invoke-RepositoryStep 'workflow-tests' 'pwsh' @('-NoProfile', '-File', (Join-Path $RepoRoot 'eng/parallel/Test-ParallelWorkflow.ps1'))
    if ((Get-ParallelHead $RepoRoot) -ne $report.head) { $report.errors += 'Repository HEAD changed during validation.' }
    if ($report.errors.Count -eq 0) { $report.status = 'passed' }
} catch { $report.errors += $_.Exception.Message }
finally {
    foreach ($entry in $savedEnvironment.GetEnumerator()) {
        if ($null -eq $entry.Value) { Remove-Item -LiteralPath "Env:$($entry.Key)" -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    }
    $report.finishedAt = [DateTime]::UtcNow.ToString('o')
    # Never write a report through an unsafe user supplied directory.
    if (-not $safeResults) { $ResultDirectory = Join-Path $RepoRoot ('.parallel/repository-failure-' + [guid]::NewGuid().ToString('N')) }
    Assert-RepositoryDestination $ResultDirectory | Out-Null
    Write-ParallelJson (Join-Path $ResultDirectory 'report.json') $report
}
Write-Output "Repository validation $($report.status): $(Join-Path $ResultDirectory 'report.json')"
if ($report.status -ne 'passed') { foreach ($message in $report.errors) { Write-Error $message -ErrorAction Continue }; exit 1 }
