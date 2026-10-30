[CmdletBinding()]
param([switch]$Demo)
$ErrorActionPreference = 'Stop'
$distributionPath = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app/LandBar.exe')) {
    $PSScriptRoot
} else { Join-Path (Split-Path $PSScriptRoot -Parent) 'dist' }
$executablePath = Join-Path $distributionPath 'app/LandBar.exe'
if (-not (Test-Path -LiteralPath $executablePath)) { throw 'LandBar.exe is missing. Run scripts/Build.ps1 first.' }
if (-not (Get-AppxPackage -Name 'LandBar.Desktop' -ErrorAction SilentlyContinue)) {
    Write-Host 'Notification access requires package identity. Run Install.ps1 once.'
}
$startArguments = @{ FilePath = $executablePath; WorkingDirectory = Split-Path $executablePath -Parent; WindowStyle = 'Normal' }
if ($Demo) { $startArguments.ArgumentList = '--demo' }
Start-Process @startArguments
