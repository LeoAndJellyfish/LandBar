using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Drawing = System.Drawing;

internal static class Program
{
    private sealed record Variant(string Name, TextFormattingMode Formatting, TextRenderingMode Rendering, TextHintingMode Hinting, bool OpaqueHint);
    private sealed record Sample(Variant Variant, double Size, TextBlock Text);
    private static readonly List<Sample> Samples = [];
    private static readonly FontFamily Family = new(new Uri("pack://application:,,,/"), "./Assets/Fonts/#Noto Sans SC");

    [STAThread]
    private static int Main(string[] args)
    {
        var output = args.FirstOrDefault() ?? Path.GetFullPath("artifacts/text-aa-check");
        Directory.CreateDirectory(output);
        int captureDelay = args.Length > 1 && int.TryParse(args[1], out var delay) ? Math.Clamp(delay, 250, 5000) : 650;
        var variants = new[]
        {
            new Variant("Display + Auto (current)", TextFormattingMode.Display, TextRenderingMode.Auto, TextHintingMode.Auto, false),
            new Variant("Ideal + Grayscale + Fixed", TextFormattingMode.Ideal, TextRenderingMode.Grayscale, TextHintingMode.Fixed, false),
            new Variant("Ideal + Grayscale + Animated (selected)", TextFormattingMode.Ideal, TextRenderingMode.Grayscale, TextHintingMode.Animated, false),
            new Variant("Ideal + ClearType + opaque TextBlock", TextFormattingMode.Ideal, TextRenderingMode.ClearType, TextHintingMode.Fixed, true),
        };
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var panel = new StackPanel { Background = Gray(8), Margin = new Thickness(20, 12, 20, 12) };
        panel.Children.Add(new TextBlock { Text = "Noto Sans SC · 14 / 15 / 20 DIP · layered WPF window", FontFamily = new FontFamily("Segoe UI"), FontSize = 15, Foreground = Gray(190), Margin = new Thickness(0, 0, 0, 10) });
        foreach (var variant in variants)
        {
            var group = new StackPanel { ClipToBounds = true, Margin = new Thickness(0, 0, 0, 11) };
            group.Children.Add(new TextBlock { Text = variant.Name, FontFamily = new FontFamily("Segoe UI"), FontSize = 13, Foreground = Gray(138), Margin = new Thickness(0, 0, 0, 3) });
            foreach (var size in new double[] { 14, 15, 20 })
            {
                var text = new TextBlock
                {
                    Text = size == 20 ? "屏幕截图已复制到剪贴板 · 媒体" : "媒体 3   通知   音乐播放器   Noto Sans SC / Clair de lune",
                    FontFamily = Family, FontSize = size,
                    FontWeight = size == 20 ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = Gray(240), Height = size == 20 ? 33 : 26,
                    Background = variant.OpaqueHint ? Gray(8) : null,
                    UseLayoutRounding = true,
                };
                TextOptions.SetTextFormattingMode(text, variant.Formatting);
                TextOptions.SetTextRenderingMode(text, variant.Rendering);
                TextOptions.SetTextHintingMode(text, variant.Hinting);
                if (variant.OpaqueHint) RenderOptions.SetClearTypeHint(text, ClearTypeHint.Enabled);
                Samples.Add(new Sample(variant, size, text));
                group.Children.Add(text);
            }
            panel.Children.Add(group);
        }
        var surface = new Border { Background = Gray(8), Child = panel };
        var window = new Window
        {
            Title = "LandBar Text Rendering Smoke", Content = surface,
            Width = 910, Height = 525, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Topmost = true,
            Left = Math.Max(10, SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - 910) / 2),
            Top = Math.Max(58, SystemParameters.WorkArea.Top + 58),
        };
        int exit = 1;
        window.Loaded += (_, _) =>
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(captureDelay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                try
                {
                    window.UpdateLayout();
                    var dpi = VisualTreeHelper.GetDpi(window);
                    DwmFlush();
                    GetWindowRect(new WindowInteropHelper(window).Handle, out var bounds);
                    using var native = new Drawing.Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var graphics = Drawing.Graphics.FromImage(native))
                    {
                        var destination = graphics.GetHdc();
                        var desktop = GetDC(IntPtr.Zero);
                        try
                        {
                            if (!BitBlt(destination, 0, 0, native.Width, native.Height, desktop, bounds.Left, bounds.Top, 0x00CC0020 | 0x40000000))
                                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                        }
                        finally { ReleaseDC(IntPtr.Zero, desktop); graphics.ReleaseHdc(destination); }
                    }
                    native.Save(Path.Combine(output, "desktop-native.png"), Drawing.Imaging.ImageFormat.Png);
                    var bitmap = new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(surface.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
                    bitmap.Render(surface); Save(bitmap, Path.Combine(output, "render-target.png"));
                    var sampleResults = Samples.Select(s =>
                    {
                        var point = s.Text.PointToScreen(new Point());
                        var rect = new Drawing.Rectangle((int)Math.Round(point.X - bounds.Left), (int)Math.Round(point.Y - bounds.Top), (int)Math.Ceiling(s.Text.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(s.Text.ActualHeight * dpi.DpiScaleY));
                        return new { variant = s.Variant.Name, size = s.Size, rectangle = new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height }, native = Analyze(native, rect), renderTarget = Analyze(bitmap, rect) };
                    }).ToArray();
                    bool smoothing = false; uint smoothingType = 0, contrast = 0;
                    bool smoothingRead = SystemParametersInfoBool(0x004A, 0, ref smoothing, 0);
                    bool typeRead = SystemParametersInfoUInt(0x200A, 0, ref smoothingType, 0);
                    bool contrastRead = SystemParametersInfoUInt(0x200C, 0, ref contrast, 0);
                    var report = new
                    {
                        os = Environment.OSVersion.Version.ToString(),
                        dpiScale = dpi.DpiScaleX,
                        renderTier = RenderCapability.Tier >> 16,
                        captureDelayMs = captureDelay,
                        fontSmoothing = new { readable = smoothingRead, enabled = smoothing, typeReadable = typeRead, type = smoothingType, contrastReadable = contrastRead, contrast },
                        nativeCapture = "Desktop pixels of this test application's own topmost window, after DwmFlush. Not RenderTargetBitmap.",
                        renderTargetCapture = "Separate WPF RenderTargetBitmap; its intermediate render path may differ from desktop ClearType.",
                        variants = sampleResults,
                    };
                    File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
                    foreach (var variant in sampleResults.Where(s => s.variant == "Ideal + Grayscale + Animated (selected)"))
                    {
                        if (variant.native.IntermediatePixels < 50) throw new Exception("Explicit grayscale produced too few anti-aliased edge pixels.");
                        if (variant.native.ChromaticPixels != 0) throw new Exception("Explicit grayscale produced colour fringes.");
                        if (variant.native.UniqueIntermediateLevels < 128) throw new Exception("Animated hinting produced too few intermediate grayscale levels.");
                    }
                    Console.WriteLine(JsonSerializer.Serialize(new { passed = true, output = Path.GetFullPath(output), dpiScale = dpi.DpiScaleX, fontSmoothing = smoothing, fontSmoothingType = smoothingType, captureDelayMs = captureDelay }));
                    exit = 0;
                }
                catch (Exception exception) { Console.Error.WriteLine(exception); }
                finally { window.Close(); app.Shutdown(); }
            };
            timer.Start();
        };
        window.Show(); app.Run(); return exit;
    }

    private sealed record PixelAnalysis(int IntermediatePixels, int ChromaticPixels, int UniqueIntermediateLevels);
    private static PixelAnalysis Analyze(Drawing.Bitmap bitmap, Drawing.Rectangle rectangle)
    {
        int intermediate = 0, chromatic = 0; var levels = new HashSet<byte>();
        for (int y = Math.Max(0, rectangle.Top); y < Math.Min(bitmap.Height, rectangle.Bottom); y++)
            for (int x = Math.Max(0, rectangle.Left); x < Math.Min(bitmap.Width, rectangle.Right); x++)
            {
                var color = bitmap.GetPixel(x, y);
                Add(color.R, color.G, color.B, ref intermediate, ref chromatic, levels);
            }
        return new(intermediate, chromatic, levels.Count);
    }
    private static PixelAnalysis Analyze(BitmapSource bitmap, Drawing.Rectangle rectangle)
    {
        int stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        int intermediate = 0, chromatic = 0; var levels = new HashSet<byte>();
        for (int y = Math.Max(0, rectangle.Top); y < Math.Min(bitmap.PixelHeight, rectangle.Bottom); y++)
            for (int x = Math.Max(0, rectangle.Left); x < Math.Min(bitmap.PixelWidth, rectangle.Right); x++)
            {
                int offset = y * stride + x * 4;
                Add(pixels[offset + 2], pixels[offset + 1], pixels[offset], ref intermediate, ref chromatic, levels);
            }
        return new(intermediate, chromatic, levels.Count);
    }
    private static void Add(byte red, byte green, byte blue, ref int intermediate, ref int chromatic, HashSet<byte> levels)
    {
        if (Math.Max(red, Math.Max(green, blue)) - Math.Min(red, Math.Min(green, blue)) > 2) chromatic++;
        if (green > 8 && green < 240) { intermediate++; levels.Add(green); }
    }
    private static SolidColorBrush Gray(byte value) { var b = new SolidColorBrush(Color.FromRgb(value,value,value)); b.Freeze(); return b; }
    private static void Save(BitmapSource bitmap, string path) { using var stream = File.Create(path); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream); }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out RECT rectangle);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint operation);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] private static extern bool SystemParametersInfoBool(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)] private static extern bool SystemParametersInfoUInt(uint action, uint parameter, ref uint value, uint flags);
}
