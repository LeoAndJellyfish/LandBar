[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$workspacePath = Split-Path $PSScriptRoot -Parent
$applicationPath = [IO.Path]::GetFullPath((Join-Path $workspacePath 'dist/app/LandBar.exe'))
$running = @(Get-Process -Name LandBar -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [string]::Equals($_.Path, $applicationPath, [StringComparison]::OrdinalIgnoreCase)
})
if ($running.Count -eq 0) { return }

# WM_CLOSE follows the application's Exit path, removing its tray item before replacing files.
# Only visible windows belonging to this exact published executable are addressed.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class LandBarGracefulExit {
    private delegate bool EnumCallback(IntPtr hwnd, IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    public static void Request(int processId) {
        var handles = new List<IntPtr>();
        EnumWindows((hwnd, state) => {
            GetWindowThreadProcessId(hwnd, out uint owner);
            if (owner == processId && IsWindowVisible(hwnd)) handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        foreach (var hwnd in handles) PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
    }
}
'@
foreach ($process in $running) { [LandBarGracefulExit]::Request($process.Id) }
foreach ($process in $running) {
    if (-not $process.WaitForExit(5000)) {
        throw "LandBar did not finish closing. Exit it from the tray before updating: $($process.Id)"
    }
}
