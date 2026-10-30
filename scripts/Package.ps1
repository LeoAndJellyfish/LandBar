[CmdletBinding()]
param(
    [ValidatePattern('^v?\d+\.\d+\.\d+$')][string]$Version,
    [string]$OutputDirectory = 'releases'
)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
[xml]$project = Get-Content -LiteralPath (Join-Path $workspacePath 'src/LandBar/LandBar.csproj') -Raw
$projectVersion = $project.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if (-not $Version) { $Version = $projectVersion }
$Version = $Version.TrimStart('v')
if ($Version -ne $projectVersion) { throw "Release version $Version does not match project version $projectVersion." }
foreach ($part in $Version.Split('.')) {
    if ([int]$part -gt 65535) { throw 'Each identity version component must be <= 65535.' }
}
$releasePath = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) }
    else { [IO.Path]::GetFullPath((Join-Path $workspacePath $OutputDirectory)) }
$buildRoot = Join-Path $workspacePath ('artifacts/package-build/' + [guid]::NewGuid().ToString('N'))
$distributionPath = Join-Path $buildRoot 'dist'
$payloadPath = Join-Path $buildRoot 'payload'
New-Item -ItemType Directory -Force $payloadPath, $releasePath | Out-Null
& (Join-Path $PSScriptRoot 'Build.ps1') -OutputDirectory $distributionPath -SkipStopRunningApp -PackageVersion "$Version.0"

# Only fresh publish outputs and explicitly approved public documents enter the archive.
# No previously running dist directory, diagnostics, settings, screenshots of user apps or old evidence is copied.
$appSource = Join-Path $distributionPath 'app'
$appDestination = Join-Path $payloadPath 'app'
foreach ($file in Get-ChildItem -LiteralPath $appSource -Recurse -File) {
    $relative = $file.FullName.Substring($appSource.Length + 1)
    $allowed = $file.Extension -in @('.dll', '.exe') -or
        $relative -in @('LandBar.deps.json', 'LandBar.runtimeconfig.json', 'LICENSE.txt', 'ThirdPartyNotices.txt') -or
        ($relative -like 'Assets\*' -and $file.Extension -in @('.png', '.txt', '.md', '.ttf', '.ico'))
    if (-not $allowed) {
        if ($file.Extension -eq '.pdb') { continue }
        throw "Unexpected publish output requires a packaging review: $relative"
    }
    $destination = Join-Path $appDestination $relative
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
foreach ($name in @('LandBar.identity.msix', 'Install.ps1', 'Start.ps1', 'Uninstall.ps1', '安装并启动.cmd', '启动.cmd', '卸载.cmd')) {
    Copy-Item -LiteralPath (Join-Path $distributionPath $name) -Destination $payloadPath
}
Copy-Item -LiteralPath (Join-Path $distributionPath 'identity') -Destination $payloadPath -Recurse
foreach ($name in @('README.md', 'THIRD_PARTY_NOTICES.md', 'docs/installation.md', 'docs/development.md')) {
    $destination = Join-Path $payloadPath $name
    New-Item -ItemType Directory -Force (Split-Path $destination -Parent) | Out-Null
    Copy-Item -LiteralPath (Join-Path $workspacePath $name) -Destination $destination
}
if (Test-Path -LiteralPath (Join-Path $workspacePath 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $workspacePath 'LICENSE') -Destination $payloadPath
}
$imagesSource = Join-Path $workspacePath 'docs/images'
if (Test-Path -LiteralPath $imagesSource) {
    foreach ($file in Get-ChildItem -LiteralPath $imagesSource -File) {
        if ($file.Extension -notin @('.svg', '.png', '.webp')) { throw "Unexpected README image: $($file.Name)" }
        $imageDestination = Join-Path $payloadPath 'docs/images'
        New-Item -ItemType Directory -Force $imageDestination | Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $imageDestination
    }
}

# The deps file identifies the runtime packs actually redistributed, including their exact versions.
$dependencies = Get-Content -LiteralPath (Join-Path $appSource 'LandBar.deps.json') -Raw | ConvertFrom-Json
$assets = Get-Content -LiteralPath (Join-Path $workspacePath 'src/LandBar/obj/project.assets.json') -Raw | ConvertFrom-Json
$packageFolders = @($assets.packageFolders.PSObject.Properties.Name)
$componentLines = [Collections.Generic.List[string]]::new()
foreach ($entry in $dependencies.libraries.PSObject.Properties) {
    if (-not $entry.Name.StartsWith('runtimepack.')) { continue }
    $parts = $entry.Name.Substring('runtimepack.'.Length).Split('/')
    $packageName = $parts[0]; $packageVersion = $parts[1]
    $packageSource = $null
    foreach ($folder in $packageFolders) {
        $candidate = Join-Path $folder ($packageName.ToLowerInvariant() + '/' + $packageVersion)
        if (Test-Path -LiteralPath $candidate) { $packageSource = $candidate; break }
    }
    if (-not $packageSource) { throw "Cannot locate licenses for runtime pack $packageName/$packageVersion." }
    $componentDestination = Join-Path $payloadPath "third_party/dotnet/$packageName/$packageVersion"
    New-Item -ItemType Directory -Force $componentDestination | Out-Null
    $licenses = @(Get-ChildItem -LiteralPath $packageSource -File | Where-Object { $_.Name -match '(?i)license|notice|copyright' })
    foreach ($file in $licenses) { Copy-Item -LiteralPath $file.FullName -Destination $componentDestination }
    $nuspec = Get-ChildItem -LiteralPath $packageSource -File -Filter '*.nuspec' | Select-Object -First 1
    if ($nuspec) { Copy-Item -LiteralPath $nuspec.FullName -Destination $componentDestination }
    if ($packageName -like 'Microsoft.NETCore.*' -or $packageName -like 'Microsoft.WindowsDesktop.*') {
        if ($licenses.Count -eq 0) { throw "Runtime license is missing: $packageName/$packageVersion" }
    }
    $componentLines.Add("$packageName $packageVersion")
}
[IO.File]::WriteAllLines((Join-Path $payloadPath 'third_party/dotnet/COMPONENTS.txt'), $componentLines)
$sdkLicenseSource = Join-Path $workspacePath 'third_party/windows-sdk'
if (-not (Test-Path -LiteralPath (Join-Path $sdkLicenseSource 'sdk_license.rtf'))) {
    throw 'The original Windows SDK license is missing from third_party/windows-sdk.'
}
Copy-Item -LiteralPath $sdkLicenseSource -Destination (Join-Path $payloadPath 'third_party') -Recurse

$binaryVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $appDestination 'LandBar.exe')).FileVersion
if ($binaryVersion -ne "$Version.0") { throw "Packaged binary version is $binaryVersion, expected $Version.0." }
[xml]$manifest = Get-Content -LiteralPath (Join-Path $payloadPath 'identity/AppxManifest.xml') -Raw
if ($manifest.Package.Identity.Version -ne "$Version.0") { throw 'Identity version does not match release version.' }
foreach ($required in @('app/LandBar.exe', 'app/LandBar.dll', 'app/coreclr.dll', 'app/Assets/Fonts/OFL.txt', 'LandBar.identity.msix', 'identity/AppxManifest.xml')) {
    if (-not (Test-Path -LiteralPath (Join-Path $payloadPath $required))) { throw "Release file missing: $required" }
}

# Stable ordering and timestamps make the archive independent of file-copy times.
Add-Type -AssemblyName System.IO.Compression
$zipName = "LandBar-v$Version-win-x64.zip"
$zipPath = Join-Path $releasePath $zipName
$fileStream = [IO.File]::Open($zipPath, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite)
try {
    $archive = [IO.Compression.ZipArchive]::new($fileStream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $payloadPath -Recurse -File | Sort-Object FullName) {
            $relative = $file.FullName.Substring($payloadPath.Length + 1).Replace('\', '/')
            $entry = $archive.CreateEntry($relative, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2000, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $input = [IO.File]::OpenRead($file.FullName)
            $output = $entry.Open()
            try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $fileStream.Dispose() }
$digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText((Join-Path $releasePath 'SHA256SUMS.txt'), "$digest  $zipName`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release archive: $zipPath"
Write-Host "SHA256: $digest"
