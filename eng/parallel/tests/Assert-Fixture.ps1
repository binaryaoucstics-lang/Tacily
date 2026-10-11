#requires -Version 7.2
param([Parameter(Mandatory)][string]$TaskId)
$ErrorActionPreference = 'Stop'
$outputPath = Join-Path (Get-Location).Path "outputs/$TaskId.txt"
if (-not (Test-Path -LiteralPath $outputPath)) { throw 'Expected worker output is missing.' }
if ((Get-Content -LiteralPath $outputPath -Raw) -match 'validation-failure') { throw 'Deliberate fixture validation failure.' }
Write-Output "Validated $TaskId"
