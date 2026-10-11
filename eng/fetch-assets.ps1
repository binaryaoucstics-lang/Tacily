param([string]$DestinationRoot = (Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference = 'Stop'
$assetRoot = [IO.Path]::GetFullPath($DestinationRoot)
$manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'assets.json') -Raw | ConvertFrom-Json
foreach ($asset in $manifest.assets) {
    $target = [IO.Path]::GetFullPath((Join-Path $assetRoot $asset.path))
    if (!$target.StartsWith($assetRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Asset path outside destination.' }
    if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -eq $asset.sha256) { continue }
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
    $temporary = $target + '.download-' + [Guid]::NewGuid().ToString('N')
    try {
        Invoke-WebRequest -Uri $asset.url -OutFile $temporary -TimeoutSec 120
        if ((Get-Item -LiteralPath $temporary).Length -ne $asset.bytes -or (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $asset.sha256) {
            throw "Pinned asset checksum mismatch: $($asset.path)"
        }
        Move-Item -LiteralPath $temporary -Destination $target -Force
        Write-Output "Ready: $($asset.path)"
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
}
Write-Output 'Assets verified.'
