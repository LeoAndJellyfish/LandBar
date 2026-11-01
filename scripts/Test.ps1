[CmdletBinding()]
param([switch]$IncludeDesktop)
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
# The default gate has no window, session, notification-permission or user-settings dependency.
Write-Host 'Running Codex public sample tests with synthetic app-server data'
& python -m unittest discover -s (Join-Path $workspacePath 'tests/LandBar.CodexPlugin.Tests') -p 'test_*.py'
if ($LASTEXITCODE -ne 0) { throw "Codex public sample tests failed with exit code $LASTEXITCODE." }
Write-Host 'Running public example archive checks'
& python -m unittest discover -s (Join-Path $workspacePath 'tests/LandBar.Release.Tests') -p 'test_*.py'
if ($LASTEXITCODE -ne 0) { throw "Public example archive checks failed with exit code $LASTEXITCODE." }
$projects = @(
    'LandBar.Core.Tests',
    'LandBar.Plugins.Tests',
    'LandBar.MediaSelection.Tests',
    'LandBar.Media.Tests',
    'LandBar.Motion.Tests',
    'LandBar.Spring.Tests',
    'LandBar.Palette.Tests',
    'LandBar.Fonts.Smoke'
)
foreach ($project in $projects) {
    Write-Host "Running $project"
    $testArguments = if ($project -eq 'LandBar.Media.Tests') { @('--', '--cache-only') } else { @() }
    & dotnet run --project (Join-Path $workspacePath "tests/$project") -c Release @testArguments
    if ($LASTEXITCODE -ne 0) { throw "$project failed with exit code $LASTEXITCODE." }
}
& dotnet build (Join-Path $workspacePath 'src/LandBar/LandBar.csproj') -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw "Application build failed with exit code $LASTEXITCODE." }

if ($IncludeDesktop) {
    # Run explicitly on an unlocked Windows desktop. Sources and permission prompts stay disabled.
    $desktopTests = @('LandBar.Idle.Smoke', 'LandBar.Plugins.Smoke', 'LandBar.PluginViews.Smoke', 'LandBar.Preview.Smoke', 'LandBar.Interaction.Smoke', 'LandBar.AutoCollapse.Smoke', 'LandBar.Transition.Smoke', 'LandBar.Topmost.Smoke')
    foreach ($project in $desktopTests) {
        $outputPath = Join-Path $workspacePath "artifacts/tests/$project"
        & dotnet run --project (Join-Path $workspacePath "tests/$project") -c Release -- $outputPath
        if ($LASTEXITCODE -ne 0) { throw "$project failed with exit code $LASTEXITCODE." }
    }
    & dotnet run --project (Join-Path $workspacePath 'tests/LandBar.PluginViews.Smoke') -c Release -- `
        (Join-Path $workspacePath 'artifacts/tests/CodexPublicViews') --codex-only
    if ($LASTEXITCODE -ne 0) { throw "Codex public view checks failed with exit code $LASTEXITCODE." }
    & dotnet run --project (Join-Path $workspacePath 'tests/LandBar.PluginViews.Smoke') -c Release -- `
        (Join-Path $workspacePath 'artifacts/tests/ClockRingAlignment') --clock-ring-only
    if ($LASTEXITCODE -ne 0) { throw "Clock and ring alignment checks failed with exit code $LASTEXITCODE." }
}
Write-Host 'LandBar checks passed.'
