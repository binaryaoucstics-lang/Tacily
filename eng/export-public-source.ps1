param([string]$OutputDirectory = 'outputs/GlobalAITranslator-0.14.5-source')
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$destination = [IO.Path]::GetFullPath((Join-Path $repo $OutputDirectory))
if (!$destination.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source output must stay in workspace.' }
if (Test-Path -LiteralPath $destination) { throw 'Use a new source directory.' }
$rootNames = @('.gitignore','README.md','CONTRIBUTING.md','SECURITY.md','LICENSE','NOTICE','ScreenTranslator.sln','Directory.Build.props','Directory.Build.targets','Directory.Packages.props','NuGet.Config','global.json')
$roots = @('.github','browser-extension','docs','eng','installer','models','src','tests')
$files = @($rootNames | ForEach-Object { Get-Item -LiteralPath (Join-Path $repo $_) })
foreach ($name in $roots) { $files += Get-ChildItem -LiteralPath (Join-Path $repo $name) -File -Recurse }
$chosen = @($files | Where-Object {
    $relative = [IO.Path]::GetRelativePath($repo, $_.FullName)
    $relative -notmatch '(^|[\\/])(bin|obj|node_modules|__pycache__)([\\/]|$)' -and
    $_.Extension -notin @('.onnx','.otf','.ttf','.dll','.exe','.pdb','.bin','.pyc','.log','.trx','.pem','.key','.pfx','.dmp') -and
    $_.Name -notin @('settings.json','native-host.json','credentials.json','.env') -and
    ($_.Extension -ne '.png' -or $relative -match '^browser-extension[\\/]icons[\\/]' -or $_.Name -in @('colour-real-en-comic.png','colour-real-ja-vertical.png'))
})
# Scan only chosen public text. Synthetic test keys stay in tests; never read a
# user's key/config to decide what to export, and never print matching secrets.
$findings = [Collections.Generic.List[string]]::new()
$localAccountName = [Environment]::UserName
foreach ($file in $chosen) {
    if ($file.Extension -notin @('.cs','.js','.json','.md','.txt','.ps1','.yml','.yaml','.xml','.xaml','.csproj','.props','.targets','.iss','.config','')) { continue }
    $text = [IO.File]::ReadAllText($file.FullName)
    if (($localAccountName.Length -ge 6 -and $text.Contains($localAccountName, [StringComparison]::OrdinalIgnoreCase)) -or
        $text -match '(?i)C:[\\/]Users[\\/](?!Test[\\/]|Public[\\/])|BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY|github_pat_[a-zA-Z0-9_]{30,}|ghp_[a-zA-Z0-9]{30,}|hf_[a-zA-Z0-9]{24,}|sk-(?:proj-)?[a-zA-Z0-9_-]{30,}') {
        $findings.Add([IO.Path]::GetRelativePath($repo, $file.FullName))
    }
}
if ($findings.Count) { throw ('Public source audit found sensitive-looking content in: ' + ($findings -join ', ')) }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($file in $chosen) {
    $target = [IO.Path]::GetFullPath((Join-Path $destination ([IO.Path]::GetRelativePath($repo, $file.FullName))))
    if (!$target.StartsWith($destination + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Source target path invalid.' }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$manifest = @($chosen | ForEach-Object { [pscustomobject]@{path=[IO.Path]::GetRelativePath($repo,$_.FullName).Replace([IO.Path]::DirectorySeparatorChar,'/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant();bytes=$_.Length} })
Set-Content -LiteralPath (Join-Path $destination 'SOURCE-MANIFEST.json') -Value (ConvertTo-Json -InputObject $manifest -Depth 3) -Encoding utf8NoBOM
[pscustomobject]@{files=$chosen.Count;bytes=($chosen|Measure-Object Length -Sum).Sum;sensitiveFindings=0;destination=$destination}|ConvertTo-Json
