using System;
using System.Drawing;
using Application = System.Windows.Application;

namespace LandBar.Presentation;

internal static class ApplicationBrand
{
    public const string Name = "LandBar";
    public const string IconUri = "pack://application:,,,/LandBar;component/Assets/Brand/LandBar.ico";

    public static Icon CreateTrayIcon(Size size)
    {
        using var stream = Application.GetResourceStream(new Uri(IconUri))!.Stream;
        using var source = new Icon(stream, size);
        return (Icon)source.Clone();
    }
}
