using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--plugin-fixture")) return RunFixtureAsync().GetAwaiter().GetResult();
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/plugins-smoke");
        Directory.CreateDirectory(output);
        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "settings.json");
        string? before = SettingsHash(settingsPath);
        var app = new ProbeApplication(output) { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var production = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Production-App.xaml"));
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            production.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        int result = app.Run();
        bool unchanged = before == SettingsHash(settingsPath);
        File.WriteAllText(Path.Combine(output, "settings-unchanged.json"), JsonSerializer.Serialize(new { unchanged }));
        return unchanged ? result : 1;
    }

    private static string? SettingsHash(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : null;

    // A real child process that implements the public protocol without an extra language runtime.
    private static async Task<int> RunFixtureAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        object gate = new();
        bool paused = false;
        int elapsed = 0;
        void Send(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
        void Upsert() => Send(new
        {
            type = "upsert", id = "timer", title = "专注计时 · 测试",
            body = $"{(paused ? "已暂停" : "运行中")} · {elapsed} 秒", icon = "timer", progress = elapsed / 100.0,
            actions = new[] { new { id = "toggle", label = paused ? "继续" : "暂停" }, new { id = "reset", label = "重置" }, new { id = "finish", label = "完成" } }
        });
        string? hello = await Console.In.ReadLineAsync();
        if (hello is null) return 1;
        using (var document = JsonDocument.Parse(hello))
            if (document.RootElement.GetProperty("type").GetString() != "hello") return 1;
        lock (gate) { Send(new { type = "hello", protocolVersion = 1 }); Upsert(); }
        using var stop = new CancellationTokenSource();
        var ticker = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    await Task.Delay(1000, stop.Token);
                    lock (gate) { if (!paused) elapsed = Math.Min(99, elapsed + 1); Upsert(); }
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                string? type = root.GetProperty("type").GetString();
                if (type == "shutdown") return 0;
                if (type != "action") continue;
                lock (gate)
                {
                    switch (root.GetProperty("actionId").GetString())
                    {
                        case "toggle": paused = !paused; break;
                        case "reset": elapsed = 0; paused = false; break;
                        case "finish": elapsed = 99; paused = true; break;
                    }
                    Upsert();
                }
            }
            return 0;
        }
        finally { stop.Cancel(); await ticker; }
    }

    // Production views, a temporary plugin directory and synthetic sources only.
    private sealed class ProbeApplication(string output) : Application
    {
        private LandBar.MainWindow _window = null!;
        private PluginManager? _manager;
        private readonly List<string> _passed = [];
        private readonly List<string> _failures = [];
        private object? _headerGeometry;

        protected override void OnStartup(StartupEventArgs e)
        {
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Plugin Integration Smoke" };
            MainWindow = _window;
            _window.Loaded += async (_, _) =>
            {
                try { await Scenarios(); }
                catch (Exception error) { _failures.Add(error.ToString()); }
                finally { await FinishAsync(); }
            };
            _window.Show();
        }

        private async Task Scenarios()
        {
            var settings = (AppSettings)Field("_settings").GetValue(_window)!;
            settings.AutoCollapseSeconds = 0; settings.Quiet = false; settings.HideContent = false;
            Invoke("ApplyAutoCollapseSetting");
            await Task.Delay(800);
            Invoke("EndDemo"); Invoke("Collapse");
            string root = Path.Combine(output, "fixture-plugins");
            string folder = Path.Combine(root, "focus-test");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
            {
                id = "focus-test", name = "专注计时器 · 测试", version = "1.0.0", description = "本地进程协议与界面联动检查",
                command = Environment.ProcessPath!, arguments = new[] { "--plugin-fixture" }, protocolVersion = 1
            }));
            _manager = new PluginManager(root);
            Invoke("ConnectPlugins", _manager);
            _manager.Discover();
            Assert(_manager.Plugins.Single().State == PluginState.Disabled && _manager.Cards.Count == 0,
                "Discovery leaves a local plugin disabled with no cards");
            Assert(await _manager.StartAsync("focus-test"), "Explicit plugin start completes the public stdio handshake");
            await Until(() => _manager.Cards.Count == 1 && Live.TotalCount == 1);
            await Settle();
            Size nativeSize = _window.RenderSize;
            Assert(Element<FrameworkElement>("ActivityView").IsVisible && Cards.Count == 1 && CardValue<bool>(0, "IsPlugin"),
                "The first plugin card expands the automatic island");
            Assert(Descendants<ProgressBar>(Element<ItemsControl>("EventItems")).Single().IsVisible,
                "A plugin progress value renders through the production progress control");
            Assert(!Descendants<Button>(Element<ItemsControl>("EventItems")).Any(button => button.IsVisible &&
                System.Windows.Automation.AutomationProperties.GetName(button) == "关闭提示"),
                "Plugin cards expose their own actions without an ineffective close button");
            Assert(VisibleButtonsFit(), "Plugin card actions fit within the visible island");
            Assert(Descendants<Button>(Element<ItemsControl>("EventItems")).Count(button => button.IsVisible) == 3,
                "All three plugin actions remain visible in the compact card layout");
            Capture("plugin-automatic.png");

            Invoke("OpenActivity");
            await Settle();
            Assert((bool)Field("_pluginsTab").GetValue(_window)! && Element<Button>("PluginsTab").Visibility == Visibility.Collapsed &&
                Descendants<Button>(Element<ItemsControl>("PluginTabItems")).Single().Content as string == "专注计时器 · 测试",
                "Clicking the automatic plugin opens its independently named activity tab");
            FindAction("暂停").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => _manager.Cards.Single().Body.StartsWith("已暂停"));
            await Settle();
            Assert(FindAction("继续").IsVisible && CardValue<string>(0, "Body").StartsWith("已暂停"),
                "The rendered action reaches the child process and updates its paused state");
            FindAction("继续").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => _manager.Cards.Single().Body.StartsWith("运行中"));
            FindAction("重置").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => _manager.Cards.Single().Progress == 0);
            await Settle();
            Assert(CardValue<string>(0, "Body").StartsWith("运行中") && CardValue<double>(0, "Progress") < 0.02,
                "Reset returns the child activity and its progress to the beginning");
            Capture("plugin-activity.png");

            Invoke("Collapse"); await Settle();
            var beforeUpdate = _manager.Cards.Single().UpdatedAt;
            await Until(() => _manager.Cards.Single().UpdatedAt > beforeUpdate, 2500);
            await Settle();
            Assert(Live.TotalCount == 0 && Element<FrameworkElement>("CompactView").IsVisible && _manager.Cards.Count == 1,
                "Periodic plugin updates retain activity data without reopening a collapsed island");
            Assert(_window.RenderSize == nativeSize, "Plugin updates preserve the fixed native host dimensions");

            await _manager.StopAsync("focus-test");
            await Until(() => Live.TotalCount == 0 && PluginEvents.Count == 0);
            Assert(_manager.Cards.Count == 0 && Live.History.All(item => item.Kind != IslandEventKind.Plugin),
                "Stopping a plugin removes its live cards and scheduler history");
            SetSessions(new MediaSessionSnapshot("demo.music", "fixture.music", "音乐 · 测试", "Clair de lune", "Claude Debussy", null, true, false, true, true, true));
            Live.Push(new("fixture.media", IslandEventKind.Media, "音乐 · 测试", "Clair de lune", "Claude Debussy", DateTimeOffset.UtcNow, "demo.music"));
            Assert(await _manager.StartAsync("focus-test"), "A stopped plugin can be started again");
            await Until(() => Live.VisibleItems.Count == 2);
            await Settle();
            Assert(Cards.Count == 2 && Cards.Cast<object>().Count(card => Value<bool>(card, "IsPlugin")) == 1 && Cards.Cast<object>().Count(card => Value<bool>(card, "IsMedia")) == 1,
                "A plugin and a media session share the two-card presentation");
            Assert(VisibleButtonsFit(), "Media and plugin actions remain inside their combined shell");
            Capture("mixed-media-plugin.png");

            SetSessions(
                new MediaSessionSnapshot("demo.music", "fixture.music", "音乐 · 测试", "曲目一", "本地夹具", null, true, false, true, true, true),
                new MediaSessionSnapshot("demo.second", "fixture.second", "播放器二", "曲目二", "本地夹具", null, false, true, true, true, true),
                new MediaSessionSnapshot("demo.browser", "fixture.browser", "播放器三", "曲目三", "本地夹具", null, false, true, true, true, true));
            Live.Push(new("fixture.notice", IslandEventKind.Notification, "通知 · 测试", "界面布局检查", "三项标签与清空按钮应保持间隔。", DateTimeOffset.UtcNow, "fixture.notice"));
            Invoke("OpenActivity");
            Element<Button>("NotificationsTab").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            var tabScroll = Element<ScrollViewer>("ActivityTabScroll");
            Rect tabs = Bounds(tabScroll);
            Rect controls = Bounds((FrameworkElement)VisualTreeHelper.GetParent(Element<Button>("ClearNotificationsButton")));
            _headerGeometry = new { tabs = tabs.ToString(), controls = controls.ToString(), gap = controls.Left - tabs.Right, scrollableWidth = tabScroll.ScrollableWidth,
                shell = Bounds(Element<FrameworkElement>("Island")).ToString() };
            Assert(tabs.Right + 4 <= controls.Left, "Three activity tabs remain separated from clear and window controls");
            tabScroll.ScrollToRightEnd();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var pluginTab = Descendants<Button>(Element<ItemsControl>("PluginTabItems")).Single();
            Assert(Bounds(pluginTab).Right <= Bounds(tabScroll).Right + .5,
                "The horizontal tab viewport can expose the complete plugin tab without covering window controls");
            tabScroll.ScrollToLeftEnd();
            Assert(VisibleButtonsFit(), "The single notification header and actions fit within the shell");
            Capture("notification-three-tabs.png");

            Invoke("PluginsSettings_Click", Element<Button>("PluginsTab"), new RoutedEventArgs(Button.ClickEvent));
            await Settle();
            Assert(Element<ScrollViewer>("PluginManagementScroll").IsVisible && Element<ItemsControl>("PluginList").Items.Count == 1,
                "The control center plugin page displays the discovered plugin");
            Assert(Descendants<TextBlock>(Element<ItemsControl>("PluginList")).Any(text => text.Text == "运行中"),
                "Plugin management reflects the actual process state");
            var management = Element<ScrollViewer>("PluginManagementScroll");
            Rect managementShell = Bounds(Element<FrameworkElement>("Island")); managementShell.Inflate(0.5, 0.5);
            Assert(management.ScrollableHeight <= 0.5 && Descendants<Button>(management).Where(button => button.IsVisible)
                .All(button => managementShell.Contains(Bounds(button))),
                "The compact single-plugin management page fits every button without clipping or extra scrolling");
            Capture("plugin-management.png");
            await _manager.StopAsync("focus-test");
            await Until(() => _manager.Cards.Count == 0 && PluginEvents.Count == 0);
            Assert(Live.History.All(item => item.Kind != IslandEventKind.Plugin) && _window.RenderSize == nativeSize,
                "Disabling removes plugin activity while retaining the fixed host");
        }

        private IslandScheduler Live => (IslandScheduler)Field("_liveScheduler").GetValue(_window)!;
        private Dictionary<string, IslandEvent> PluginEvents => (Dictionary<string, IslandEvent>)Field("_pluginEvents").GetValue(_window)!;
        private ItemCollection Cards => Element<ItemsControl>("EventItems").Items;
        private T CardValue<T>(int index, string property) => Value<T>(Cards[index], property);
        private static T Value<T>(object item, string property) => (T)item.GetType().GetProperty(property)!.GetValue(item)!;
        private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
        private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Invoke(string name, params object?[] args) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(_window, args);
        private void SetSessions(params MediaSessionSnapshot[] sessions) => Field("_sessions").SetValue(_window, sessions);
        private Button FindAction(string text) => Descendants<Button>(Element<ItemsControl>("EventItems")).Single(button => button.IsVisible && button.Content as string == text);
        private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) yield return match;
                foreach (var descendant in Descendants<T>(child)) yield return descendant;
            }
        }
        private Rect Bounds(FrameworkElement element) => new(element.TranslatePoint(new Point(), Element<FrameworkElement>("HoverSurface")), element.RenderSize);
        private bool VisibleButtonsFit()
        {
            Rect shell = Bounds(Element<FrameworkElement>("Island")); shell.Inflate(0.5, 0.5);
            return Descendants<Button>(Element<FrameworkElement>("ActivityView")).Where(button => button.IsVisible)
                .All(button => shell.Contains(Bounds(button)));
        }
        private static Task Settle() => Task.Delay(850);
        private static async Task Until(Func<bool> condition, int timeout = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds >= timeout) throw new TimeoutException("Plugin integration state did not arrive before its deadline.");
                await Task.Delay(25);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        }
        private void Assert(bool condition, string description)
        {
            (condition ? _passed : _failures).Add(description);
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {description}");
        }
        private void Capture(string name)
        {
            var root = Element<FrameworkElement>("HoverSurface");
            var shell = Element<FrameworkElement>("Island");
            var dpi = VisualTreeHelper.GetDpi(_window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var origin = shell.TranslatePoint(new Point(), root);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY),
                (int)Math.Ceiling(shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(shell.ActualHeight * dpi.DpiScaleY)));
            using var stream = File.Create(Path.Combine(output, name));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
        }
        private async Task FinishAsync()
        {
            Field("_closing").SetValue(_window, true);
            ((DispatcherTimer)Field("_timer").GetValue(_window)!).Stop();
            if (_manager is not null) await _manager.DisposeAsync();
            foreach (string name in new[] { "_topmost", "_contentTransition", "_motion", "_notifications", "_media", "_tray" })
                (Field(name).GetValue(_window) as IDisposable)?.Dispose();
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                assemblyVersion = typeof(LandBar.MainWindow).Assembly.GetName().Version?.ToString(),
                startsSystemSources = false, usesTemporaryPluginDirectory = true, callsPermissionApi = false, controlsUserMedia = false,
                headerGeometry = _headerGeometry, passed = _passed, failures = _failures
            }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(_failures.Count == 0 ? 0 : 1);
        }
    }
}
