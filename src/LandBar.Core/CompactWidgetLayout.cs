namespace LandBar.Core;

public enum CompactMediaDetail { Full, Cover, Clock }

/// <summary>A centered compact body with balanced side tracks and whole-widget overflow.</summary>
public sealed record CompactWidgetLayout(
    double Width, double CenterWidth, double SideWidth, bool ShowLeft, bool ShowRight,
    CompactMediaDetail MediaDetail)
{
    public static CompactWidgetLayout Calculate(double availableWidth, double builtinWidth,
        double leftWidth, double rightWidth, double? contentWidth = null, int mediaCount = 0)
    {
        foreach (double value in new[] { availableWidth, builtinWidth, leftWidth, rightWidth })
            if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(availableWidth));
        if (availableWidth < 80 || builtinWidth < 80 || leftWidth > 64 || rightWidth > 64 ||
            contentWidth is { } content && (!double.IsFinite(content) || content < 0 || content > 400))
            throw new ArgumentOutOfRangeException(nameof(availableWidth));

        // Fourteen DIP includes the outer twelve-DIP margin and rasterized shell border.
        const double edges = 28, gap = 8, clockMinimum = 56;
        double limit = Math.Min(680, availableWidth);
        bool left = leftWidth > 0, right = rightWidth > 0;
        double Side() => Math.Max(left ? leftWidth + gap : 0, right ? rightWidth + gap : 0);
        double Budget() => limit - edges - Side() * 2;
        while (true)
        {
            var detail = CompactMediaDetail.Full;
            double desired = Math.Max(clockMinimum, contentWidth ?? builtinWidth - edges);
            double minimum = contentWidth.HasValue || mediaCount == 0 ? clockMinimum : 124;
            if (contentWidth is null && mediaCount > 0 && Budget() < minimum)
            {
                detail = CompactMediaDetail.Cover;
                minimum = 80;
                desired = Math.Min(desired, 104);
                if (Budget() < minimum) { detail = CompactMediaDetail.Clock; minimum = clockMinimum; desired = 84; }
            }
            if (Budget() >= minimum || (!left && !right))
            {
                double center = Math.Max(0, Math.Min(desired, Budget()));
                double width = Math.Min(limit, edges + center + Side() * 2);
                return new(width, center, Side(), left, right, detail);
            }
            // Keep complete widgets. On exceptionally narrow displays, withdraw the wider one first.
            if (left && (!right || leftWidth > rightWidth)) left = false;
            else right = false;
        }
    }
}
