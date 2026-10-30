using LandBar.Presentation;

var tests = new (string Name, Action Run)[]
{
    ("Rest stays exact without consuming frame time", () =>
    {
        var axis = new CriticalSpring(176);
        axis.Retarget(176, 0.32);
        axis.Advance(1);
        Equal(176, axis.Position); Equal(0, axis.Velocity);
        Assert(axis.IsAtRest(), "A stationary spring must be at rest");
    }),
    ("A resting spring approaches its target without overshoot", () =>
    {
        foreach (var (start, target) in new[] { (176d, 728d), (244d, 40d) })
        {
            var axis = new CriticalSpring(start);
            axis.Retarget(target, 0.32);
            double previous = start;
            for (int frame = 0; frame < 240; frame++)
            {
                axis.Advance(1d / 240);
                Assert(axis.Position >= Math.Min(start, target) - 1e-9 && axis.Position <= Math.Max(start, target) + 1e-9, "Critical damping overshot");
                Assert((axis.Position - previous) * (target - start) >= -1e-8, "A zero-velocity start must be monotonic");
                previous = axis.Position;
            }
            Assert(axis.IsAtRest(), "Spring did not settle");
        }
    }),
    ("Retargeting preserves the exact presentation position and velocity", () =>
    {
        var axis = new CriticalSpring(176);
        axis.Retarget(728, 0.32); axis.Advance(0.11);
        double value = axis.Position, velocity = axis.Velocity;
        Assert(velocity > 0, "Initial spring must be expanding");
        axis.Retarget(176, 0.28);
        Equal(value, axis.Position, 0); Equal(velocity, axis.Velocity, 0);
        axis.Advance(0.000001);
        Assert(axis.Position > value, "A reversal must first inherit the old velocity");
        Assert(Math.Abs(axis.Velocity - velocity) < 1, "Reversal created a velocity discontinuity");
        axis.Advance(1);
        Assert(axis.IsAtRest(), "Reversed spring did not settle");
    }),
    ("Current-value retargeting does not erase existing momentum", () =>
    {
        var axis = new CriticalSpring(176);
        axis.Retarget(728, 0.32); axis.Advance(0.09);
        double position = axis.Position, velocity = axis.Velocity;
        axis.Retarget(position, 0.32);
        Equal(velocity, axis.Velocity, 0);
        Assert(!axis.IsAtRest(), "Nonzero velocity must prevent immediate rest");
        axis.Advance(0.001);
        Assert(axis.Position > position, "Momentum was discarded");
        axis.Advance(1); Assert(axis.IsAtRest(), "Current-value retarget failed to return");
    }),
    ("Analytical integration is independent of frame rate", () =>
    {
        var coarse = new CriticalSpring(176);
        var fine = new CriticalSpring(176);
        coarse.Retarget(728, 0.35); fine.Retarget(728, 0.35);
        coarse.Advance(0.17);
        for (int frame = 0; frame < 170; frame++) fine.Advance(0.001);
        Equal(coarse.Position, fine.Position, 1e-8); Equal(coarse.Velocity, fine.Velocity, 1e-7);
        coarse.Retarget(216, 0.28); fine.Retarget(216, 0.28);
        coarse.Advance(0.31);
        for (int frame = 0; frame < 310; frame++) fine.Advance(0.001);
        Equal(coarse.Position, fine.Position, 1e-8); Equal(coarse.Velocity, fine.Velocity, 1e-7);
    }),
    ("Independent axes retain different responses and settle exactly", () =>
    {
        var width = new CriticalSpring(176); var height = new CriticalSpring(40);
        width.Retarget(728, 0.32); height.Retarget(244, 0.35);
        width.Advance(0.12); height.Advance(0.12);
        Assert((width.Position - 176) / (728 - 176) > (height.Position - 40) / (244 - 40), "Width should lead the physical expansion");
        width.Advance(1); height.Advance(1);
        Assert(width.IsAtRest() && height.IsAtRest(), "Axes did not settle");
        width.SnapToTarget(); height.SnapToTarget();
        Equal(728, width.Position, 0); Equal(244, height.Position, 0);
        Equal(0, width.Velocity, 0); Equal(0, height.Velocity, 0);
    }),
    ("Large stalls remain finite and arrive at rest", () =>
    {
        var axis = new CriticalSpring(176); axis.Retarget(728, 0.32);
        axis.Advance(3600);
        Equal(728, axis.Position, 0); Equal(0, axis.Velocity, 0);
        axis.Advance(1e300);
        Assert(double.IsFinite(axis.Position) && double.IsFinite(axis.Velocity), "A stall created nonfinite geometry");
    }),
    ("Repeated re-targets preserve bounded positive window geometry", () =>
    {
        var random = new Random(863);
        var axis = new CriticalSpring(176);
        for (int action = 0; action < 50000; action++)
        {
            if (random.Next(4) == 0)
            {
                double beforePosition = axis.Position, beforeVelocity = axis.Velocity;
                axis.Retarget(random.Next(40, 1201), random.Next(25, 36) / 100d);
                Equal(beforePosition, axis.Position, 0); Equal(beforeVelocity, axis.Velocity, 0);
            }
            axis.Advance(random.Next(1, 25) / 1000d);
            Assert(axis.Position > 0 && axis.Position < 1400 && double.IsFinite(axis.Velocity), "Random redirection escaped a valid window region");
        }
        axis.Advance(3); Assert(axis.IsAtRest(), "Stress sequence failed to settle");
    }),
    ("Invalid values are rejected before they reach WPF", () =>
    {
        Throws<ArgumentOutOfRangeException>(() => new CriticalSpring(double.NaN));
        var axis = new CriticalSpring(176);
        Throws<ArgumentOutOfRangeException>(() => axis.Retarget(double.PositiveInfinity, 0.32));
        Throws<ArgumentOutOfRangeException>(() => axis.Retarget(728, 0));
        Throws<ArgumentOutOfRangeException>(() => axis.Retarget(728, double.NaN));
        Throws<ArgumentOutOfRangeException>(() => axis.Advance(-0.1));
        Throws<ArgumentOutOfRangeException>(() => axis.Advance(double.NaN));
        Throws<ArgumentOutOfRangeException>(() => axis.Advance(double.PositiveInfinity));
    })
};
int passed = 0;
foreach (var (name, run) in tests)
{
    try { run(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception exception) { Console.Error.WriteLine("FAIL " + name + ": " + exception.Message); }
}
Console.WriteLine($"{passed}/{tests.Length} passed");
return passed == tests.Length ? 0 : 1;

static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal(double expected, double actual, double tolerance = 1e-9) => Assert(Math.Abs(expected - actual) <= tolerance, $"Expected {expected}, actual {actual}");
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name);
}
