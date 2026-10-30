using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace LandBar.Presentation;

/// <summary>
/// Morphs a visual surface with independent, critically damped springs.
/// The frame callback updates the surface only; a fixed native host avoids reallocating
/// a layered window for every frame. Content is arranged independently from this shell.
/// Content fading belongs to the view, so text never stretches with the shell.
/// </summary>
public sealed class IslandMotion : IDisposable
{
    private const double DimensionTolerance = 0.1;
    private readonly Window _window;
    private readonly Action<double, double> _positionFrame;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _disposed;
    private double _startedAt;
    private readonly CriticalSpring _width;
    private readonly CriticalSpring _height;
    private double _lastStepAt;
    private double _duration;
    private bool _expanding;
    private double _currentWidth;
    private double _currentHeight;
    private long _revision;
    private TimeSpan? _lastRenderingTime;

    public IslandMotion(Window window, FrameworkElement surface, Action<double, double> positionFrame)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(surface);
        ArgumentNullException.ThrowIfNull(positionFrame);
        window.Dispatcher.VerifyAccess();
        _window = window;
        _positionFrame = positionFrame;
        surface.ClipToBounds = true;
        _currentWidth = InitialDimension(surface.Width, surface.ActualWidth,
            InitialDimension(window.Width, window.ActualWidth, 216));
        _currentHeight = InitialDimension(surface.Height, surface.ActualHeight,
            InitialDimension(window.Height, window.ActualHeight, 44));
        _width = new CriticalSpring(_currentWidth);
        _height = new CriticalSpring(_currentHeight);
        TargetWidth = _currentWidth;
        TargetHeight = _currentHeight;
        _window.Closed += WindowClosed;
    }

    public bool IsAnimating { get; private set; }
    public double TargetWidth { get; private set; }
    public double TargetHeight { get; private set; }
    public double CurrentWidth => _currentWidth;
    public double CurrentHeight => _currentHeight;
    public double WidthVelocity => _width.Velocity;
    public double HeightVelocity => _height.Velocity;
    public bool IsExpanding => _expanding;
    /// <summary>Estimated settling horizon, rather than a fixed-duration animation.</summary>
    public double DurationMilliseconds => _duration * 1000;

    /// <summary>Estimated time progress; the final 1 is emitted only after the springs settle.</summary>
    public event Action<double>? ProgressChanged;
    public event Action? Completed;

    public void TransitionTo(double width, double height, bool immediate = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        ValidateDimension(width, nameof(width));
        ValidateDimension(height, nameof(height));

        // Render runs on a separate low-frequency timer. An unchanged target never restarts motion.
        if (!immediate && SystemParameters.ClientAreaAnimation && Near(width, TargetWidth) && Near(height, TargetHeight) &&
            (IsAnimating || (_width.IsAtRest() && _height.IsAtRest()))) return;

        _revision++;
        TargetWidth = width;
        TargetHeight = height;
        _expanding = height > _currentHeight + DimensionTolerance ||
            (Near(height, _currentHeight) && width > _currentWidth);
        // Both axes inherit their current velocity. Changing a target never resets momentum.
        _width.Retarget(width, _expanding ? 0.32 : 0.28);
        _height.Retarget(height, _expanding ? 0.35 : 0.25);

        if (immediate || !SystemParameters.ClientAreaAnimation ||
            (_width.IsAtRest() && _height.IsAtRest()))
        {
            StopRendering();
            _duration = 0;
            _width.SnapToTarget();
            _height.SnapToTarget();
            ApplyFrame(width, height);
            ProgressChanged?.Invoke(1);
            Completed?.Invoke();
            return;
        }

        _duration = Math.Max(_width.ResponseSeconds, _height.ResponseSeconds) * 2;
        _startedAt = _lastStepAt = _clock.Elapsed.TotalSeconds;
        if (!IsAnimating)
        {
            IsAnimating = true;
            CompositionTarget.Rendering += RenderFrame;
        }
        ProgressChanged?.Invoke(0);
    }

    private void RenderFrame(object? sender, EventArgs args)
    {
        if (_disposed || !IsAnimating) return;
        // WPF may report the same composition frame more than once during native resize.
        if (args is RenderingEventArgs rendering)
        {
            if (_lastRenderingTime == rendering.RenderingTime) return;
            _lastRenderingTime = rendering.RenderingTime;
        }
        var revision = _revision;
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = Math.Max(0, now - _startedAt);
        double step = Math.Max(0, now - _lastStepAt);
        _lastStepAt = now;
        _width.Advance(step);
        _height.Advance(step);
        bool settled = _width.IsAtRest() && _height.IsAtRest();
        if (!SystemParameters.ClientAreaAnimation) settled = true;
        if (settled) { _width.SnapToTarget(); _height.SnapToTarget(); }
        var progress = settled ? 1 : Math.Clamp(elapsed / _duration, 0, 0.999);
        ApplyFrame(_width.Position, _height.Position);
        ProgressChanged?.Invoke(progress);

        // A progress handler may request another target. Its new transition owns completion.
        if (!settled || revision != _revision) return;
        StopRendering();
        Completed?.Invoke();
    }

    private void ApplyFrame(double width, double height)
    {
        _currentWidth = width;
        _currentHeight = height;
        // Surface values stay in DIP precision. Pixel rounding belongs to the stable host,
        // not to this path, so the slow tail can move by less than a physical pixel.
        _positionFrame(width, height);
    }

    /// <summary>Holds the current shell while content leaves, preserving velocity for the next target.</summary>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _window.Dispatcher.VerifyAccess();
        _revision++;
        StopRendering();
    }

    private void StopRendering()
    {
        if (!IsAnimating) return;
        CompositionTarget.Rendering -= RenderFrame;
        IsAnimating = false;
    }

    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _window.Dispatcher.VerifyAccess();
        _disposed = true;
        StopRendering();
        _window.Closed -= WindowClosed;
        ProgressChanged = null;
        Completed = null;
        _clock.Stop();
    }

    private static bool Near(double first, double second) => Math.Abs(first - second) < DimensionTolerance;
    private static double InitialDimension(double value, double actual, double fallback) =>
        double.IsFinite(value) && value > 0 ? value : double.IsFinite(actual) && actual > 0 ? actual : fallback;
    private static void ValidateDimension(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }
}
