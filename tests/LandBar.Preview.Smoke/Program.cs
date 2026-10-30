using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/preview-smoke");
        Directory.CreateDirectory(output);
        string settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "settings.json");
        string? beforeHash = SettingsHash(settingsPath);
        var app = new ProbeApplication(output);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var production = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Production-App.xaml"));
        var resources = new XElement(presentation + "ResourceDictionary", new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            production.Root!.Element(presentation + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        int result = app.Run();
        bool unchanged = beforeHash == SettingsHash(settingsPath);
        File.WriteAllText(Path.Combine(output, "settings-unchanged.json"), JsonSerializer.Serialize(new { unchanged }));
        return unchanged ? result : 1;
    }
    private static string? SettingsHash(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : null;

    // Exercises production views and transition callbacks with synthetic local fixtures only.
    // LandBar.App.OnStartup is skipped; no system source, permission API or media control starts.
    private sealed class ProbeApplication(string output) : Application
    {
        private LandBar.MainWindow _window = null!;
        private readonly List<string> _passed = [];
        private readonly List<string> _failures = [];

        protected override void OnStartup(StartupEventArgs e)
        {
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Preview Lifecycle Smoke" };
            MainWindow = _window;
            _window.Loaded += async (_, _) =>
            {
                try { await RunScenarios(); }
                catch (Exception exception) { _failures.Add(exception.ToString()); }
                finally { Finish(); }
            };
            _window.Show();
        }

        private async Task RunScenarios()
        {
            // Lifecycle checks use a stable view lifetime instead of inheriting user idle settings.
            ((AppSettings)Field("_settings").GetValue(_window)!).AutoCollapseSeconds = 0;
            Invoke("ApplyAutoCollapseSetting");
            await Task.Delay(650);
            ((AppSettings)Field("_settings").GetValue(_window)!).Quiet = false;
            Invoke("EndDemo");
            await Task.Delay(750);
            Assert(History().Length == 0 && Active.TotalCount == 0, "Leaving the initial preview removes every example notification");
            Assert(((IReadOnlyList<MediaSessionSnapshot>)Field("_examples").GetValue(_window)!).Count == 0, "Leaving preview clears example media");

            var live = (IslandScheduler)Field("_liveScheduler").GetValue(_window)!;
            live.Push(Notification("fixture.hidden"));
            live.Push(Notification("fixture.visible"));
            Hidden.Add("fixture.hidden");
            Field("_preferredSessionId").SetValue(_window, "fixture.player");
            Field("_sessions").SetValue(_window, new[]
            {
                new MediaSessionSnapshot("fixture.player", "fixture.app", "测试播放器", "真实来源测试", "本地夹具", null, true, false, true, true, true)
            });
            Invoke("OpenActivity");

            Invoke("ShowDemo");
            var preview = Active;
            Assert(!ReferenceEquals(preview, live) && preview.History.Count == 2, "Preview has a separate queue and history");
            Assert(live.History.Count == 2 && live.TotalCount == 2, "Entering preview retains live history and active events");
            Assert(Hidden.Count == 0 && Field("_preferredSessionId").GetValue(_window) is null, "Preview starts with separate dismissal and selection state");
            string exampleId = preview.History[0].Id;
            Invoke("Dismiss_Click", new Button { Tag = exampleId }, new RoutedEventArgs(Button.ClickEvent));
            string renderKey = (string)Field("_renderKey").GetValue(_window)!;
            Invoke("Receive", Notification("fixture.during-preview"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Assert(live.History.Any(x => x.Id == "fixture.during-preview") && preview.History.Count == 2,
                "Live events received during preview stay in the live queue");
            Assert(renderKey == (string)Field("_renderKey").GetValue(_window)!, "Live arrival does not re-render preview");

            // Use the actual button handler. The current preview may still have a pending fade callback.
            Invoke("Demo_Click", _window.FindName("PreviewButton"), new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(800);
            Assert(ReferenceEquals(Active, live), "Ending preview restores the original live scheduler");
            Assert(History().All(x => !x.Id.StartsWith("demo-")) && History().Select(x => x.Id).SequenceEqual(new[] { "fixture.during-preview", "fixture.visible" }),
                "Notification history restores live events without dismissed or example notifications");
            Assert(Hidden.SetEquals(new[] { "fixture.hidden" }) && (string?)Field("_preferredSessionId").GetValue(_window) == "fixture.player",
                "Ending preview restores live dismissal and media selection state");
            Assert(!live.Push(Notification("fixture.visible")), "Live duplicate suppression survives preview");
            Assert(CardValues("SessionId").SequenceEqual(new[] { "fixture.player" }), "Pending transition presents the live media source after exit");

            Field("_notificationsTab").SetValue(_window, true);
            Invoke("Render");
            await Task.Delay(750);
            Assert(CardValues("Id").SequenceEqual(new[] { "fixture.during-preview", "fixture.visible" }), "The restored notification tab displays live cards only");
            Capture("after-preview-notifications.png");

            for (int i = 0; i < 6; i++)
            {
                Invoke("ShowDemo");
                Assert(Active.History.Count == 2, $"Preview cycle {i + 1} begins with exactly two examples");
                Field("_notificationsTab").SetValue(_window, true);
                Invoke("Render");
                await Task.Delay(i % 2 == 0 ? 15 : 90);
                Invoke("Demo_Click", _window.FindName("PreviewButton"), new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(120);
            }
            await Task.Delay(800);
            Assert(ReferenceEquals(Active, live) && live.History.Count == 3 && live.TotalCount == 3,
                "Rapid repeated preview cycles preserve the live queue without accumulating examples");
            Assert(History().All(x => !x.Id.StartsWith("demo-")) && CardValues("SessionId").SequenceEqual(new[] { "fixture.player" }),
                "Late transition callbacks do not restore example cards");
            Assert(((FrameworkElement)_window.FindName("ContentLayout")).Opacity > 0.99, "Final live content remains visible");

            var clear = (Button)_window.FindName("ClearNotificationsButton");
            Assert(clear.Visibility == Visibility.Collapsed, "Clear notifications is hidden on the media tab");
            live.Push(new IslandEvent("fixture.media-event", IslandEventKind.Media, "测试播放器", "曲目", "本地夹具", DateTimeOffset.UtcNow, "fixture.player"));
            Field("_notificationsTab").SetValue(_window, true);
            Field("_page").SetValue(_window, 1);
            Invoke("Render");
            await Task.Delay(750);
            Assert(clear.Visibility == Visibility.Visible && clear.IsEnabled, "Notification tab exposes the clear messages button");
            Capture("clear-messages-before.png");
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(800);
            Assert(History().Length == 0 && live.History.All(item => item.Kind == IslandEventKind.Media) &&
                live.TotalCount == 1 && live.VisibleItems.All(item => item.Kind == IslandEventKind.Media),
                "Clear messages removes active, queued and historical notifications while retaining media events");
            Assert(Hidden.Count == 0 && (int)Field("_page").GetValue(_window)! == 0, "Clear messages resets notification dismissal state and page");
            Assert(((FrameworkElement)_window.FindName("EmptyState")).Visibility == Visibility.Visible && !clear.IsEnabled,
                "An empty notification page displays its empty state and disables clear");
            Assert(((IReadOnlyList<MediaSessionSnapshot>)Field("_sessions").GetValue(_window)!).Single().IsPlaying,
                "Clearing messages preserves the media playback snapshot");
            Assert(!live.Push(Notification("fixture.visible")), "Cleared notification IDs remain deduplicated");
            Capture("clear-messages-empty.png");
            Invoke("Receive", Notification("fixture.after-clear"));
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            await Task.Delay(750);
            Assert(History().Single().Id == "fixture.after-clear" && clear.IsEnabled, "New notifications appear and enable clear after a previous clear");
            Invoke("ShowDemo");
            Field("_notificationsTab").SetValue(_window, true);
            Invoke("Render");
            await Task.Delay(750);
            clear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert(Active.History.Count == 0 && live.History.Count == 2, "Clearing preview notifications leaves the live history untouched");
            Invoke("EndDemo");
            Assert(ReferenceEquals(Active, live) && History().Single().Id == "fixture.after-clear", "Leaving a cleared preview restores the live notifications");
        }

        private IslandScheduler Active => (IslandScheduler)Field("_scheduler").GetValue(_window)!;
        private HashSet<string> Hidden => (HashSet<string>)Field("_hiddenHistory").GetValue(_window)!;
        private IslandEvent[] History() => ((IReadOnlyList<IslandEvent>)typeof(LandBar.MainWindow)
            .GetProperty("NotificationHistory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window)!).ToArray();
        private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Invoke(string name, params object?[] parameters) => typeof(LandBar.MainWindow)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
            .Single(method => method.Name == name && method.GetParameters().Length == parameters.Length).Invoke(_window, parameters);
        private string[] CardValues(string property) => ((ItemsControl)_window.FindName("EventItems")).Items.Cast<object>()
            .Select(card => (string)card.GetType().GetProperty(property)!.GetValue(card)!).ToArray();
        private void Assert(bool condition, string description)
        {
            (condition ? _passed : _failures).Add(description);
            Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {description}");
        }
        private void Capture(string name)
        {
            var root = (FrameworkElement)_window.FindName("HoverSurface");
            var island = (FrameworkElement)_window.FindName("Island");
            var dpi = VisualTreeHelper.GetDpi(_window);
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var origin = island.TranslatePoint(new Point(), root);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY),
                (int)Math.Ceiling(island.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(island.ActualHeight * dpi.DpiScaleY)));
            using var stream = File.Create(Path.Combine(output, name));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
        }
        private void Finish()
        {
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                assemblyVersion = typeof(LandBar.MainWindow).Assembly.GetName().Version?.ToString(),
                autoCollapseEnabled = ((IslandInactivityTimer)Field("_inactivity").GetValue(_window)!).Timeout is not null,
                startsSystemSources = false, callsPermissionApi = false, controlsUserMedia = false,
                passed = _passed, failures = _failures
            }, new JsonSerializerOptions { WriteIndented = true }));
            ((DispatcherTimer)Field("_timer").GetValue(_window)!).Stop();
            foreach (string name in new[] { "_contentTransition", "_motion", "_notifications", "_media", "_tray" })
                (Field(name).GetValue(_window) as IDisposable)?.Dispose();
            Field("_closing").SetValue(_window, true);
            Shutdown(_failures.Count == 0 ? 0 : 1);
        }
        private static IslandEvent Notification(string id) => new(id, IslandEventKind.Notification,
            "本地通知夹具", id, "测试内容", DateTimeOffset.UtcNow, "fixture.notifications");
    }
}
