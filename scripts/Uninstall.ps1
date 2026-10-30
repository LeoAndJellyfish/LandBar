[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$packages = @(Get-AppxPackage -Name 'LandBar.Desktop' -ErrorAction SilentlyContinue)
foreach ($package in $packages) {
    Remove-AppxPackage -Package $package.PackageFullName -ErrorAction Stop
}
# Source code, release files and the user's display preferences are retained.
$statePath = Join-Path $env:LOCALAPPDATA 'LandBar/deployment.json'
if (Test-Path -LiteralPath $statePath) { Remove-Item -LiteralPath $statePath -Force }
$runKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
if (Get-ItemProperty -LiteralPath $runKeyPath -Name LandBar -ErrorAction SilentlyContinue) {
    Remove-ItemProperty -LiteralPath $runKeyPath -Name LandBar
}
Write-Host 'LandBar package identity and automatic startup registration were removed.'
Write-Host 'Exit LandBar from its tray menu if the application is still running.'
