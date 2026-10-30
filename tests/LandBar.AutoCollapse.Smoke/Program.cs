using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using LandBar.Core;
using LandBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/auto-collapse-smoke");
        Directory.CreateDirectory(output);
        var app = new ProbeApplication(output);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var production = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Production-App.xaml"));
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            production.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        app.Run();
        return app.Passed ? 0 : 1;
    }

    // Real production window, isolated local fixtures, no source startup or system input.
    private sealed class ProbeApplication(string output) : Application
    {
        private readonly List<object> _checks = [];
        private readonly List<string> _failures = [];
        private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "settings.json");
        private string? _settingsHash;
        private LandBar.MainWindow _window = null!;
        private bool _finished;
        public bool Passed { get; private set; }
        private AppSettings Settings => Get<AppSettings>("_settings");
        private IslandInactivityTimer Idle => Get<IslandInactivityTimer>("_inactivity");
        private Stopwatch Clock => Get<Stopwatch>("_clock");
        private IslandScheduler Scheduler => Get<IslandScheduler>("_scheduler");
        private string View => Field("_view").GetValue(_window)!.ToString()!;
        private Button[] Options => ((UniformGrid)_window.FindName("AutoCollapseOptions")).Children.OfType<Button>().ToArray();

        protected override void OnStartup(StartupEventArgs e)
        {
            _settingsHash = SettingsHash();
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Auto Collapse Options Smoke" };
            MainWindow = _window;
            _window.Loaded += (_, _) => { _ = RunScenarios(); };
            _window.Show();
            var guard = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            guard.Tick += (_, _) => { guard.Stop(); Finish("Options smoke timed out."); };
            guard.Start();
        }

        private async Task RunScenarios()
        {
            try
            {
                Get<DispatcherTimer>("_timer").Stop();
                // Hover-specific checks use synthetic local points in Interaction.Smoke.
                // Keep this options fixture clear of the user's actual pointer.
                _window.Left = -3000; _window.Top = -3000;
                Settings.Quiet = false;
                CheckSettingsSerialization();
                ChangeView("Settings");
                Select(5);
                await Task.Delay(850);
                Check("Six readable options are available", Options.Select(button => (string)button.Tag).SequenceEqual(new[] { "1", "2", "3", "4", "5", "0" }),
                    new { labels = Options.Select(button => button.Content), width = Options.Min(button => button.ActualWidth) });
                Check("Settings with options fits without scrolling", ((ScrollViewer)_window.FindName("SettingsScroll")).ScrollableHeight == 0,
                    new { height = ((Border)_window.FindName("Island")).ActualHeight, scroll = ((ScrollViewer)_window.FindName("SettingsScroll")).ScrollableHeight });
                Capture("settings-options.png");
                foreach (int seconds in new[] { 1, 2, 3, 4, 5, 0 })
                {
                    var before = Clock.Elapsed;
                    Select(seconds);
                    var selected = Options.Where(button => AutomationProperties.GetItemStatus(button) == "已选择").ToArray();
                    bool deadline = seconds == 0 ? Idle.Deadline is null && Idle.Timeout is null :
                        Idle.Timeout == TimeSpan.FromSeconds(seconds) && Idle.Deadline >= before + TimeSpan.FromSeconds(seconds) &&
                        Idle.Deadline <= Clock.Elapsed + TimeSpan.FromSeconds(seconds);
                    Check($"Option {seconds} applies timer and selection immediately", Settings.AutoCollapseSeconds == seconds && deadline &&
                        selected.Length == 1 && (string)selected[0].Tag == seconds.ToString(), new { seconds, timeout = Idle.Timeout?.TotalSeconds, deadline = Idle.Deadline?.TotalMilliseconds });
                    Check($"Option {seconds} applies expiry to live and preview queues", Scheduler.ExpireVisibleItems == (seconds != 0) &&
                        Get<IslandScheduler>("_liveScheduler").ExpireVisibleItems == (seconds != 0), new { seconds });
                }

                var clip = Get<RectangleGeometry>("_surfaceClip");
                Invoke("UpdatePointerHover", new Point(clip.Rect.X + clip.Rect.Width / 2, clip.Rect.Y + 50));
                Select(3);
                Check("Changing the finite option while hovered keeps inactivity suspended", Idle.Timeout == TimeSpan.FromSeconds(3) && Idle.Deadline is null,
                    new { timeout = Idle.Timeout?.TotalSeconds, deadline = Idle.Deadline?.TotalMilliseconds });
                Invoke("UpdatePointerHover", new Point(0, 0));
                Check("Leaving after an option change starts the complete selected interval", Idle.Deadline >= Clock.Elapsed + TimeSpan.FromMilliseconds(2900),
                    new { remainingMilliseconds = (Idle.Deadline - Clock.Elapsed)?.TotalMilliseconds });

                Select(0);
                Invoke("Collapse");
                Invoke("OpenActivity");
                Invoke("RecordInteraction");
                Check("Reopening and input preserve Never", Idle.Timeout is null && Idle.Deadline is null && View == "Activity", new { view = View });
                Invoke("ShowDemo");
                Check("A new preview retains Never and disables card expiry", Idle.Deadline is null && !Scheduler.ExpireVisibleItems, new { expiry = Scheduler.ExpireVisibleItems });
                Invoke("EndDemo");
                Invoke("Collapse");
                foreach (string id in new[] { "option-a", "option-b", "option-c" })
                    Invoke("Receive", new IslandEvent(id, IslandEventKind.Notification, "本地选项测试", id, "内存夹具", DateTimeOffset.UnixEpoch, "test.options"));
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                Check("New automatic events preserve Never", Scheduler.TotalCount == 3 && Idle.Deadline is null && !Scheduler.ExpireVisibleItems,
                    new { count = Scheduler.TotalCount });
                await Task.Delay(850);
                Set("_previousTick", Clock.Elapsed);
                var duration = Stopwatch.StartNew();
                var pages = new HashSet<int>();
                while (duration.Elapsed < TimeSpan.FromSeconds(9.2))
                {
                    await Task.Delay(200);
                    Set("_hover", false);
                    Invoke("OnTick", null, EventArgs.Empty);
                    pages.Add(Scheduler.CurrentPage);
                }
                Check("Never stays expanded beyond the former eight-second budget", duration.Elapsed.TotalSeconds > 9 &&
                    Scheduler.TotalCount == 3 && Idle.Deadline is null && View == "Automatic" && ((FrameworkElement)_window.FindName("ActivityView")).Visibility == Visibility.Visible,
                    new { elapsedSeconds = duration.Elapsed.TotalSeconds, count = Scheduler.TotalCount });
                Check("Never continues automatic carousel rotation", pages.Count > 1, new { pages = pages.ToArray() });
                Capture("never-after-nine-seconds.png");

                ChangeView("Settings");
                Select(2);
                var resumed = Clock.Elapsed;
                Check("Returning from Never starts a complete two-second interval", Idle.Timeout == TimeSpan.FromSeconds(2) &&
                    Idle.Deadline >= resumed + TimeSpan.FromMilliseconds(1950) && Scheduler.ExpireVisibleItems,
                    new { remainingMs = (Idle.Deadline - Clock.Elapsed)?.TotalMilliseconds });
                await Task.Delay(2150);
                Invoke("OnTick", null, EventArgs.Empty);
                await Task.Delay(850);
                Check("Resumed finite option actually collapses", View == "Automatic" && Scheduler.TotalCount == 0 && Idle.Deadline is null &&
                    Math.Abs(((Border)_window.FindName("Island")).ActualHeight - 44) < .15, new { view = View });
                Check("Collapse retains actual notification history", Scheduler.History.Count == 3, new { history = Scheduler.History.Count });
                Check("Sources remain isolated and user settings remain unchanged", Get<WindowsNotificationSource>("_notifications").Status == "尚未连接" &&
                    Get<WindowsMediaSource>("_media").Status == "尚未连接" && _settingsHash == SettingsHash(), new { settingsUnchanged = _settingsHash == SettingsHash() });
                Finish();
            }
            catch (Exception exception) { Finish(exception.ToString()); }
        }

        private void CheckSettingsSerialization()
        {
            Check("Old settings default to five seconds", JsonSerializer.Deserialize<AppSettings>("{\"HideContent\":true}")!.AutoCollapseSeconds == 5, new { });
            foreach (int seconds in new[] { 0, 1, 2, 3, 4, 5 })
            {
                var settings = JsonSerializer.Deserialize<AppSettings>($"{{\"AutoCollapseSeconds\":{seconds}}}")!;
                var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
                Check($"Setting {seconds} persists through JSON roundtrip", copy.AutoCollapseSeconds == seconds, new { seconds });
            }
            foreach (int invalid in new[] { -1, 6, int.MinValue, int.MaxValue })
                Check($"Invalid setting {invalid} falls back to five", JsonSerializer.Deserialize<AppSettings>($"{{\"AutoCollapseSeconds\":{invalid}}}")!.AutoCollapseSeconds == 5, new { invalid });
        }
        private void Select(int seconds) => Options.Single(button => (string)button.Tag == seconds.ToString()).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        private static FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private T Get<T>(string name) => (T)Field(name).GetValue(_window)!;
        private void Set(string name, object value) => Field(name).SetValue(_window, value);
        private void Invoke(string name, params object?[] args) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(_window, args);
        private void ChangeView(string name) => Invoke("ChangeView", Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, name));
        private string? SettingsHash() => File.Exists(_settingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_settingsPath))) : null;
        private void Check(string name, bool passed, object detail)
        {
            _checks.Add(new { name, passed, detail });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) _failures.Add(name);
        }
        private void Capture(string name)
        {
            var root = (FrameworkElement)_window.FindName("HoverSurface");
            var island = (FrameworkElement)_window.FindName("Island");
            var dpi = VisualTreeHelper.GetDpi(_window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var origin = island.TranslatePoint(new Point(), root);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY),
                (int)Math.Ceiling(island.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(island.ActualHeight * dpi.DpiScaleY)));
            using var stream = File.Create(Path.Combine(output, name));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
        }
        private void Finish(string? failure = null)
        {
            if (_finished) return;
            _finished = true;
            if (failure is not null) _failures.Add(failure);
            Passed = _failures.Count == 0;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                passed = Passed, assemblyVersion = typeof(LandBar.MainWindow).Assembly.GetName().Version?.ToString(),
                checks = _checks, failures = _failures, sourcesStarted = false, systemInputSent = false,
                userSettingsUnchanged = _settingsHash == SettingsHash()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { passed = Passed, count = _checks.Count, failures = _failures, output }));
            Invoke("Exit");
        }
    }
}
