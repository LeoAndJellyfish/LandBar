using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LandBar.Presentation;

/// <summary>Original, local artwork for the explicitly labelled demonstration media sessions.</summary>
public static class DemoArtwork
{
    private const int Size = 256;
    private static readonly BitmapSource?[] Cache = new BitmapSource?[3];
    private static readonly object CacheLock = new();

    public static BitmapSource Get(int theme)
    {
        int index = (theme % Cache.Length + Cache.Length) % Cache.Length;
        lock (CacheLock)
        {
            if (Cache[index] is { } cached) return cached;
            var visual = new DrawingVisual();
            using (var drawing = visual.RenderOpen())
            {
                switch (index)
                {
                    case 0: DrawNocturne(drawing); break;
                    case 1: DrawBloom(drawing); break;
                    default: DrawStudio(drawing); break;
                }
                AddGrain(drawing, (uint)(71 + index * 97));
            }
            var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            Cache[index] = bitmap;
            return bitmap;
        }
    }

    private static void DrawNocturne(DrawingContext drawing)
    {
        drawing.DrawRectangle(Gradient("#0E1A31", "#2B4054", new Point(0.2, 0), new Point(0.8, 1)), null, Square);
        // The lunar glow is broad and faint; the warm disc carries the visual weight.
        drawing.DrawEllipse(new RadialGradientBrush(
            new GradientStopCollection
            {
                new(Color.FromArgb(22, 234, 221, 191), 0),
                new(Color.FromArgb(0, 234, 221, 191), 1)
            }) { RadiusX = 0.5, RadiusY = 0.5 }, null, new Point(167, 80), 86, 86);
        drawing.DrawEllipse(Gradient("#F2E6CB", "#BBAF96", new Point(0.25, 0), new Point(0.8, 1)), null, new Point(167, 79), 34, 34);

        DrawPath(drawing, "M -20,191 C 23,156 51,167 83,144 C 111,125 145,140 171,153 C 203,166 227,137 278,145 L 278,274 L -20,274 Z", "#344C61");
        DrawPath(drawing, "M -20,213 C 15,184 52,173 85,188 C 124,206 146,211 179,185 C 212,157 245,177 276,169 L 276,274 L -20,274 Z", "#20384D");
        DrawPath(drawing, "M -20,236 C 20,220 45,214 78,223 C 117,234 147,218 174,209 C 216,194 241,211 276,205 L 276,274 L -20,274 Z", "#10283A");
        DrawPath(drawing, "M -20,256 C 42,240 70,249 108,241 C 152,232 192,229 276,242 L 276,274 L -20,274 Z", "#0B1F2E");

        var contour = new Pen(new SolidColorBrush(Color.FromArgb(32, 169, 190, 202)), 0.7);
        drawing.DrawGeometry(null, contour, Geometry.Parse("M -4,222 C 31,208 58,205 90,216 C 123,227 144,212 177,201"));
    }

    private static void DrawBloom(DrawingContext drawing)
    {
        drawing.DrawRectangle(Gradient("#43242D", "#21151D", new Point(0, 0), new Point(0.95, 1)), null, Square);
        DrawPath(drawing, "M 126,153 C 128,184 113,218 104,270 L 118,270 C 119,222 139,184 138,151 Z", "#6B6B55");
        DrawPath(drawing, "M 119,215 C 87,209 73,188 72,175 C 96,176 117,193 122,207 Z", "#77755D");

        // Asymmetric, overlapping lobes retain a recognisable silhouette at compact cover sizes.
        var petal = Geometry.Parse("M 128,145 C 91,133 70,102 80,72 C 88,47 114,37 133,54 C 159,76 157,113 128,145 Z");
        (double Angle, string Top, string Bottom)[] petals =
        {
            (-54, "#B98282", "#8F5A65"),
            (18, "#E0B4A5", "#BE8C8E"),
            (91, "#C99691", "#9D6875"),
            (161, "#D8ABA0", "#B07B84"),
            (234, "#D2A2A0", "#9F6D7A")
        };
        foreach (var (angle, top, bottom) in petals)
        {
            drawing.PushTransform(new RotateTransform(angle, 128, 145));
            drawing.DrawGeometry(Gradient(top, bottom, new Point(0.2, 0), new Point(0.8, 1)), null, petal);
            drawing.Pop();
        }
        drawing.DrawEllipse(Gradient("#75434D", "#4B2A38", new Point(0, 0), new Point(1, 1)), null, new Point(128, 145), 17, 19);
        drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(90, 227, 184, 160)), null, new Point(122, 139), 2, 2);
    }

    private static void DrawStudio(DrawingContext drawing)
    {
        drawing.DrawRectangle(Gradient("#193A31", "#0C211D", new Point(0, 0), new Point(1, 1)), null, Square);
        drawing.DrawRectangle(Brush("#102C25"), null, new Rect(0, 181, 256, 75));
        drawing.DrawEllipse(Brush("#C6BE9E"), null, new Point(45, 47), 8, 8);
        var arch = Geometry.Parse("M 62,256 L 62,120 C 62,32 194,32 194,120 L 194,256 Z");
        drawing.DrawGeometry(Gradient("#93A38A", "#667E6B", new Point(0.1, 0), new Point(0.95, 1)), null, arch);
        var inset = Geometry.Parse("M 81,256 L 81,122 C 81,57 176,57 176,122 L 176,256 Z");
        drawing.DrawGeometry(Gradient("#243F34", "#10281F", new Point(0, 0.1), new Point(0.7, 1)), null, inset);
        var inner = Geometry.Parse("M 99,256 L 99,136 C 99,92 159,92 159,136 L 159,256 Z");
        drawing.DrawGeometry(Gradient("#7C9175", "#4F6A55", new Point(0, 0), new Point(1, 1)), null, inner);
        var opening = Geometry.Parse("M 111,256 L 111,140 C 111,113 148,113 148,140 L 148,256 Z");
        drawing.DrawGeometry(Gradient("#263E30", "#0B2019", new Point(0.2, 0), new Point(0.9, 1)), null, opening);

        DrawPath(drawing, "M 194,181 L 256,217 L 256,256 L 194,256 Z", "#0B241D");
        var edge = new Pen(new SolidColorBrush(Color.FromArgb(50, 179, 193, 161)), 0.8);
        drawing.DrawGeometry(null, edge, Geometry.Parse("M 16,256 L 62,218 M 219,256 L 194,222"));
    }

    private static void AddGrain(DrawingContext drawing, uint seed)
    {
        // Tiny, deterministic vector dots add a restrained paper finish without bitmap/file dependencies.
        for (int dot = 0; dot < 320; dot++)
        {
            seed = seed * 1664525 + 1013904223;
            double x = (seed & 65535) / 65535.0 * Size;
            seed = seed * 1664525 + 1013904223;
            double y = (seed & 65535) / 65535.0 * Size;
            byte alpha = (byte)(4 + (seed >> 20) % 7);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromArgb(alpha, 247, 238, 221)), null, new Point(x, y), 0.35, 0.35);
        }
    }

    private static Rect Square => new(0, 0, Size, Size);
    private static SolidColorBrush Brush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
    private static LinearGradientBrush Gradient(string first, string second, Point start, Point end) => new(
        (Color)ColorConverter.ConvertFromString(first), (Color)ColorConverter.ConvertFromString(second), start, end);
    private static void DrawPath(DrawingContext drawing, string path, string fill) =>
        drawing.DrawGeometry(Brush(fill), null, Geometry.Parse(path));
}
