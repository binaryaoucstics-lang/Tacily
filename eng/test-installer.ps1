param(
    [Parameter(Mandatory)]
    [string]$InstallerPath
)

$ErrorActionPreference = 'Stop'
$installer = [System.IO.Path]::GetFullPath($InstallerPath)
if (-not (Test-Path -LiteralPath $installer -PathType Leaf)) {
    throw "Installer does not exist: $installer"
}

$testRoot = Join-Path ([System.IO.Path]::GetTempPath()) (
    'ScreenTranslator-InstallerTest-' + [Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testRoot 'App'
$settingsRoot = Join-Path (
    [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::LocalApplicationData)) 'ScreenTranslator'
$markerPath = Join-Path $settingsRoot (
    'installer-smoke-' + [Guid]::NewGuid().ToString('N') + '.tmp')

New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
New-Item -ItemType Directory -Path $settingsRoot -Force | Out-Null
[System.IO.File]::WriteAllText($markerPath, 'preserve')

try {
    $install = Start-Process -FilePath $installer -Wait -PassThru `
        -ArgumentList @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART',
            '/SP-',
            "/DIR=$installDirectory",
            '/TASKS='
        )
    if ($install.ExitCode -ne 0) {
        throw "Installer failed with exit code $($install.ExitCode)"
    }

    $executable = Join-Path $installDirectory 'ScreenTranslator.exe'
    $manifest = Join-Path $installDirectory 'browser-extension\manifest.json'
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    foreach ($required in @($executable, $manifest, $uninstaller)) {
        if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
            throw "Installed file is missing: $required"
        }
    }

    $register = Start-Process -FilePath $executable -Wait -PassThru `
        -ArgumentList '--register-browser-host'
    if ($register.ExitCode -ne 0) {
        throw "Browser registration command failed."
    }

    $uninstall = Start-Process -FilePath $uninstaller -Wait -PassThru `
        -ArgumentList @(
            '/VERYSILENT',
            '/SUPPRESSMSGBOXES',
            '/NORESTART'
        )
    if ($uninstall.ExitCode -ne 0) {
        throw "Uninstaller failed with exit code $($uninstall.ExitCode)"
    }

    if (Test-Path -LiteralPath $executable) {
        throw "Application remained after uninstall."
    }
    if (-not (Test-Path -LiteralPath $markerPath)) {
        throw "Default uninstall unexpectedly deleted user data."
    }
}
finally {
    if (Test-Path -LiteralPath $markerPath) {
        Remove-Item -LiteralPath $markerPath -Force
    }
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [System.IO.Path]::GetFullPath($testRoot)
        $resolvedTemp = [System.IO.Path]::GetFullPath(
            [System.IO.Path]::GetTempPath())
        if (-not $resolvedTestRoot.StartsWith(
                $resolvedTemp,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove installer test path: $resolvedTestRoot"
        }
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
