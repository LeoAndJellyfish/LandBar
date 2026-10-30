using System.Windows.Media;
using System.Windows.Media.Imaging;
using LandBar.Presentation;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var tests = new (string Name, Action Test)[]
        {
            ("Missing and transparent art use a neutral family", () =>
            {
                Assert(CoverPalette.FromBitmap(null) == CoverPalette.Neutral);
                Assert(CoverPalette.FromBitmap(Image(24, 24, (_, _) => Color.FromArgb(0, 240, 30, 20))) == CoverPalette.Neutral);
            }),
            ("Monochrome art and sparse bright marks stay neutral", () =>
            {
                Assert(CoverPalette.FromBitmap(Image(24, 24, (x, _) => Color.FromRgb((byte)(x * 10), (byte)(x * 10), (byte)(x * 10)))) == CoverPalette.Neutral);
                Assert(CoverPalette.FromBitmap(Image(24, 24, (x, y) => x == 12 && y == 12 ? Colors.Red : Colors.White)) == CoverPalette.Neutral);
            }),
            ("A dominant cool region controls the palette", () =>
            {
                var art = Image(24, 24, (x, _) => x < 19 ? Colors.RoyalBlue : Colors.OrangeRed);
                var palette = CoverPalette.FromBitmap(art);
                Assert(palette.Accent.B > palette.Accent.R && palette.Accent.B > palette.Accent.G);
                Assert(palette.Surface.B >= palette.Surface.R);
                Assert(palette == CoverPalette.FromBitmap(art));
            }),
            ("One-pixel and non-alpha RGB covers remain usable", () =>
            {
                var palette = CoverPalette.FromBitmap(Image(1, 1, (_, _) => Colors.DarkOrange));
                Assert(palette != CoverPalette.Neutral);
                Assert(palette.Accent.R > palette.Accent.G && palette.Accent.G > palette.Accent.B);
                var rgb = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgr24, null, new byte[] { 0, 140, 0 }, 3);
                rgb.Freeze();
                Assert(CoverPalette.FromBitmap(rgb) == CoverPalette.FromBitmap(Image(1, 1, (_, _) => Color.FromRgb(0, 140, 0))));
            }),
            ("Bright art produces restrained surfaces and legible accents", () =>
            {
                foreach (var colour in new[] { Colors.Red, Colors.Lime, Colors.Blue, Colors.Magenta, Colors.Cyan, Colors.Yellow })
                {
                    var palette = CoverPalette.FromBitmap(Image(24, 24, (_, _) => colour));
                    foreach (byte channel in new[] { palette.Surface.R, palette.Surface.G, palette.Surface.B }) Assert(channel is >= 20 and <= 35);
                    Assert(Contrast(palette.Accent, palette.Surface) >= 6);
                    Assert(Contrast(palette.Foreground, palette.Surface) >= 12);
                    Assert(Math.Max(palette.Accent.R, Math.Max(palette.Accent.G, palette.Accent.B)) - Math.Min(palette.Accent.R, Math.Min(palette.Accent.G, palette.Accent.B)) <= 55);
                }
            }),
            ("Large and extreme aspect images remain bounded and usable", () =>
            {
                var square = CoverPalette.FromBitmap(Image(720, 720, (_, _) => Colors.SeaGreen));
                var narrow = CoverPalette.FromBitmap(Image(1, 1024, (_, _) => Colors.SeaGreen));
                Assert(square != CoverPalette.Neutral);
                Assert(narrow != CoverPalette.Neutral);
                Assert(square.Accent.G > square.Accent.R);
                Assert(narrow.Accent.G > narrow.Accent.R);
            })
        };
        int passed = 0;
        foreach (var (name, test) in tests)
        {
            try { test(); passed++; Console.WriteLine("PASS " + name); }
            catch (Exception exception) { Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
        }
        Console.WriteLine($"{passed}/{tests.Length} passed");
        return passed == tests.Length ? 0 : 1;
    }

    private static BitmapSource Image(int width, int height, Func<int, int, Color> pixel)
    {
        byte[] buffer = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            var colour = pixel(x, y);
            int offset = (y * width + x) * 4;
            buffer[offset] = colour.B; buffer[offset + 1] = colour.G; buffer[offset + 2] = colour.R; buffer[offset + 3] = colour.A;
        }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, buffer, width * 4);
        source.Freeze();
        return source;
    }
    private static double Luminance(Color colour)
    {
        static double Linear(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Linear(colour.R) + 0.7152 * Linear(colour.G) + 0.0722 * Linear(colour.B);
    }
    private static double Contrast(Color first, Color second)
    {
        double a = Luminance(first), b = Luminance(second);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Palette requirement failed"); }
}
