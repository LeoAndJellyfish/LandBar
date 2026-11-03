using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;
using LandBar.Services;

// Loads production BAML with an isolated tab provider and synthetic notifications/media.
internal sealed class ActivityTabsProbe(string output) : Application
{
    private LandBar.MainWindow _window = null!;
    private PluginManager? _manager;
    private readonly List<string> _passed = [], _failures = [];
    private readonly List<object> _measurements = [];
    private readonly List<object> _frames = [];
    private bool _observe;

    protected override void OnStartup(StartupEventArgs e)
    {
        _window = new LandBar.MainWindow(demo: true, smoke: false) { Title = "LandBar Synthetic Activity Tabs Checks" };
        MainWindow = _window;
        _window.Loaded += async (_, _) =>
        {
            try { await Scenarios(); }
            catch (Exception error) { Check(false, error.ToString()); }
            finally { await Finish(); }
        };
        _window.Show();
    }

    private async Task Scenarios()
    {
        var settings = (AppSettings)Get("_settings")!;
        settings.AutoCollapseSeconds = 0; settings.Quiet = true; settings.ShrinkMode = false;
        settings.EnablePluginViews = true; settings.PluginViewBindings = new();
        Invoke("ApplyAutoCollapseSetting"); await Settle(); Invoke("EndDemo"); Invoke("Collapse"); await Settle();
        SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, -12000, -12000, 0, 0, 0x0015);
        ((DispatcherTimer)Get("_timer")!).Stop();
        _observe = true; CompositionTarget.Rendering += ObserveFrame;
        string folder = Path.Combine(output, "synthetic-tab-plugins", "tabs-fixture"); Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
        {
            id = "tabs-fixture", name = "标签测试", version = "1.0.0", description = "纯合成标签与页面",
            command = Environment.ProcessPath!, arguments = new[] { "--tabs-fixture" }, protocolVersion = 1
        }));
        _manager = new PluginManager(Path.GetDirectoryName(folder)!); Invoke("ConnectPlugins", _manager); _manager.Discover();
        Check(await _manager.StartAsync("tabs-fixture"), "The synthetic tab provider completes a real pipe handshake");
        await Until(() => _manager.Views.Count == 3); Render(); await Settle();
        Sessions(1); Notices(1); ChangeView("Activity"); SelectBuiltin("NotificationsTab"); await Settle();
        Check(TabButtons().Count == 5, "Media, notifications and three named plugin tabs coexist");
        CheckCentered("The notification selection is centered in the narrow tab viewport");
        Capture("tabs-notifications-narrow.png");

        _observe = false; Scroll.ScrollToRightEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SelectBuiltin("MediaTab"); await Settle();
        _observe = true;
        CheckCentered("Selecting media after a retained right offset reveals and centers its complete label");
        Check(Element<FrameworkElement>("EventItems").IsVisible && !(bool)Get("_pluginsTab")!, "Revealing the media tab retains its actual media page");
        Capture("tabs-media-from-right.png");
        _observe = false; Scroll.ScrollToRightEnd(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        SelectBuiltin("MediaTab"); await Settle();
        _observe = true;
        CheckCentered("Re-selecting the current media tab repairs a manually retained scroll offset");

        var initialButtons = PluginButtons().ToArray();
        Click(Tab("focus")); await Settle(); CheckCentered("The final focus tab is centered and complete at 400 DIP");
        Check(Element<PluginViewPresenter>("ActivityOverrideContent").View?.Id == "focus", "Selecting focus displays the focus page rather than withdrawing it");
        Check(initialButtons.Zip(PluginButtons()).All(pair => ReferenceEquals(pair.First, pair.Second)), "Selection updates preserve every existing plugin tab button instance");
        Capture("tabs-focus-narrow.png");

        var selected = Tab("focus");
        Check(await _manager.SendContextAsync("tabs-fixture", "activity.tab", new { fixture = "payload" }), "The provider receives a synthetic page-body update");
        await Until(() => _manager.Views.Any(view => view.Id == "focus" && view.Root.Text == "更新后的专注页面")); await Settle();
        Check(ReferenceEquals(selected, Tab("focus")), "A page payload refresh preserves its selected tab button");
        CheckCentered("Page body updates retain the selected tab center");

        SelectBuiltin("NotificationsTab"); await Settle(); CheckCentered("Switching focus to notifications preserves the selected title in the narrow header");
        Notices(2); Render(); await Settle();
        Check(Math.Abs(Element<FrameworkElement>("Island").ActualWidth - 680) < 1, "Two synthetic notification cards expand the island to 680 DIP");
        CheckCentered("The selected notification tab recenters when the viewport grows"); Capture("tabs-notifications-wide.png");
        Notices(1); Render(); await Settle(); CheckCentered("Returning to one card recenters after the viewport shrinks");
        Click(Tab("codex")); await Settle(); CheckCentered("The Codex tab is centered when the notification-only clear action disappears");
        SelectBuiltin("MediaTab"); Sessions(2); Render(); await Settle(); CheckCentered("The media tab is centered when two media sessions widen the header");
        Sessions(1); Render(); await Settle(); CheckCentered("The media tab remains complete after shrinking back to one player");

        Click(Tab("focus")); await Settle();
        Check(await _manager.SendContextAsync("tabs-fixture", "activity.tab", new { fixture = "title" }), "The synthetic provider can update its current tab title");
        await Until(() => _manager.Views.Any(view => view.Id == "focus" && view.Title == "专注计时与当前任务")); await Settle();
        CheckCentered("A longer active title is remeasured and centered without disappearing");
        Check(Element<PluginViewPresenter>("ActivityOverrideContent").View?.Id == "focus", "Changing the title preserves the selected activity page");
        var longTitleButton = Tab("focus");
        longTitleButton.BringIntoView(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(100);
        CheckCentered("A WPF bring-into-view request retains the selected label's center");
        Check(ReferenceEquals(selected, longTitleButton), "Changing a title reuses its existing plugin tab button");
        CheckHeaderSeparation("The tab viewport and navigation remain separate from the fixed page actions");

        var keys = new[] { "builtin:media", "builtin:notifications" }.Concat(_manager.Views.Where(view => view.Slot == "activity.tab").Select(view => view.StableKey)).ToArray();
        Invoke("SelectActivityTab", keys[0], false); await Settle();
        var visited = new List<string> { SelectedKey() };
        for (int i = 1; i < keys.Length; i++)
        {
            Invoke("NavigateActivityTab", 1, false); await Settle(); visited.Add(SelectedKey());
            CheckCentered("Sequential overflow navigation exposes complete label " + i);
        }
        Check(visited.SequenceEqual(keys), "Every media, notification and plugin page remains reachable through adjacent navigation");
        Invoke("NavigateActivityTab", 1, false); await Settle();
        Check(SelectedKey() == keys[^1], "Advancing at the final tab keeps the final available page selected");
        SelectBuiltin("MediaTab"); await Settle();
        Tab("pet").BringIntoView(); await Task.Delay(100);
        Check(SelectedKey() == "builtin:media", "An unrelated bring-into-view request does not replace the current media selection");
        CheckCentered("Implicit focus scrolling cannot hide the selected media label");
        await RoutedInteractionScenarios(keys);
        await DpiScenarios();
        Click(Tab("focus")); await Settle();
        Check(await _manager.SendContextAsync("tabs-fixture", "activity.tab", new { fixture = "narrow-title" }), "The provider can contribute a minimum-width page with a long tab title");
        await Until(() => _manager.Views.Any(view => view.Id == "focus" && view.Root.Width == 276)); await Settle();
        Check(Math.Abs(Element<FrameworkElement>("Island").ActualWidth - 320) < 1, "The minimum-width custom page uses a 320-DIP island");
        CheckCentered("A long selected tab fits and centers inside the minimum-width page header");
        Check(Tab("focus").ToolTip as string == "一个很长的专注计时与当前任务", "Ellipsized tab text retains its complete accessible tooltip");
        CheckHeaderSeparation("The minimum-width page keeps tab navigation clear of fixed page actions");
        Capture("tabs-minimum-width.png");
        Check(await _manager.SendContextAsync("tabs-fixture", "activity.tab", new { fixture = "title" }), "The provider can return from its minimum-width page");
        await Until(() => _manager.Views.Any(view => view.Id == "focus" && view.Root.Width == 356)); await Settle();
        CheckCentered("Restoring the larger custom page recenters the selected tab");

        var previous = _window.FindName("ActivityTabsPrevious") as Button;
        var next = _window.FindName("ActivityTabsNext") as Button;
        Check(previous is not null && next is not null, "Overflow navigation exposes both adjacent-tab controls");
        if (previous is not null && next is not null)
        {
            Click(previous); await Settle(); CheckCentered("The previous-tab control reveals a centered reachable neighbor");
            Click(next); await Settle(); CheckCentered("The next-tab control returns to the complete focus selection");
        }
        Click(Tab("focus")); await Settle();
        Check(await _manager.SendContextAsync("tabs-fixture", "activity.tab", new { fixture = "remove-focus" }), "The provider can explicitly withdraw only its focus tab");
        await Until(() => _manager.Views.Count == 2); await Settle();
        Check(TabButtons().Count == 4 && Element<PluginViewPresenter>("ActivityOverrideContent").View is not null,
            "Withdrawing the current plugin tab chooses an available page without losing media or other tabs");
        CheckCentered("The surviving selection is centered after the current tab is withdrawn");
        for (int i = 0; i < 4; i++)
        {
            SelectBuiltin("NotificationsTab"); Notices(i % 2 + 1); Render(); await Task.Delay(45);
            Click(Tab("pet")); await Task.Delay(45); SelectBuiltin("MediaTab"); Sessions(i % 2 + 1); Render(); await Task.Delay(45);
        }
        Sessions(1); Render(); await Settle();
        CheckCentered("Rapid tab changes and width retargeting settle on the latest complete selection");
        Check(TabButtons().Count == 4, "Rapid retargeting preserves the complete set of surviving tabs");
        Capture("tabs-rapid-final.png");
        CheckHeaderSeparation("Rapid layout changes preserve the separation between navigation and page actions");
    }

    private async Task RoutedInteractionScenarios(string[] keys)
    {
        Sessions(4); SelectBuiltin("MediaTab"); await Settle();
        Set("_page", 1); Render(); await Settle();
        var boundary = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        Element<Button>("MediaTab").RaiseEvent(boundary); await Settle();
        Check(boundary.Handled && SelectedKey() == "builtin:media" && (int)Get("_page")! == 1,
            "Header navigation at the first tab preserves the media body's second page");
        var down = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, -120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        Scroll.RaiseEvent(down); await Settle();
        Check(down.Handled && SelectedKey() == "builtin:notifications" && (int)Get("_page")! == 0,
            "A routed wheel event on the tab viewport selects its neighbor without paging the media body");
        CheckCentered("Routed wheel navigation centers the new notification selection");
        var up = new MouseWheelEventArgs(Mouse.PrimaryDevice, 0, 120) { RoutedEvent = UIElement.PreviewMouseWheelEvent };
        Element<Button>("NotificationsTab").RaiseEvent(up); await Settle();
        Check(up.Handled && SelectedKey() == "builtin:media" && (int)Get("_page")! == 0,
            "A routed wheel event from the label reaches header navigation and returns to media");
        CheckCentered("Reverse wheel navigation returns to the centered media selection");
        foreach (var (key, expected) in new[] { (Key.Right, "builtin:notifications"), (Key.End, keys[^1]), (Key.Home, "builtin:media") })
        {
            var button = Selected()!;
            var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window), 0, key)
                { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            button.RaiseEvent(args); await Settle();
            Check(args.Handled && SelectedKey() == expected, $"A routed {key} key from a tab selects the expected page");
            CheckCentered($"Routed {key} key navigation keeps its selected label centered");
            var target = Selected()!;
            bool keyboardFocused = target.IsKeyboardFocused;
            bool logicalFocused = ReferenceEquals(FocusManager.GetFocusedElement(_window), target);
            _measurements.Add(new { input = key.ToString(), selected = SelectedKey(), keyboardFocused, logicalFocused,
                target.IsTabStop, target.IsVisible, target.IsEnabled, target.IsHitTestVisible, target.Focusable,
                pendingFocus = Get("_activityTabFocusKey"), contentInteractive = Element<FrameworkElement>("ContentLayout").IsHitTestVisible });
            Check(target.IsTabStop && (keyboardFocused || logicalFocused), $"Routed {key} navigation directs focus to the selected tab");
        }
        Sessions(1); Render(); await Settle();
    }

    private async Task DpiScenarios()
    {
        foreach (double scale in new[] { 1d, 1.25, 1.5, 2d })
        foreach (string key in new[] { "builtin:media", "builtin:notifications", "view:tabs-fixture:focus" })
        {
            var window = new LandBar.MainWindow(demo: true, smoke: false);
            try
            {
                var settings = (AppSettings)Field("_settings").GetValue(window)!; settings.EnablePluginViews = true;
                Field("_plugins").SetValue(window, _manager);
                Field("_view").SetValue(window, Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, "Activity"));
                Field("_pluginsTab").SetValue(window, key.StartsWith("view:", StringComparison.Ordinal));
                Field("_notificationsTab").SetValue(window, key == "builtin:notifications");
                if (key.StartsWith("view:", StringComparison.Ordinal)) Field("_activePluginTabKey").SetValue(window, key);
                foreach (string name in new[] { "CompactView", "ControlCenterView", "SettingsScroll", "PluginManagementScroll" })
                    ((FrameworkElement)window.FindName(name)).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("ActivityView")).Visibility = Visibility.Visible;
                ((FrameworkElement)window.FindName("ActivityTabs")).Visibility = Visibility.Visible;
                typeof(LandBar.MainWindow).GetMethod("RenderPluginTabs", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                var content = (Grid)window.FindName("ContentLayout"); ((Panel)content.Parent).Children.Remove(content);
                var frame = new Grid { Width = 400, Height = 240 };
                using var source = new HwndSource(new HwndSourceParameters("LandBar Synthetic DPI Tabs")
                {
                    PositionX = -12000, PositionY = -12000, Width = (int)(400 * scale), Height = (int)(240 * scale),
                    WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000000
                });
                source.RootVisual = frame;
                System.Windows.Documents.TextElement.SetFontFamily(frame, window.FontFamily);
                VisualTreeHelper.SetRootDpi(frame, new DpiScale(scale, scale)); frame.Children.Add(content);
                content.Width = 400 - 2 * Math.Round(.7 * scale) / scale; content.Height = 238;
                for (int pass = 0; pass < 8; pass++)
                {
                    frame.Measure(new Size(400, 240)); frame.Arrange(new Rect(0, 0, 400, 240)); frame.UpdateLayout();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                }
                var scroll = (ScrollViewer)window.FindName("ActivityTabScroll");
                var button = key.StartsWith("view:", StringComparison.Ordinal)
                    ? Descendants<Button>((ItemsControl)window.FindName("PluginTabItems")).Single(item => item.Tag as string == key)
                    : (Button)window.FindName(key == "builtin:media" ? "MediaTab" : "NotificationsTab");
                var viewport = Descendants<ScrollContentPresenter>(scroll).First();
                var rect = button.TransformToAncestor(viewport).TransformBounds(new Rect(button.RenderSize));
                double error = Math.Abs(rect.X + rect.Width / 2 - viewport.ActualWidth / 2);
                Check(error * scale <= 1 && rect.Left >= -.75 && rect.Right <= viewport.ActualWidth + .75,
                    $"The production header centers {key} at {scale * 100:0}% DPI within one physical pixel");
            }
            finally
            {
                Field("_closing").SetValue(window, true);
                typeof(LandBar.MainWindow).GetMethod("DisposePluginViews", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
                foreach (string name in new[] { "_notifications", "_media" }) (Field(name).GetValue(window) as IDisposable)?.Dispose();
                window.Close();
            }
        }
    }

    private string SelectedKey() => (bool)Get("_pluginsTab")! ? (string)Get("_activePluginTabKey")!
        : (bool)Get("_notificationsTab")! ? "builtin:notifications" : "builtin:media";
    private void CheckHeaderSeparation(string name)
    {
        var view = Element<FrameworkElement>("ActivityView"); var navigation = Element<FrameworkElement>("ActivityTabNavigation");
        var actions = (FrameworkElement)VisualTreeHelper.GetParent(VisualTreeHelper.GetParent(Element<Button>("ClearNotificationsButton")));
        var nav = navigation.TransformToAncestor(view).TransformBounds(new Rect(navigation.RenderSize));
        var right = actions.TransformToAncestor(view).TransformBounds(new Rect(actions.RenderSize));
        Check(nav.Right <= right.Left + .75, name);
    }
    private void ObserveFrame(object? sender, EventArgs args)
    {
        if (!_observe || !Element<FrameworkElement>("ActivityView").IsVisible) return;
        var content = Element<FrameworkElement>("ContentLayout");
        var selected = TabButtons().FirstOrDefault(button => System.Windows.Automation.AutomationProperties.GetItemStatus(button) == "已选择");
        if (selected is null || !selected.IsVisible || selected.RenderSize.Width <= 0) return;
        var rect = selected.TransformToAncestor(Scroll).TransformBounds(new Rect(selected.RenderSize));
        _frames.Add(new { key = selected.Tag as string ?? selected.Name, requested = SelectedKey(), content.Opacity, Scroll.ViewportWidth, Scroll.HorizontalOffset,
            centerError = Math.Abs(rect.X + rect.Width / 2 - Scroll.ViewportWidth / 2),
            complete = rect.Left >= -.75 && rect.Right <= Scroll.ViewportWidth + .75 });
    }

    private void CheckCentered(string name)
    {
        Button? selected = Selected();
        if (selected is null) { Check(false, name + " (selection missing)"); return; }
        var rect = selected.TransformToAncestor(Scroll).TransformBounds(new Rect(selected.RenderSize));
        double viewportWidth = Scroll.ViewportWidth;
        var presenter = Descendants<ScrollContentPresenter>(Scroll).FirstOrDefault();
        double viewportLeft = presenter?.TransformToAncestor(Scroll).Transform(new Point()).X ?? 0;
        double error = Math.Abs(rect.X + rect.Width / 2 - viewportLeft - viewportWidth / 2);
        bool complete = rect.Left >= viewportLeft - .75 && rect.Right <= viewportLeft + viewportWidth + .75 && selected.IsVisible;
        _measurements.Add(new { name, width = Element<FrameworkElement>("Island").ActualWidth, viewportWidth, viewportLeft,
            offset = Scroll.HorizontalOffset, rect = rect.ToString(), centerError = error, complete,
            current = selected.Tag as string ?? selected.Name, titles = TabButtons().Select(button => button.Content?.ToString()).ToArray() });
        Check(complete && error <= 1, name + $" (center error {error:F2} DIP, complete {complete})");
    }
    private Button? Selected() => (bool)Get("_pluginsTab")! ? PluginButtons().FirstOrDefault(button => button.Tag as string == (string?)Get("_activePluginTabKey"))
        : Element<Button>((bool)Get("_notificationsTab")! ? "NotificationsTab" : "MediaTab");
    private List<Button> TabButtons() => new[] { Element<Button>("MediaTab"), Element<Button>("NotificationsTab") }.Concat(PluginButtons()).ToList();
    private IEnumerable<Button> PluginButtons() => Descendants<Button>(Element<ItemsControl>("PluginTabItems"));
    private Button Tab(string id) => PluginButtons().Single(button => button.Tag as string == "view:tabs-fixture:" + id);
    private void SelectBuiltin(string name) => Click(Element<Button>(name));
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private void Sessions(int count) => Set("_sessions", Enumerable.Range(0, count).Select(i => new MediaSessionSnapshot("demo." + i, "synthetic.media", "合成播放器", "合成曲目", "合成作者", null, true, i == 0, true, true, true)).ToArray());
    private void Notices(int count)
    {
        var live = (IslandScheduler)Get("_liveScheduler")!; live.ClearNotifications();
        for (int i = 0; i < count; i++) live.Push(new("synthetic.notice." + Guid.NewGuid().ToString("N"), IslandEventKind.Notification,
            "合成通知", "合成通知标题", "合成通知正文", DateTimeOffset.UtcNow));
    }
    private ScrollViewer Scroll => Element<ScrollViewer>("ActivityTabScroll");
    private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
    private static FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;
    private object? Get(string name) => Field(name).GetValue(_window);
    private void Set(string name, object? value) => Field(name).SetValue(_window, value);
    private void Invoke(string name, params object?[] arguments) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
        .Single(method => method.Name == name && method.GetParameters().Length == arguments.Length).Invoke(_window, arguments);
    private void Render() => Invoke("Render");
    private void ChangeView(string value) => Invoke("ChangeView", Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, value));
    private static Task Settle() => Task.Delay(800);
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew(); while (!condition()) { if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Tab fixture did not reach its expected state."); await Task.Delay(25); }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }
    private void Check(bool condition, string name) { (condition ? _passed : _failures).Add(name); Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}"); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T value) yield return value; foreach (var descendant in Descendants<T>(child)) yield return descendant; }
    }
    private void Capture(string name)
    {
        var root = Element<FrameworkElement>("HoverSurface"); var shell = Element<FrameworkElement>("Island"); var dpi = VisualTreeHelper.GetDpi(_window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(root); var origin = shell.TranslatePoint(new Point(), root);
        var crop = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY), (int)Math.Ceiling(shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(shell.ActualHeight * dpi.DpiScaleY)));
        using var file = File.Create(Path.Combine(output, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(crop)); encoder.Save(file);
    }
    private async Task Finish()
    {
        _observe = false; CompositionTarget.Rendering -= ObserveFrame;
        Set("_closing", true); ((DispatcherTimer)Get("_timer")!).Stop(); if (_manager is not null) await _manager.DisposeAsync(); Invoke("DisposePluginViews");
        foreach (string name in new[] { "_topmost", "_contentTransition", "_motion", "_notifications", "_media", "_tray", "_trayIcon" }) (Get(name) as IDisposable)?.Dispose();
        File.WriteAllText(Path.Combine(output, "tabs-report.json"), JsonSerializer.Serialize(new { startsSystemSources = false, privateAccountData = false,
            usesSyntheticChildProcess = true, passed = _passed, failures = _failures, measurements = _measurements }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.Combine(output, "tabs-frames.json"), JsonSerializer.Serialize(_frames));
        Console.WriteLine($"{_passed.Count}/{_passed.Count + _failures.Count} activity-tab checks passed"); _window.Close(); Shutdown(_failures.Count == 0 ? 0 : 1);
    }

    internal static async Task<int> RunFixture()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        void Send(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
        void Tab(string id, string title, string body = "合成页面", int width = 356) => Send(new { type = "view", id, slot = "activity.tab", title,
            root = new { type = "text", width, height = 140, text = body, fontSize = 18 } });
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var document = JsonDocument.Parse(line); var message = document.RootElement;
            switch (message.GetProperty("type").GetString())
            {
                case "hello": Send(new { type = "hello", protocolVersion = 1 }); Tab("codex", "Codex"); Tab("pet", "像素伙伴"); Tab("focus", "专注计时"); break;
                case "shutdown": return 0;
                case "context":
                    if (message.GetProperty("data").TryGetProperty("fixture", out var fixture))
                    {
                        if (fixture.GetString() == "payload") Tab("focus", "专注计时", "更新后的专注页面");
                        if (fixture.GetString() == "title") Tab("focus", "专注计时与当前任务");
                        if (fixture.GetString() == "narrow-title") Tab("focus", "一个很长的专注计时与当前任务", width: 276);
                        if (fixture.GetString() == "remove-focus") Send(new { type = "removeView", id = "focus" });
                    }
                    break;
            }
        }
        return 0;
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
