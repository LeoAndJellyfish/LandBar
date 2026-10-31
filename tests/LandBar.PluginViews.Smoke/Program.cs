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
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;
using LandBar.Services;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--view-fixture")) return Fixture().GetAwaiter().GetResult();
        string output = Path.GetFullPath(args.FirstOrDefault(argument => !argument.StartsWith("--", StringComparison.Ordinal)) ?? "artifacts/plugin-views-smoke");
        Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "settings.json");
        string? before = Hash(settings);
        Application app = args.Contains("--ring-only") ? new RingProbe(output)
            : args.Contains("--codex-only") ? new CodexViewProbe(output) : new Probe(output);
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Production-App.xaml"));
        var resources = new XElement(ns + "ResourceDictionary", new XAttribute("xmlns", ns.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"),
            source.Root!.Element(ns + "Application.Resources")!.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        int result = app.Run();
        bool unchanged = before == Hash(settings);
        File.WriteAllText(Path.Combine(output, "settings-unchanged.json"), JsonSerializer.Serialize(new { unchanged }));
        return unchanged ? result : 1;
    }

    private static string? Hash(string file) => File.Exists(file)
        ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))) : null;

    private sealed class RingProbe(string output) : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            try { Shutdown(RingAlignmentChecks.Run(output, (FontFamily)Resources["AppFont"])); }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "ring-error.txt"), error.ToString());
                Console.WriteLine("FAIL " + error);
                Shutdown(1);
            }
        }
    }

    // A synthetic provider runs in a real, isolated child process and never reads account data.
    private static async Task<int> Fixture()
    {
        Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
        int clicks = 0;
        var contexts = new Dictionary<string, string>();
        var pages = new Dictionary<string, string>
        {
            ["media"] = "media.page", ["notices"] = "notifications.page", ["center"] = "controlCenter.page",
            ["settings"] = "settings.page", ["management"] = "pluginManagement.page"
        };
        void Send(object message) { Console.WriteLine(JsonSerializer.Serialize(message)); Console.Out.Flush(); }
        object Text(string value, int size = 15) => new { type = "text", text = value, fontSize = size, color = "#DADAE2" };
        void View(string id, string slot, object root, string? title = null, object? placement = null)
        {
            var message = new Dictionary<string, object?> { ["type"] = "view", ["id"] = id, ["slot"] = slot, ["title"] = title, ["root"] = root };
            if (placement is not null) message["placement"] = placement;
            Send(message);
        }
        void Page(string id, string slot, string title)
        {
            View(id, slot, new
            {
                type = "column", width = 320, height = 140, spacing = 10, padding = 12,
                children = new object[] { Text(title, 20), Text($"点击 {clicks} · {contexts.GetValueOrDefault(slot, "context:pending")}"),
                    new { type = "button", text = "更新", actionId = "increment", width = 96, height = 36 } }
            }, slot == "activity.tab" ? title : null);
        }
        string? line = await Console.In.ReadLineAsync();
        if (line is null) return 1;
        using (var hello = JsonDocument.Parse(line))
            if (hello.RootElement.GetProperty("type").GetString() != "hello") return 1;
        Send(new { type = "hello", protocolVersion = 1 });
        string frame1 = string.Concat(Enumerable.Range(0, 64).Select(i => i % 8 is >= 2 and <= 5 && i / 8 is >= 1 and <= 6 ? "1" : "0"));
        string frame2 = string.Concat(frame1.Select(pixel => pixel == '1' ? '2' : '0'));
        void Widgets(string mode)
        {
            object avatar = mode switch
            {
                "intrinsic" => new { type = "ring", height = 32, value = .5, text = "50", fontSize = 12 },
                "wide-ring" => new { type = "ring", width = 64, value = .5, text = "50", fontSize = 12 },
                _ => new { type = "sprite", width = mode == "wide" ? 64 : 28, height = 28, pixelWidth = 8, pixelHeight = 8,
                    palette = new[] { "#00000000", "#BEDDC9", "#86BBA4" }, frames = new[] { frame1, frame2 }, fps = 6 }
            };
            View("avatar", "idle.widget", avatar, placement: new { defaultSide = "left", allowedSides = new[] { "left", "right" } });
            View("ring", "idle.right", new { type = "ring", width = 28, height = 28, value = 0.5, text = "50", fontSize = 12,
                color = "#BEDDC9", trackColor = "#292D30", thickness = 3 },
                placement: new { defaultSide = "right", allowedSides = mode == "flexible" ? new[] { "left", "right" } : new[] { "right" } });
        }
        Widgets("normal");
        View("idlemain", "idle.content", new { type = "text", width = 116, height = 28, text = "自定义状态", fontSize = 15, align = "center" });
        Page("first", "activity.tab", "任务"); Page("second", "activity.tab", "统计");
        foreach (var (id, slot) in pages) Page(id, slot, "测试页面 · " + id);
        while (await Console.In.ReadLineAsync() is { } incoming)
        {
            using var document = JsonDocument.Parse(incoming);
            var root = document.RootElement;
            string? type = root.GetProperty("type").GetString();
            if (type == "shutdown") return 0;
            if (type == "viewAction")
            {
                if (root.GetProperty("actionId").GetString() == "increment") clicks++;
                string id = root.GetProperty("viewId").GetString()!;
                if (id is "first" or "second") Page(id, "activity.tab", id == "first" ? "任务" : "统计");
                else if (pages.TryGetValue(id, out var slot)) Page(id, slot, "测试页面 · " + id);
            }
            else if (type == "context")
            {
                string slot = root.GetProperty("slot").GetString()!;
                var data = root.GetProperty("data");
                if (slot == "idle.widget" && data.TryGetProperty("layoutFixture", out var layoutFixture))
                {
                    Widgets(layoutFixture.GetString()!);
                    continue;
                }
                string status = "context:valid";
                if (slot == "media.page")
                    status = data.GetProperty("sessions").EnumerateArray().Any(session => session.GetProperty("Title").GetString() == "合成曲目") ? "media:valid" : "media:missing";
                if (slot == "notifications.page")
                    status = data.GetProperty("notifications").EnumerateArray().Any(item => item.GetProperty("Title").GetString() == "新通知" && item.GetProperty("Body").GetString() == "内容已隐藏") ? "privacy:valid" : "privacy:missing";
                contexts[slot] = status;
                if (slot == "activity.tab") { Page("first", slot, "任务"); Page("second", slot, "统计"); }
                else foreach (var entry in pages.Where(entry => entry.Value == slot)) Page(entry.Key, slot, "测试页面 · " + entry.Key);
            }
        }
        return 0;
    }

    private sealed class Probe(string output) : Application
    {
        private LandBar.MainWindow _window = null!;
        private PluginManager? _manager;
        private AppSettings _settings = null!;
        private readonly List<string> _passed = [], _failures = [];
        protected override void OnStartup(StartupEventArgs e)
        {
            _window = new LandBar.MainWindow(demo: true, smoke: false, preview: false) { Title = "LandBar Synthetic Plugin Views Smoke" };
            MainWindow = _window;
            _window.Loaded += async (_, _) =>
            {
                try { await Scenarios(); }
                catch (Exception error) { _failures.Add(error.ToString()); }
                finally { await Finish(); }
            };
            _window.Show();
        }

        private async Task Scenarios()
        {
            _settings = (AppSettings)Get("_settings")!;
            _settings.AutoCollapseSeconds = 0; _settings.Quiet = false; _settings.HideContent = false;
            _settings.EnablePluginViews = false; _settings.PluginViewBindings = new();
            Invoke("ApplyAutoCollapseSetting");
            await Settle(); Invoke("EndDemo"); Invoke("Collapse"); await Settle();
            SetWindowPos(new WindowInteropHelper(_window).Handle, IntPtr.Zero, -12000, -12000, 0, 0, 0x0015);
            var nativeSize = _window.RenderSize;
            string root = Path.Combine(output, "synthetic-plugins"); string folder = Path.Combine(root, "fixture");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "plugin.json"), JsonSerializer.Serialize(new
            {
                id = "fixture", name = "通用界面测试", version = "1.0.0", description = "合成像素动画、独立标签和页面替换",
                command = Environment.ProcessPath!, arguments = new[] { "--view-fixture" }, protocolVersion = 1
            }));
            _manager = new PluginManager(root); Invoke("ConnectPlugins", _manager); _manager.Discover();
            Check(await _manager.StartAsync("fixture"), "The synthetic provider completes a real process handshake");
            await Until(() => _manager.Views.Count == 10); await Settle();
            Check(_manager.Cards.Count == 0 && Live.TotalCount == 0, "Declarative views do not add notification cards or reopen the shell");
            Check(Presenter("PluginIdleLeft").View is null && Element<ItemsControl>("PluginTabItems").Items.Count == 0,
                "Unselected disabled views retain the built-in compact UI");
            Bind("idle.left", "avatar"); Bind("idle.right", "ring");
            _settings.EnablePluginViews = true; Render(); await Settle();
            var sprite = Descendants<PluginSpriteImage>(Presenter("PluginIdleLeft")).Single();
            Check(Presenter("PluginIdleLeft").View?.Id == "avatar" && Presenter("PluginIdleRight").View?.Root.Value == .5,
                "Selected left sprite and right ring coexist with the built-in clock");
            Check(Element<FrameworkElement>("BuiltinCompactView").IsVisible && sprite.IsVisible,
                "Selected widgets preserve the built-in compact center");
            if (SystemParameters.ClientAreaAnimation)
            {
                int frame = sprite.FrameIndex; await Until(() => sprite.FrameIndex != frame, 2000);
                Check(sprite.IsAnimating, "A visible sprite advances real frames on its dispatcher timer");
            }
            else Check(!sprite.IsAnimating, "System animation preferences disable sprite timers");
            Capture("synthetic-idle-widgets.png");
            await PlacementScenarios();
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            Invoke("PluginIdle_Click", Presenter("PluginIdleLeft"), click); await Settle();
            Check((string?)Get("_activePluginTabKey") == "view:fixture:first" && Presenter("ActivityOverrideContent").View?.Title == "任务",
                "Clicking an idle widget opens its provider's independent tab");
            Check(!sprite.IsAnimating && !sprite.IsVisible, "Hidden or disposed compact sprites stop their timer");
            var tabs = Descendants<Button>(Element<ItemsControl>("PluginTabItems")).ToArray();
            Check(tabs.Length == 2 && tabs.Select(button => button.Content as string).Order().SequenceEqual(new[] { "任务", "统计" }.Order()),
                "Two named tabs from one provider remain independent");
            Tab("second").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Check(Presenter("ActivityOverrideContent").View?.Id == "second" && (string?)Get("_activePluginTabKey") == "view:fixture:second",
                "Selecting the second title displays only its own view");
            Tab("first").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Settle();
            Descendants<Button>(Presenter("ActivityOverrideContent")).Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => ViewText("first").Any(text => text.StartsWith("点击 1"))); await Settle();
            Check(Texts(Presenter("ActivityOverrideContent").View!.Root).Any(text => text.StartsWith("点击 1")),
                "A rendered custom button reaches the provider and updates its tree");
            Check(ViewText("first").Any(text => text.Contains("context:valid")), "Selected independent tabs receive their host context");
            Capture("synthetic-independent-tabs.png");

            Bind("idle.content", "idlemain"); Invoke("Collapse"); await Settle();
            Check(Presenter("PluginIdleContent").View?.Id == "idlemain" && !Element<FrameworkElement>("BuiltinCompactView").IsVisible,
                "An explicitly selected compact center replaces the built-in center");
            Capture("synthetic-idle-center.png");
            _settings.PluginViewBindings.Remove("idle.content");
            Set("_sessions", new[] { new MediaSessionSnapshot("synthetic.media", "synthetic.app", "合成播放器", "合成曲目", "测试来源", null, true, true, true, true, true) });
            Live.Push(new("synthetic.notice", IslandEventKind.Notification, "合成通知", "合成标题", "合成正文", DateTimeOffset.UtcNow));
            _settings.HideContent = true;
            foreach (var scenario in new[]
            {
                (Slot:"media.page", Id:"media", State:"Activity", Notification:false, Presenter:"ActivityOverrideContent"),
                (Slot:"notifications.page", Id:"notices", State:"Activity", Notification:true, Presenter:"ActivityOverrideContent"),
                (Slot:"controlCenter.page", Id:"center", State:"ControlCenter", Notification:false, Presenter:"PageOverrideContent"),
                (Slot:"settings.page", Id:"settings", State:"Settings", Notification:false, Presenter:"PageOverrideContent"),
                (Slot:"pluginManagement.page", Id:"management", State:"PluginSettings", Notification:false, Presenter:"PageOverrideContent")
            })
            {
                Bind(scenario.Slot, scenario.Id); Set("_pluginsTab", false); Set("_notificationsTab", scenario.Notification);
                ChangeView(scenario.State); await Settle();
                await Until(() => ViewText(scenario.Id).Any(text => text.Contains(scenario.Id == "media" ? "media:valid" : scenario.Id == "notices" ? "privacy:valid" : "context:valid")));
                Check(Presenter(scenario.Presenter).IsVisible && Presenter(scenario.Presenter).View?.Slot == scenario.Slot,
                    $"Explicit {scenario.Slot} selection renders its own native view");
                Check(Texts(Presenter(scenario.Presenter).View!.Root).Any(text => text.Contains(scenario.Id == "media" ? "media:valid" : scenario.Id == "notices" ? "privacy:valid" : "context:valid")),
                    $"The selected {scenario.Slot} provider receives correct bounded context");
                Capture("synthetic-" + scenario.Id + "-page.png");
            }
            await _manager.StopAsync("fixture");
            await Until(() => _manager.Views.Count == 0); Invoke("Collapse"); Set("_sessions", Array.Empty<MediaSessionSnapshot>()); Render(); await Settle();
            Check(Presenter("PluginIdleLeft").View is null && Presenter("PluginIdleRight").View is null &&
                Presenter("ActivityOverrideContent").View is null && Presenter("PageOverrideContent").View is null,
                "Stopping a provider withdraws every selected widget and page");
            Check(Element<ItemsControl>("PluginTabItems").Items.Count == 0 && Math.Abs(Element<FrameworkElement>("Island").ActualWidth - 112) < .75,
                "Provider stop removes its tabs and restores the 112-DIP idle clock");
            Check(!sprite.IsAnimating, "A removed sprite retains no running timer");
            Check(await _manager.StartAsync("fixture"), "The same synthetic provider can restart after complete cleanup");
            await Until(() => _manager.Views.Count == 10); Render(); await Settle();
            Invoke("RestoreBuiltinViews"); await Settle();
            Check(!_settings.EnablePluginViews && Element<FrameworkElement>("ControlCenterView").IsVisible &&
                !Element<FrameworkElement>("PageOverrideView").IsVisible && Element<ItemsControl>("PluginTabItems").Items.Count == 0,
                "Restore built-in views removes custom tabs and restores the control center");
            Check(_window.RenderSize == nativeSize, "All custom transitions preserve the fixed native host size");

            // Exercise the exact public example shipped in the release, independently of the fixture.
            await _manager.StopAsync("fixture");
            string sample = Path.Combine(root, "pixel-pet"); Directory.CreateDirectory(sample);
            foreach (string name in new[] { "plugin.json", "plugin.ps1", "README.md" })
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "pixel-pet", name), Path.Combine(sample, name), overwrite: true);
            _settings.PluginViewBindings.Clear(); _settings.HideContent = false; _settings.EnablePluginViews = true;
            _manager.Discover();
            Check(await _manager.StartAsync("pixel-pet"), "The distributed PowerShell pixel-pet sample completes a real handshake");
            await Until(() => _manager.Views.Count == 2);
            _settings.PluginViewBindings["idle.left"] = "view:pixel-pet:avatar";
            Invoke("Collapse"); Render(); await Settle();
            var publicSprite = Descendants<PluginSpriteImage>(Presenter("PluginIdleLeft")).Single();
            var publicAvatar = _manager.Views.Single(view => view.Id == "avatar");
            Check(publicAvatar.Slot == "idle.widget" && publicAvatar.Placement is { DefaultSide: "left" } &&
                publicAvatar.Placement.AllowedSides.SequenceEqual(new[] { "left", "right" }),
                "The distributed sample explicitly declares left and right placement");
            Check(publicAvatar.Root.Sprite is { PixelWidth: 16, PixelHeight: 16, Fps: 4 } && publicAvatar.Root.Sprite.Frames.Count == 4,
                "The public sample supplies its actual sixteen-pixel four-frame animation");
            if (SystemParameters.ClientAreaAnimation)
            {
                int frame = publicSprite.FrameIndex; await Until(() => publicSprite.FrameIndex != frame, 2000);
                Check(publicSprite.IsAnimating, "The public sample's visible animation advances real frames");
            }
            else Check(!publicSprite.IsAnimating, "The public sample respects system animation preferences");
            Capture("public-pixel-idle.png");
            Check(Assign("view:pixel-pet:avatar", "right"), "The distributed sample can move to the right side");
            Render(); await Settle();
            Check(Presenter("PluginIdleLeft").View is null && Presenter("PluginIdleRight").View?.StableKey == publicAvatar.StableKey &&
                _settings.PluginViewBindings.Values.Count(value => value == publicAvatar.StableKey) == 1,
                "Moving the distributed sample preserves its identity without duplicate avatars");
            CheckCenteredCompact("A single right-side public avatar keeps the idle clock centered");
            CheckCompactBounds("The public right-side avatar remains entirely within the shell");
            var movedSprite = Descendants<PluginSpriteImage>(Presenter("PluginIdleRight")).Single();
            if (SystemParameters.ClientAreaAnimation)
            {
                int frame = movedSprite.FrameIndex; await Until(() => movedSprite.FrameIndex != frame, 2000);
                Check(movedSprite.IsAnimating && !publicSprite.IsAnimating, "Moving a public avatar restarts only its visible animation");
            }
            else Check(!movedSprite.IsAnimating && !publicSprite.IsAnimating, "Moved avatars respect disabled system animations");
            Capture("public-pixel-idle-right.png");
            var publicClick = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.MouseLeftButtonUpEvent };
            Invoke("PluginIdle_Click", Presenter("PluginIdleRight"), publicClick); await Settle();
            Check((string?)Get("_activePluginTabKey") == "view:pixel-pet:pet" && Presenter("ActivityOverrideContent").View?.Title == "像素伙伴",
                "Clicking the public sample widget opens its own named tab");
            Check(!publicSprite.IsAnimating, "Opening the public sample tab stops its hidden compact animation");
            var publicRing = Descendants<PluginRing>(Presenter("ActivityOverrideContent")).Single();
            var ringBox = (FrameworkElement)VisualTreeHelper.GetParent(publicRing);
            var row = (Grid)VisualTreeHelper.GetParent(ringBox);
            Check(row.ColumnDefinitions[Grid.GetColumn(ringBox)].ActualWidth + .5 >= ringBox.ActualWidth + ringBox.Margin.Left + ringBox.Margin.Right,
                "The public sample ring fits its row column including inter-item spacing");
            var publicScroll = Element<ScrollViewer>("ActivityOverrideScroll");
            var publicButton = Descendants<Button>(Presenter("ActivityOverrideContent")).Single();
            var buttonBounds = publicButton.TransformToAncestor(publicScroll).TransformBounds(new Rect(publicButton.RenderSize));
            var viewport = new Rect(publicScroll.RenderSize); viewport.Inflate(.5, .5);
            Check(viewport.Contains(buttonBounds), "The public sample action remains fully visible without scrolling");
            Capture("public-pixel-page.png");
            string initialColor = publicAvatar.Root.Sprite!.Palette[1];
            var colorButton = Descendants<Button>(Presenter("ActivityOverrideContent")).Single();
            colorButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => _manager.Views.Single(view => view.Id == "avatar").Root.Sprite!.Palette[1] != initialColor);
            await Settle();
            Check(Texts(Presenter("ActivityOverrideContent").View!.Root).Any(text => text == "配色：晴蓝"),
                "The public sample button reaches PowerShell and updates the page and avatar palette");
            Capture("public-pixel-page-theme.png");
            await _manager.StopAsync("pixel-pet"); Invoke("Collapse"); Render(); await Settle();
            Check(_manager.Views.Count == 0 && Element<ItemsControl>("PluginTabItems").Items.Count == 0 &&
                Presenter("PluginIdleLeft").View is null && Presenter("PluginIdleRight").View is null,
                "Stopping the public sample removes its widget and independent tab");
        }

        private async Task PlacementScenarios()
        {
            const string avatar = "view:fixture:avatar", ring = "view:fixture:ring";
            ChangeView("PluginSettings"); await Settle();
            var choices = Element<ItemsControl>("PluginViewChoices");
            var avatarButtons = Descendants<Button>(choices).Where(button => button.Tag is string tag && tag.StartsWith(avatar + "|", StringComparison.Ordinal)).ToArray();
            var ringButtons = Descendants<Button>(choices).Where(button => button.Tag is string tag && tag.StartsWith(ring + "|", StringComparison.Ordinal)).ToArray();
            Check(avatarButtons.Where(button => button.Visibility == Visibility.Visible).Select(button => button.Content as string).Order()
                    .SequenceEqual(new[] { "左侧", "右侧" }.Order()),
                "Management renders both declared side choices for a flexible widget");
            Check(ringButtons.Single(button => button.Tag as string == ring + "|left").Visibility == Visibility.Collapsed &&
                ringButtons.Single(button => button.Tag as string == ring + "|right").Visibility == Visibility.Visible &&
                ringButtons.Single(button => button.Tag as string == ring + "|right").ToolTip as string == "固定右侧",
                "Management exposes only the fixed right side and its provider constraint");
            Check(avatarButtons.Single(button => button.Tag as string == avatar + "|left").Background is SolidColorBrush { Color: var leftColor } && leftColor == (Color)ColorConverter.ConvertFromString("#31463B") &&
                ringButtons.Single(button => button.Tag as string == ring + "|right").Background is SolidColorBrush { Color: var rightColor } && rightColor == leftColor,
                "Management highlights the actual selected physical sides");
            string ChoiceTitle(string key)
            {
                var item = choices.Items.Cast<object>().Single(item => (string)item.GetType().GetProperty("Key")!.GetValue(item)! == key);
                return (string)item.GetType().GetProperty("Title")!.GetValue(item)!;
            }
            Check(ChoiceTitle(avatar) != ChoiceTitle(ring), "Multiple widgets from one provider retain distinguishable management titles");
            var scroll = Element<ScrollViewer>("PluginManagementScroll");
            avatarButtons.First().BringIntoView(); await Settle();
            scroll.ScrollToVerticalOffset(scroll.VerticalOffset + avatarButtons.First().TranslatePoint(new Point(), scroll).Y - 100);
            await Settle();
            var viewport = new Rect(scroll.RenderSize); viewport.Inflate(.75, .75);
            Check(avatarButtons.Where(button => button.Visibility == Visibility.Visible)
                    .All(button => viewport.Contains(button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize)))),
                "Flexible position controls remain complete within the management viewport");
            Capture("synthetic-widget-placement-settings.png");
            ringButtons.Single(button => button.Visibility == Visibility.Visible).BringIntoView(); await Settle();
            Check(ringButtons.Where(button => button.Visibility == Visibility.Visible)
                    .All(button => viewport.Contains(button.TransformToAncestor(scroll).TransformBounds(new Rect(button.RenderSize)))),
                "Fixed position controls remain complete within their scrolled management viewport");
            Capture("synthetic-fixed-widget-settings.png");
            Invoke("Collapse"); await Settle();
            await RingTransitionScenario();
            string before = JsonSerializer.Serialize(_settings.PluginViewBindings);
            Check(!Assign(ring, "left") && before == JsonSerializer.Serialize(_settings.PluginViewBindings),
                "A provider's fixed right-side declaration rejects left placement without changing bindings");
            Check(Assign(avatar, "right"), "A flexible widget accepts the opposite side");
            Render(); await Settle();
            Check(Presenter("PluginIdleLeft").View is null && Presenter("PluginIdleRight").View?.StableKey == avatar &&
                _settings.PluginViewBindings.Values.Count(value => value == avatar) == 1 &&
                !_settings.PluginViewBindings.Values.Contains(ring),
                "A move replaces an incompatible occupant without duplicating or relocating a fixed widget");
            CheckCenteredCompact("A single side widget leaves the idle clock at the shell center");
            _settings.PluginViewBindings["idle.left"] = avatar;
            Render(); await Settle();
            Check(Presenter("PluginIdleLeft").View?.StableKey == avatar && Presenter("PluginIdleRight").View is null,
                "Duplicate persisted bindings display a flexible widget once on its declared default side");

            _settings.PluginViewBindings.Clear();
            Assign(avatar, "left"); Assign(ring, "right");
            await ConfigureWidgets("flexible", () => _manager!.Views.Single(view => view.Id == "ring").Placement!.AllowedSides.Count == 2);
            Check(Assign(avatar, "right"), "A flexible widget can move into a compatible occupied side");
            Render(); await Settle();
            Check(Presenter("PluginIdleLeft").View?.StableKey == ring && Presenter("PluginIdleRight").View?.StableKey == avatar &&
                _settings.PluginViewBindings.Count == 2,
                "Compatible occupied widgets exchange sides atomically");
            Check(Assign(avatar, "left"), "A swapped widget can move back to its original side");

            await ConfigureWidgets("wide", () => _manager!.Views.Single(view => view.Id == "avatar").Root.Width == 64 &&
                _manager.Views.Single(view => view.Id == "ring").Placement!.AllowedSides.Count == 1);
            Render(); await Settle();
            Check(Math.Abs(Presenter("PluginIdleLeft").ActualWidth - 64) < .75 && Math.Abs(Presenter("PluginIdleRight").ActualWidth - 28) < .75,
                "The host gives asymmetric 64-DIP and 28-DIP widgets their complete measured widths");
            CheckCenteredCompact("Asymmetric side widths preserve the idle clock's physical center");
            CheckCompactBounds("Asymmetric complete widgets fit inside the rounded compact shell");
            Capture("synthetic-asymmetric-widgets.png");
            var narrow = (CompactWidgetLayout)InvokeResult("CalculateCompactPluginLayout", 208.0, 160.0, 2)!;
            Check(narrow.Width <= 160 && !narrow.ShowLeft && narrow.ShowRight && narrow.MediaDetail == CompactMediaDetail.Clock && narrow.CenterWidth >= 56,
                "A narrow host preserves the clock and whole smaller widget while withdrawing the wider widget");

            foreach (int count in new[] { 1, 2 })
            {
                Set("_sessions", Enumerable.Range(0, count).Select(index => new MediaSessionSnapshot("layout." + index,
                    "layout.app." + index, "合成播放器", "合成曲目", "合成来源", null, true, true, true, true, true)).ToArray());
                Render(); await Settle();
                Check(Presenter("PluginIdleLeft").View?.StableKey == avatar && Presenter("PluginIdleRight").View?.StableKey == ring &&
                    Element<FrameworkElement>("BuiltinCompactView").IsVisible && !Element<FrameworkElement>("ActivityView").IsVisible,
                    $"{count} media session(s) coexist with both selected widgets without expanding the island");
                CheckCompactBounds($"Both widgets and the built-in body fit with {count} media session(s)");
            }
            Set("_sessions", Array.Empty<MediaSessionSnapshot>());
            await ConfigureWidgets("intrinsic", () => _manager!.Views.Single(view => view.Id == "avatar").Root is { Type: "ring", Width: null });
            Render(); await Settle();
            Check(Math.Abs(Presenter("PluginIdleLeft").ActualWidth - 32) < .75,
                "An unspecified root width uses the rendered ring's 32-DIP intrinsic width");
            CheckCenteredCompact("Intrinsic widget measurement preserves the centered idle clock");
            CheckCompactBounds("An intrinsically sized ring stays whole within the compact shell");

            await ConfigureWidgets("wide-ring", () => _manager!.Views.Single(view => view.Id == "avatar").Root is { Type: "ring", Width: 64, Height: null });
            Render(); await Settle();
            var wideRing = Descendants<PluginRing>(Presenter("PluginIdleLeft")).Single();
            Check(Math.Abs(Presenter("PluginIdleLeft").ActualWidth - 64) < .75 && wideRing.ActualHeight <= 36 &&
                Presenter("PluginIdleLeft").Child is FrameworkElement { ActualHeight: <= 36 },
                "A 64-DIP ring without an explicit height respects the compact 36-DIP height budget");
            CheckCenteredCompact("A wide ring with inferred height preserves the centered idle clock");
            CheckCompactBounds("A wide ring with inferred height remains complete within its presenter and shell");

            await ConfigureWidgets("normal", () => _manager!.Views.Single(view => view.Id == "avatar").Root is { Type: "sprite", Width: 28 });
            _settings.PluginViewBindings.Clear(); Bind("idle.left", "avatar"); Bind("idle.right", "ring");
            Render(); await Settle();
        }

        private async Task RingTransitionScenario()
        {
            _settings.PluginViewBindings.Clear(); Render(); await Settle();
            var samples = new List<object>();
            int visible = 0, clipped = 0;
            async Task Observe(string phase)
            {
                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < 800)
                {
                    await Task.Delay(8);
                    var viewport = Element<FrameworkElement>("IslandViewport");
                    var content = Element<FrameworkElement>("ContentLayout");
                    var ring = Descendants<PluginRing>(Presenter("PluginIdleRight")).FirstOrDefault();
                    bool painted = ring is { IsVisible: true, ActualWidth: > 0 } && content.Opacity > .02;
                    bool fits = true;
                    var bounds = ring is null ? Rect.Empty : ring.TransformToAncestor(viewport).TransformBounds(new Rect(ring.RenderSize));
                    if (painted)
                    {
                        visible++;
                        var clip = viewport.Clip!;
                        fits = clip.FillContains(bounds.TopLeft) && clip.FillContains(bounds.TopRight) &&
                            clip.FillContains(bounds.BottomLeft) && clip.FillContains(bounds.BottomRight);
                        if (!fits) clipped++;
                    }
                    samples.Add(new { phase, elapsedMs = clock.Elapsed.TotalMilliseconds, opacity = content.Opacity, painted, fits,
                        shellWidth = Element<FrameworkElement>("Island").ActualWidth,
                        ringX = bounds.IsEmpty ? 0 : bounds.X, ringY = bounds.IsEmpty ? 0 : bounds.Y,
                        ringWidth = bounds.IsEmpty ? 0 : bounds.Width, ringHeight = bounds.IsEmpty ? 0 : bounds.Height });
                }
            }
            Bind("idle.right", "ring"); Render(); await Observe("grow");
            _settings.PluginViewBindings.Clear(); Render(); await Observe("withdraw");
            File.WriteAllText(Path.Combine(output, "ring-transition.json"), JsonSerializer.Serialize(new { visible, clipped, samples }, new JsonSerializerOptions { WriteIndented = true }));
            Check(visible > 0, "Ring-width transitions expose their completed live ring during frame sampling");
            Check(clipped == 0, "Visible ring drawings remain inside the rounded clip during growth and withdrawal");
            Bind("idle.left", "avatar"); Bind("idle.right", "ring"); Render(); await Settle();
        }

        private async Task ConfigureWidgets(string mode, Func<bool> ready)
        {
            if (!await _manager!.SendContextAsync("fixture", "idle.widget", new { layoutFixture = mode }))
                throw new InvalidOperationException("Synthetic widget configuration was rejected.");
            await Until(ready);
        }

        private bool Assign(string key, string side) => (bool)InvokeResult("TryAssignPluginSide", key, side)!;

        private void CheckCenteredCompact(string name)
        {
            var shell = Element<FrameworkElement>("Island");
            var center = Element<FrameworkElement>("BuiltinCompactView");
            var root = Element<FrameworkElement>("HoverSurface");
            double shellCenter = shell.TranslatePoint(new Point(shell.ActualWidth / 2, 0), root).X;
            double bodyCenter = center.TranslatePoint(new Point(center.ActualWidth / 2, 0), root).X;
            double clockCenter = Element<FrameworkElement>("ClockLabel").TranslatePoint(new Point(Element<FrameworkElement>("ClockLabel").ActualWidth / 2, 0), root).X;
            double tolerance = 1 / VisualTreeHelper.GetDpi(_window).DpiScaleX;
            Check(Math.Abs(shellCenter - bodyCenter) <= tolerance && Math.Abs(shellCenter - clockCenter) <= tolerance, name);
        }

        private void CheckCompactBounds(string name)
        {
            var root = Element<FrameworkElement>("HoverSurface");
            var shell = Element<FrameworkElement>("Island");
            var shellBounds = shell.TransformToAncestor(root).TransformBounds(new Rect(shell.RenderSize));
            shellBounds.Inflate(.75, .75);
            bool fits = true;
            foreach (var element in new FrameworkElement[] { Presenter("PluginIdleLeft"), Presenter("PluginIdleRight"), Element<FrameworkElement>("BuiltinCompactView") })
            {
                if (!element.IsVisible) continue;
                var bounds = element.TransformToAncestor(root).TransformBounds(new Rect(element.RenderSize));
                fits &= element.ActualWidth > 0 && element.ActualHeight > 0 && shellBounds.Contains(bounds);
                if (element is PluginViewPresenter presenter && presenter.Child is FrameworkElement child)
                {
                    var childBounds = child.TransformToAncestor(root).TransformBounds(new Rect(child.RenderSize));
                    var presenterBounds = bounds; presenterBounds.Inflate(.75, .75);
                    fits &= presenterBounds.Contains(childBounds) && shellBounds.Contains(childBounds);
                }
            }
            Check(fits, name);
        }

        private IslandScheduler Live => (IslandScheduler)Get("_liveScheduler")!;
        private void Bind(string slot, string id) => _settings.PluginViewBindings[slot] = "view:fixture:" + id;
        private void Render() => Invoke("Render");
        private void ChangeView(string name) => Invoke("ChangeView", Enum.Parse(typeof(LandBar.MainWindow).GetNestedType("IslandView", BindingFlags.NonPublic)!, name));
        private PluginViewPresenter Presenter(string name) => Element<PluginViewPresenter>(name);
        private IEnumerable<string> ViewText(string id) => Texts(_manager!.Views.Single(view => view.Id == id).Root);
        private static IEnumerable<string> Texts(PluginNode node) => new[] { node.Text }.Concat(node.Children.SelectMany(Texts));
        private Button Tab(string id) => Descendants<Button>(Element<ItemsControl>("PluginTabItems")).Single(button => button.Tag as string == "view:fixture:" + id);
        private T Element<T>(string name) where T : FrameworkElement => (T)_window.FindName(name);
        private FieldInfo Field(string name) => typeof(LandBar.MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        private object? Get(string name) => Field(name).GetValue(_window);
        private void Set(string name, object? value) => Field(name).SetValue(_window, value);
        private void Invoke(string name, params object?[] args) => InvokeResult(name, args);
        private object? InvokeResult(string name, params object?[] args) => typeof(LandBar.MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
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
        private static Task Settle() => Task.Delay(800);
        private static async Task Until(Func<bool> condition, int timeout = 5000)
        {
            var watch = Stopwatch.StartNew();
            while (!condition())
            {
                if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("Synthetic view integration state timed out.");
                await Task.Delay(25);
            }
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
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
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
            {
                startsSystemSources = false, usesSyntheticChildProcess = true, movesUserPointer = false,
                privateAccountData = false, passed = _passed, failures = _failures
            }, new JsonSerializerOptions { WriteIndented = true }));
            Shutdown(_failures.Count == 0 ? 0 : 1);
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    }
}
