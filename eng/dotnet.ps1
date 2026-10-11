$ErrorActionPreference = 'Stop'

$workspaceRoot = Split-Path -Parent $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path $workspaceRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $workspaceRoot '.tools\nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$dotnetExecutable = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
if (!(Test-Path -LiteralPath $dotnetExecutable)) { $dotnetExecutable = (Get-Command dotnet -ErrorAction Stop).Source }
& $dotnetExecutable @args
exit $LASTEXITCODE
