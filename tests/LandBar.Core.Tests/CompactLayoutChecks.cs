using LandBar.Core;

internal static class CompactLayoutChecks
{
    internal static void Balanced()
    {
        var idle = CompactWidgetLayout.Calculate(680, 112, 0, 0);
        Require(idle.Width == 112 && idle.SideWidth == 0 && idle.CenterWidth == 84, "Idle clock baseline changed");
        var asymmetric = CompactWidgetLayout.Calculate(680, 112, 64, 28);
        Require(asymmetric.Width == 256 && asymmetric.SideWidth == 72 && asymmetric.CenterWidth == 84 &&
            asymmetric.ShowLeft && asymmetric.ShowRight, "Asymmetric widgets must share equal side tracks");
        Require(asymmetric == CompactWidgetLayout.Calculate(680, 112, 28, 64), "Mirrored widgets must preserve the shell geometry");
        var single = CompactWidgetLayout.Calculate(680, 112, 28, 0);
        Require(single.Width == 184 && single.SideWidth == 36 && single.ShowLeft && !single.ShowRight,
            "A single widget must retain the physical center");
        var custom = CompactWidgetLayout.Calculate(680, 112, 64, 28, 400);
        Require(custom.Width == 572 && custom.CenterWidth == 400, "Custom body and side tracks must include safe shell padding");
    }

    internal static void Constrained()
    {
        var media = CompactWidgetLayout.Calculate(160, 180, 64, 64, mediaCount: 1);
        Require(!media.ShowLeft && !media.ShowRight && media.MediaDetail == CompactMediaDetail.Full && media.Width == 160,
            "Removing widgets should recover the richest media layout that fits");
        var mixed = CompactWidgetLayout.Calculate(220, 208, 64, 28, mediaCount: 2);
        Require(!mixed.ShowLeft && mixed.ShowRight && mixed.MediaDetail == CompactMediaDetail.Cover && mixed.CenterWidth == 104,
            "The narrower widget should remain complete alongside the condensed media body");
        var clock = CompactWidgetLayout.Calculate(204, 180, 64, 28, mediaCount: 1);
        Require(!clock.ShowLeft && clock.ShowRight && clock.Width <= 204, "Narrow media must stay inside its display budget");
        var custom = CompactWidgetLayout.Calculate(240, 112, 64, 28, 400);
        Require(custom.ShowLeft && custom.ShowRight && custom.CenterWidth == 68 && custom.Width == 240,
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
            Require(Math.Abs(result.Width - (28 + result.SideWidth * 2 + result.CenterWidth)) < .0001,
                "Symmetric tracks and body must exhaust exactly the shell budget");
            Require(!result.ShowLeft || result.SideWidth >= left + 8, "Left widget was partially fitted");
            Require(!result.ShowRight || result.SideWidth >= right + 8, "Right widget was partially fitted");
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
