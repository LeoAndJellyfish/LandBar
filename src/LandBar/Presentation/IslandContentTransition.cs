using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Windows.Shapes;

namespace LandBar.Presentation;

/// <summary>Changes live content without taking snapshots or stopping the moving shell.</summary>
public sealed class IslandContentTransition : IDisposable
{
    private readonly FrameworkElement _viewport;
    private readonly FrameworkElement _content;
    private readonly Image _ghost;
    private readonly IslandMotion _motion;
    private readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private readonly List<(Rect Bounds, bool ScrollViewport)> _footprints = [];
    private Action? _pendingConfigure;
    private double _pendingWidth, _pendingHeight;
    private long _revision;
    private bool _leaving, _pendingReveal, _fadingIn, _configuring, _legacyPrepared, _disposed;

    public IslandContentTransition(FrameworkElement viewport, FrameworkElement content, Image ghost, IslandMotion motion)
    {
        _viewport = viewport; _content = content; _ghost = ghost; _motion = motion;
        TargetWidth = motion.TargetWidth; TargetHeight = motion.TargetHeight;
        _leaveTimer.Tick += LeaveFinished;
        _viewport.LayoutUpdated += LayoutUpdated;
        _motion.ProgressChanged += MotionProgress;
        _motion.Completed += MotionCompleted;
        ClearGhost();
    }

    public bool IsTransitioning => !_disposed && (_pendingConfigure is not null || _legacyPrepared || _leaving ||
        _motion.IsAnimating || _pendingReveal || _fadingIn);
    public Rect ContentBounds { get; private set; } = Rect.Empty;
    public double TargetWidth { get; private set; }
    public double TargetHeight { get; private set; }

    /// <summary>Queues the latest visual target. Requests during departure share the original deadline.</summary>
    public void Present(double width, double height, Action configure, bool forceTransition = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configure);
        ValidateDimension(width, nameof(width)); ValidateDimension(height, nameof(height));
        bool changed = Math.Abs(width - TargetWidth) > 0.1 || Math.Abs(height - TargetHeight) > 0.1;
        if (!changed && !forceTransition)
        {
            Refresh(configure);
            return;
        }

        TargetWidth = _pendingWidth = width;
        TargetHeight = _pendingHeight = height;
        _pendingConfigure = configure;
        _revision++;
        _legacyPrepared = false;
        if (_configuring) return;
        if (!SystemParameters.ClientAreaAnimation) { ApplyPending(immediate: true); return; }
        if (_leaving) return;

        // Keep any existing shell motion running while the old live view leaves.
        if (_content.Opacity > 0.001) BeginLeave();
        else ApplyPending();
    }

    /// <summary>Updates the current target's data without restarting its motion or fade.</summary>
    public void Refresh(Action configure)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(configure);
        if (_leaving || _configuring || _pendingConfigure is not null)
        {
            _pendingConfigure = configure;
            _pendingWidth = TargetWidth; _pendingHeight = TargetHeight;
            return;
        }

        _configuring = true;
        try
        {
            configure();
            _content.UpdateLayout();
            UpdateContentBounds();
        }
        finally { _configuring = false; }
        if (_pendingConfigure is not null) ApplyPending();
        else EvaluateVisibility();
    }

    private void BeginLeave()
    {
        double from = _content.Opacity;
        _content.BeginAnimation(UIElement.OpacityProperty, null);
        _content.Opacity = 0;
        _content.IsHitTestVisible = false;
        _pendingReveal = _fadingIn = false;
        _leaving = true;
        _content.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(from, 0, _leaveTimer.Interval)
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        });
        _leaveTimer.Start();
        // A retarget can arrive while the previous view already exceeds the moving clip.
        if (!FitsViewport()) ApplyPending();
    }

    private void LeaveFinished(object? sender, EventArgs e)
    {
        _leaveTimer.Stop();
        if (!_disposed) ApplyPending();
    }

    private void ApplyPending(bool immediate = false)
    {
        if (_disposed || _configuring || _pendingConfigure is null) return;
        _leaveTimer.Stop();
        _leaving = _legacyPrepared = false;
        _revision++;
        _content.BeginAnimation(UIElement.OpacityProperty, null);
        _content.Opacity = 0;
        _content.IsHitTestVisible = false;
        _pendingReveal = _fadingIn = false;
        _configuring = true;
        double width = _pendingWidth, height = _pendingHeight;
        try
        {
            // A synchronous layout callback can request a newer target. Apply the latest one once.
            while (_pendingConfigure is { } configure)
            {
                width = _pendingWidth; height = _pendingHeight;
                _pendingConfigure = null;
                configure();
                _content.UpdateLayout();
                UpdateContentBounds();
            }
        }
        finally { _configuring = false; }

        _pendingReveal = true;
        _motion.TransitionTo(width, height, immediate: immediate || !SystemParameters.ClientAreaAnimation);
        EvaluateVisibility();
    }

    private void MotionProgress(double _) => EvaluateVisibility();
    private void MotionCompleted() => EvaluateVisibility();
    private void LayoutUpdated(object? sender, EventArgs e)
    {
        if (!_disposed && !_configuring && (_leaving || _pendingReveal || _fadingIn || _motion.IsAnimating))
            EvaluateVisibility();
    }

    // The content's local layout is fixed for a target. Never traverse it once per animation frame.
    private void UpdateContentBounds()
    {
        Rect bounds = Rect.Empty;
        _footprints.Clear();
        Collect(_content, new Rect(_content.RenderSize));
        ContentBounds = bounds;

        void Collect(DependencyObject node, Rect restriction)
        {
            if (node is UIElement ui && ui.Visibility != Visibility.Visible) return;
            if (node is FrameworkElement element)
            {
                if (element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0) return;
                var transform = element.TransformToAncestor(_content);
                if (element.ClipToBounds || element is ScrollContentPresenter or ScrollViewer)
                    restriction.Intersect(transform.TransformBounds(new Rect(element.RenderSize)));
                if (element.Clip is { } clip) restriction.Intersect(transform.TransformBounds(clip.Bounds));
                if (restriction.IsEmpty) return;
                if (element is ScrollViewer scroll && (scroll.ScrollableHeight > 0.5 || scroll.ScrollableWidth > 0.5))
                {
                    var rect = transform.TransformBounds(new Rect(scroll.RenderSize));
                    rect.Intersect(restriction);
                    if (!rect.IsEmpty) { bounds.Union(rect); _footprints.Add((rect, true)); }
                    return;
                }
                bool paintedButton = element is ButtonBase button && button.Background is { Opacity: > 0 } background &&
                    (background is not SolidColorBrush solid || solid.Color.A > 0);
                if (paintedButton || element is TextBlock or Shape or PluginRing or PluginRingLabel || element is Image { Source: not null })
                {
                    var rect = transform.TransformBounds(new Rect(element.RenderSize));
                    rect.Intersect(restriction);
                    if (!rect.IsEmpty) { bounds.Union(rect); _footprints.Add((rect, false)); }
                    return;
                }
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) Collect(VisualTreeHelper.GetChild(node, i), restriction);
        }
    }

    private bool FitsViewport()
    {
        if (ContentBounds.IsEmpty || _viewport.ActualWidth < 1 || _viewport.ActualHeight < 1) return false;
        var transform = _content.TransformToAncestor(_viewport);
        var available = _viewport.Clip?.Bounds ?? new Rect(_viewport.RenderSize);
        foreach (var footprint in _footprints)
        {
            var rect = transform.TransformBounds(footprint.Bounds);
            if (footprint.ScrollViewport)
            {
                var roundedAvailable = available;
                var dpi = VisualTreeHelper.GetDpi(_viewport);
                roundedAvailable.Inflate(1 / dpi.DpiScaleX, 1 / dpi.DpiScaleY);
                if (!roundedAvailable.Contains(rect)) return false;
                continue;
            }
            if (!available.Contains(rect)) return false;
            if (_viewport.Clip is { } clip && !(clip.FillContains(rect.TopLeft) && clip.FillContains(rect.TopRight) &&
                clip.FillContains(rect.BottomLeft) && clip.FillContains(rect.BottomRight))) return false;
        }
        return true;
    }

    private void EvaluateVisibility()
    {
        if (_disposed || _configuring || _legacyPrepared) return;
        bool fits = FitsViewport();
        if (_leaving)
        {
            // Old content must leave before it is cut by a shrinking shell, without freezing motion.
            if (!fits) ApplyPending();
            return;
        }
        if (!fits)
        {
            if (_content.Opacity > 0 || _fadingIn)
            {
                _revision++;
                _content.BeginAnimation(UIElement.OpacityProperty, null);
                _content.Opacity = 0; _content.IsHitTestVisible = false;
                _fadingIn = false;
            }
            _pendingReveal = true;
            return;
        }
        if (_pendingReveal) ShowContent(immediate: !SystemParameters.ClientAreaAnimation);
    }

    private void ShowContent(bool immediate)
    {
        _pendingReveal = false;
        _content.Opacity = 1;
        if (immediate)
        {
            _content.BeginAnimation(UIElement.OpacityProperty, null);
            _fadingIn = false; _content.IsHitTestVisible = true;
            return;
        }
        _fadingIn = true;
        long revision = _revision;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            if (_disposed || revision != _revision) return;
            _fadingIn = false; _content.IsHitTestVisible = true;
            _content.BeginAnimation(UIElement.OpacityProperty, null);
        };
        _content.BeginAnimation(UIElement.OpacityProperty, fade);
    }

    // Compatibility for the isolated constrained-viewport probe. Production uses Present/Refresh.
    public void Prepare()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _revision++;
        _leaveTimer.Stop();
        _pendingConfigure = null;
        _leaving = _pendingReveal = _fadingIn = false;
        _legacyPrepared = true;
        _content.BeginAnimation(UIElement.OpacityProperty, null);
        _content.Opacity = 0; _content.IsHitTestVisible = false;
        ClearGhost();
    }

    public void TransitionTo(double width, double height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidateDimension(width, nameof(width)); ValidateDimension(height, nameof(height));
        TargetWidth = _pendingWidth = width; TargetHeight = _pendingHeight = height;
        _pendingConfigure = static () => { };
        _legacyPrepared = false;
        ApplyPending(immediate: !SystemParameters.ClientAreaAnimation);
    }

    private void ClearGhost()
    {
        _ghost.BeginAnimation(UIElement.OpacityProperty, null);
        _ghost.Opacity = 0; _ghost.Source = null; _ghost.Visibility = Visibility.Collapsed;
    }

    private static void ValidateDimension(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _revision++;
        _pendingConfigure = null;
        _leaveTimer.Stop(); _leaveTimer.Tick -= LeaveFinished;
        _viewport.LayoutUpdated -= LayoutUpdated;
        _motion.ProgressChanged -= MotionProgress; _motion.Completed -= MotionCompleted;
        _content.BeginAnimation(UIElement.OpacityProperty, null);
        ClearGhost();
    }
}
