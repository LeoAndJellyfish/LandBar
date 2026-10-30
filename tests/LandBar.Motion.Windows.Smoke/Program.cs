using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using LandBar.Presentation;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var surface = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(22) };
        var window = new Window
        {
            Title = "LandBar Motion Smoke",
            Width = 216, Height = 44,
            Content = surface,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowActivated = false, ShowInTaskbar = false, Opacity = 0,
            Left = 12, Top = 12
        };
        var watch = Stopwatch.StartNew();
        int frameCount = 0;
        int completions = 0;
        bool continuity = true;
        bool velocityContinuity = true;
        bool repeatedTargetKeepsMomentum = true;
        bool pausePreservesPresentation = true;
        bool pausedFramesStopped = true;
        bool pausedSameTargetResumes = true;
        bool immediateModeSettlesSynchronously = true;
        bool anchorStable = true;
        double maximumAnchorError = 0;
        bool disposedCleanly = false;
        var probe = new GeometryProbe(window);
        window.SourceInitialized += (_, _) => probe.Attach();
        bool legacyDiagnostic = args.Contains("--legacy-diagnostic");
        var motion = new IslandMotion(window, surface, (width, height) =>
        {
            frameCount++;
            var dpi = VisualTreeHelper.GetDpi(window);
            if (legacyDiagnostic)
            {
                window.Width = width; window.Height = height;
                GeometryProbe.MoveOnly(window, 600 - (int)Math.Round(width * dpi.DpiScaleX / 2), (int)Math.Round(12 * dpi.DpiScaleY));
            }
            else NativeWindowBounds.Apply(window, 100, 0, 1000, 12, width, height, dpi.DpiScaleX, dpi.DpiScaleY);
            var physical = GeometryProbe.PhysicalBounds(window);
            double anchorError = Math.Abs(physical.Left + physical.Width / 2 - 600);
            maximumAnchorError = Math.Max(maximumAnchorError, anchorError);
            anchorStable &= anchorError <= 1 && Math.Abs(physical.Top - 12 * dpi.DpiScaleY) <= 1;
            probe.CenterX = 600;
            probe.Top = (int)Math.Round(12 * dpi.DpiScaleY);
            probe.Recording = true;
        });
        motion.Completed += () => completions++;

        window.Loaded += (_, _) =>
        {
            motion.TransitionTo(580, 186);
            After(130, () =>
            {
                var beforeWidth = motion.CurrentWidth;
                var beforeHeight = motion.CurrentHeight;
                var beforeWidthVelocity = motion.WidthVelocity;
                var beforeHeightVelocity = motion.HeightVelocity;
                motion.TransitionTo(216, 44);
                continuity &= !SystemParameters.ClientAreaAnimation || (beforeWidth == motion.CurrentWidth && beforeHeight == motion.CurrentHeight);
                velocityContinuity &= beforeWidthVelocity == motion.WidthVelocity && beforeHeightVelocity == motion.HeightVelocity;
            });
            After(200, () =>
            {
                var beforeWidth = motion.CurrentWidth;
                var beforeHeight = motion.CurrentHeight;
                var beforeWidthVelocity = motion.WidthVelocity;
                var beforeHeightVelocity = motion.HeightVelocity;
                motion.TransitionTo(580, 186);
                continuity &= !SystemParameters.ClientAreaAnimation || (beforeWidth == motion.CurrentWidth && beforeHeight == motion.CurrentHeight);
                velocityContinuity &= beforeWidthVelocity == motion.WidthVelocity && beforeHeightVelocity == motion.HeightVelocity;
                double redirectedWidthVelocity = motion.WidthVelocity;
                double redirectedHeightVelocity = motion.HeightVelocity;
                motion.TransitionTo(580, 186); // A repeated UI render must not restart the animation.
                repeatedTargetKeepsMomentum &= redirectedWidthVelocity == motion.WidthVelocity && redirectedHeightVelocity == motion.HeightVelocity;
            });
            After(300, () =>
            {
                double pausedWidth = motion.CurrentWidth, pausedHeight = motion.CurrentHeight;
                double pausedWidthVelocity = motion.WidthVelocity, pausedHeightVelocity = motion.HeightVelocity;
                int pausedFrameCount = frameCount;
                bool wasAnimating = motion.IsAnimating;
                motion.Pause();
                pausePreservesPresentation &= !motion.IsAnimating && motion.CurrentWidth == pausedWidth && motion.CurrentHeight == pausedHeight &&
                    motion.WidthVelocity == pausedWidthVelocity && motion.HeightVelocity == pausedHeightVelocity;
                After(75, () =>
                {
                    pausedFramesStopped &= frameCount == pausedFrameCount && !motion.IsAnimating &&
                        motion.CurrentWidth == pausedWidth && motion.CurrentHeight == pausedHeight &&
                        motion.WidthVelocity == pausedWidthVelocity && motion.HeightVelocity == pausedHeightVelocity;
                    motion.TransitionTo(580, 186); // Same target must resume a paused, unfinished spring.
                    pausedSameTargetResumes &= !SystemParameters.ClientAreaAnimation || (wasAnimating && motion.IsAnimating);
                    pausePreservesPresentation &= motion.CurrentWidth == pausedWidth && motion.CurrentHeight == pausedHeight &&
                        motion.WidthVelocity == pausedWidthVelocity && motion.HeightVelocity == pausedHeightVelocity;
                });
            });
            After(1100, () =>
            {
                var finalDpi = VisualTreeHelper.GetDpi(window);
                var expectedFinal = NativeWindowBounds.Calculate(100, 0, 1000, 12, 580, 186, finalDpi.DpiScaleX, finalDpi.DpiScaleY);
                var actualFinal = GeometryProbe.PhysicalBounds(window);
                bool settled = !motion.IsAnimating && Math.Abs(actualFinal.Width - expectedFinal.Width) <= (legacyDiagnostic ? 1 : 0) &&
                    Math.Abs(actualFinal.Height - expectedFinal.Height) < 0.01 &&
                    Math.Abs(window.ActualWidth * finalDpi.DpiScaleX - actualFinal.Width) < 0.51 &&
                    Math.Abs(window.ActualHeight * finalDpi.DpiScaleY - actualFinal.Height) < 0.51;
                // Deterministically exercise the same branch used by Windows reduced motion.
                motion.TransitionTo(260, 60, immediate: true);
                immediateModeSettlesSynchronously &= !motion.IsAnimating && motion.CurrentWidth == 260 && motion.CurrentHeight == 60 &&
                    motion.WidthVelocity == 0 && motion.HeightVelocity == 0;
                motion.TransitionTo(580, 186, immediate: true);
                immediateModeSettlesSynchronously &= !motion.IsAnimating && motion.CurrentWidth == 580 && motion.CurrentHeight == 186;
                motion.Dispose();
                int framesAtDisposal = frameCount;
                After(100, () =>
                {
                    disposedCleanly = !motion.IsAnimating && frameCount == framesAtDisposal;
                    probe.Recording = false;
                    double finalWidth = window.Width, finalHeight = window.Height;
                    double finalActualWidth = window.ActualWidth, finalActualHeight = window.ActualHeight;
                    var matrix = RunPixelMatrix(window);
                    bool passed = continuity && velocityContinuity && repeatedTargetKeepsMomentum && pausePreservesPresentation &&
                        pausedFramesStopped && pausedSameTargetResumes && immediateModeSettlesSynchronously &&
                        (legacyDiagnostic || anchorStable) && disposedCleanly && settled && matrix.passed && (!SystemParameters.ClientAreaAnimation || frameCount >= 4) && completions >= 1;
                    passed &= legacyDiagnostic ? probe.MaximumCenterError > 1 : probe.MaximumCenterError <= 0.5;
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        passed, frameCount, completions, continuity, velocityContinuity, repeatedTargetKeepsMomentum,
                        pausePreservesPresentation, pausedFramesStopped, pausedSameTargetResumes,
                        immediateModeSettlesSynchronously, anchorStable, maximumAnchorError, settled, disposedCleanly,
                        reducedMotion = !SystemParameters.ClientAreaAnimation,
                        elapsedMilliseconds = watch.ElapsedMilliseconds,
                        finalWidth, finalHeight,
                        actualWidth = finalActualWidth, actualHeight = finalActualHeight,
                        nativeWindowChanges = probe.NativeChanges,
                        nativeMaximumCenterError = probe.MaximumCenterError,
                        nativeMaximumTopError = probe.MaximumTopError,
                        legacyDiagnostic, legacyJitterReproduced = legacyDiagnostic && probe.MaximumCenterError > 1,
                        dpiAndNegativeOriginMatrix = matrix
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    probe.Recording = false; probe.Detach(); window.Close();
                    app.Shutdown(passed ? 0 : 1);
                });
            });
        };
        After(5000, () => { Console.Error.WriteLine("Native motion smoke timed out"); motion.Dispose(); app.Shutdown(2); });
        window.Show();
        return app.Run();
    }

    private sealed record PixelMatrixResult(bool passed, int cases, int samples, int nativeChanges, double maximumCenterError);

    private static PixelMatrixResult RunPixelMatrix(Window window)
    {
        bool passed = true;
        int cases = 0, samples = 0, changes = 0;
        double maximumCenterError = 0;
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2 })
        foreach (var area in new[] { (left: 100, top: 0, width: 1920), (left: -1920, top: -1080, width: 1919) })
        {
            cases++;
            var first = NativeWindowBounds.Apply(window, area.left, area.top, area.width, 12, 164, 44, scale, scale);
            var probe = new GeometryProbe(window) { CenterX = area.left + area.width / 2d, Top = first.Top };
            probe.Attach(); probe.Recording = true;
            for (int step = 0; step <= 96; step++)
            {
                double width = 164 + step * 6.375;
                double height = 44 + step * 2.125;
                var wanted = NativeWindowBounds.Calculate(area.left, area.top, area.width, 12, width, height, scale, scale);
                NativeWindowBounds.Apply(window, area.left, area.top, area.width, 12, width, height, scale, scale);
                var actual = GeometryProbe.PhysicalBounds(window);
                samples++;
                passed &= actual.Left == wanted.Left && actual.Top == wanted.Top && actual.Width == wanted.Width && actual.Height == wanted.Height;
                passed &= wanted.CenterX == area.left + area.width / 2d;
            }
            probe.Recording = false; probe.Detach();
            passed &= probe.MaximumCenterError == 0 && probe.MaximumTopError == 0;
            maximumCenterError = Math.Max(maximumCenterError, probe.MaximumCenterError);
            changes += probe.NativeChanges;
        }
        return new PixelMatrixResult(passed, cases, samples, changes, maximumCenterError);
    }

    private static void After(int milliseconds, Action callback)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); callback(); };
        timer.Start();
    }
}
