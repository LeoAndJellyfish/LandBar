namespace LandBar.Presentation;

/// <summary>
/// Shared shell timings. Expansion follows Ionic's drawer curve; collapse uses strong ease-out.
/// No overshoot keeps the pill inside its final hit region and the text free of stretching.
/// </summary>
public static class IslandMotionCurve
{
    public const double ExpandSeconds = 0.380;
    public const double CollapseSeconds = 0.260;
    public const double ExpandHeightDelaySeconds = 0.040;
    public const double CollapseHeightSeconds = 0.220;

    public static double Evaluate(double elapsedSeconds, bool expanding, bool height)
    {
        if (double.IsNaN(elapsedSeconds)) throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        double delay = expanding && height ? ExpandHeightDelaySeconds : 0;
        double duration = expanding ? ExpandSeconds - delay : height ? CollapseHeightSeconds : CollapseSeconds;
        double progress = Math.Clamp((elapsedSeconds - delay) / duration, 0, 1);
        return expanding
            ? CubicBezier(progress, 0.32, 0.72, 0, 1)
            : CubicBezier(progress, 0.23, 1, 0.32, 1);
    }

    /// <summary>Maps elapsed x to eased y, solving the Bezier x coordinate rather than using t as x.</summary>
    public static double CubicBezier(double progress, double x1, double y1, double x2, double y2)
    {
        if (double.IsNaN(progress)) throw new ArgumentOutOfRangeException(nameof(progress));
        if (progress <= 0) return 0;
        if (progress >= 1) return 1;
        double lower = 0;
        double upper = 1;
        double parameter = progress;
        // A bracketed Newton solve stays stable for the drawer curve's zero second x control point.
        for (int iteration = 0; iteration < 24; iteration++)
        {
            double x = Coordinate(parameter, x1, x2);
            double error = x - progress;
            if (Math.Abs(error) < 1e-9) break;
            if (error > 0) upper = parameter;
            else lower = parameter;
            double derivative = Derivative(parameter, x1, x2);
            double next = Math.Abs(derivative) > 1e-9 ? parameter - error / derivative : double.NaN;
            parameter = double.IsFinite(next) && next > lower && next < upper ? next : (lower + upper) / 2;
        }
        return Math.Clamp(Coordinate(parameter, y1, y2), 0, 1);
    }

    private static double Coordinate(double t, double first, double second)
    {
        double inverse = 1 - t;
        return 3 * inverse * inverse * t * first + 3 * inverse * t * t * second + t * t * t;
    }

    private static double Derivative(double t, double first, double second)
    {
        double inverse = 1 - t;
        return 3 * inverse * inverse * first + 6 * inverse * t * (second - first) + 3 * t * t * (1 - second);
    }
}
