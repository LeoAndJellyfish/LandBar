namespace LandBar.Presentation;

/// <summary>
/// Exact solution of a critically damped spring (damping ratio 1). Response is the
/// undamped period in seconds: omega = 2*pi/response. Retargeting preserves both
/// the presentation value and its velocity, even when the direction reverses.
/// </summary>
public sealed class CriticalSpring
{
    public CriticalSpring(double position)
    {
        ValidateFinite(position, nameof(position));
        Position = Target = position;
    }

    public double Position { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public double ResponseSeconds { get; private set; } = 0.32;

    public void Retarget(double target, double responseSeconds)
    {
        ValidateFinite(target, nameof(target));
        if (!double.IsFinite(responseSeconds) || responseSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(responseSeconds));
        Target = target;
        ResponseSeconds = responseSeconds;
    }

    public void Advance(double elapsedSeconds)
    {
        if (!double.IsFinite(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (elapsedSeconds == 0) return;
        double omega = 2 * Math.PI / ResponseSeconds;
        double decay = Math.Exp(-omega * elapsedSeconds);
        if (decay == 0) { SnapToTarget(); return; }
        double displacement = Position - Target;
        double coupling = Velocity + omega * displacement;
        double nextDisplacement = (displacement + coupling * elapsedSeconds) * decay;
        double nextVelocity = (Velocity - omega * coupling * elapsedSeconds) * decay;
        Position = Target + nextDisplacement;
        Velocity = nextVelocity;
    }

    public bool IsAtRest(double positionTolerance = 0.08, double velocityTolerance = 0.8) =>
        Math.Abs(Position - Target) <= positionTolerance && Math.Abs(Velocity) <= velocityTolerance;

    public void SnapToTarget() { Position = Target; Velocity = 0; }

    private static void ValidateFinite(double value, string name)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(name);
    }
}
