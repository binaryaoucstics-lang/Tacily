# Copied ONLY into a temporary fixture before its baseline commit. Never run the
# production Test-Repository from this suite: it calls the suite recursively.
#requires -Version 7.2
param([string]$RepoRoot=(Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$ResultDirectory,[string]$DotnetPath,[string]$AssetSource)
. (Join-Path $PSScriptRoot 'Common.ps1')
. (Join-Path $PSScriptRoot 'tests/Write-FixtureReport.ps1')
Write-WorkflowFixtureReport -RepoRoot $RepoRoot -ResultDirectory $ResultDirectory | Write-Output
exit 0
