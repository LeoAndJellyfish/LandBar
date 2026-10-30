[CmdletBinding()]
param([switch]$NoLaunch, [string]$SignedPackagePath)
$ErrorActionPreference = 'Stop'
$distributionPath = if (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'app/LandBar.exe')) {
    $PSScriptRoot
} else { Join-Path (Split-Path $PSScriptRoot -Parent) 'dist' }
$appPath = Join-Path $distributionPath 'app'
$manifestPath = Join-Path $distributionPath 'identity/AppxManifest.xml'
$statePath = Join-Path $env:LOCALAPPDATA 'LandBar/deployment.json'
if (-not (Test-Path -LiteralPath (Join-Path $appPath 'LandBar.exe')) -or
    -not (Test-Path -LiteralPath $manifestPath)) {
    throw 'Release files are missing. Run scripts/Build.ps1 first, or extract the complete release folder.'
}
if ([Environment]::OSVersion.Version.Build -lt 19041) {
    throw 'LandBar requires Windows 10 version 2004 or later.'
}

$appPath = (Resolve-Path -LiteralPath $appPath).Path
$manifestPath = (Resolve-Path -LiteralPath $manifestPath).Path
$mode = if ($SignedPackagePath) { 'Signed' } else { 'Development' }
if ($SignedPackagePath) {
    $SignedPackagePath = (Resolve-Path -LiteralPath $SignedPackagePath).Path
    $registrationFile = $SignedPackagePath
} else {
    $developerPolicy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' -ErrorAction SilentlyContinue
    if (-not $developerPolicy -or $developerPolicy.AllowDevelopmentWithoutDevLicense -ne 1) {
        throw 'Local development registration requires Developer Mode. Enable it manually in Windows Settings > System > For developers, then retry; alternatively install a trusted signed MSIX with -SignedPackagePath. This script does not modify Developer Mode or certificate trust.'
    }
    $registrationFile = $manifestPath
}
$identityName = 'LandBar.Desktop'
$packageHash = (Get-FileHash -LiteralPath $registrationFile -Algorithm SHA256).Hash
$installedPackage = Get-AppxPackage -Name $identityName -ErrorAction SilentlyContinue | Select-Object -First 1
$previousState = $null
if (Test-Path -LiteralPath $statePath) {
    try { $previousState = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json } catch { }
}
$alreadyRegistered = $installedPackage -and $previousState -and
    $previousState.AppPath -eq $appPath -and $previousState.PackageHash -eq $packageHash -and $previousState.Mode -eq $mode

if (-not $alreadyRegistered) {
    Write-Host 'Registering the local LandBar package identity for notification permission.'
    Write-Host 'Developer Mode and certificate trust remain unchanged.'
    try {
        if ($SignedPackagePath) {
            Add-AppxPackage -Path $SignedPackagePath -ExternalLocation $appPath `
                -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
        } else {
            Add-AppxPackage -Register $manifestPath -ExternalLocation $appPath `
                -ForceApplicationShutdown -ForceUpdateFromAnyVersion -ErrorAction Stop
        }
    } catch {
        Write-Host 'Package registration failed. Existing application data and Windows security settings were retained.'
        Write-Host 'Review the Windows deployment error below. Development mode or a trusted signed package is required.'
        throw
    }
    $installedPackage = Get-AppxPackage -Name $identityName -ErrorAction Stop | Select-Object -First 1
    if (-not $installedPackage) { throw 'Windows did not return the registered LandBar package.' }
    New-Item -ItemType Directory -Force (Split-Path $statePath -Parent) | Out-Null
    @{ AppPath = $appPath; PackageHash = $packageHash; Mode = $mode; PackageFamilyName = $installedPackage.PackageFamilyName } |
        ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
}
Write-Host "Registered: $($installedPackage.PackageFamilyName)"
Write-Host 'In LandBar settings, choose notification access and approve the Windows permission dialog.'
if (-not $NoLaunch) {
    Start-Process -FilePath (Join-Path $appPath 'LandBar.exe') -WorkingDirectory $appPath -WindowStyle Normal
}
