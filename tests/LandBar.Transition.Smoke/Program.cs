using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using LandBar;
using LandBar.Core;
using LandBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault(a => !a.StartsWith("--")) ?? "artifacts/transition-smoke");
        Directory.CreateDirectory(output);
        var app = new ProbeApplication(output, args.Contains("--diagnostic"), args.Contains("--constrained-settings"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var production = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Production-App.xaml"));
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            production.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        return app.Run();
    }

    // Skips LandBar.App.OnStartup: no singleton, system sources, toast or user media control.
    private sealed class ProbeApplication(string output, bool diagnostic, bool constrainedSettings) : Application
    {
        private readonly List<Frame> _frames = [];
        private readonly List<BitmapSource> _images = [];
        private readonly List<string> _failures = [];
        private readonly List<object> _settled = [];
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private LandBar.MainWindow _window = null!;
        private Border _island = null!;
        private FrameworkElement _root = null!;
        private FrameworkElement _layout = null!;
        private Window? _underlay;
        private HwndSource? _nativeSource;
        private HostBounds? _baselineHost;
        private readonly List<object> _hostChanges = [];
        private readonly List<object> _hitTests = [];
        private object? _examples;
        private string _stage = "initial-demo";
        private double _nextImageAt;
        private bool _capturePending, _finished;
        private TimeSpan? _lastCompositionTime;
        private int _cornerSamples, _leakedFrames, _clippedControlFrames, _ghostDuringMotionFrames, _overlapFrames;
        private int _outsideHostSamples, _outsideHostLeaks, _anchorErrorFrames, _hostOverflowFrames, _nativeRectChanges;
        private double _maximumCenterError, _maximumTopError;

        protected override void OnStartup(StartupEventArgs e)
        {
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Transition Smoke" };
            MainWindow = _window;
            _island = (Border)_window.FindName("Island");
            _root = (FrameworkElement)_window.FindName("HoverSurface");
            _layout = (FrameworkElement)_window.FindName("ContentLayout");
            _window.SourceInitialized += (_, _) =>
            {
                _nativeSource = HwndSource.FromHwnd(new WindowInteropHelper(_window).Handle);
                _nativeSource?.AddHook(NativeWindowMessage);
            };
            _window.Loaded += (_, _) =>
            {
                // Geometry sampling is independent of the user's inactivity preference.
                // Reflection-driven tab changes are not real input, and frame capture adds load.
                ((AppSettings)Field("_settings").GetValue(_window)!).AutoCollapseSeconds = 0;
                Invoke("ApplyAutoCollapseSetting");
                _examples = Field("_examples").GetValue(_window);
                CompositionTarget.Rendering += OnRendering;
                _ = RunScenarios();
            };
            _window.Show();
            After(22000, () => Finish("Transition smoke timed out."));
        }

        private async Task RunScenarios()
        {
            try
            {
                await Task.Delay(1100);
                CheckSettled();
                _baselineHost = PhysicalBounds(_window);
                CreateUnderlay();
                CheckNativeHitTesting();
                if (constrainedSettings)
                {
                    await StageView("settings-before-constrain", "Settings");
                    ((DispatcherTimer)Field("_timer").GetValue(_window)!).Stop();
                    _stage = "constrained-settings";
                    var transition = Field("_contentTransition").GetValue(_window)!;
                    transition.GetType().GetMethod("Present")!.Invoke(transition,
                        [448d, 250d, (Action)(() => _layout.Height = 248.6), true]);
                    await Task.Delay(1100);
                    CheckSettled();
                    var scroll = (ScrollViewer)_window.FindName("SettingsScroll");
                    File.WriteAllText(Path.Combine(output, "constrained-scroll.json"), JsonSerializer.Serialize(new
                    {
                        scrollableHeight = scroll.ScrollableHeight, viewportHeight = scroll.ViewportHeight, extentHeight = scroll.ExtentHeight,
                        verticalBar = scroll.ComputedVerticalScrollBarVisibility.ToString(),
                        opacity = _layout.Opacity, interactive = _layout.IsHitTestVisible
                    }, new JsonSerializerOptions { WriteIndented = true }));
                    if (scroll.ScrollableHeight <= 0) _failures.Add("Constrained settings did not produce scrollable content.");
                    if (_layout.Opacity < 0.99) DumpConstrainedFootprints();
                    Finish();
                    return;
                }
                await Stage("collapse", "Collapse");
                SetOneExample();
                await Stage("single-media-expand", "OpenActivity");
                await StageView("activity-to-control", "ControlCenter");
                await StageView("control-to-settings", "Settings");
                await Stage("settings-collapse", "Collapse");
                Field("_examples").SetValue(_window, _examples);
                await Stage("dual-media-expand", "OpenActivity");
                _stage = "media-to-notifications";
                Field("_notificationsTab").SetValue(_window, true);
                Invoke("Render");
                await Task.Delay(1050);
                CheckSettled();
                _stage = "notifications-to-media";
                Field("_notificationsTab").SetValue(_window, false);
                Invoke("Render");
                await Task.Delay(1050);
                CheckSettled();
                _stage = "page-size-change";
                Field("_page").SetValue(_window, 1);
                Invoke("Render");
                await Task.Delay(1050);
                CheckSettled();
                _stage = "rapid-retarget";
                Invoke("Collapse"); await Task.Delay(120);
                Invoke("OpenActivity"); await Task.Delay(130);
                ChangeView("Settings"); await Task.Delay(90);
                Invoke("Collapse"); await Task.Delay(160);
                Invoke("OpenActivity"); await Task.Delay(1400);
                CheckSettled();
                Finish();
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        private async Task Stage(string stage, string method)
        {
            _stage = stage; Invoke(method); await Task.Delay(1050); CheckSettled();
        }
        private async Task StageView(string stage, string view)
        {
            _stage = stage; ChangeView(view); await Task.Delay(1050); CheckSettled();
        }
        private void CheckSettled()
        {
            _window.UpdateLayout();
            var transition = typeof(LandBar.MainWindow).GetField("_contentTransition", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(_window);
            bool transitioning = transition?.GetType().GetProperty("IsTransitioning")?.GetValue(transition) is true;
            var bounds = transition?.GetType().GetProperty("ContentBounds")?.GetValue(transition) is Rect measured ? measured : Rect.Empty;
            var viewport = (FrameworkElement?)_window.FindName("IslandViewport");
            var transformed = viewport is not null && !bounds.IsEmpty ? _layout.TransformToAncestor(viewport).TransformBounds(bounds) : Rect.Empty;
            bool[]? cornerFits = viewport?.Clip is Geometry clip && !transformed.IsEmpty
                ? new[] { transformed.TopLeft, transformed.TopRight, transformed.BottomLeft, transformed.BottomRight }.Select(clip.FillContains).ToArray() : null;
            int visibleControls = Descendants(_layout).OfType<Button>().Count(button => button.IsVisible && EffectiveOpacity(button) > 0.99);
            double opacity = EffectiveOpacity(_layout);
            var status = new { stage = _stage, view = Field("_view").GetValue(_window)?.ToString(), opacity, transitioning, interactive = _layout.IsHitTestVisible, visibleControls, width = _island.ActualWidth, height = _island.ActualHeight,
                hostSize = _root.RenderSize.ToString(), islandBoundsInHost = IslandBounds().ToString(),
                contentBounds = bounds.ToString(), contentSize = _layout.RenderSize.ToString(), viewportSize = viewport?.RenderSize.ToString(),
                transformedBounds = transformed.ToString(), clipBounds = viewport?.Clip?.Bounds.ToString(), cornerFits };
            _settled.Add(status);
            if (opacity < 0.99) Console.WriteLine(JsonSerializer.Serialize(status));
            if (opacity < 0.99 || transitioning) _failures.Add($"Stage {_stage} did not settle to visible content (opacity={opacity}, transitioning={transitioning}).");
            if (!_layout.IsHitTestVisible) _failures.Add($"Stage {_stage} remained non-interactive after settling.");
            bool compact = (FrameworkElement)_window.FindName("CompactView") is { Visibility: Visibility.Visible };
            if (!compact && visibleControls == 0) _failures.Add($"Stage {_stage} has no visible controls.");
            CheckNativeHitTesting();
        }
        private void DumpConstrainedFootprints()
        {
            var viewport = (FrameworkElement)_window.FindName("IslandViewport");
            var offending = new List<object>();
            Collect(_layout, new Rect(_layout.RenderSize));
            File.WriteAllText(Path.Combine(output, "constrained-footprints.json"), JsonSerializer.Serialize(offending, new JsonSerializerOptions { WriteIndented = true }));

            void Collect(DependencyObject node, Rect restriction)
            {
                if (node is UIElement { Visibility: not Visibility.Visible }) return;
                if (node is FrameworkElement element)
                {
                    if (element.RenderSize.Width <= 0 || element.RenderSize.Height <= 0) return;
                    var transform = element.TransformToAncestor(_layout);
                    if (element.ClipToBounds || element is ScrollContentPresenter or ScrollViewer)
                        restriction.Intersect(transform.TransformBounds(new Rect(element.RenderSize)));
                    if (element.Clip is { } localClip) restriction.Intersect(transform.TransformBounds(localClip.Bounds));
                    if (restriction.IsEmpty) return;
                    bool paintedButton = element is System.Windows.Controls.Primitives.ButtonBase button && HasPaint(button.Background);
                    if (paintedButton || element is TextBlock or System.Windows.Shapes.Shape || element is Image { Source: not null })
                    {
                        var rect = transform.TransformBounds(new Rect(element.RenderSize)); rect.Intersect(restriction);
                        if (!rect.IsEmpty)
                        {
                            var mapped = _layout.TransformToAncestor(viewport).TransformBounds(rect);
                            var available = new Rect(viewport.RenderSize);
                            bool[] corners = viewport.Clip is { } clip ? new[] { mapped.TopLeft, mapped.TopRight, mapped.BottomLeft, mapped.BottomRight }.Select(clip.FillContains).ToArray() : [true, true, true, true];
                            if (!available.Contains(mapped) || corners.Any(value => !value))
                                offending.Add(new { type = element.GetType().Name, name = element.Name, text = element is TextBlock text ? text.Text : null,
                                    actual = element.RenderSize.ToString(), footprint = rect.ToString(), mapped = mapped.ToString(), corners,
                                    ancestors = AncestorNames(element) });
                        }
                        return;
                    }
                }
                for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++) Collect(VisualTreeHelper.GetChild(node, index), restriction);
            }
            static string AncestorNames(DependencyObject node)
            {
                var names = new List<string>();
                for (DependencyObject? current = node; current is not null; current = VisualTreeHelper.GetParent(current))
                    names.Add(current.GetType().Name + (current is FrameworkElement { Name.Length: > 0 } e ? "#" + e.Name : ""));
                return string.Join(" <- ", names);
            }
        }
        private void ChangeView(string name)
        {
            var view = typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!;
            Invoke("ChangeView", Enum.Parse(view, name));
        }
        private void SetOneExample()
        {
            var values = ((System.Collections.IEnumerable)_examples!).Cast<object>().ToArray();
            var one = Array.CreateInstance(values[0].GetType(), 1); one.SetValue(values[0], 0);
            Field("_examples").SetValue(_window, one);
        }
        private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Invoke(string name, params object[] args) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(_window, args);

        private void OnRendering(object? sender, EventArgs args)
        {
            if (_capturePending || _finished) return;
            if (args is RenderingEventArgs rendering)
            {
                if (_lastCompositionTime == rendering.RenderingTime) return;
                _lastCompositionTime = rendering.RenderingTime;
            }
            _capturePending = true;
            Dispatcher.BeginInvoke(() =>
            {
                _capturePending = false;
                if (!_finished) Capture();
            }, DispatcherPriority.Render);
        }

        private void Capture()
        {
            try
            {
                _window.UpdateLayout();
                var dpi = VisualTreeHelper.GetDpi(_window);
                var islandBounds = IslandBounds();
                int width = Math.Max(1, (int)Math.Ceiling(_root.ActualWidth * dpi.DpiScaleX));
                int height = Math.Max(1, (int)Math.Ceiling(_root.ActualHeight * dpi.DpiScaleY));
                var bitmap = new RenderTargetBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(_root); bitmap.Freeze();
                var pixelBounds = new Rect(islandBounds.X * dpi.DpiScaleX, islandBounds.Y * dpi.DpiScaleY,
                    islandBounds.Width * dpi.DpiScaleX, islandBounds.Height * dpi.DpiScaleY);
                var analysis = AnalyzePixels(bitmap, pixelBounds, _island.CornerRadius.TopLeft * dpi.DpiScaleX);
                _cornerSamples += analysis.CornerSamples;
                if (analysis.CornerLeaks > 0) _leakedFrames++;
                _outsideHostSamples += analysis.OutsideSamples;
                if (analysis.OutsideLeaks > 0) _outsideHostLeaks++;
                double centerError = Math.Abs(islandBounds.X + islandBounds.Width / 2 - _root.ActualWidth / 2);
                double topError = Math.Abs(islandBounds.Y);
                _maximumCenterError = Math.Max(_maximumCenterError, centerError);
                _maximumTopError = Math.Max(_maximumTopError, topError);
                if (centerError * dpi.DpiScaleX > 0.5 || topError * dpi.DpiScaleY > 0.5) _anchorErrorFrames++;
                if (islandBounds.Left * dpi.DpiScaleX < -0.5 || islandBounds.Top * dpi.DpiScaleY < -0.5 ||
                    (islandBounds.Right - _root.ActualWidth) * dpi.DpiScaleX > 0.5 ||
                    (islandBounds.Bottom - _root.ActualHeight) * dpi.DpiScaleY > 0.5) _hostOverflowFrames++;
                double opacity = EffectiveOpacity(_layout);
                var clipped = PaintedContent(_layout).Where(element => (!constrainedSettings || _stage != "constrained-settings") && element.IsVisible && element.RenderSize.Width > 0 && element.RenderSize.Height > 0 && EffectiveOpacity(element) > 0.025)
                    .Select(element => (element, bounds: RelativeToIsland(element, islandBounds)))
                    .Where(pair => pair.bounds.Left < -0.75 || pair.bounds.Top < -0.75 || pair.bounds.Right > islandBounds.Width + 0.75 || pair.bounds.Bottom > islandBounds.Height + 0.75)
                    .Select(pair => AutomationProperties.GetName(pair.element) is string name && name.Length > 0 ? name :
                        pair.element is TextBlock text ? text.Text : pair.element.Name.Length > 0 ? pair.element.Name : pair.element.GetType().Name).ToArray();
                if (clipped.Length > 0) _clippedControlFrames++;
                var ghost = (FrameworkElement)_window.FindName("TransitionGhost");
                var motion = Field("_motion").GetValue(_window);
                bool shellAnimating = motion?.GetType().GetProperty("IsAnimating")?.GetValue(motion) is true;
                double ghostOpacity = EffectiveOpacity(ghost);
                if (shellAnimating && ghostOpacity > 0.025) _ghostDuringMotionFrames++;
                if (opacity > 0.025 && ghostOpacity > 0.025) _overlapFrames++;
                double time = _clock.Elapsed.TotalMilliseconds;
                var frame = new Frame(_stage, time, islandBounds.Width, islandBounds.Height, islandBounds.X, islandBounds.Y,
                    centerError, topError, opacity, ghostOpacity, shellAnimating, analysis.CornerLeaks, analysis.OutsideLeaks, clipped);
                _frames.Add(frame);
                if (time >= _nextImageAt && _images.Count < 140)
                {
                    _nextImageAt = time + 90;
                    var crop = CropIsland(bitmap, pixelBounds);
                    Save(crop, Path.Combine(output, $"frame-{_images.Count:D3}-{_stage}.png"));
                    _images.Add(crop);
                }
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        private Rect IslandBounds() => _island.TransformToAncestor(_root).TransformBounds(new Rect(_island.RenderSize));

        private Rect RelativeToIsland(FrameworkElement element, Rect islandBounds)
        {
            var rect = element.TransformToAncestor(_root).TransformBounds(new Rect(element.RenderSize));
            rect.Offset(-islandBounds.X, -islandBounds.Y);
            return rect;
        }

        private static BitmapSource CropIsland(BitmapSource bitmap, Rect rect)
        {
            int left = Math.Max(0, (int)Math.Floor(rect.Left)), top = Math.Max(0, (int)Math.Floor(rect.Top));
            int right = Math.Min(bitmap.PixelWidth, (int)Math.Ceiling(rect.Right));
            int bottom = Math.Min(bitmap.PixelHeight, (int)Math.Ceiling(rect.Bottom));
            var crop = new CroppedBitmap(bitmap, new Int32Rect(left, top, Math.Max(1, right - left), Math.Max(1, bottom - top)));
            crop.Freeze();
            return crop;
        }

        private sealed record PixelAnalysis(int CornerSamples, int CornerLeaks, int OutsideSamples, int OutsideLeaks);
        private static PixelAnalysis AnalyzePixels(BitmapSource bitmap, Rect rect, double radius)
        {
            int stride = bitmap.PixelWidth * 4;
            byte[] pixels = new byte[stride * bitmap.PixelHeight]; bitmap.CopyPixels(pixels, stride, 0);
            int tested = 0, leaked = 0;
            radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2d);
            int extent = (int)Math.Ceiling(radius + 2);
            foreach (bool right in new[] { false, true }) foreach (bool bottom in new[] { false, true })
            for (int y = 0; y < extent; y++) for (int x = 0; x < extent; x++)
            {
                int px = right ? (int)Math.Ceiling(rect.Right) - 1 - x : (int)Math.Floor(rect.Left) + x;
                int py = bottom ? (int)Math.Ceiling(rect.Bottom) - 1 - y : (int)Math.Floor(rect.Top) + y;
                if (px < 0 || px >= bitmap.PixelWidth || py < 0 || py >= bitmap.PixelHeight) continue;
                double cornerX = right ? rect.Right - radius : rect.Left + radius;
                double cornerY = bottom ? rect.Bottom - radius : rect.Top + radius;
                double dx = px + 0.5 - cornerX, dy = py + 0.5 - cornerY;
                if (Math.Sqrt(dx * dx + dy * dy) <= radius + 1.5) continue;
                tested++;
                if (pixels[py * stride + px * 4 + 3] > 1) leaked++;
            }
            int outsideSamples = 0, outsideLeaks = 0;
            var margin = rect; margin.Inflate(2, 2);
            for (int y = 4; y < bitmap.PixelHeight; y += 17)
            for (int x = 4; x < bitmap.PixelWidth; x += 17)
            {
                if (margin.Contains(new Point(x + 0.5, y + 0.5))) continue;
                outsideSamples++;
                if (pixels[y * stride + x * 4 + 3] > 1) outsideLeaks++;
            }
            return new(tested, leaked, outsideSamples, outsideLeaks);
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            {
                var child = VisualTreeHelper.GetChild(root, index); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        private static IEnumerable<FrameworkElement> PaintedContent(DependencyObject node)
        {
            if (node is UIElement { Visibility: not Visibility.Visible }) yield break;
            if (node is Button button && HasPaint(button.Background))
            {
                yield return button; yield break;
            }
            if (node is TextBlock { Text.Length: > 0 } text) { yield return text; yield break; }
            if (node is Image { Source: not null } image) { yield return image; yield break; }
            if (node is System.Windows.Shapes.Shape shape && (HasPaint(shape.Fill) || HasPaint(shape.Stroke))) { yield return shape; yield break; }
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
                foreach (var element in PaintedContent(VisualTreeHelper.GetChild(node, index))) yield return element;
        }
        private static bool HasPaint(Brush? brush) => brush is not null && brush.Opacity > 0 && (brush is not SolidColorBrush solid || solid.Color.A > 0);
        private static double EffectiveOpacity(UIElement element)
        {
            double opacity = 1;
            for (DependencyObject? current = element; current is UIElement visual; current = VisualTreeHelper.GetParent(current))
            {
                if (visual.Visibility != Visibility.Visible) return 0;
                opacity *= visual.Opacity;
            }
            return opacity;
        }

        private readonly record struct HostBounds(int Left, int Top, int Right, int Bottom);
        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect bounds);
        [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        [DllImport("dwmapi.dll")] private static extern int DwmFlush();

        private static HostBounds PhysicalBounds(Window window) => PhysicalBounds(new WindowInteropHelper(window).Handle);
        private static HostBounds PhysicalBounds(IntPtr window)
        {
            if (!GetWindowRect(window, out var rect)) throw new InvalidOperationException("GetWindowRect failed.");
            return new HostBounds(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private IntPtr NativeWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (!_finished && message == 0x0047 && _baselineHost is { } baseline)
            {
                var current = PhysicalBounds(hwnd);
                if (current != baseline)
                {
                    _nativeRectChanges++;
                    if (_hostChanges.Count < 20) _hostChanges.Add(new { stage = _stage, bounds = current });
                }
            }
            return IntPtr.Zero;
        }

        private void CreateUnderlay()
        {
            var host = PhysicalBounds(_window);
            _underlay = new Window
            {
                Title = "LandBar Transition Test Underlay", Width = 10, Height = 10,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                Background = Brushes.DimGray, ShowInTaskbar = false, ShowActivated = false, Topmost = true
            };
            _underlay.Show();
            // Both windows belong to this isolated test. Do not activate or send input to other apps.
            if (!SetWindowPos(new WindowInteropHelper(_underlay).Handle, new WindowInteropHelper(_window).Handle,
                host.Left, host.Top, host.Right - host.Left, host.Bottom - host.Top, 0x0010 | 0x0200))
                throw new InvalidOperationException("Test underlay could not be positioned below the host.");
        }

        private void CheckNativeHitTesting()
        {
            if (_underlay is null) return;
            DwmFlush();
            var bounds = IslandBounds();
            var excluded = bounds; excluded.Inflate(2, 2);
            var points = new[]
            {
                new Point(3, 3), new Point(_root.ActualWidth - 3, 3),
                new Point(3, _root.ActualHeight - 3), new Point(_root.ActualWidth - 3, _root.ActualHeight - 3),
                new Point(_root.ActualWidth / 2, _root.ActualHeight - 3)
            };
            var lowerHandle = new WindowInteropHelper(_underlay).Handle;
            foreach (var point in points.Where(point => !excluded.Contains(point))) Check(point, lowerHandle, "underlay");
            Check(new Point(bounds.X + bounds.Width / 2, bounds.Y + Math.Min(22, bounds.Height / 2)),
                new WindowInteropHelper(_window).Handle, "island");

            void Check(Point local, IntPtr expected, string kind)
            {
                var physical = _root.PointToScreen(local);
                var hit = WindowFromPoint(new NativePoint { X = (int)Math.Round(physical.X), Y = (int)Math.Round(physical.Y) });
                bool passed = hit == expected;
                _hitTests.Add(new { stage = _stage, kind, x = physical.X, y = physical.Y, passed,
                    expected = expected.ToInt64(), actual = hit.ToInt64() });
                if (!passed) _failures.Add($"Stage {_stage} native hit test for {kind} did not reach the expected test window.");
            }
        }

        private void Finish(string? failure = null)
        {
            if (_finished) return;
            _finished = true; CompositionTarget.Rendering -= OnRendering;
            if (failure is not null) _failures.Add(failure);
            if (_leakedFrames > 0) _failures.Add($"Rounded corner alpha leaked in {_leakedFrames} frames.");
            if (_clippedControlFrames > 0) _failures.Add($"Visible painted controls, text or artwork exceeded the shell in {_clippedControlFrames} frames.");
            if (_ghostDuringMotionFrames > 0) _failures.Add($"An outgoing snapshot remained visible during shell movement in {_ghostDuringMotionFrames} frames.");
            if (_overlapFrames > 0) _failures.Add($"The outgoing snapshot covered incoming content in {_overlapFrames} frames.");
            if (_outsideHostLeaks > 0) _failures.Add($"Transparent host space contained painted pixels in {_outsideHostLeaks} frames.");
            if (_anchorErrorFrames > 0) _failures.Add($"The visible island lost its top-center anchor in {_anchorErrorFrames} frames.");
            if (_hostOverflowFrames > 0) _failures.Add($"The visible island exceeded its fixed native host in {_hostOverflowFrames} frames.");
            if (_nativeRectChanges > 0) _failures.Add($"The native host rectangle changed {_nativeRectChanges} times after initialization.");
            if (_outsideHostSamples < 100) _failures.Add("Too few blank host pixels were checked.");
            if (_hitTests.Count == 0) _failures.Add("Native hit testing did not exercise the isolated underlay.");
            if (_frames.Count < 20) _failures.Add("Too few animation frames were observed.");
            bool passed = _failures.Count == 0;
            var result = new { passed, diagnostic, constrainedSettings,
                autoCollapseEnabled = ((IslandInactivityTimer)Field("_inactivity").GetValue(_window)!).Timeout is not null,
                frameCount = _frames.Count, cornerPixelsTested = _cornerSamples,
                leakedFrames = _leakedFrames, clippedControlFrames = _clippedControlFrames, ghostDuringMotionFrames = _ghostDuringMotionFrames, overlappingContentFrames = _overlapFrames, failures = _failures,
                outsideHostSamples = _outsideHostSamples, outsideHostLeakedFrames = _outsideHostLeaks,
                anchorErrorFrames = _anchorErrorFrames, hostOverflowFrames = _hostOverflowFrames,
                maximumCenterErrorDip = _maximumCenterError, maximumTopErrorDip = _maximumTopError,
                nativeRectChangesAfterInitialization = _nativeRectChanges, hostChanges = _hostChanges, nativeHitTests = _hitTests,
                capture = "Offscreen full-host production visual tree, cropped to the visible island; validates geometry and content, not presentation FPS.",
                reducedMotion = !SystemParameters.ClientAreaAnimation, settled = _settled, frames = _frames };
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            MakeContactSheet();
            Console.WriteLine(JsonSerializer.Serialize(new { passed, diagnostic, frameCount = _frames.Count,
                leakedFrames = _leakedFrames, clippedControlFrames = _clippedControlFrames, ghostDuringMotionFrames = _ghostDuringMotionFrames, overlappingContentFrames = _overlapFrames,
                outsideHostLeakedFrames = _outsideHostLeaks, anchorErrorFrames = _anchorErrorFrames, hostOverflowFrames = _hostOverflowFrames,
                nativeRectChangesAfterInitialization = _nativeRectChanges,
                nativeHitTests = _hitTests.Count, output, failures = _failures }));
            _nativeSource?.RemoveHook(NativeWindowMessage);
            _underlay?.Close();
            Shutdown(diagnostic || passed ? 0 : 1);
        }

        private void MakeContactSheet()
        {
            if (_images.Count == 0) return;
            int[] indices = Enumerable.Range(0, Math.Min(_images.Count, 36)).Select(index => index * (_images.Count - 1) / Math.Max(1, Math.Min(_images.Count, 36) - 1)).Distinct().ToArray();
            const int columns = 6, tileWidth = 270, tileHeight = 220;
            var visual = new DrawingVisual();
            using (var context = visual.RenderOpen())
            {
                context.DrawRectangle(new SolidColorBrush(Color.FromRgb(66, 66, 70)), null, new Rect(0, 0, columns * tileWidth, ((indices.Length + columns - 1) / columns) * tileHeight));
                for (int tile = 0; tile < indices.Length; tile++)
                {
                    var image = _images[indices[tile]];
                    double scale = Math.Min((tileWidth - 12d) / image.PixelWidth, (tileHeight - 24d) / image.PixelHeight);
                    double x = tile % columns * tileWidth + (tileWidth - image.PixelWidth * scale) / 2, y = tile / columns * tileHeight + 18;
                    context.DrawImage(image, new Rect(x, y, image.PixelWidth * scale, image.PixelHeight * scale));
                    var label = new FormattedText($"frame {indices[tile]:D3} · {image.PixelWidth}×{image.PixelHeight}", System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.White, 1);
                    context.DrawText(label, new Point(tile % columns * tileWidth + 8, tile / columns * tileHeight + 2));
                }
            }
            var sheet = new RenderTargetBitmap(columns * tileWidth, ((indices.Length + columns - 1) / columns) * tileHeight, 96, 96, PixelFormats.Pbgra32);
            sheet.Render(visual); Save(sheet, Path.Combine(output, "contact-sheet.png"));
        }
        private static void Save(BitmapSource bitmap, string path)
        {
            using var stream = File.Create(path);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(stream);
        }
        private static void After(int milliseconds, Action callback)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(milliseconds) };
            timer.Tick += (_, _) => { timer.Stop(); callback(); }; timer.Start();
        }
    }

    private sealed record Frame(string stage, double milliseconds, double width, double height, double left, double top,
        double centerErrorDip, double topErrorDip, double contentOpacity, double ghostOpacity, bool shellAnimating,
        int outsideCornerPixels, int paintedHostSpacePixels, string[] clippedControls);
}
