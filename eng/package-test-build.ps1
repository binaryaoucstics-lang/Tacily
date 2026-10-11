param(
    [string]$OutputDirectory = "work/release/package-0.14.5",
    [string]$PublishedDirectory = "work/release/publish-0.14.5-validated",
    [ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version = '0.14.5'
)
$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$destination = [IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$published = [IO.Path]::GetFullPath((Join-Path $repoRoot $PublishedDirectory))
if (!$destination.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package output must remain inside the workspace.' }
if ($destination -eq $published) { throw 'Package output must be different from the publish directory.' }
if ((Test-Path -LiteralPath $destination) -and (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1)) { throw 'Use a new empty package directory.' }
foreach ($required in @('ScreenTranslator.exe','models/v5/ch_PP-OCRv5_mobile_det.onnx','models/v5/ch_PP-OCRv5_rec_mobile.onnx','fonts/NotoSansCJKsc-Regular.otf','words','browser-extension/manifest.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $published $required))) { throw "Required published resource missing: $required" }
}
$forbidden = Get-ChildItem -LiteralPath $published -Recurse -File | Where-Object {
    $_.Name -in @('settings.json','native-host.json','deepseek-api-key.bin','ProductBench.exe','raw-response.json') -or $_.Extension -in @('.pfx','.pem','.key','.bin')
}
if ($forbidden) { throw 'Unexpected local or secret files in published directory.' }
New-Item -ItemType Directory -Path $destination -Force | Out-Null
foreach ($item in Get-ChildItem -LiteralPath $published) { Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $repoRoot 'LICENSE') -Destination (Join-Path $destination 'LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'README.md') -Destination (Join-Path $destination '使用说明.md')
Copy-Item -LiteralPath (Join-Path $repoRoot "docs/$Version-手动验证.txt") -Destination (Join-Path $destination '手动验证.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot "docs/$Version-先读我.txt") -Destination (Join-Path $destination '先读我.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/THIRD-PARTY-NOTICE.txt') -Destination (Join-Path $destination 'NOTICE.txt')
Copy-Item -LiteralPath (Join-Path $repoRoot 'NOTICE') -Destination (Join-Path $destination 'NOTICE')
foreach ($reportName in @("$Version-验证报告.md")) {
    $validationReport = Join-Path $repoRoot "docs/$reportName"
    if (Test-Path -LiteralPath $validationReport) {
        $documentation = Join-Path $destination 'docs'
        New-Item -ItemType Directory -Path $documentation -Force | Out-Null
        Copy-Item -LiteralPath $validationReport -Destination $documentation
    }
}
$licenses = Join-Path $destination 'licenses'
New-Item -ItemType Directory -Path $licenses -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/licenses/YamlDotNet-LICENSE.txt') -Destination $licenses
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs/licenses/ScreenTranslator-MIT.txt') -Destination $licenses
$depsPath = Join-Path $repoRoot 'src/ScreenTranslator.App/bin/Release/net8.0-windows10.0.19041.0/win-x64/ScreenTranslator.deps.json'
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
foreach ($entry in $deps.libraries.PSObject.Properties) {
    if ($entry.Value.type -ne 'package') { continue }
    $package = $entry.Name.ToLowerInvariant()
    $directory = Join-Path (Join-Path $repoRoot '.tools/nuget') $package
    if (!(Test-Path -LiteralPath $directory)) { continue }
    foreach ($license in Get-ChildItem -LiteralPath $directory -File | Where-Object Name -Match '^(LICENSE|THIRD.PARTY.NOTICES)') {
        Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses (($package -replace '/', '-') + '-' + $license.Name))
    }
}
foreach ($name in @('LICENSE.txt','ThirdPartyNotices.txt')) {
    $path = Join-Path (Join-Path $repoRoot 'work/sdk/dotnet') $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination (Join-Path $licenses ('dotnet-' + $name)) }
}
foreach ($name in @('RapidOcrNet-LICENSE.txt','RapidOcrNet-NOTICE.txt','OpenCvSharp-LICENSE.txt')) {
    $path = Join-Path (Join-Path $repoRoot 'work/vendor') $name
    if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $licenses }
}
Get-FileHash -LiteralPath (Join-Path $destination 'ScreenTranslator.exe') -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  ScreenTranslator.exe" } |
    Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
Write-Output $destination
