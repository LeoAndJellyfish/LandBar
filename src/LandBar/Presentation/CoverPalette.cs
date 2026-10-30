using System;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LandBar.Presentation;

/// <summary>Restrained cover colours for a dark media surface.</summary>
public sealed record AlbumPalette(Color Accent, Color Surface, Color Foreground);

/// <summary>
/// Extracts a small, deterministic palette from an already decoded image.
/// It neither opens image files nor retains the supplied bitmap. Cache the result by artwork identity.
/// </summary>
public static class CoverPalette
{
    public const int MaximumSampleSide = 24;
    private const int HueBins = 24;

    public static AlbumPalette Neutral { get; } = new(
        Color.FromRgb(190, 193, 202),
        Color.FromRgb(25, 25, 29),
        Color.FromRgb(242, 242, 245));

    public static AlbumPalette FromBitmap(BitmapSource? source)
    {
        if (source is null || !source.CheckAccess()) return Neutral;
        try
        {
            if (source.PixelWidth <= 0 || source.PixelHeight <= 0) return Neutral;
            var sample = MakeSample(source);
            int width = sample.PixelWidth;
            int height = sample.PixelHeight;
            int stride = width * 4;
            var pixels = new byte[stride * height];
            sample.CopyPixels(pixels, stride, 0);

            var weights = new double[HueBins];
            var saturationTotals = new double[HueBins];
            int colouredSamples = 0;
            for (int offset = 0; offset < pixels.Length; offset += 4)
            {
                // Low-alpha edge pixels contribute matte or antialias colours, not cover identity.
                if (pixels[offset + 3] < 192) continue;
                double red = pixels[offset + 2] / 255.0;
                double green = pixels[offset + 1] / 255.0;
                double blue = pixels[offset] / 255.0;
                double maximum = Math.Max(red, Math.Max(green, blue));
                double minimum = Math.Min(red, Math.Min(green, blue));
                double lightness = (maximum + minimum) / 2;
                if (lightness < 0.055 || lightness > 0.92 || maximum - minimum < 0.055) continue;
                double saturation = (maximum - minimum) / (1 - Math.Abs(2 * lightness - 1));
                if (saturation < 0.14) continue;

                double hue = Hue(red, green, blue, maximum, minimum);
                int bin = Math.Min(HueBins - 1, (int)(hue * HueBins));
                // Frequency leads; restrained saturation weighting avoids a bright isolated pixel winning.
                double weight = 0.5 + 0.5 * saturation;
                weights[bin] += weight;
                saturationTotals[bin] += saturation * weight;
                colouredSamples++;
            }

            // A few colourful marks on a monochrome cover should not tint the entire interface.
            if (colouredSamples < Math.Max(1, (int)Math.Ceiling(width * height * 0.04))) return Neutral;
            int dominant = 0;
            double greatestWeight = 0;
            for (int bin = 0; bin < HueBins; bin++)
            {
                double clusterWeight = weights[Wrap(bin - 1)] + weights[bin] + weights[Wrap(bin + 1)];
                if (clusterWeight <= greatestWeight) continue;
                greatestWeight = clusterWeight;
                dominant = bin;
            }

            double hueX = 0, hueY = 0, saturationSum = 0, totalWeight = 0;
            for (int neighbour = -1; neighbour <= 1; neighbour++)
            {
                int bin = Wrap(dominant + neighbour);
                double weight = weights[bin];
                double angle = (bin + 0.5) / HueBins * Math.PI * 2;
                hueX += Math.Cos(angle) * weight;
                hueY += Math.Sin(angle) * weight;
                saturationSum += saturationTotals[bin];
                totalWeight += weight;
            }
            if (totalWeight <= 0) return Neutral;
            double representativeHue = Math.Atan2(hueY, hueX) / (Math.PI * 2);
            if (representativeHue < 0) representativeHue += 1;
            double representativeSaturation = saturationSum / totalWeight;

            // A light muted accent, a near-black colour wash and near-neutral white text form one family.
            var accent = FromHsl(representativeHue, Math.Clamp(0.12 + representativeSaturation * 0.26, 0.20, 0.38), 0.75);
            var surface = FromHsl(representativeHue, Math.Clamp(representativeSaturation * 0.18, 0.08, 0.18), 0.105);
            surface = Color.FromRgb(
                (byte)Math.Clamp((int)surface.R, 20, 35),
                (byte)Math.Clamp((int)surface.G, 20, 35),
                (byte)Math.Clamp((int)surface.B, 20, 35));
            var foreground = FromHsl(representativeHue, 0.055, 0.945);
            return new AlbumPalette(accent, surface, foreground);
        }
        catch (ArgumentException) { return Neutral; }
        catch (InvalidOperationException) { return Neutral; }
        catch (NotSupportedException) { return Neutral; }
    }

    private static BitmapSource MakeSample(BitmapSource source)
    {
        BitmapSource sample = source;
        if (source.PixelWidth > MaximumSampleSide || source.PixelHeight > MaximumSampleSide)
        {
            double scale = Math.Min((double)MaximumSampleSide / source.PixelWidth, (double)MaximumSampleSide / source.PixelHeight);
            sample = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        }
        if (sample.Format != PixelFormats.Bgra32)
            sample = new FormatConvertedBitmap(sample, PixelFormats.Bgra32, null, 0);
        return sample;
    }

    private static int Wrap(int bin) => (bin + HueBins) % HueBins;

    private static double Hue(double red, double green, double blue, double maximum, double minimum)
    {
        double delta = maximum - minimum;
        double hue = maximum == red ? (green - blue) / delta : maximum == green ? (blue - red) / delta + 2 : (red - green) / delta + 4;
        return (hue / 6 + 1) % 1;
    }

    private static Color FromHsl(double hue, double saturation, double lightness)
    {
        double chroma = (1 - Math.Abs(2 * lightness - 1)) * saturation;
        double x = chroma * (1 - Math.Abs(hue * 6 % 2 - 1));
        double match = lightness - chroma / 2;
        (double red, double green, double blue) = (int)(hue * 6) switch
        {
            0 => (chroma, x, 0.0),
            1 => (x, chroma, 0.0),
            2 => (0.0, chroma, x),
            3 => (0.0, x, chroma),
            4 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x)
        };
        return Color.FromRgb(Channel(red + match), Channel(green + match), Channel(blue + match));
    }

    private static byte Channel(double value) => (byte)Math.Clamp((int)Math.Round(value * 255), 0, 255);
}
