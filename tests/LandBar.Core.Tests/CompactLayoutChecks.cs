using LandBar.Core;

internal static class CompactLayoutChecks
{
    internal static void OccupiedTracks()
    {
        var idle = CompactWidgetLayout.Calculate(680, 112, 0, 0);
        Require(idle.Width == 112 && idle.LeftWidth == 0 && idle.RightWidth == 0 && idle.CenterWidth == 84,
            "Idle clock baseline changed");
        var asymmetric = CompactWidgetLayout.Calculate(680, 112, 64, 28);
        Require(asymmetric.Width == 220 && asymmetric.LeftWidth == 72 && asymmetric.RightWidth == 36 &&
            asymmetric.CenterWidth == 84 && asymmetric.ShowLeft && asymmetric.ShowRight,
            "Different widgets must consume their individual widths and one gap each");
        var mirrored = CompactWidgetLayout.Calculate(680, 112, 28, 64);
        Require(asymmetric.Width == mirrored.Width && asymmetric.CenterWidth == mirrored.CenterWidth &&
            asymmetric.LeftWidth == mirrored.RightWidth && asymmetric.RightWidth == mirrored.LeftWidth,
            "Mirroring the widgets must swap tracks without changing the shell width");
        var leftOnly = CompactWidgetLayout.Calculate(680, 112, 28, 0);
        Require(leftOnly.Width == 148 && leftOnly.LeftWidth == 36 && leftOnly.RightWidth == 0 &&
            leftOnly.ShowLeft && !leftOnly.ShowRight,
            "A left-only widget must leave no right-side placeholder");
        var rightOnly = CompactWidgetLayout.Calculate(680, 112, 0, 28);
        Require(rightOnly.Width == 148 && rightOnly.LeftWidth == 0 && rightOnly.RightWidth == 36 &&
            !rightOnly.ShowLeft && rightOnly.ShowRight,
            "A right-only widget must leave no left-side placeholder");
        var custom = CompactWidgetLayout.Calculate(680, 112, 64, 28, 400);
        Require(custom.Width == 536 && custom.CenterWidth == 400,
            "A custom body must add only occupied tracks and safe shell padding");
    }

    internal static void RemovalAndRestoration()
    {
        foreach (int mediaCount in new[] { 0, 1, 2 })
        foreach (double? contentWidth in new double?[] { null, 116, 400 })
        {
            double builtinWidth = mediaCount == 0 ? 112 : mediaCount == 1 ? 180 : 208;
            var both = CompactWidgetLayout.Calculate(680, builtinWidth, 28, 28, contentWidth, mediaCount);
            var removedLeft = CompactWidgetLayout.Calculate(680, builtinWidth, 0, 28, contentWidth, mediaCount);
            var removedRight = CompactWidgetLayout.Calculate(680, builtinWidth, 28, 0, contentWidth, mediaCount);
            var none = CompactWidgetLayout.Calculate(680, builtinWidth, 0, 0, contentWidth, mediaCount);
            Require(both.Width - removedLeft.Width == 36 && both.Width - removedRight.Width == 36 &&
                removedLeft.Width - none.Width == 36 && removedRight.Width - none.Width == 36,
                "Removing either widget must reclaim its complete track instead of reserving an opposite copy");
            Require(removedLeft.LeftWidth == 0 && removedLeft.RightWidth == 36 &&
                removedRight.LeftWidth == 36 && removedRight.RightWidth == 0,
                "The occupied side must retain its own width while the empty side collapses");
            Require(both.CenterWidth == removedLeft.CenterWidth && both.CenterWidth == removedRight.CenterWidth &&
                both.CenterWidth == none.CenterWidth,
                "Removing widgets must preserve the media, clock or custom body's preferred width");
            Require(both == CompactWidgetLayout.Calculate(680, builtinWidth, 28, 28, contentWidth, mediaCount),
                "Restoring both widgets must reproduce the original compact geometry");
        }
    }

    internal static void Constrained()
    {
        var media = CompactWidgetLayout.Calculate(160, 180, 64, 64, mediaCount: 1);
        Require(media.ShowLeft && !media.ShowRight && media.LeftWidth == 72 && media.RightWidth == 0 &&
            media.MediaDetail == CompactMediaDetail.Clock && media.CenterWidth == 60 && media.Width == 160,
            "A narrow host must retain one complete wide widget when one track and the clock still fit");
        var mixed = CompactWidgetLayout.Calculate(220, 208, 64, 28, mediaCount: 2);
        Require(mixed.ShowLeft && mixed.ShowRight && mixed.MediaDetail == CompactMediaDetail.Cover && mixed.CenterWidth == 84 &&
            mixed.Width == 220, "A narrow host must retain both unequal widgets when their actual widths fit");
        var clock = CompactWidgetLayout.Calculate(204, 180, 64, 28, mediaCount: 1);
        Require(clock.ShowLeft && clock.ShowRight && clock.CenterWidth == 68 && clock.MediaDetail == CompactMediaDetail.Clock &&
            clock.Width == 204, "Both widgets must remain when a compact clock fits the remaining space");
        var recovered = CompactWidgetLayout.Calculate(160, 208, 64, 28, mediaCount: 2);
        Require(!recovered.ShowLeft && recovered.ShowRight && recovered.LeftWidth == 0 && recovered.RightWidth == 36 &&
            recovered.CenterWidth == 96 && recovered.MediaDetail == CompactMediaDetail.Cover && recovered.Width == 160,
            "Withdrawing the wider widget must recover cover detail without an empty track");
        var custom = CompactWidgetLayout.Calculate(240, 112, 64, 28, 400);
        Require(custom.ShowLeft && custom.ShowRight && custom.CenterWidth == 104 && custom.Width == 240,
            "A custom body must receive only the remaining center budget");
    }

    internal static void Budgets()
    {
        foreach (double available in new double[] { 80, 112, 160, 184, 204, 220, 256, 400, 680, 900 })
        foreach (double left in new double[] { 0, 1, 28, 32, 64 })
        foreach (double right in new double[] { 0, 1, 28, 32, 64 })
        foreach (int media in new[] { 0, 1, 2 })
        foreach (double? custom in new double?[] { null, 0, 56, 116, 400 })
        {
            var result = CompactWidgetLayout.Calculate(available, media == 0 ? 112 : media == 1 ? 180 : 208,
                left, right, custom, media);
            Require(double.IsFinite(result.Width) && result.Width <= Math.Min(680, available) && result.CenterWidth > 0,
                "The shell exceeded its finite budget");
            Require(Math.Abs(result.Width - (28 + result.LeftWidth + result.RightWidth + result.CenterWidth)) < .0001,
                "The body and occupied tracks must exhaust exactly the shell budget");
            Require(result.LeftWidth == (result.ShowLeft ? left + 8 : 0),
                "Left track must contain exactly one complete widget and gap, or consume zero space");
            Require(result.RightWidth == (result.ShowRight ? right + 8 : 0),
                "Right track must contain exactly one complete widget and gap, or consume zero space");
            Require(left > 0 || !result.ShowLeft, "Missing left widget must consume no track");
            Require(right > 0 || !result.ShowRight, "Missing right widget must consume no track");
        }
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -1, 79 })
        {
            try { CompactWidgetLayout.Calculate(invalid, 112, 28, 28); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("An invalid display width was accepted");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
