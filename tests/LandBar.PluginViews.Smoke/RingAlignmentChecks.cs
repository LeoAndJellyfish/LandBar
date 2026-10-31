using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandBar.Plugins;
using LandBar.Presentation;

/// <summary>Measures rendered numeral pixels, independently of the label's positioning formula.</summary>
internal static class RingAlignmentChecks
{
    public static int Run(string output, FontFamily font)
    {
        Directory.CreateDirectory(output);
        var measurements = new List<object>();
        int failures = 0, count = 0;
        foreach (double size in new[] { 28d, 48d })
        foreach (string label in new[] { "0", "44", "100", "—" })
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        {
            using var presenter = new PluginViewPresenter();
            TextElement.SetFontFamily(presenter, font);
            TextOptions.SetTextFormattingMode(presenter, TextFormattingMode.Ideal);
            TextOptions.SetTextRenderingMode(presenter, TextRenderingMode.Grayscale);
            TextOptions.SetTextHintingMode(presenter, TextHintingMode.Animated);
            presenter.Present(new PluginView("ring-test", "ring", "idle.right", new PluginNode
            {
                Type = "ring", Width = size, Height = size, Text = label,
                FontSize = size == 28 ? 12 : 16, Color = "#FFFFFF",
                Value = 1, Thickness = 3, TrackColor = "#FFFFFF"
            }, DateTimeOffset.UnixEpoch), size, size);
            VisualTreeHelper.SetRootDpi(presenter, new DpiScale(scale, scale));
            presenter.Measure(new Size(size, size));
            presenter.Arrange(new Rect(0, 0, size, size));
            presenter.UpdateLayout();
            var ring = Descendants<PluginRing>(presenter).Single();
            var labelElement = ((System.Windows.Controls.Panel)VisualTreeHelper.GetParent(ring)).Children
                .OfType<FrameworkElement>().Last();
            double actualTextDpi = VisualTreeHelper.GetDpi(labelElement).PixelsPerDip;
            string suffix = $"{size:0}-{(label == "—" ? "dash" : label)}-{scale * 100:0}";
            if (label == "100") Save(Render(presenter, size, scale), Path.Combine(output, "ring-" + suffix + ".png"));
            // Hidden preserves the actual ring layout while isolating the text's rasterized pixels.
            ring.Visibility = Visibility.Hidden;
            presenter.UpdateLayout();
            var textBitmap = Render(presenter, size, scale);
            var ink = InkBounds(textBitmap);
            double errorX = Math.Abs((ink.Left + ink.Right + 1) / 2d - size * scale / 2);
            double errorY = Math.Abs((ink.Top + ink.Bottom + 1) / 2d - size * scale / 2);
            bool passed = !ink.IsEmpty && errorX <= .75 && errorY <= .75 && Math.Abs(actualTextDpi - scale) < .0001;
            count++;
            if (!passed) failures++;
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {size:0} DIP {label} at {scale * 100:0}%: ink-center dx={errorX:0.###}px dy={errorY:0.###}px");
            measurements.Add(new { size, label, scale, actualTextDpi, passed, errorX, errorY, ink.Left, ink.Top, ink.Right, ink.Bottom });
            if (label == "100") Save(textBitmap, Path.Combine(output, "ink-" + suffix + ".png"));
        }
        File.WriteAllText(Path.Combine(output, "ring-alignment.json"), JsonSerializer.Serialize(new
        {
            passed = failures == 0, count, failures, privateAccountData = false,
            startsSystemSources = false, movesUserPointer = false, measurements
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"{count - failures}/{count} ring pixel alignment checks passed");
        return failures == 0 ? 0 : 1;
    }

    private static RenderTargetBitmap Render(FrameworkElement presenter, double size, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(size * scale), (int)Math.Ceiling(size * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(presenter);
        return bitmap;
    }

    private static PixelBounds InkBounds(BitmapSource bitmap)
    {
        int stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);
        int left = bitmap.PixelWidth, top = bitmap.PixelHeight, right = -1, bottom = -1;
        for (int y = 0; y < bitmap.PixelHeight; y++)
        for (int x = 0; x < bitmap.PixelWidth; x++)
        {
            // Ignore only near-transparent edge coverage; the rectangle comes from actual glyph ink.
            if (pixels[y * stride + x * 4 + 3] < 32) continue;
            left = Math.Min(left, x); top = Math.Min(top, y);
            right = Math.Max(right, x); bottom = Math.Max(bottom, y);
        }
        return new(left, top, right, bottom);
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T result) yield return result;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static void Save(BitmapSource bitmap, string path)
    {
        using var stream = File.Create(path);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(stream);
    }

    private sealed record PixelBounds(int Left, int Top, int Right, int Bottom)
    {
        public bool IsEmpty => Right < Left || Bottom < Top;
    }
}
