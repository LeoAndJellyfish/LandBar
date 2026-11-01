using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;
using LandBar.Services;

/// <summary>Rasterizes the actual production compact subtree at each root DPI without opening a native window.</summary>
internal sealed class ClockRingAlignmentProbe(string output) : Application
{
    private LandBar.MainWindow _window = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        int result = 1;
        try { result = RunChecks(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "clock-ring-error.txt"), error.ToString());
            Console.WriteLine("FAIL " + error);
        }
        finally
        {
            if (_window is not null)
            {
                Field("_closing").SetValue(_window, true);
                ((DispatcherTimer)Field("_timer").GetValue(_window)!).Stop();
                Invoke("DisposePluginViews");
                foreach (string name in new[] { "_notifications", "_media" })
                    (Field(name).GetValue(_window) as IDisposable)?.Dispose();
                _window.Close();
            }
            Shutdown(result);
        }
    }

    private int RunChecks()
    {
        _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false);
        var settings = (AppSettings)Field("_settings").GetValue(_window)!;
        settings.Quiet = true;
        var clock = Element<TextBlock>("ClockLabel");
        var content = Element<Grid>("ContentLayout");
        ((Panel)content.Parent).Children.Remove(content);
        var frame = new Grid { UseLayoutRounding = false, SnapsToDevicePixels = false };
        var background = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 8, 10)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(43, 43, 50)),
            BorderThickness = new Thickness(.7), CornerRadius = new CornerRadius(22)
        };
        frame.Children.Add(background); frame.Children.Add(content);
        TextElement.SetFontFamily(frame, _window.FontFamily);
        TextOptions.SetTextFormattingMode(frame, TextOptions.GetTextFormattingMode(_window));
        TextOptions.SetTextRenderingMode(frame, TextOptions.GetTextRenderingMode(_window));
        TextOptions.SetTextHintingMode(frame, TextOptions.GetTextHintingMode(_window));
        var left = Element<PluginViewPresenter>("PluginIdleLeft"); left.Visibility = Visibility.Collapsed;
        var center = Element<PluginViewPresenter>("PluginIdleContent"); center.Visibility = Visibility.Collapsed;
        Element<ColumnDefinition>("CompactLeftColumn").Width = new GridLength(0);
        Element<ColumnDefinition>("CompactRightColumn").Width = new GridLength(36);
        var right = Element<PluginViewPresenter>("PluginIdleRight"); right.Visibility = Visibility.Visible;
        var covers = Element<Grid>("CompactCovers");
        var bars = Element<StackPanel>("AudioBars");
        var measurements = new List<object>();
        var failures = new List<string>();
        var compatibility = new List<string>();
        int count = 0;
        double maximumClockError = 0, maximumRingError = 0, maximumCenterDifference = 0;
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (int mediaCount in new[] { 0, 1, 2 })
        foreach (string time in new[] { "23:39", "08:10", "11:11", "00:00" })
        foreach (string label in new[] { "0", "41", "100", "—" })
        {
            var sessions = Enumerable.Range(0, mediaCount).Select(index => new MediaSessionSnapshot(
                index == 0 ? "demo.music" : "demo.second", "synthetic.app." + index, "合成播放器", "合成曲目", "合成作者",
                null, true, true, true, true, true)).ToArray();
            Invoke("RenderCompact", (object)sessions);
            clock.Text = time;
            right.Present(new PluginView("synthetic-codex", "ring", "idle.widget", new PluginNode
            {
                Type = "ring", Width = 28, Height = 28, FontSize = 12, Text = label,
                Value = label == "—" ? 0 : .41, Color = "#BEDDC9", TrackColor = "#292D30", Thickness = 3
            }, DateTimeOffset.UnixEpoch, Placement: new PluginPlacement("right", Array.AsReadOnly(new[] { "left", "right" }))), 64, 36);
            var ring = Descendants<PluginRing>(right).Single();
            var ringLabel = Descendants<PluginRingLabel>(right).Single();
            double desiredWidth = (mediaCount == 0 ? 112 : mediaCount == 1 ? 180 : 208) + 36;
            var native = NativeWindowBounds.Calculate(0, 0, 1920, 0, desiredWidth, 44, scale, scale);
            double width = native.Width / scale, height = native.Height / scale;
            frame.Width = width; frame.Height = height;
            frame.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 22, 22);
            double border = Math.Round(.7 * scale);
            content.Width = (native.Width - border * 2) / scale;
            content.Height = (native.Height - border * 2) / scale;
            content.Margin = new Thickness(0, border / scale, 0, 0);
            VisualTreeHelper.SetRootDpi(frame, new DpiScale(scale, scale));
            Layout(frame, width, height);

            background.Opacity = 0; covers.Opacity = 0; bars.Opacity = 0; right.Opacity = 0; clock.Opacity = 1;
            var clockBitmap = Render(frame, native.Width, native.Height, scale);
            var clockInk = InkBounds(clockBitmap);
            clock.Opacity = 0; right.Opacity = 1; ring.Visibility = Visibility.Hidden;
            var ringBitmap = Render(frame, native.Width, native.Height, scale);
            var ringInk = InkBounds(ringBitmap);
            double rowCenter = native.Height / 2d;
            double clockCenter = clockInk.CenterY, ringCenter = ringInk.CenterY;
            double clockError = Math.Abs(clockCenter - rowCenter), ringError = Math.Abs(ringCenter - rowCenter);
            double centerDifference = Math.Abs(clockCenter - ringCenter);
            double actualClockDpi = VisualTreeHelper.GetDpi(clock).PixelsPerDip;
            double actualRingDpi = VisualTreeHelper.GetDpi(ringLabel).PixelsPerDip;
            bool passed = !clockInk.IsEmpty && !ringInk.IsEmpty && clockError <= .75 && ringError <= .75 &&
                centerDifference <= 1 && Math.Abs(actualClockDpi - scale) < .0001 && Math.Abs(actualRingDpi - scale) < .0001 &&
                clock.FontSize == (mediaCount == 0 ? 16 : 14) && ringLabel.FontSize == 12;
            count++;
            string name = $"{mediaCount} media · {time} / {label} at {scale * 100:0}%";
            if (!passed) failures.Add(name);
            maximumClockError = Math.Max(maximumClockError, clockError);
            maximumRingError = Math.Max(maximumRingError, ringError);
            maximumCenterDifference = Math.Max(maximumCenterDifference, centerDifference);
            measurements.Add(new { mediaCount, time, label, scale, passed, rowCenter, clockCenter, ringCenter, clockError, ringError,
                clockEmpty = clockInk.IsEmpty, ringEmpty = ringInk.IsEmpty,
                centerDifference, clockFontSize = clock.FontSize, actualClockDpi, actualRingDpi, clockType = clock.GetType().FullName });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}: clock={clockCenter:0.###} ring={ringCenter:0.###} target={rowCenter:0.###} px");
            if (time == "23:39" && label == "41")
            {
                string suffix = $"{mediaCount}-media-{scale * 100:0}";
                Save(clockBitmap, Path.Combine(output, "clock-ink-" + suffix + ".png"));
                Save(ringBitmap, Path.Combine(output, "ring-ink-" + suffix + ".png"));
                clock.Opacity = 1; ring.Visibility = Visibility.Visible; background.Opacity = 1; covers.Opacity = 1; bars.Opacity = .3;
                Save(Render(frame, native.Width, native.Height, scale), Path.Combine(output, "compact-clock-ring-" + suffix + ".png"));
            }
            clock.Opacity = 1; ring.Visibility = Visibility.Visible; background.Opacity = 1; covers.Opacity = 1; bars.Opacity = .3;
        }
        if (clock is TextBlock && clock.Text == "00:00" && clock.FontSize == 14)
            compatibility.Add("The production clock retains TextBlock identity, dynamic Text and FontSize updates");
        if (clock.FontFamily.Source.Contains("Noto Sans SC", StringComparison.Ordinal))
            compatibility.Add("The production embedded Noto Sans SC font remains selected");
        if (Typography.GetNumeralAlignment(clock) == FontNumeralAlignment.Tabular)
            compatibility.Add("The production tabular numeral typography setting remains selected");
        if (compatibility.Count != 3) failures.Add("Clock production property compatibility");
        File.WriteAllText(Path.Combine(output, "clock-ring-alignment.json"), JsonSerializer.Serialize(new
        {
            passed = failures.Count == 0, count, failures, maximumClockError, maximumRingError, maximumCenterDifference,
            clockType = clock.GetType().FullName, startsSystemSources = false, startsNativeWindow = false,
            privateAccountData = false, movesUserPointer = false, usesProductionCompactSubtree = true, compatibility, measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{count - failures.Count} / {count} clock/ring raster cases passed");
        return failures.Count == 0 ? 0 : 1;
    }

    private static void Layout(FrameworkElement root, double width, double height)
    {
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
    }
    private static RenderTargetBitmap Render(FrameworkElement root, int width, int height, double scale)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(root); return bitmap;
    }
    private static PixelBounds InkBounds(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4; var pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
        int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.PixelHeight; y++)
        for (int x = 0; x < bitmap.PixelWidth; x++)
        {
            if (pixels[y * stride + x * 4 + 3] < 32) continue;
            left = Math.Min(left, x); top = Math.Min(top, y); right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return new(left, top, right, bottom);
    }
    private static void Save(BitmapSource bitmap, string path)
    {
        using var file = File.Create(path); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
    }
    private T Element<T>(string name) where T : DependencyObject => (T)_window.FindName(name);
    private static FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private void Invoke(string name, params object?[] arguments) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(_window, arguments);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private sealed record PixelBounds(int Left, int Top, int Right, int Bottom)
    {
        public bool IsEmpty => Right < Left || Bottom < Top;
        public double CenterY => IsEmpty ? 0 : (Top + Bottom + 1) / 2d;
    }
}
