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
        string output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/idle-smoke");
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
        int exitCode = app.Run();
        bool unchanged = before == SettingsHash(settingsPath);
        File.WriteAllText(Path.Combine(output, "settings-unchanged.json"), JsonSerializer.Serialize(new { unchanged }));
        return unchanged ? exitCode : 1;
    }

    private static string? SettingsHash(string path) => File.Exists(path)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))) : null;

    // Loads production WPF views with synthetic media only; user sources and plugin processes stay off.
    private sealed class ProbeApplication(string output) : Application
    {
        private LandBar.MainWindow _window = null!;
        private readonly List<string> _passed = [];
        private readonly List<string> _failures = [];

        protected override void OnStartup(StartupEventArgs e)
        {
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Idle State Smoke" };
            MainWindow = _window;
            _window.Loaded += async (_, _) =>
            {
                try { await Scenarios(); }
                catch (Exception exception) { _failures.Add(exception.ToString()); }
                finally { Finish(); }
            };
            _window.Show();
        }

        private async Task Scenarios()
        {
            ((AppSettings)Field("_settings").GetValue(_window)!).AutoCollapseSeconds = 0;
            Invoke("ApplyAutoCollapseSetting");
            await Task.Delay(800);
            _window.Left = -3000; _window.Top = -3000;
            Invoke("EndDemo");
            Invoke("Collapse");
            await Task.Delay(850);
            var idle = Element<Grid>("CompactView");
            var clock = Element<TextBlock>("ClockLabel");
            var shell = Element<FrameworkElement>("Island");
            Size nativeSize = _window.RenderSize;
            Assert(idle.IsVisible && Math.Abs(shell.ActualWidth - 112) < 0.5 && Math.Abs(shell.ActualHeight - 44) < 0.5,
                "No activity uses a 112 by 44 clock capsule");
            Assert(!Element<Grid>("CompactCovers").IsVisible && !Element<FrameworkElement>("AudioBars").IsVisible,
                "Idle state has no cover placeholder or audio bars");
            Assert(Element<TextBlock>("CompactGlyph").Text.Length == 0,
                "Idle state contains no message or media glyph");
            Assert(clock.Text == DateTime.Now.ToString("HH:mm") && clock.FontSize == 16,
                "Idle clock is current and uses the larger text size");
            Assert(ClockCenterError() <= 0.5 && Grid.GetColumnSpan(clock) == 3,
                "Idle clock is horizontally centered across the entire capsule");
            Assert(NoAnimatedAudioBars(), "Idle state has no repeating audio animation");
            Capture("idle-clock.png");

            SetSessions(new MediaSessionSnapshot("demo.music", "fixture.music", "测试播放器", "Clair de lune", "Claude Debussy", null, true, false, true, true, true));
            Invoke("Render");
            await Task.Delay(850);
            Assert(Math.Abs(shell.ActualWidth - 180) < 0.5 && Element<Grid>("CompactCovers").IsVisible && Element<FrameworkElement>("AudioBars").IsVisible,
                "An actual media session exposes its compact cover and playback state");
            Assert(Element<Image>("CompactArtwork").Source is not null && Element<TextBlock>("CompactGlyph").Text == "\uE8D6",
                "Media state uses the media artwork and media fallback glyph");
            Assert(Grid.GetColumn(clock) == 2 && Grid.GetColumnSpan(clock) == 1,
                "The media clock returns to the right column");
            Capture("single-media.png");

            SetSessions(
                new MediaSessionSnapshot("demo.music", "fixture.music", "测试播放器", "Clair de lune", "Claude Debussy", null, true, false, true, true, true),
                new MediaSessionSnapshot("demo.second", "fixture.second", "另一播放器", "Bloom", "The Paper Kites", null, false, true, true, true, true));
            Invoke("Render");
            await Task.Delay(850);
            Assert(Math.Abs(shell.ActualWidth - 208) < 0.5 && Element<FrameworkElement>("SecondCover").IsVisible && Element<Image>("SecondaryArtwork").Source is not null,
                "Two media sessions preserve both compact covers");
            Capture("dual-media.png");

            SetSessions();
            Invoke("Render");
            await Task.Delay(850);
            Assert(Math.Abs(shell.ActualWidth - 112) < 0.5 && !Element<Grid>("CompactCovers").IsVisible && !Element<FrameworkElement>("SecondCover").IsVisible,
                "Removing all media restores the clock capsule and clears both covers");
            Assert(Element<Image>("CompactArtwork").Source is null && Element<Image>("SecondaryArtwork").Source is null && NoAnimatedAudioBars(),
                "Removing media clears artwork and stops every audio animation");
            Assert(ClockCenterError() <= 0.5 && _window.RenderSize == nativeSize,
                "Returning to idle preserves centered content and the fixed native host");
            Invoke("OpenActivity");
            await Task.Delay(850);
            Assert(Element<FrameworkElement>("ActivityView").IsVisible && Element<TextBlock>("EmptyState").Text == "暂无媒体",
                "Opening an idle capsule exposes the real empty activity view");
            Invoke("Collapse");
            await Task.Delay(850);
            Assert(idle.IsVisible && ClockCenterError() <= 0.5 && _window.RenderSize == nativeSize,
                "Collapsing the activity view restores the centered clock without resizing the native host");
            Capture("idle-return.png");

            var inactivity = (IslandInactivityTimer)Field("_inactivity").GetValue(_window)!;
            var settings = (AppSettings)Field("_settings").GetValue(_window)!;
            settings.AutoCollapseSeconds = 5;
            Invoke("ApplyAutoCollapseSetting");
            var media = new MediaSessionSnapshot("demo.music", "fixture.music", "测试播放器", "曲目一", "本地夹具", null, true, false, true, true, true);
            SetSessions(media);
            Receive(new("fixture.media-new", IslandEventKind.Media, media.AppName, media.Title, media.Artist, DateTimeOffset.UtcNow, media.SessionId));
            await Task.Delay(850);
            var live = (IslandScheduler)Field("_liveScheduler").GetValue(_window)!;
            Assert(idle.IsVisible && live.TotalCount == 0 && live.History.All(item => item.Kind != IslandEventKind.Media) && inactivity.Deadline is null,
                "A new media source updates compact state without opening an automatic card or arming inactivity");
            Invoke("OpenActivity");
            await Task.Delay(850);
            var deadline = inactivity.Deadline;
            Assert(Math.Abs(shell.ActualWidth - 400) < .5 && Math.Abs(shell.ActualHeight - 240) < .5,
                "Manual media activity uses the compact 400 by 240 panel");
            Assert(CardText("PlayPauseLabel") == "暂停", "A manually opened playing snapshot exposes the pause control");
            media = media with { Title = "曲目二", IsPlaying = false };
            SetSessions(media);
            Receive(new("fixture.media-next", IslandEventKind.Media, media.AppName, media.Title, media.Artist, DateTimeOffset.UtcNow, media.SessionId));
            await Task.Delay(850);
            Assert(CardText("Title") == "曲目二" && CardText("PlayPauseLabel") == "播放" && inactivity.Deadline == deadline,
                "Track and playback snapshot changes refresh title and controls without extending inactivity");
            Capture("media-manual-paused.png");
            Invoke("Collapse");
            media = media with { Title = "曲目三", IsPlaying = true };
            SetSessions(media);
            Receive(new("fixture.media-resume", IslandEventKind.Media, media.AppName, media.Title, media.Artist, DateTimeOffset.UtcNow, media.SessionId));
            await Task.Delay(850);
            Assert(idle.IsVisible && live.TotalCount == 0 && inactivity.Deadline is null,
                "A song change and resumed playback keep the island collapsed");
            Receive(new("fixture.notice-after-media", IslandEventKind.Notification, "通知 · 测试", "通知仍自动展开", "正文保持十五号字号，紧凑面板完整显示。", DateTimeOffset.UtcNow, "fixture.notice"));
            await Task.Delay(850);
            Assert(Element<FrameworkElement>("ActivityView").IsVisible && live.TotalCount == 1 && inactivity.Deadline is not null &&
                Math.Abs(shell.ActualWidth - 400) < .5 && Math.Abs(shell.ActualHeight - 180) < .5,
                "A real notification still opens the compact 400 by 180 automatic panel");
            Capture("notification-compact.png");
        }

        private double ClockCenterError()
        {
            var clock = Element<TextBlock>("ClockLabel");
            var shell = Element<FrameworkElement>("Island");
            var root = Element<FrameworkElement>("HoverSurface");
            var clockOrigin = clock.TranslatePoint(new Point(), root);
            var shellOrigin = shell.TranslatePoint(new Point(), root);
            return Math.Abs(clockOrigin.X + clock.ActualWidth / 2 - shellOrigin.X - shell.ActualWidth / 2);
        }
        private bool NoAnimatedAudioBars() => new[] { "AudioBar1", "AudioBar2", "AudioBar3" }
            .All(name => !Element<FrameworkElement>(name).RenderTransform.HasAnimatedProperties);
        private void SetSessions(params MediaSessionSnapshot[] sessions) => typeof(WindowsMediaSource)
            .GetMethod("Publish", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Field("_media").GetValue(_window), new object[] { sessions });
        private void Receive(IslandEvent item) => typeof(LandBar.MainWindow)
            .GetMethod("Receive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(_window, new object[] { item });
        private string CardText(string property)
        {
            object card = Element<ItemsControl>("EventItems").Items[0];
            return (string)card.GetType().GetProperty(property)!.GetValue(card)!;
        }
        private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
        private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private void Invoke(string name) => typeof(LandBar.MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)!.Invoke(_window, null);
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
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY),
                dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            bitmap.Render(root);
            var origin = shell.TranslatePoint(new Point(), root);
            var cropped = new CroppedBitmap(bitmap, new Int32Rect((int)Math.Floor(origin.X * dpi.DpiScaleX), (int)Math.Floor(origin.Y * dpi.DpiScaleY),
                (int)Math.Ceiling(shell.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(shell.ActualHeight * dpi.DpiScaleY)));
            using var stream = File.Create(Path.Combine(output, name));
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped)); encoder.Save(stream);
        }
        private void Finish()
        {
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                assemblyVersion = typeof(LandBar.MainWindow).Assembly.GetName().Version?.ToString(),
                startsSystemSources = false, startsPlugins = false, callsPermissionApi = false, controlsUserMedia = false,
                passed = _passed, failures = _failures
            }, new JsonSerializerOptions { WriteIndented = true }));
            ((DispatcherTimer)Field("_timer").GetValue(_window)!).Stop();
            foreach (string name in new[] { "_topmost", "_contentTransition", "_motion", "_notifications", "_media", "_tray" })
                (Field(name).GetValue(_window) as IDisposable)?.Dispose();
            Field("_closing").SetValue(_window, true);
            Shutdown(_failures.Count == 0 ? 0 : 1);
        }
    }
}
