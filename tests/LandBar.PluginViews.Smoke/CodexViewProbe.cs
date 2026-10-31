using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandBar.Plugins;
using LandBar.Presentation;
using LandBar.Services;

// Only the sample's pure view builder is invoked; no Codex app-server or account is opened.
internal sealed class CodexViewProbe(string output) : Application
{
    private LandBar.MainWindow _window = null!;
    private PluginManager? _manager;
    private readonly List<string> _passed = [], _failures = [];
    private readonly List<object> _layouts = [];

    protected override void OnStartup(StartupEventArgs e)
    {
        _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Synthetic Codex Views" };
        MainWindow = _window;
        _window.Loaded += async (_, _) =>
        {
            try { await RunScenarios(); }
            catch (Exception error) { _failures.Add(error.ToString()); }
            finally { await Finish(); }
        };
        _window.Show();
    }

    private async Task RunScenarios()
    {
        var settings = (AppSettings)Get("_settings")!;
        settings.AutoCollapseSeconds = 0; settings.Quiet = true; settings.HideContent = false;
        settings.EnablePluginViews = true; settings.PluginViewBindings = new();
        Invoke("ApplyAutoCollapseSetting");
        await Task.Delay(800); Invoke("EndDemo"); Invoke("Collapse"); await Task.Delay(800);
        _window.Left = -12000; _window.Top = -12000;
        ((DispatcherTimer)Get("_timer")!).Stop();
        string root = Path.Combine(output, "synthetic-codex-provider");
        string folder = Path.Combine(root, "codex-fixture"); Directory.CreateDirectory(folder);
        string fixture = Path.Combine(folder, "fixture.py"), scenarioPath = Path.Combine(folder, "scenario.json");
        File.WriteAllText(fixture, FixtureSource);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
        {
            id = "codex-fixture", name = "Codex 合成示例", version = "1.0.0", description = "公开样例视图的离线布局检查",
            command = "python.exe", arguments = new[] { "-u", fixture,
                Path.Combine(AppContext.BaseDirectory, "Samples", "codex-balance", "plugin.py"), scenarioPath }, protocolVersion = 1
        }));
        _manager = new PluginManager(root); Invoke("ConnectPlugins", _manager); _manager.Discover();
        var primary = new { usedPercent = 24, windowDurationMins = 300, resetsAt = 1893456000 };
        var secondary = new { usedPercent = 58, windowDurationMins = 10080, resetsAt = 1894060800 };
        var cases = new (string Name, object? Snapshot, string? Failure)[]
        {
            ("single", new { bucket = new { planType = "plus", primary, credits = new { balance = "12.50", unlimited = false } }, fetchedAt = 1000, resetCount = 0 }, null),
            ("dual", new { bucket = new { planType = "pro", primary, secondary, credits = new { balance = "0", unlimited = false } }, fetchedAt = 1000, resetCount = 2 }, null),
            ("unknown", null, null),
            ("failure", new { bucket = new { planType = "plus", primary, secondary, credits = new { balance = "12.50", unlimited = false } }, fetchedAt = 1000, resetCount = 0 }, "额度读取失败，正在重试"),
            ("long-fields", new { bucket = new { planType = "enterprise_cbp_automation", primary, secondary, credits = new { balance = "123456789012345678901234", unlimited = false } }, fetchedAt = 1000, resetCount = long.MaxValue }, null)
        };
        foreach (var scenario in cases)
        {
            File.WriteAllText(scenarioPath, JsonSerializer.Serialize(new { snapshot = scenario.Snapshot, failure = scenario.Failure }));
            Check(await _manager.StartAsync("codex-fixture"), scenario.Name + ": public view builder fixture handshakes");
            await Until(() => _manager.Views.Count == 2);
            Set("_activePluginTabKey", "view:codex-fixture:quota-page"); Set("_pluginsTab", true); Set("_notificationsTab", false);
            Invoke("ChangeView", Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, "Activity"));
            Invoke("Render"); await Task.Delay(900);
            var presenter = Element<PluginViewPresenter>("ActivityOverrideContent");
            var scroll = Element<ScrollViewer>("ActivityOverrideScroll");
            var shell = Element<FrameworkElement>("Island");
            Check(presenter.View?.PluginId == "codex-fixture" && presenter.View.Id == "quota-page" && presenter.IsVisible,
                scenario.Name + ": exact public sample tree is visible in its independent tab");
            Check(Math.Abs(shell.ActualWidth - 384) < 1.5 && shell.ActualHeight <= 456.5,
                scenario.Name + ": real host keeps the bounded compact activity size");
            Check(Element<FrameworkElement>("ContentLayout").Opacity >= .99 && Element<FrameworkElement>("ContentLayout").IsHitTestVisible,
                scenario.Name + ": content transition finishes visible and interactive");
            var button = Descendants<Button>(presenter).Single();
            scroll.ScrollToEnd(); await Task.Delay(100);
            var viewport = new Rect(scroll.RenderSize); viewport.Inflate(.75, .75);
            var buttonBounds = button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize));
            var rootBounds = ((FrameworkElement)presenter.Child).TransformToAncestor(scroll).TransformBounds(new Rect(((FrameworkElement)presenter.Child).RenderSize));
            Check(viewport.Contains(buttonBounds), scenario.Name + ": refresh button is fully visible or scroll-reachable");
            var rootLocalButton = button.TransformToAncestor((FrameworkElement)presenter.Child).TransformBounds(new Rect(button.RenderSize));
            var rootLocalBounds = new Rect(((FrameworkElement)presenter.Child).RenderSize); rootLocalBounds.Inflate(.75, .75);
            Check(rootLocalBounds.Contains(rootLocalButton), scenario.Name + ": fixed root height contains the entire refresh button");
            Check(rootLocalBounds.Height - rootLocalButton.Bottom <= (scenario.Name == "long-fields" ? 60 : 32),
                scenario.Name + ": compact page avoids excessive unused space below its actions");
            var rings = Descendants<PluginRing>(presenter).ToArray();
            int expectedRings = scenario.Name == "unknown" ? 0 : scenario.Name == "single" ? 1 : 2;
            Check(rings.Length == expectedRings && rings.All(ring => ring.ActualWidth >= 47.5 && ring.ActualHeight >= 47.5),
                scenario.Name + ": quota rings retain their complete 48-DIP bounds");
            _layouts.Add(new { scenario.Name, shell = new { shell.ActualWidth, shell.ActualHeight },
                root = rootBounds.ToString(), button = buttonBounds.ToString(), rootLocalButton = rootLocalButton.ToString(),
                scroll.ScrollableHeight, scroll.VerticalOffset, rootHeight = presenter.View?.Root.Height });
            scroll.ScrollToTop(); await Task.Delay(80); Capture("codex-" + scenario.Name + ".png");
            await _manager.StopAsync("codex-fixture"); await Until(() => _manager.Views.Count == 0);
        }
    }

    private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
    private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private object? Get(string name) => Field(name).GetValue(_window);
    private void Set(string name, object? value) => Field(name).SetValue(_window, value);
    private void Invoke(string name, params object?[] args) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
        .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(_window, args);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T value) yield return value;
            foreach (var item in Descendants<T>(child)) yield return item;
        }
    }
    private static async Task Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("Synthetic Codex view process timed out.");
            await Task.Delay(25);
        }
    }
    private void Check(bool condition, string name)
    {
        (condition ? _passed : _failures).Add(name);
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    }
    private void Capture(string name)
    {
        var root = Element<FrameworkElement>("HoverSurface"); var shell = Element<FrameworkElement>("Island");
        var dpi = VisualTreeHelper.GetDpi(_window);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(root); var origin = shell.TranslatePoint(new Point(), root);
        var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY),
            (int)Math.Ceiling(shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(shell.ActualHeight * dpi.DpiScaleY)));
        using var stream = File.Create(Path.Combine(output, name)); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
    }
    private async Task Finish()
    {
        Set("_closing", true); ((DispatcherTimer)Get("_timer")!).Stop();
        if (_manager is not null) await _manager.DisposeAsync();
        Invoke("DisposePluginViews");
        foreach (string name in new[] { "_topmost", "_contentTransition", "_motion", "_notifications", "_media", "_tray", "_trayIcon" })
            (Get(name) as IDisposable)?.Dispose();
        File.WriteAllText(Path.Combine(output, "codex-report.json"), JsonSerializer.Serialize(new
        {
            startsSystemSources = false, startsCodexAppServer = false, usesSyntheticChildProcess = true,
            movesUserPointer = false, privateAccountData = false, passed = _passed, failures = _failures, layouts = _layouts
        }, new JsonSerializerOptions { WriteIndented = true }));
        Shutdown(_failures.Count == 0 ? 0 : 1);
    }

    private const string FixtureSource = """
        import importlib.util, json, sys
        sys.dont_write_bytecode = True
        sys.stdout.reconfigure(encoding="utf-8", newline="\n", line_buffering=True)
        spec = importlib.util.spec_from_file_location("synthetic_public_codex", sys.argv[1])
        sample = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(sample)
        with open(sys.argv[2], encoding="utf-8") as stream:
            scenario = json.load(stream)
        for raw in sys.stdin.buffer:
            message = json.loads(raw)
            if message.get("type") == "shutdown": break
            if message.get("type") == "hello":
                print(json.dumps({"type":"hello","protocolVersion":1}), flush=True)
                for view in sample.make_views(scenario["snapshot"], scenario["failure"], now=1000):
                    print(json.dumps(view, ensure_ascii=False, separators=(",", ":")), flush=True)
        """;
}
