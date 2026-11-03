using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;
using LandBar.Services;

// Uses an isolated JSONL provider and synthetic media; no account or system source is started.
internal sealed class ShrinkModeProbe(string output) : Application
{
    private LandBar.MainWindow _window = null!;
    private PluginManager? _manager;
    private AppSettings _settings = null!;
    private readonly List<string> _passed = [], _failures = [];
    private readonly List<object> _samples = [];
    private bool _observe;
    private int _paintedFrames, _clippedFrames, _anchorErrors, _nativeChanges;
    private NativeRectangle _nativeStart;
    private readonly Stopwatch _watch = Stopwatch.StartNew();

    protected override void OnStartup(StartupEventArgs e)
    {
        _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Synthetic Shrink Mode Checks" };
        MainWindow = _window;
        _window.Loaded += async (_, _) =>
        {
            try { await Scenarios(); }
            catch (Exception error) { _failures.Add(error.ToString()); Console.WriteLine("FAIL " + error); }
            finally { await Finish(); }
        };
        _window.Show();
    }

    private async Task Scenarios()
    {
        var defaults = JsonSerializer.Deserialize<AppSettings>("{}")!;
        Check(!defaults.ShrinkMode && defaults.ShrinkComponent == "builtin:clock", "Legacy settings keep normal mode and default to the built-in clock");
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(new AppSettings
        { ShrinkMode = true, ShrinkComponent = "view:shrink-fixture:ring" }))!;
        Check(roundTrip.ShrinkMode && roundTrip.ShrinkComponent == "view:shrink-fixture:ring", "Shrink mode and the component preference survive a settings serialization round trip");
        _settings = (AppSettings)Get("_settings")!;
        _settings.AutoCollapseSeconds = 0; _settings.Quiet = false; _settings.HideContent = false;
        _settings.EnablePluginViews = true; _settings.PluginViewBindings = new();
        _settings.ShrinkMode = false; _settings.ShrinkComponent = "builtin:clock";
        Invoke("ApplyAutoCollapseSetting"); await Settle(); Invoke("EndDemo"); Invoke("Collapse"); await Settle();
        SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, -12000, -12000, 0, 0, 0x0015);
        ((DispatcherTimer)Get("_timer")!).Stop();
        GetWindowRect(new WindowInteropHelper(_window).Handle, out _nativeStart);
        CompositionTarget.Rendering += ObserveFrame; _observe = true;
        Check(Math.Abs(Shell.ActualWidth - 112) < .75, "Normal mode retains the 112-DIP idle clock capsule");
        string providerRoot = Path.Combine(output, "synthetic-shrink-plugins"), folder = Path.Combine(providerRoot, "shrink-fixture");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
        {
            id = "shrink-fixture", name = "合成组件", version = "1.0.0", description = "收缩模式隔离测试",
            command = Environment.ProcessPath!, arguments = new[] { "--shrink-fixture" }, protocolVersion = 1
        }));
        _manager = new PluginManager(providerRoot); Invoke("ConnectPlugins", _manager); _manager.Discover();
        Check(await _manager.StartAsync("shrink-fixture"), "The isolated standalone provider completes its real JSONL handshake");
        await Until(() => _manager.Views.Count == 7); Render(); await Settle();
        Check(_settings.PluginViewBindings.Count == 0 && Math.Abs(Shell.ActualWidth - 112) < .75,
            "Publishing unbound component views leaves the normal compact layout unchanged");

        Toggle(true); await Settle();
        Check(_settings.ShrinkMode && Element<CheckBox>("ShrinkModeCheck").IsChecked == true && Math.Abs(Shell.ActualWidth - 84) < .75,
            "The settings toggle enables a single 84 by 44 clock component");
        Check(Element<TextBlock>("ClockLabel").IsVisible && !Standalone.IsVisible && Hidden("CompactCovers", "AudioBars", "PluginIdleLeft", "PluginIdleRight", "PluginIdleContent"),
            "Clock-only mode removes media, side widgets and custom-body placeholders");
        Capture("shrink-clock.png");

        Select("view:shrink-fixture:ring"); await Settle();
        Check(Standalone.View?.Id == "ring" && Standalone.IsVisible && Math.Abs(Shell.ActualWidth - 44) < .75 && Math.Abs(Shell.ActualHeight - 44) < .75,
            "An unbound 28-DIP plugin ring occupies a single 44 by 44 island");
        Check(Hidden("ClockLabel", "CompactCovers", "AudioBars", "PluginIdleLeft", "PluginIdleRight", "PluginIdleContent", "BuiltinCompactView") &&
            Column("CompactLeftColumn").ActualWidth < .75 && Column("CompactRightColumn").ActualWidth < .75,
            "The ring replaces the entire compact composition and reserves neither a clock nor side tracks");
        CheckComplete("The isolated ring is centered, complete, visible and interactive");
        Capture("shrink-ring.png");
        MouseUp(Standalone); await Settle();
        Check(Get("_view")!.ToString() == "Activity" && (string?)Get("_activePluginTabKey") == "view:shrink-fixture:tab" &&
            Element<PluginViewPresenter>("ActivityOverrideContent").View?.Id == "tab",
            "Clicking the standalone plugin opens that provider's independent tab");
        Invoke("Collapse"); await Settle();
        Check(Standalone.View?.Id == "ring" && Math.Abs(Shell.ActualWidth - 44) < .75,
            "Collapsing the plugin page restores the selected ring");

        await Configure("remove-ring", () => !_manager.Views.Any(view => view.Id == "ring"));
        CheckFallback("Withdrawing the selected view falls back to the clock without discarding its preference");
        await Configure("restore-ring", () => _manager.Views.Any(view => view.Id == "ring"));
        Check(Standalone.View?.Id == "ring", "Republishing the selected view automatically restores the standalone ring");
        await Configure("deny-ring", () => _manager.Views.Any(view => view.Id == "ring" && !view.AllowStandalone));
        CheckFallback("A live allowStandalone opt-out withdraws the component and preserves the preference");
        await Configure("restore-ring", () => _manager.Views.Any(view => view.Id == "ring" && view.AllowStandalone));
        Check(Standalone.View?.Id == "ring", "A live eligibility update restores the selected component");
        _settings.EnablePluginViews = false; Render(); await Settle();
        CheckFallback("Disabling plugin views falls back to the clock without erasing the selected plugin");
        _settings.EnablePluginViews = true; Render(); await Settle();
        Check(Standalone.View?.Id == "ring", "Re-enabling plugin views restores the preferred ring");
        await _manager.StopAsync("shrink-fixture"); await Until(() => _manager.Views.Count == 0); Render(); await Settle();
        CheckFallback("Stopping the provider removes its standalone view and retains the user's selection");
        Check(await _manager.StartAsync("shrink-fixture"), "A stopped standalone provider can restart");
        await Until(() => _manager.Views.Count == 7); Render(); await Settle();
        Check(Standalone.View?.Id == "ring", "Provider restart restores the preferred component without a side binding");

        Select("view:shrink-fixture:sprite"); await Settle();
        var sprite = Descendants<PluginSpriteImage>(Standalone).Single();
        Check(sprite.IsVisible && Math.Abs(Shell.ActualWidth - 44) < .75, "The pixel companion can independently occupy the single component position");
        if (SystemParameters.ClientAreaAnimation)
        {
            int frame = sprite.FrameIndex; await Until(() => sprite.FrameIndex != frame);
            Check(sprite.IsAnimating, "The standalone pixel companion continues to animate while visible");
        }
        else Check(!sprite.IsAnimating, "Standalone animation follows the Windows reduced-motion preference");
        Capture("shrink-sprite.png");

        Select("view:shrink-fixture:wide"); await Settle();
        Check(Standalone.View?.Id == "wide" && Math.Abs(Standalone.Child!.RenderSize.Width - 64) < .75 &&
            Math.Abs(Standalone.Child.RenderSize.Height - 36) < .75 && Shell.ActualWidth >= 88 - .75,
            "A legal 64 by 36 widget retains its entire root instead of clipping to a 28-DIP ring budget");
        CheckComplete("The tallest legal widget clears the rounded capsule corners"); Capture("shrink-wide.png");
        Select("view:shrink-fixture:intrinsic"); await Settle();
        Check(Standalone.View?.Id == "intrinsic" && Math.Abs(Standalone.Child!.RenderSize.Width - 32) < .75 && Shell.ActualWidth >= 50 - .75,
            "An intrinsic ring without root dimensions is measured at its complete 32-DIP size");
        CheckComplete("The intrinsic standalone ring remains centered and visible"); Capture("shrink-intrinsic.png");
        Select("view:shrink-fixture:body"); await Settle();
        Check(Standalone.View?.Id == "body" && Math.Abs(Standalone.Child!.RenderSize.Width - 116) < .75 && Hidden("BuiltinCompactView"),
            "A custom idle.content view can serve as the only component without a residual clock");
        CheckComplete("The selected custom body has its full measured width"); Capture("shrink-body.png");

        Select("builtin:media"); await Settle();
        Check(_settings.ShrinkComponent == "builtin:media" && Element<TextBlock>("ClockLabel").IsVisible && Math.Abs(Shell.ActualWidth - 84) < .75,
            "Selecting the player without a media session temporarily displays the clock");
        var first = Session("demo.music", true, true);
        var second = Session("demo.second", false, false);
        Set("_sessions", new[] { first, second }); Render(); await Settle();
        Check(Math.Abs(Shell.ActualWidth - 44) < .75 && Element<FrameworkElement>("CompactCovers").IsVisible && Hidden("ClockLabel", "AudioBars", "SecondCover") && !Standalone.IsVisible,
            "Multiple sessions still occupy one compact media component without a clock or a second cover");
        var firstCover = Element<Image>("CompactArtwork").Source;
        Set("_preferredSessionId", "demo.second"); Render(); await Settle();
        Check(Element<Image>("CompactArtwork").Source is not null && !ReferenceEquals(firstCover, Element<Image>("CompactArtwork").Source),
            "The standalone player respects the existing explicit media-session preference");
        Set("_sessions", new[] { first with { IsPlaying = false }, second }); Render(); await Settle();
        Check(Element<FrameworkElement>("CompactCovers").IsVisible && Math.Abs(Shell.ActualWidth - 44) < .75,
            "Pausing all sessions retains the selected player component");
        CheckComplete("The standalone media cover is centered and complete"); Capture("shrink-media.png");
        MouseUp(Element<FrameworkElement>("BuiltinCompactView")); await Settle();
        Check(Get("_view")!.ToString() == "Activity" && !(bool)Get("_pluginsTab")! && !(bool)Get("_notificationsTab")!,
            "Clicking the standalone player opens media even while plugin tabs exist");
        Invoke("Collapse"); await Settle();
        Set("_sessions", Array.Empty<MediaSessionSnapshot>()); Render(); await Settle();
        Check(Element<TextBlock>("ClockLabel").IsVisible && _settings.ShrinkComponent == "builtin:media" && Math.Abs(Shell.ActualWidth - 84) < .75,
            "The last media session disappearing restores the clock and retains the player's selection");

        Set("_sessions", new[] { first }); _settings.PluginViewBindings["idle.right"] = "view:shrink-fixture:ring";
        Toggle(false); await Settle();
        Check(Math.Abs(Shell.ActualWidth - 216) < .75 && Element<PluginViewPresenter>("PluginIdleRight").View?.Id == "ring" &&
            Element<TextBlock>("ClockLabel").IsVisible && Element<FrameworkElement>("AudioBars").IsVisible,
            "Disabling shrink mode restores the ordinary media, clock and independently bound side widget");
        Toggle(true); await Settle();
        Check(_settings.ShrinkComponent == "builtin:media" && Math.Abs(Shell.ActualWidth - 44) < .75,
            "Re-enabling shrink mode retains its independent selection and suppresses ordinary side bindings");
        Select("view:shrink-fixture:ring"); await Settle();
        Invoke("OpenActivity"); await Settle();
        Check((bool)Get("_pluginsTab")! && (string?)Get("_activePluginTabKey") == "view:shrink-fixture:tab",
            "Clicking the shell's surrounding space opens the selected plugin even while media is playing");
        Invoke("Collapse"); await Settle();
        ((IslandScheduler)Get("_liveScheduler")!).Push(new IslandEvent("shrink.synthetic-notice", IslandEventKind.Notification,
            "合成通知", "收缩模式提示", "离线测试消息", DateTimeOffset.UtcNow, "synthetic.notice"));
        Render(); await Settle();
        Check(Element<FrameworkElement>("ActivityView").IsVisible && !Element<FrameworkElement>("CompactView").IsVisible && Shell.ActualWidth >= 399,
            "Shrink mode preserves the ordinary automatic notification presentation");
        Invoke("Collapse"); await Settle();
        Check(Standalone.View?.Id == "ring" && Math.Abs(Shell.ActualWidth - 44) < .75,
            "Dismissing an automatic notification restores the selected standalone component");
        await SettingsScenarios();
        await DpiScenarios();
        _observe = false; CompositionTarget.Rendering -= ObserveFrame;
        Check(_paintedFrames > 0 && _clippedFrames == 0, "Every painted frame keeps the selected component inside the rounded island clip");
        Check(_anchorErrors == 0 && _nativeChanges == 0, "Mode changes and component swaps retain the top-center anchor and fixed native window");
    }

    private async Task SettingsScenarios()
    {
        ChangeView("Settings"); await Settle();
        Element<ScrollViewer>("SettingsScroll").ScrollToTop(); await Task.Delay(100); Capture("shrink-settings-entry.png");
        var options = Element<ItemsControl>("ShrinkComponentOptions");
        var buttons = Descendants<Button>(options).ToArray();
        Check(buttons.Length == 7 && buttons.Any(button => button.Tag as string == "builtin:clock") &&
            buttons.Any(button => button.Tag as string == "builtin:media") && !buttons.Any(button => button.Tag as string == "view:shrink-fixture:denied"),
            "Settings list the clock, player and eligible plugin components while excluding opt-out views and pages");
        Check(buttons.All(button => button.ActualWidth > 180 && button.ActualHeight >= 39.5),
            "Each settings choice has a full readable button and visible selection target");
        var scroll = Element<ScrollViewer>("SettingsScroll"); scroll.ScrollToEnd(); await Task.Delay(100);
        var viewport = new Rect(scroll.RenderSize); viewport.Inflate(.75, .75);
        var last = buttons[^1]; last.BringIntoView(); await Task.Delay(100);
        var bounds = last.TransformToAncestor(scroll).TransformBounds(new Rect(last.RenderSize));
        Check(viewport.Contains(bounds), "The last plugin component remains fully reachable in the scrolling settings page");
        Capture("shrink-settings.png");
        var ringButton = buttons.Single(button => button.Tag as string == "view:shrink-fixture:ring");
        ringButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(100);
        Check(_settings.ShrinkComponent == "view:shrink-fixture:ring", "The real settings button changes the preferred component");
        Invoke("Collapse"); await Settle();
        Check(Standalone.View?.Id == "ring", "Leaving settings applies the selected standalone plugin");
        _settings.ShrinkComponent = "view:missing:removed"; Render(); await Settle();
        ChangeView("Settings"); await Settle();
        Check(Descendants<Button>(options).Any(button => button.Tag as string == "view:missing:removed") && Element<TextBlock>("ShrinkStatus").IsVisible,
            "Settings retain an unavailable saved selection and explain the temporary clock fallback");
        Invoke("Collapse"); await Settle();
        Check(Element<TextBlock>("ClockLabel").IsVisible && Math.Abs(Shell.ActualWidth - 84) < .75,
            "An unknown saved plugin key recovers to a complete single clock component");
    }

    private async Task DpiScenarios()
    {
        // Rasterize a detached copy of the production subtree; no monitor or desktop DPI is changed.
        _observe = false;
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (string id in new[] { "ring", "wide", "intrinsic", "body" })
        {
            var window = new LandBar.MainWindow(demo: true, smoke: false, preview: false);
            try
            {
                var settings = (AppSettings)Field("_settings").GetValue(window)!;
                settings.ShrinkMode = true; settings.ShrinkComponent = "view:shrink-fixture:" + id;
                settings.EnablePluginViews = true; settings.PluginViewBindings = new();
                Field("_plugins").SetValue(window, _manager);
                var selected = InvokeResult(window, "ResolveStandaloneComponent", (object)Array.Empty<MediaSessionSnapshot>());
                var layout = (CompactWidgetLayout)InvokeResult(window, "CalculateStandaloneLayout", selected, 680d)!;
                Field("_standaloneComponent").SetValue(window, selected); Field("_compactLayout").SetValue(window, layout);
                InvokeResult(window, "RenderCompact", (object)Array.Empty<MediaSessionSnapshot>());
                InvokeResult(window, "PresentPluginViews", true, false, null, layout.Width, 44d);
                var content = (Grid)window.FindName("ContentLayout"); ((Panel)content.Parent).Children.Remove(content);
                var frame = new Grid { UseLayoutRounding = false, SnapsToDevicePixels = false };
                var native = NativeWindowBounds.Calculate(0, 0, 1920, 0, layout.Width, 44, scale, scale);
                double width = native.Width / scale, height = native.Height / scale, border = Math.Round(.7 * scale) / scale;
                frame.Width = width; frame.Height = height;
                frame.Clip = new RectangleGeometry(new Rect(0, 0, width, height), 22, 22);
                TextElement.SetFontFamily(frame, window.FontFamily); VisualTreeHelper.SetRootDpi(frame, new DpiScale(scale, scale));
                frame.Children.Add(content); content.Width = width - 2 * border; content.Height = height - 2 * border;
                content.Margin = new Thickness(0, border, 0, 0); content.VerticalAlignment = VerticalAlignment.Top;
                frame.Measure(new Size(width, height)); frame.Arrange(new Rect(0, 0, width, height)); frame.UpdateLayout();
                var presenter = (PluginViewPresenter)window.FindName("PluginStandalone"); var child = (FrameworkElement)presenter.Child;
                var childBounds = child.TransformToAncestor(frame).TransformBounds(new Rect(child.RenderSize));
                var clip = frame.Clip; bool fits = clip.FillContains(childBounds.TopLeft) && clip.FillContains(childBounds.TopRight) &&
                    clip.FillContains(childBounds.BottomLeft) && clip.FillContains(childBounds.BottomRight);
                var bitmap = new RenderTargetBitmap(native.Width, native.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(frame);
                byte[] pixels = new byte[native.Width * native.Height * 4]; bitmap.CopyPixels(pixels, native.Width * 4, 0);
                bool painted = Enumerable.Range(0, pixels.Length / 4).Any(index => pixels[index * 4 + 3] > 32);
                Check(fits && painted && child.ActualWidth > 0 && child.ActualHeight > 0,
                    $"{id} remains painted and complete at {scale * 100:0}% DPI in the production compact subtree");
                using var file = File.Create(Path.Combine(output, $"shrink-{id}-{scale * 100:0}.png"));
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); encoder.Save(file);
            }
            finally
            {
                Field("_closing").SetValue(window, true); InvokeResult(window, "DisposePluginViews");
                foreach (string field in new[] { "_notifications", "_media" }) (Field(field).GetValue(window) as IDisposable)?.Dispose();
                window.Close();
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
    }

    private void ObserveFrame(object? sender, EventArgs e)
    {
        if (!_observe) return;
        var surface = Element<FrameworkElement>("HoverSurface"); var content = Element<FrameworkElement>("ContentLayout");
        var origin = Shell.TranslatePoint(new Point(), surface);
        double anchorError = Math.Abs(origin.X + Shell.ActualWidth / 2 - surface.ActualWidth / 2);
        if (anchorError > 1 / VisualTreeHelper.GetDpi(_window).DpiScaleX) _anchorErrors++;
        GetWindowRect(new WindowInteropHelper(_window).Handle, out var native);
        if (!native.Equals(_nativeStart)) _nativeChanges++;
        bool fits = true, painted = content.Opacity > .02;
        if (painted)
        {
            _paintedFrames++;
            if (Element<FrameworkElement>("CompactView").IsVisible)
            {
                var viewport = Element<FrameworkElement>("IslandViewport");
                var elements = new List<FrameworkElement>();
                if (Standalone.View is not null && Standalone.Child is FrameworkElement child) elements.Add(child);
                foreach (string name in new[] { "ClockLabel", "CompactCovers", "AudioBars" })
                    if (Element<FrameworkElement>(name).IsVisible) elements.Add(Element<FrameworkElement>(name));
                foreach (var item in elements)
                {
                    var bounds = item.TransformToAncestor(viewport).TransformBounds(new Rect(item.RenderSize)); var clip = viewport.Clip!;
                    fits &= clip.FillContains(bounds.TopLeft) && clip.FillContains(bounds.TopRight) && clip.FillContains(bounds.BottomLeft) && clip.FillContains(bounds.BottomRight);
                }
            }
            if (!fits) _clippedFrames++;
        }
        _samples.Add(new { elapsedMs = _watch.Elapsed.TotalMilliseconds, width = Shell.ActualWidth, content.Opacity, painted, fits, anchorError });
    }

    private void CheckFallback(string name) => Check(_settings.ShrinkComponent == "view:shrink-fixture:ring" &&
        Element<TextBlock>("ClockLabel").IsVisible && !Standalone.IsVisible && Math.Abs(Shell.ActualWidth - 84) < .75, name);
    private void CheckComplete(string name)
    {
        var root = Element<FrameworkElement>("HoverSurface"); var content = Element<FrameworkElement>("ContentLayout");
        FrameworkElement component = Standalone.IsVisible ? (FrameworkElement)Standalone.Child : Element<FrameworkElement>("CompactCovers");
        var bounds = component.TransformToAncestor(root).TransformBounds(new Rect(component.RenderSize));
        var shellBounds = Shell.TransformToAncestor(root).TransformBounds(new Rect(Shell.RenderSize));
        var viewport = Element<FrameworkElement>("IslandViewport"); var clip = viewport.Clip!;
        var local = component.TransformToAncestor(viewport).TransformBounds(new Rect(component.RenderSize));
        bool fits = clip.FillContains(local.TopLeft) && clip.FillContains(local.TopRight) && clip.FillContains(local.BottomLeft) && clip.FillContains(local.BottomRight);
        Check(fits && Math.Abs(bounds.X + bounds.Width / 2 - shellBounds.X - shellBounds.Width / 2) <= 1 &&
            content.Opacity >= .99 && content.IsHitTestVisible, name);
    }
    private async Task Configure(string mode, Func<bool> ready)
    {
        Check(await _manager!.SendContextAsync("shrink-fixture", "activity.tab", new { fixture = mode }), mode + ": the synthetic view update reaches the owned provider");
        await Until(ready); Render(); await Settle();
    }
    private static MediaSessionSnapshot Session(string id, bool playing, bool current) => new(id, "synthetic." + id, "合成播放器", "合成曲目", "合成作者", null, playing, current, true, true, true);
    private void Toggle(bool enabled) { Element<CheckBox>("ShrinkModeCheck").IsChecked = enabled; Invoke("ShrinkMode_Click", Element<CheckBox>("ShrinkModeCheck"), new RoutedEventArgs(Button.ClickEvent)); }
    private void Select(string key) => Invoke("ShrinkComponent_Click", new Button { Tag = key }, new RoutedEventArgs(Button.ClickEvent));
    private static void MouseUp(UIElement element) => element.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = UIElement.MouseLeftButtonUpEvent });
    private bool Hidden(params string[] names) => names.All(name => !Element<FrameworkElement>(name).IsVisible);
    private ColumnDefinition Column(string name) => (ColumnDefinition)_window.FindName(name);
    private FrameworkElement Shell => Element<FrameworkElement>("Island");
    private PluginViewPresenter Standalone => Element<PluginViewPresenter>("PluginStandalone");
    private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
    private static FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private object? Get(string name) => Field(name).GetValue(_window);
    private void Set(string name, object? value) => Field(name).SetValue(_window, value);
    private void Invoke(string name, params object?[] args) => InvokeResult(_window, name, args);
    private void Render() => Invoke("Render");
    private void ChangeView(string name) => Invoke("ChangeView", Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, name));
    private static object? InvokeResult(LandBar.MainWindow window, string name, params object?[] args) => typeof(LandBar.MainWindow)
        .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly).Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(window, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index); if (child is T value) yield return value;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static Task Settle() => Task.Delay(800);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Standalone synthetic provider timed out."); await Task.Delay(25); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private void Check(bool condition, string name) { (condition ? _passed : _failures).Add(name); Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}"); }
    private void Capture(string name)
    {
        var root = Element<FrameworkElement>("HoverSurface"); var dpi = VisualTreeHelper.GetDpi(_window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(root); var origin = Shell.TranslatePoint(new Point(), root);
        var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY), (int)Math.Ceiling(Shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(Shell.ActualHeight * dpi.DpiScaleY)));
        using var file = File.Create(Path.Combine(output, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(file);
    }
    private async Task Finish()
    {
        _observe = false; CompositionTarget.Rendering -= ObserveFrame;
        if (_window is not null)
        {
            Set("_closing", true); ((DispatcherTimer)Get("_timer")!).Stop();
            if (_manager is not null) await _manager.DisposeAsync();
            Invoke("DisposePluginViews");
            foreach (string name in new[] { "_topmost", "_contentTransition", "_motion", "_notifications", "_media", "_tray", "_trayIcon" }) (Get(name) as IDisposable)?.Dispose();
        }
        File.WriteAllText(Path.Combine(output, "shrink-report.json"), JsonSerializer.Serialize(new
        {
            startsSystemSources = false, startsCodexAppServer = false, usesSyntheticChildProcess = true, movesUserPointer = false,
            privateAccountData = false, passed = _passed, failures = _failures, paintedFrames = _paintedFrames,
            clippedFrames = _clippedFrames, anchorErrors = _anchorErrors, nativeChanges = _nativeChanges
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "shrink-frames.json"), JsonSerializer.Serialize(_samples));
        Shutdown(_failures.Count == 0 ? 0 : 1);
    }

    internal static async Task<int> RunFixture()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        void Send(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
        void View(string id, string slot, string title, object root, object? placement = null, bool allowStandalone = true)
        {
            var message = new Dictionary<string, object?> { ["type"] = "view", ["id"] = id, ["slot"] = slot, ["title"] = title, ["root"] = root };
            if (placement is not null) message["placement"] = placement;
            if (slot.StartsWith("idle.", StringComparison.Ordinal)) message["allowStandalone"] = allowStandalone;
            Send(message);
        }
        object placement = new { defaultSide = "right", allowedSides = new[] { "left", "right" } };
        void Ring(bool allowed = true) => View("ring", "idle.widget", "合成额度", new { type = "ring", width = 28, height = 28, value = .41, text = "41", fontSize = 12 }, placement, allowed);
        void Publish()
        {
            Ring();
            View("sprite", "idle.widget", "像素伙伴", new { type = "sprite", width = 28, height = 28, pixelWidth = 2, pixelHeight = 2,
                palette = new[] { "#00000000", "#BEDDC9", "#86BBA4" }, frames = new[] { "0110", "0220" }, fps = 6 }, placement);
            View("body", "idle.content", "自定义主体", new { type = "text", width = 116, height = 28, text = "自定义状态", fontSize = 15, align = "center" });
            View("wide", "idle.widget", "宽组件", new { type = "ring", width = 64, height = 36, value = .41, text = "41", fontSize = 12 }, placement);
            View("intrinsic", "idle.widget", "固有尺寸", new { type = "ring", value = .41, text = "41", fontSize = 12 }, placement);
            View("denied", "idle.widget", "拒绝单独展示", new { type = "ring", width = 28, height = 28, value = .41, text = "41", fontSize = 12 }, placement, false);
            View("tab", "activity.tab", "合成详情", new { type = "text", width = 320, height = 140, text = "合成组件详情", fontSize = 20 });
        }
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line); var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "hello": Send(new { type = "hello", protocolVersion = 1 }); Publish(); break;
                case "shutdown": return 0;
                case "context":
                    if (message.GetProperty("data").TryGetProperty("fixture", out var fixture))
                    {
                        if (fixture.GetString() == "remove-ring") Send(new { type = "removeView", id = "ring" });
                        if (fixture.GetString() == "restore-ring") Ring();
                        if (fixture.GetString() == "deny-ring") Ring(false);
                    }
                    break;
            }
        }
        return 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr window, out NativeRectangle rectangle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
