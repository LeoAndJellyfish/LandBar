using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml.Linq;
using LandBar.Core;
using LandBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/interaction-smoke");
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

    // Uses the production window with demo=true: no singleton, source startup or system input.
    private sealed class ProbeApplication(string output) : Application
    {
        private readonly List<string> _failures = [];
        private readonly List<object> _checks = [];
        private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "settings.json");
        private LandBar.MainWindow _window = null!;
        private string? _originalSettingsHash;
        private bool _finished;
        public bool Passed { get; private set; }
        private double _actualIdleMilliseconds;
        private Rect _hostBefore;
        private DispatcherTimer Timer => Get<DispatcherTimer>("_timer");
        private Stopwatch Clock => Get<Stopwatch>("_clock");
        private IslandInactivityTimer Idle => Get<IslandInactivityTimer>("_inactivity");
        private IslandScheduler Scheduler => Get<IslandScheduler>("_scheduler");
        private string View => Field("_view").GetValue(_window)!.ToString()!;

        protected override void OnStartup(StartupEventArgs e)
        {
            _originalSettingsHash = SettingsHash();
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Interaction Smoke" };
            MainWindow = _window;
            _window.Loaded += (_, _) => { _ = RunScenarios(); };
            _window.Show();
            var timeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timeout.Tick += (_, _) => { timeout.Stop(); Finish("Interaction smoke timed out."); };
            timeout.Start();
        }

        private async Task RunScenarios()
        {
            try
            {
                Get<AppSettings>("_settings").Quiet = false;
                // Exercise the five-second behavior consistently without changing the user's choice.
                Get<AppSettings>("_settings").AutoCollapseSeconds = 5;
                Invoke("ApplyAutoCollapseSetting");
                await Task.Delay(1000);
                _hostBefore = HostBounds();

                // Exercise the real DispatcherTimer once rather than inferring its behavior from math.
                Invoke("OpenActivity");
                await Task.Delay(1000);
                Set("_hover", true);
                Invoke("RecordInteraction");
                var deadline = Idle.Deadline!.Value;
                int initialHistory = Scheduler.History.Count;
                var actual = Stopwatch.StartNew();
                while (View != "Automatic" && actual.ElapsedMilliseconds < 6500) await Task.Delay(20);
                _actualIdleMilliseconds = actual.Elapsed.TotalMilliseconds;
                Check("Real timer collapses stationary hover at five seconds", View == "Automatic" &&
                    _actualIdleMilliseconds is >= 4900 and < 5700,
                    new { elapsedMilliseconds = _actualIdleMilliseconds, deadlineMilliseconds = deadline.TotalMilliseconds, hover = Get<bool>("_hover") });
                Timer.Stop();
                await Task.Delay(900);
                Check("Idle collapse preserves notification history and stops its deadline", Scheduler.TotalCount == 0 &&
                    Scheduler.History.Count == initialHistory && Idle.Deadline is null && IsCompact(),
                    new { active = Scheduler.TotalCount, history = Scheduler.History.Count, expectedHistory = initialHistory });

                foreach (var view in new[] { "Activity", "ControlCenter", "Settings" })
                {
                    ChangeView(view);
                    var opened = Idle.Deadline;
                    Check($"{view} opening arms five seconds", opened is { } value &&
                        value >= Clock.Elapsed + TimeSpan.FromMilliseconds(4900), new { deadline = opened?.TotalMilliseconds });
                    await Task.Delay(900);
                    Idle.Reset(Clock.Elapsed - TimeSpan.FromMilliseconds(5001));
                    Invoke("OnTick", null, EventArgs.Empty);
                    await Task.Delay(850);
                    Check($"{view} expiry collapses completely", View == "Automatic" && IsCompact() && Idle.Deadline is null,
                        new { view = View, compact = IsCompact() });
                }

                ChangeView("Settings");
                await Task.Delay(950);
                await CheckInput("Mouse press extends inactivity", () => RaiseMouse(Mouse.PreviewMouseDownEvent));
                await CheckInput("Mouse release extends inactivity", () => RaiseMouse(Mouse.PreviewMouseUpEvent));
                await CheckInput("Wheel extends inactivity", () => _window.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120)
                    { RoutedEvent = Mouse.PreviewMouseWheelEvent }));
                await CheckInput("Keyboard extends inactivity", () => _window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,
                    PresentationSource.FromVisual(_window), Environment.TickCount, Key.F6) { RoutedEvent = Keyboard.PreviewKeyDownEvent }));

                var clip = Get<System.Windows.Media.RectangleGeometry>("_surfaceClip");
                var inside = new Point(clip.Rect.X + clip.Rect.Width / 2, Math.Min(60, clip.Rect.Bottom - 10));
                Invoke("RecordPointerInteraction", inside);
                var pointerDeadline = Idle.Deadline;
                await Task.Delay(30);
                Invoke("RecordPointerInteraction", inside);
                Check("Stationary pointer does not extend deadline", Idle.Deadline == pointerDeadline, new { deadline = pointerDeadline?.TotalMilliseconds });
                Invoke("RecordPointerInteraction", new Point(0, 0));
                Check("Transparent host movement does not extend deadline", Idle.Deadline == pointerDeadline, new { deadline = Idle.Deadline?.TotalMilliseconds });
                await Task.Delay(30);
                Invoke("RecordPointerInteraction", inside + new Vector(4, 0));
                Check("Movement within island extends deadline", Idle.Deadline > pointerDeadline, new { previous = pointerDeadline?.TotalMilliseconds, current = Idle.Deadline?.TotalMilliseconds });

                var periodicDeadline = Idle.Deadline;
                for (int i = 0; i < 3; i++) { Invoke("Render"); Invoke("Navigate", 1); await Task.Delay(30); }
                Check("Render and page rotation do not extend deadline", Idle.Deadline == periodicDeadline, new { deadline = Idle.Deadline?.TotalMilliseconds });

                _window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(_window), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Check("Escape collapses and disarms deadline", View == "Automatic" && Idle.Deadline is null, new { view = View });
                Invoke("RecordInteraction");
                Check("Compact idle input leaves timer disarmed", Idle.Deadline is null, new { deadline = Idle.Deadline?.TotalMilliseconds });

                // End the isolated preview, then inject local in-memory events through the real receive entry.
                Invoke("EndDemo");
                Invoke("Collapse");
                Get<AppSettings>("_settings").Quiet = false;
                var incoming = new IslandEvent("interaction-test-a", IslandEventKind.Notification, "本地回归测试", "测试 A", "本地内存事件", DateTimeOffset.UnixEpoch, "test.local");
                Invoke("Receive", incoming);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                var sourceDeadline = Idle.Deadline;
                Check("New automatic notification arms inactivity", Scheduler.TotalCount == 1 && sourceDeadline is not null,
                    new { active = Scheduler.TotalCount, deadline = sourceDeadline?.TotalMilliseconds });
                await Task.Delay(30);
                Invoke("Receive", incoming);
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("Duplicate source polling does not extend inactivity", Idle.Deadline == sourceDeadline && Scheduler.TotalCount == 1,
                    new { active = Scheduler.TotalCount, deadline = Idle.Deadline?.TotalMilliseconds });
                await Task.Delay(30);
                Invoke("Receive", incoming with { Id = "interaction-test-b", Title = "测试 B" });
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                Check("A newly accepted source event extends inactivity", Idle.Deadline > sourceDeadline && Scheduler.TotalCount == 2,
                    new { active = Scheduler.TotalCount, deadline = Idle.Deadline?.TotalMilliseconds });
                Set("_hover", true);
                Idle.Reset(Clock.Elapsed - TimeSpan.FromMilliseconds(5001));
                Invoke("OnTick", null, EventArgs.Empty);
                await Task.Delay(900);
                Check("Automatic expiry clears active cards and retains real history", View == "Automatic" && Scheduler.TotalCount == 0 &&
                    Scheduler.History.Count == 2 && IsCompact() && Idle.Deadline is null,
                    new { active = Scheduler.TotalCount, history = Scheduler.History.Count, compact = IsCompact() });
                var sourcesNotStarted = Get<LandBar.Services.WindowsNotificationSource>("_notifications").Status == "尚未连接" &&
                    Get<LandBar.Services.WindowsMediaSource>("_media").Status == "尚未连接";
                Check("Test remains isolated from actual sources", sourcesNotStarted, new { notificationStatus = Get<LandBar.Services.WindowsNotificationSource>("_notifications").Status });
                Check("Native host remains fixed throughout interaction", HostBounds() == _hostBefore,
                    new { before = _hostBefore.ToString(), after = HostBounds().ToString() });
                Check("User configuration file was not modified", _originalSettingsHash == SettingsHash(), new { unchanged = _originalSettingsHash == SettingsHash() });
                Finish();
            }
            catch (Exception ex) { Finish(ex.ToString()); }
        }

        private async Task CheckInput(string name, Action action)
        {
            var before = Idle.Deadline;
            await Task.Delay(30);
            action();
            Check(name, Idle.Deadline > before, new { previous = before?.TotalMilliseconds, current = Idle.Deadline?.TotalMilliseconds });
        }

        private void RaiseMouse(RoutedEvent routedEvent) => _window.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = routedEvent });
        private bool IsCompact() => ((FrameworkElement)_window.FindName("CompactView")).Visibility == Visibility.Visible &&
            Math.Abs(((Border)_window.FindName("Island")).ActualHeight - 44) < 0.15;
        private Rect HostBounds() => new(_window.Left, _window.Top, _window.ActualWidth, _window.ActualHeight);
        private static FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private T Get<T>(string name) => (T)Field(name).GetValue(_window)!;
        private void Set(string name, object value) => Field(name).SetValue(_window, value);
        private void Invoke(string name, params object?[] args) => typeof(LandBar.MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == args.Length).Invoke(_window, args);
        private void ChangeView(string name)
        {
            var viewType = typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!;
            Invoke("ChangeView", Enum.Parse(viewType, name));
        }
        private string? SettingsHash() => File.Exists(_settingsPath) ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_settingsPath))) : null;
        private void Check(string name, bool passed, object detail)
        {
            _checks.Add(new { name, passed, detail });
            Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {name}");
            if (!passed) _failures.Add(name);
        }
        private void Finish(string? failure = null)
        {
            if (_finished) return;
            _finished = true;
            if (failure is not null) _failures.Add(failure);
            bool passed = Passed = _failures.Count == 0;
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                passed, assemblyVersion = typeof(LandBar.MainWindow).Assembly.GetName().Version?.ToString(), checks = _checks, failures = _failures,
                realIdleMilliseconds = _actualIdleMilliseconds,
                sourceMode = "isolated demo window and local in-memory source events",
                sendsSystemInput = false, requestsPermissions = false,
                changesUserConfiguration = _originalSettingsHash != SettingsHash()
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { passed, checkCount = _checks.Count, failures = _failures, output }));
            Invoke("Exit");
            Environment.ExitCode = passed ? 0 : 1;
        }
    }
}
