# Windows text rendering smoke check

This interactive Windows test displays its own non-activating WPF layered window,
captures only that window's desktop rectangle, and closes automatically. It uses
the same embedded Noto Sans SC 400/600 files as LandBar at 14, 15, and 20 DIP.
It does not change any Windows font-smoothing settings.

```powershell
dotnet run --project tests/LandBar.TextRendering.Smoke -c Release -- artifacts/text-aa-check 650
dotnet run --project tests/LandBar.TextRendering.Smoke -c Release -- artifacts/text-aa-check-repeat 2000
python tests/LandBar.TextRendering.Smoke/create-comparison.py
```

The optional comparison command requires Pillow. The two application runs need
an interactive unlocked desktop; screen capture is not a headless rendering test.
The window uses default hardware rendering and is topmost only during the check.

`desktop-native.png` is captured with desktop BitBlt after DwmFlush, before the
separate `RenderTargetBitmap` capture. `render-target.png` is retained to expose
differences in the off-screen rendering path. `report.json` records native and
off-screen edge pixels independently, DPI, render tier, and read-only SPI font
smoothing values. The selected Animated grayscale path must produce antialiased
edges with at least 128 observed intermediate levels and no RGB colour fringes.

The comparison retains `Ideal + Grayscale + Fixed` as its baseline. `Animated`
is a rendering hint; this test creates no Timeline or continuously running
animation. The single DispatcherTimer schedules the screenshot and shuts down.
