using LandBar.Presentation;

var tests = new (string, Action)[]
{
    ("Shell curves have exact resting endpoints", () =>
    {
        foreach (bool expanding in new[] { false, true })
        foreach (bool height in new[] { false, true })
        {
            Equal(0, IslandMotionCurve.Evaluate(0, expanding, height));
            Equal(0, IslandMotionCurve.Evaluate(-1, expanding, height));
            Equal(1, IslandMotionCurve.Evaluate(1, expanding, height));
        }
    }),
    ("Curves stay monotonic and inside the window hit region", () =>
    {
        foreach (bool expanding in new[] { false, true })
        foreach (bool height in new[] { false, true })
        {
            double previous = 0;
            for (int sample = 0; sample <= 10000; sample++)
            {
                double current = IslandMotionCurve.Evaluate(sample / 10000d, expanding, height);
                Assert(current >= previous - 1e-8 && current >= 0 && current <= 1, "Curve overshot or reversed");
                previous = current;
            }
        }
    }),
    ("Expansion leads with width before revealing height", () =>
    {
        Equal(0, IslandMotionCurve.Evaluate(0.039, true, true));
        Assert(IslandMotionCurve.Evaluate(0.039, true, false) > 0, "Width must move immediately");
        Equal(1, IslandMotionCurve.Evaluate(IslandMotionCurve.ExpandSeconds, true, true));
        Equal(1, IslandMotionCurve.Evaluate(IslandMotionCurve.ExpandSeconds, true, false));
    }),
    ("Collapse closes height before width settles", () =>
    {
        Equal(1, IslandMotionCurve.Evaluate(IslandMotionCurve.CollapseHeightSeconds, false, true));
        Assert(IslandMotionCurve.Evaluate(IslandMotionCurve.CollapseHeightSeconds, false, false) < 1, "Width should settle last");
        Equal(1, IslandMotionCurve.Evaluate(IslandMotionCurve.CollapseSeconds, false, false));
    }),
    ("Bezier solver handles linear and known custom curves", () =>
    {
        for (int sample = 0; sample <= 100; sample++)
        {
            double progress = sample / 100d;
            Equal(progress, IslandMotionCurve.CubicBezier(progress, 0, 0, 1, 1), 1e-6);
        }
        // Reference values computed from the exact curves, useful for catching x/y parameter mixups.
        Assert(IslandMotionCurve.CubicBezier(0.5, 0.32, 0.72, 0, 1) > 0.9, "Drawer curve midpoint should be nearly settled");
        Assert(IslandMotionCurve.CubicBezier(0.5, 0.23, 1, 0.32, 1) > 0.95, "Ease-out midpoint should be nearly settled");
    }),
    ("No NaN dimensions can propagate from the curve", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => IslandMotionCurve.Evaluate(double.NaN, true, true));
        Throws<ArgumentOutOfRangeException>(() => IslandMotionCurve.CubicBezier(double.NaN, 0.32, 0.72, 0, 1));
        Equal(1, IslandMotionCurve.Evaluate(double.PositiveInfinity, true, false));
        Equal(0, IslandMotionCurve.Evaluate(double.NegativeInfinity, true, false));
    })
};

int passed = 0;
foreach (var (name, test) in tests)
{
    try { test(); Console.WriteLine("PASS " + name); passed++; }
    catch (Exception error) { Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
}
Console.WriteLine($"{passed}/{tests.Length} passed");
return passed == tests.Length ? 0 : 1;

static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static void Equal(double expected, double actual, double tolerance = 1e-8) => Assert(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, actual {actual}");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
