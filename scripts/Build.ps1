[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [switch]$FrameworkDependent,
    [string]$OutputDirectory,
    [switch]$SkipStopRunningApp,
    [ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SigningCertificateThumbprint,
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion
)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
$defaultDistributionPath = Join-Path $workspacePath 'dist'
$distributionPath = if ($OutputDirectory) {
    if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $workspacePath $OutputDirectory)) }
} else { $defaultDistributionPath }
$appPath = Join-Path $distributionPath 'app'
$stagingPath = Join-Path $workspacePath ('artifacts/package-identity/' + [guid]::NewGuid().ToString('N'))
$identityPath = Join-Path $distributionPath 'identity'
New-Item -ItemType Directory -Force $distributionPath, $appPath, $stagingPath, $identityPath | Out-Null

if (-not $SkipPublish) {
    if (-not $SkipStopRunningApp -and $distributionPath -eq $defaultDistributionPath) {
        & (Join-Path $PSScriptRoot 'Stop.ps1')
    }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    & dotnet publish (Join-Path $workspacePath 'src/LandBar/LandBar.csproj') `
        -c Release -r win-x64 --self-contained $selfContained `
        -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true `
        "-p:PathMap=$workspacePath=/_/LandBar" -o $appPath
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
}
if (-not (Test-Path -LiteralPath (Join-Path $appPath 'LandBar.exe'))) {
    throw "Missing published application: $appPath\LandBar.exe"
}

$sdkPath = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
$makeAppxPath = Get-ChildItem -LiteralPath $sdkPath -Directory -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending |
    ForEach-Object { Join-Path $_.FullName 'x64/makeappx.exe' } |
    Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $makeAppxPath) {
    throw 'Windows SDK MakeAppx.exe is required to build the identity package. Install the Windows 10/11 SDK and retry.'
}

# The sparse MSIX contains metadata only. Referenced binaries and icons live in dist/app.
$manifestPath = Join-Path $stagingPath 'AppxManifest.xml'
[xml]$manifest = Get-Content -LiteralPath (Join-Path $workspacePath 'packaging/AppxManifest.xml') -Raw -Encoding UTF8
if (-not $PackageVersion) {
    $buildDay = [int]((Get-Date).Date - [datetime]'2026-01-01').TotalDays
    $buildMinute = ((Get-Date).Hour * 60) + (Get-Date).Minute
    [xml]$developmentProject = Get-Content -LiteralPath (Join-Path $workspacePath 'src/LandBar/LandBar.csproj') -Raw
    $developmentVersion = $developmentProject.SelectSingleNode('/Project/PropertyGroup/Version').InnerText.Split('.')
    $PackageVersion = "$($developmentVersion[0]).$($developmentVersion[1]).$buildDay.$buildMinute"
}
foreach ($part in $PackageVersion.Split('.')) {
    if ([int]$part -gt 65535) { throw 'Each package version component must be between 0 and 65535.' }
}
$manifest.Package.Identity.Version = $PackageVersion
$manifest.Save($manifestPath)
Copy-Item -LiteralPath $manifestPath -Destination $identityPath -Force
Copy-Item -LiteralPath (Join-Path $workspacePath 'packaging/Assets') -Destination $appPath -Recurse -Force
$packagePath = Join-Path $distributionPath 'LandBar.identity.msix'
& $makeAppxPath pack /o /d $stagingPath /nv /p $packagePath
if ($LASTEXITCODE -ne 0) { throw 'MakeAppx failed.' }
if ($SigningCertificateThumbprint) {
    $certificate = Get-Item -LiteralPath "Cert:/CurrentUser/My/$SigningCertificateThumbprint" -ErrorAction Stop
    if ($certificate.Subject -cne $manifest.Package.Identity.Publisher) {
        throw 'The signing certificate subject must exactly match Publisher in both application manifests.'
    }
    $signToolPath = Join-Path (Split-Path $makeAppxPath -Parent) 'signtool.exe'
    & $signToolPath sign /sha1 $SigningCertificateThumbprint /s My /fd SHA256 $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed.' }
    & $signToolPath verify /pa $packagePath
    if ($LASTEXITCODE -ne 0) { throw 'MSIX signature validation failed. The certificate must already be trusted on the target device.' }
}

foreach ($name in @('Install.ps1', 'Uninstall.ps1', 'Start.ps1')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $distributionPath -Force
}
foreach ($name in @('安装并启动.cmd', '启动.cmd', '卸载.cmd')) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination $distributionPath -Force
}
Get-FileHash -LiteralPath $packagePath -Algorithm SHA256 | Format-List
Write-Host "Ready: $distributionPath"
Write-Host 'Local development: run Install.ps1 once with Developer Mode already enabled.'
Write-Host 'Signed deployment: run Install.ps1 -SignedPackagePath ./LandBar.identity.msix.'
