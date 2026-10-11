param(
    [Parameter(Mandatory)]
    [string]$ArtifactDirectory
)

$ErrorActionPreference = 'Stop'
$resolvedDirectory = [System.IO.Path]::GetFullPath($ArtifactDirectory)
if (-not (Test-Path -LiteralPath $resolvedDirectory -PathType Container)) {
    throw "Artifact directory does not exist: $resolvedDirectory"
}

$expectedFiles = @(
    'ScreenTranslator-Setup-x64.exe',
    'ScreenTranslator-Portable-x64.zip',
    'ScreenTranslator-Browser-Extension.zip'
)
$missing = $expectedFiles |
    Where-Object {
        -not (Test-Path -LiteralPath (
            Join-Path $resolvedDirectory $_) -PathType Leaf)
    }
if ($missing) {
    throw "Cannot write checksums; missing: $($missing -join ', ')"
}

$lines = foreach ($name in $expectedFiles) {
    $hash = Get-FileHash -Algorithm SHA256 -LiteralPath (
        Join-Path $resolvedDirectory $name)
    "$($hash.Hash.ToLowerInvariant())  $name"
}
$checksumPath = Join-Path $resolvedDirectory 'SHA256SUMS.txt'
[System.IO.File]::WriteAllLines(
    $checksumPath,
    $lines,
    [System.Text.UTF8Encoding]::new($false))
Write-Output $checksumPath
