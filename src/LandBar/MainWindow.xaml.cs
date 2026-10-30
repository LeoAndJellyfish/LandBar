using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LandBar.Core;
using LandBar.Services;
using LandBar.Presentation;
using LandBar.Plugins;
using Windows.UI.Notifications.Management;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LandBar;

public partial class MainWindow : Window
{
    private enum IslandView { Automatic, Activity, ControlCenter, Settings, PluginSettings }
    private readonly IslandScheduler _liveScheduler = new();
    private IslandScheduler _scheduler;
    private readonly IslandInactivityTimer _inactivity = new();
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly WindowsNotificationSource _notifications = new();
    private readonly WindowsMediaSource _media = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Dictionary<string, BitmapImage> _images = new();
    private readonly Dictionary<BitmapSource, AlbumPalette> _palettes = new();
    private readonly RectangleGeometry _surfaceClip = new();
    private readonly HashSet<string> _busySessions = [];
    private readonly HashSet<string> _hiddenHistory = [];
    private readonly List<string> _activityOrder = [];
    private readonly bool _demo;
    private readonly bool _smoke;
    private readonly string? _capturePath;
    private IslandMotion? _motion;
    private IslandContentTransition? _contentTransition;
    private TopmostWindowController? _topmost;
    private Forms.NotifyIcon? _tray;
    private Drawing.Icon? _trayIcon;
    private Forms.Screen? _screen;
    private IslandView _view;
    private bool _hover, _closing, _notificationsTab, _exampleMode;
    private int _page, _ticks;
    private TimeSpan _previousTick;
    private Point? _lastPointerPosition;
    private IReadOnlyList<MediaSessionSnapshot> _sessions = [];
    private IReadOnlyList<MediaSessionSnapshot> _examples = [];
    private string? _preferredSessionId;
    private string? _prePreviewPreferredSessionId;
    private string[] _prePreviewHiddenHistory = [];
    private string _renderKey = "";
    private string _pagerKey = "";
    private IslandView? _lastView;
    private bool _lastCompact;
    private bool? _audioAnimating;
    private string _compactState = "";
    private string _sourceStatusKey = "";
    private int _renderedAutoCollapseSeconds = -1;
    private double _hostWidth = 792, _hostHeight = 488;

    public MainWindow(bool demo, bool smoke, bool preview = false, string? capturePath = null)
    {
        _scheduler = _liveScheduler;
        _demo = demo; _smoke = smoke; _capturePath = capturePath;
        InitializeComponent();
        Icon = BitmapFrame.Create(new Uri(ApplicationBrand.IconUri));
        IslandViewport.Clip = _surfaceClip;
        PreviewMouseMove += TrackPointerInteraction;
        PreviewMouseDown += (_, _) => RecordInteraction();
        PreviewMouseUp += (_, _) => RecordInteraction();
        PreviewMouseWheel += (_, _) => RecordInteraction();
        HoverSurface.SizeChanged += (_, _) => UpdateSurfaceClip(Island.Width, Island.Height);
        HoverSurface.MouseLeftButtonUp += (_, args) =>
        {
            if (_view == IslandView.Automatic) { OpenActivity(); args.Handled = true; }
        };
        if (preview) { ShowInTaskbar = true; ShowActivated = true; }
        _screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _settings.ScreenDeviceName);
        PrivacyCheck.IsChecked = _settings.HideContent;
        QuietCheck.IsChecked = _settings.Quiet;
        StartupCheck.IsChecked = AppSettings.StartupEnabled;
        ApplyAutoCollapseSetting();
        _notifications.EventReceived += Receive;
        _media.EventReceived += Receive;
        _media.SessionsChanged += sessions =>
        {
            if (!_stop.IsCancellationRequested) Dispatcher.BeginInvoke(() =>
            {
                if (_sessions.SequenceEqual(sessions)) return;
                _sessions = sessions; Render();
            });
        };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (!preview) SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x08000000L | 0x80L));
            _motion = new IslandMotion(this, Island, PositionFrame);
            _contentTransition = new IslandContentTransition(IslandViewport, ContentLayout, TransitionGhost, _motion);
            _motion.Completed += () =>
            {
                // Layout callbacks never rebind cards. Hover is checked against the final shape.
                _hover = HoverSurface.IsMouseOver;
                _topmost?.RequestCheck();
            };
            PositionWindow();
            _topmost = new TopmostWindowController(this, () => new Rect(Island.TranslatePoint(new Point(), this), Island.RenderSize));
        };
        Loaded += OnLoaded;
        _timer.Tick += OnTick;
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        DpiChanged += (_, args) =>
        {
            if (ReferenceEquals(args.OriginalSource, this)) Dispatcher.BeginInvoke(() =>
            {
                PositionWindow(); UpdateTrayIcon(); _renderKey = ""; Render();
            }, DispatcherPriority.Background);
        };
        Closing += (_, args) => { if (!_closing) { args.Cancel = true; Exit(); } };
        PreviewKeyDown += (_, args) =>
        {
            RecordInteraction();
            if (args.Key == Key.Escape) { Collapse(); args.Handled = true; }
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        CreateTray();
        if (!_demo)
        {
            InitializePlugins();
            _ = WatchSource(() => _media.StartAsync(_stop.Token));
            _ = WatchSource(() => _notifications.StartAsync(_stop.Token));
        }
        _timer.Start();
        if (_demo) ShowDemo();
        Render();
        if (_capturePath is not null)
        {
            var captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
            captureTimer.Tick += (_, _) =>
            {
                captureTimer.Stop();
                var dpi = VisualTreeHelper.GetDpi(this);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                bitmap.Render(HoverSurface);
                var origin = Island.TranslatePoint(new Point(), HoverSurface);
                int left = Math.Max(0, (int)Math.Floor(origin.X * dpi.DpiScaleX));
                int top = Math.Max(0, (int)Math.Floor(origin.Y * dpi.DpiScaleY));
                var visible = new CroppedBitmap(bitmap, new Int32Rect(left, top,
                    Math.Min(bitmap.PixelWidth - left, (int)Math.Ceiling(Island.ActualWidth * dpi.DpiScaleX)),
                    Math.Min(bitmap.PixelHeight - top, (int)Math.Ceiling(Island.ActualHeight * dpi.DpiScaleY))));
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_capturePath))!);
                using var stream = File.Create(_capturePath);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(visible)); encoder.Save(stream);
            };
            captureTimer.Start();
        }
        if (_smoke)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                string? identity = null;
                try { identity = Windows.ApplicationModel.Package.Current.Id.Name; } catch { }
                bool regularResolved = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var regularFace);
                bool semiboldResolved = new Typeface(FontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal).TryGetGlyphTypeface(out var semiboldFace);
                var result = new { packageIdentity = identity, notificationStatus = _notifications.Status, mediaStatus = _media.Status,
                    trayText = _tray?.Text, trayIconSize = _trayIcon?.Size.ToString(), windowIconPresent = Icon is not null,
                    autoCollapseSeconds = _settings.AutoCollapseSeconds, autoCollapseEnabled = _inactivity.Timeout is not null,
                    topmost = Topmost, topmostEventHooks = _topmost?.ActiveHookCount,
                    topmostRepairs = _topmost?.RepairCount,
                    notificationAccess = _notifications.AccessStatus.ToString(), notificationAppFilter = "all-windows-notifications",
                    notificationHistoryCount = _scheduler.History.Count(item => item.Kind == IslandEventKind.Notification),
                    notificationSelfTestReceived = _scheduler.History.Any(item => item.Kind == IslandEventKind.Notification && item.Title == "LandBar 通知测试"),
                    sessionCount = _sessions.Count, playingCount = _sessions.Count(s => s.IsPlaying), timerTicks = _ticks,
                    windowWidth = ActualWidth, windowHeight = ActualHeight, animationCompleted = _motion?.IsAnimating == false,
                    islandWidth = Island.ActualWidth, islandHeight = Island.ActualHeight, fixedNativeHost = true,
                    renderTier = RenderCapability.Tier >> 16, fontFamily = FontFamily.Source,
                    regularFontUri = regularResolved ? regularFace.FontUri.ToString() : null,
                    regularFontWeight = regularResolved ? regularFace.Weight.ToOpenTypeWeight() : 0,
                    semiboldFontWeight = semiboldResolved ? semiboldFace.Weight.ToOpenTypeWeight() : 0,
                    chineseGlyphAvailable = regularResolved && regularFace.CharacterToGlyphMap.ContainsKey(0x4E2D),
                    textFormatting = TextOptions.GetTextFormattingMode(this).ToString(),
                    textRendering = TextOptions.GetTextRenderingMode(this).ToString(),
                    textHinting = TextOptions.GetTextHintingMode(this).ToString(),
                    contentOpacity = ContentLayout.Opacity, contentInteractive = ContentLayout.IsHitTestVisible,
                    contentTransitionCompleted = _contentTransition?.IsTransitioning == false,
                    contentBounds = _contentTransition?.ContentBounds.ToString(),
                    contentSize = ContentLayout.RenderSize.ToString(), viewportSize = IslandViewport.RenderSize.ToString() };
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "smoke-result.json"),
                    System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                Exit();
            };
            timer.Start();
        }
    }

    private static async Task WatchSource(Func<Task> start)
    {
        try { await start(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { AppSettings.WriteDiagnostic(ex.GetType().Name); }
    }

    private void Receive(IslandEvent item)
    {
        if (_stop.IsCancellationRequested) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (_settings.Quiet || !_liveScheduler.Push(item)) return;
            if (_exampleMode) return;
            _inactivity.Reset(_clock.Elapsed);
            Render();
        });
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _plugins?.Expire(DateTimeOffset.UtcNow);
        var now = _clock.Elapsed;
        if (_view == IslandView.Automatic && _scheduler.TotalCount == 0) _inactivity.Stop();
        else if (_inactivity.IsExpired(now)) Collapse();
        _scheduler.SetPaused(_hover || _view != IslandView.Automatic || _settings.Quiet || _contentTransition?.IsTransitioning == true);
        int count = _scheduler.TotalCount, page = _scheduler.CurrentPage;
        _scheduler.Tick(now - _previousTick);
        _previousTick = now;
        string time = DateTime.Now.ToString("HH:mm");
        if (ClockLabel.Text != time) ClockLabel.Text = time;
        if (count != _scheduler.TotalCount || page != _scheduler.CurrentPage ||
            _sourceStatusKey != _notifications.Status + "|" + _media.Status) Render();
        _ticks++;
    }

    private IReadOnlyList<MediaSessionSnapshot> OrderedSessions
    {
        get
        {
            var ordered = MediaSessionSelection.Order(_exampleMode ? _examples : _sessions, _preferredSessionId);
            if (_view != IslandView.Activity) return ordered;
            var byId = ordered.ToDictionary(s => s.SessionId);
            var stable = _activityOrder.Where(byId.ContainsKey).Select(id => byId[id])
                .Concat(ordered.Where(s => !_activityOrder.Contains(s.SessionId))).ToArray();
            _activityOrder.Clear(); _activityOrder.AddRange(stable.Select(s => s.SessionId));
            return stable;
        }
    }

    private IReadOnlyList<IslandEvent> NotificationHistory =>
        _scheduler.History.Where(x => x.Kind == IslandEventKind.Notification && !_hiddenHistory.Contains(x.Id)).Reverse().DistinctBy(x => x.Id).ToArray();

    private void Render()
    {
        if (_closing) return;
        _sourceStatusKey = _notifications.Status + "|" + _media.Status;
        RenderConnectionStatus();
        QuietLabel.Text = _settings.Quiet ? "恢复提示" : "暂停提示";
        PreviewButton.Content = _exampleMode ? "结束预览" : "预览通知与媒体";
        ScreenButton.Content = $"屏幕：{Array.FindIndex(Forms.Screen.AllScreens, s => s.DeviceName == GetScreen().DeviceName) + 1}";
        OffsetButton.Content = $"顶部间距：{_settings.TopOffset}";
        RenderAutoCollapseOptions();
        RenderPluginManagement();
        var sessions = OrderedSessions;
        var history = NotificationHistory;
        bool automaticCards = _view == IslandView.Automatic && !_settings.Quiet && _scheduler.TotalCount > 0;
        bool compact = _view == IslandView.Automatic && !automaticCards;
        bool activity = _view == IslandView.Activity || automaticCards;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double availableWidth = Math.Max(160, GetScreen().WorkingArea.Width / dpi - 24);
        double availableHeight = Math.Max(160, GetScreen().WorkingArea.Height / dpi - _settings.TopOffset - 12);
        var cards = new List<CardView>();
        int pageCount = 1;
        if (activity)
        {
            if (automaticCards)
            {
                cards.AddRange(_scheduler.VisibleItems.Select(item => FromEvent(item, sessions)));
                pageCount = Math.Max(1, _scheduler.PageCount);
            }
            else if (_pluginsTab)
            {
                var pluginCards = CurrentPluginCards;
                pageCount = Math.Max(1, (pluginCards.Count + 1) / 2);
                _page = Math.Clamp(_page, 0, pageCount - 1);
                cards.AddRange(pluginCards.Skip(_page * 2).Take(2).Select(FromPlugin));
            }
            else if (!_notificationsTab)
            {
                pageCount = Math.Max(1, (sessions.Count + 1) / 2);
                _page = Math.Clamp(_page, 0, pageCount - 1);
                cards.AddRange(sessions.Skip(_page * 2).Take(2).Select(FromMedia));
            }
            else
            {
                pageCount = Math.Max(1, (history.Count + 1) / 2);
                _page = Math.Clamp(_page, 0, pageCount - 1);
                cards.AddRange(history.Skip(_page * 2).Take(2).Select(item => FromEvent(item, sessions)));
            }
        }
        double compactWidth = sessions.Count > 1 ? 208 : sessions.Count > 0 ? 180 : 112;
        double width = Math.Min(availableWidth, compact ? compactWidth : _view == IslandView.ControlCenter ? 396 :
            _view is IslandView.Settings or IslandView.PluginSettings ? 448 : cards.Count > 1 ? 792 : 464);
        double height = compact ? 44 : _view == IslandView.ControlCenter ? 300 :
            _view == IslandView.Settings ? 488 : _view == IslandView.PluginSettings ? Math.Min(488, 320 + (_plugins?.Plugins.Count ?? 0) * 76) : cards.Count == 0 ? 168 :
            cards.Any(c => c.IsPlugin && (c.PluginActions.Count > 0 || c.ProgressVisibility == Visibility.Visible)) ? 280 :
            cards.Any(c => c.IsMedia) ? (cards.Count > 1 ? 280 : 288) : 208;
        if (activity && pageCount > 1) height += 28;
        height = Math.Min(height, availableHeight);
        foreach (var (card, index) in cards.Select((value, index) => (value, index)))
        {
            card.CardWidth = (width - 14) / Math.Max(1, cards.Count);
            card.Divider = new Thickness(index > 0 ? 1 : 0, 0, 0, 0);
            card.CoverSize = card.IsMedia ? cards.Count > 1 ? 84 : 92 : 60;
            card.TitleSize = cards.Count > 1 ? 20 : 22;
        }
        bool viewChanged = _lastView != _view || _lastCompact != compact;
        bool shapeChanged = _contentTransition is null || Math.Abs(_contentTransition.TargetWidth - width) > 0.5 ||
            Math.Abs(_contentTransition.TargetHeight - height) > 0.5;
        var targetView = _view;
        bool showNotifications = _notificationsTab;
        bool showPlugins = _pluginsTab;
        string key = $"{targetView}:{showNotifications}:{showPlugins}:{width}:{_settings.HideContent}:" + string.Join("|", cards.Select(c => c.Key));
        int page = automaticCards ? Math.Max(0, _scheduler.CurrentPage - 1) : _page;
        string pagerKey = $"{pageCount}:{page}";
        void ApplyPresentation()
        {
            SetVisible(CompactView, compact);
            SetVisible(ActivityView, activity);
            SetVisible(ControlCenterView, targetView == IslandView.ControlCenter);
            SetVisible(SettingsScroll, targetView == IslandView.Settings);
            SetVisible(PluginManagementScroll, targetView == IslandView.PluginSettings);
            SetVisible(ActivityTabs, targetView == IslandView.Activity);
            SetVisible(ClearNotificationsButton, targetView == IslandView.Activity && showNotifications);
            ClearNotificationsButton.IsEnabled = history.Count > 0;
            SetVisible(EmptyState, activity && cards.Count == 0);
            EmptyState.Text = showPlugins ? "暂无插件活动" : showNotifications ? "暂无通知" : "暂无媒体";
            MediaTab.Foreground = Brush(showNotifications || showPlugins ? "#83838F" : "#F2F2F4");
            NotificationsTab.Foreground = Brush(showNotifications ? "#F2F2F4" : "#83838F");
            MediaTab.Background = Brush(showNotifications || showPlugins ? "#08080A" : "#24242B");
            NotificationsTab.Background = Brush(showNotifications ? "#24242B" : "#08080A");
            MediaTab.Content = sessions.Count > 1 ? $"媒体 {sessions.Count}" : "媒体";
            PluginsTab.Foreground = Brush(showPlugins ? "#F2F2F4" : "#83838F");
            PluginsTab.Background = Brush(showPlugins ? "#24242B" : "#08080A");
            SetVisible(PageControls, pageCount > 1);
            if (_renderKey != key)
            {
                _renderKey = key;
                EventItems.ItemsSource = cards;
            }
            if (_pagerKey != pagerKey)
            {
                _pagerKey = pagerKey;
                PageDots.ItemsSource = Enumerable.Range(0, pageCount).Select(i => Brush(i == page ? "#D5D5DC" : "#41414B")).ToArray();
            }
            RenderCompact(sessions);
            var scale = VisualTreeHelper.GetDpi(this);
            var area = GetScreen().WorkingArea;
            var target = NativeWindowBounds.Calculate(area.Left, area.Top, area.Width, _settings.TopOffset,
                width, height, scale.DpiScaleX, scale.DpiScaleY);
            var border = Island.BorderThickness;
            ContentLayout.Width = Math.Max(1, (target.Width - Math.Round(border.Left * scale.DpiScaleX) -
                Math.Round(border.Right * scale.DpiScaleX)) / scale.DpiScaleX);
            ContentLayout.Height = Math.Max(1, (target.Height - Math.Round(border.Top * scale.DpiScaleY) -
                Math.Round(border.Bottom * scale.DpiScaleY)) / scale.DpiScaleY);
        }
        if (shapeChanged || viewChanged)
        {
            _contentTransition?.Present(width, height, ApplyPresentation, forceTransition: viewChanged);
            _lastView = _view;
            _lastCompact = compact;
        }
        else _contentTransition?.Refresh(ApplyPresentation);
    }

    private void RenderCompact(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        var primary = sessions.FirstOrDefault();
        bool playing = sessions.Any(s => s.IsPlaying);
        SetVisible(CompactCovers, primary is not null);
        SetVisible(SecondCover, sessions.Count > 1);
        SetVisible(AudioBars, sessions.Count > 0);
        Grid.SetColumn(ClockLabel, primary is null ? 0 : 2);
        Grid.SetColumnSpan(ClockLabel, primary is null ? 3 : 1);
        ClockLabel.FontSize = primary is null ? 16 : 14;
        if (ClockLabel.Text.Length == 0) ClockLabel.Text = DateTime.Now.ToString("HH:mm");
        AudioBars.Opacity = _settings.Quiet ? 0.3 : playing ? 1 : 0.35;
        CompactGlyph.Text = primary is null ? "" : "\uE8D6";
        string artwork = string.Join("|", sessions.Take(2).Select(s => s.SessionId + s.ArtworkPath));
        if (_compactState != artwork)
        {
            _compactState = artwork;
            var image = primary is null ? null : ArtworkFor(primary);
            CompactArtwork.Source = image;
            SecondaryArtwork.Source = sessions.Count > 1 ? ArtworkFor(sessions[1]) : null;
            var accent = FrozenBrush(GetPalette(image).Accent);
            AudioBar1.Background = AudioBar2.Background = AudioBar3.Background = accent;
        }
        bool animate = playing && CompactView.Visibility == Visibility.Visible && SystemParameters.ClientAreaAnimation && !_settings.Quiet;
        if (_audioAnimating != animate)
        {
            _audioAnimating = animate;
            var bars = new[] { AudioBar1, AudioBar2, AudioBar3 };
            for (int i = 0; i < bars.Length; i++)
            {
                var transform = (ScaleTransform)bars[i].RenderTransform;
                transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                transform.ScaleY = playing ? 0.55 : 0.2;
                if (animate) transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1, TimeSpan.FromMilliseconds(410 + i * 115))
                    { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromMilliseconds(i * 130), EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            }
        }
    }

    private BitmapSource? ArtworkFor(MediaSessionSnapshot session) => session.SessionId.StartsWith("demo.")
        ? DemoArtwork.Get(session.SessionId == "demo.music" ? 0 : session.SessionId == "demo.second" ? 1 : 2) : LoadArtwork(session.ArtworkPath);
    private AlbumPalette GetPalette(BitmapSource? source)
    {
        if (source is null) return CoverPalette.Neutral;
        if (_palettes.TryGetValue(source, out var palette)) return palette;
        if (_palettes.Count >= 48) _palettes.Clear();
        return _palettes[source] = CoverPalette.FromBitmap(source);
    }
    private CardView FromMedia(MediaSessionSnapshot session)
    {
        var artwork = ArtworkFor(session);
        return new(session, artwork, GetPalette(artwork), _busySessions.Contains(session.SessionId));
    }
    private CardView FromEvent(IslandEvent item, IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        if (item.Kind == IslandEventKind.Plugin && FindPluginCard(item.Id) is { } plugin) return FromPlugin(plugin);
        var session = item.Kind == IslandEventKind.Media ? sessions.FirstOrDefault(s => s.SessionId == item.AppId) : null;
        return session is not null ? FromMedia(session) : new(item, _settings.HideContent, LoadArtwork(item.ArtworkPath));
    }

    private BitmapImage? LoadArtwork(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
        if (_images.TryGetValue(path, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit(); image.DecodePixelWidth = 124; image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
            if (_images.Count >= 48) _images.Clear();
            _images[path] = image; return image;
        }
        catch { return null; }
    }

    private void ChangeView(IslandView view)
    {
        _view = view; _page = 0;
        if (view == IslandView.Automatic) _inactivity.Stop();
        else _inactivity.Reset(_clock.Elapsed);
        if (!_demo)
        {
            _settings.OnboardingComplete = true;
            _settings.Save();
        }
        Render();
    }
    private void OpenActivity()
    {
        bool openingPlugin = _view == IslandView.Automatic && _scheduler.VisibleItems.FirstOrDefault()?.Kind == IslandEventKind.Plugin;
        _activityOrder.Clear();
        _notificationsTab = !openingPlugin && OrderedSessions.Count == 0 && NotificationHistory.Count > 0;
        _pluginsTab = openingPlugin || OrderedSessions.Count == 0 && !_notificationsTab && CurrentPluginCards.Count > 0;
        ChangeView(IslandView.Activity);
    }
    private void Collapse() { _scheduler.Clear(); ChangeView(IslandView.Automatic); }
    private void RecordInteraction()
    {
        if (_view != IslandView.Automatic || _scheduler.TotalCount > 0)
            _inactivity.Reset(_clock.Elapsed);
    }
    private void TrackPointerInteraction(object sender, System.Windows.Input.MouseEventArgs e)
    {
        // The fixed host keeps coordinates stable while the shell grows under a stationary pointer.
        RecordPointerInteraction(e.GetPosition(HoverSurface));
    }
    private void RecordPointerInteraction(Point position)
    {
        bool moved = _lastPointerPosition is not { } previous || position != previous;
        _lastPointerPosition = position;
        if (moved && _surfaceClip.FillContains(position)) RecordInteraction();
    }
    private void ToggleActivity_Click(object sender, MouseButtonEventArgs e) { OpenActivity(); e.Handled = true; }
    private void ActivitySurface_Click(object sender, MouseButtonEventArgs e) { if (_view == IslandView.Automatic) OpenActivity(); }
    private void OpenActivity_Click(object sender, RoutedEventArgs e) { OpenActivity(); e.Handled = true; }
    private void ControlCenter_Click(object sender, RoutedEventArgs e) { ChangeView(IslandView.ControlCenter); e.Handled = true; }
    private void ControlCenter_RightClick(object sender, MouseButtonEventArgs e) { ChangeView(IslandView.ControlCenter); e.Handled = true; }
    private void Settings_Click(object sender, RoutedEventArgs e) { ChangeView(IslandView.Settings); e.Handled = true; }
    private void Collapse_Click(object sender, RoutedEventArgs e) { Collapse(); e.Handled = true; }
    private void MediaTab_Click(object sender, RoutedEventArgs e) { _pluginsTab = false; _notificationsTab = false; _page = 0; Render(); e.Handled = true; }
    private void NotificationsTab_Click(object sender, RoutedEventArgs e) { _pluginsTab = false; _notificationsTab = true; _page = 0; Render(); e.Handled = true; }
    private void ClearNotifications_Click(object sender, RoutedEventArgs e)
    {
        _scheduler.ClearNotifications();
        _hiddenHistory.Clear(); _page = 0;
        RecordInteraction(); Render();
        e.Handled = true;
    }
    private void Island_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) { _hover = true; _scheduler.SetPaused(true); }
    private void Island_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) { _hover = false; }
    private void Island_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_view is IslandView.Settings or IslandView.ControlCenter or IslandView.PluginSettings) return;
        Navigate(e.Delta > 0 ? -1 : 1); e.Handled = true;
    }
    private void Navigate(int direction)
    {
        if (_view == IslandView.Automatic) { if (direction > 0) _scheduler.NextPage(); else _scheduler.PreviousPage(); }
        else
        {
            int count = _pluginsTab ? CurrentPluginCards.Count : _notificationsTab ? NotificationHistory.Count : OrderedSessions.Count;
            int pages = Math.Max(1, (count + 1) / 2);
            _page = (_page + direction + pages) % pages;
        }
        Render();
    }
    private void NextPage_Click(object sender, RoutedEventArgs e) { Navigate(1); e.Handled = true; }
    private void PreviousPage_Click(object sender, RoutedEventArgs e) { Navigate(-1); e.Handled = true; }
    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string id)
        {
            _scheduler.Dismiss(id); _hiddenHistory.Add(id);
            if (_hiddenHistory.Count > 128) _hiddenHistory.IntersectWith(_scheduler.History.Select(item => item.Id));
        }
        Render(); e.Handled = true;
    }
    private void Quiet_Click(object sender, RoutedEventArgs e)
    {
        _settings.Quiet = !_settings.Quiet; QuietCheck.IsChecked = _settings.Quiet;
        _scheduler.Clear(); _settings.Save(); Render(); e.Handled = true;
    }

    private void Demo_Click(object sender, RoutedEventArgs e)
    {
        if (_exampleMode) EndDemo();
        else ShowDemo();
        e.Handled = true;
    }
    private void EndDemo()
    {
        _exampleMode = false;
        _scheduler = _liveScheduler;
        _examples = [];
        _hiddenHistory.Clear();
        _hiddenHistory.UnionWith(_prePreviewHiddenHistory);
        _prePreviewHiddenHistory = [];
        _preferredSessionId = _prePreviewPreferredSessionId;
        _prePreviewPreferredSessionId = null;
        OpenActivity();
    }
    private void ShowDemo()
    {
        if (!_exampleMode)
        {
            _prePreviewPreferredSessionId = _preferredSessionId;
            _prePreviewHiddenHistory = _hiddenHistory.ToArray();
        }
        _exampleMode = true;
        // Preview history, dismissal state and duplicate IDs never enter the live scheduler.
        _scheduler = new IslandScheduler { ExpireVisibleItems = _settings.AutoCollapseSeconds != 0 };
        _hiddenHistory.Clear(); _activityOrder.Clear(); _preferredSessionId = null; _pluginsTab = false;
        _examples = new[]
        {
            new MediaSessionSnapshot("demo.music", "demo.music", "音乐 · 示例", "Clair de lune", "Claude Debussy", null, true, false, true, true, true),
            new MediaSessionSnapshot("demo.browser", "demo.browser", "浏览器 · 示例", "A quiet afternoon", "Studio sessions", null, false, true, true, true, true),
            new MediaSessionSnapshot("demo.second", "demo.second", "另一个播放器 · 示例", "Bloom", "The Paper Kites", null, true, false, true, true, true)
        };
        var now = DateTimeOffset.Now;
        _scheduler.Push(new($"demo-{Guid.NewGuid()}", IslandEventKind.Notification, "微信 · 示例", "设计讨论组", "新的界面方案已发出，方便时看一下。", now, "demo.wechat"));
        _scheduler.Push(new($"demo-{Guid.NewGuid()}", IslandEventKind.Notification, "QQ · 示例", "文件助手", "课程资料已同步至电脑。", now, "demo.qq"));
        _notificationsTab = false; ChangeView(IslandView.Activity);
    }

    private async void PreviousTrack_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ControlMedia(sender, "previous"); }
    private async void NextTrack_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ControlMedia(sender, "next"); }
    private async void PlayPause_Click(object sender, RoutedEventArgs e) { e.Handled = true; await ControlMedia(sender, "toggle"); }
    private async Task ControlMedia(object sender, string action)
    {
        var id = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(id) || !_busySessions.Add(id)) return;
        try
        {
            if (id.StartsWith("demo."))
            {
                _examples = _examples.Select(s => s.SessionId == id && action == "toggle" ? s with { IsPlaying = !s.IsPlaying } : s).ToArray();
            }
            else
            {
                Render();
                bool success = action switch { "previous" => await _media.PreviousAsync(id), "next" => await _media.NextAsync(id), _ => await _media.TogglePlayPauseAsync(id) };
                if (!success) AppSettings.WriteDiagnostic("MediaControlUnavailable");
            }
        }
        catch (Exception ex) { AppSettings.WriteDiagnostic(ex.GetType().Name); }
        finally { _busySessions.Remove(id); Render(); }
    }

    private async void RequestAccess_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_notifications.AccessStatus == UserNotificationListenerAccessStatus.Denied)
        {
            try { Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true }); }
            catch (Exception ex) { AppSettings.WriteDiagnostic(ex.GetType().Name); }
            return;
        }
        PermissionButton.IsEnabled = false;
        try
        {
            await _notifications.RequestAccessAsync();
        }
        catch (Exception ex) { AppSettings.WriteDiagnostic(ex.GetType().Name); }
        finally { PermissionButton.IsEnabled = true; Render(); }
    }

    private void RenderConnectionStatus()
    {
        string notification = "未授权", hint = "";
        string action = "授权";
        bool granted = false, warning = false, needsAction = true;
        if (_exampleMode)
        {
            notification = "示例"; needsAction = false;
        }
        else if (_notifications.AccessStatus == UserNotificationListenerAccessStatus.Allowed)
        {
            notification = "已授权"; granted = true; needsAction = false;
        }
        else if (_notifications.AccessStatus == UserNotificationListenerAccessStatus.Denied)
        {
            notification = "已关闭"; warning = true; action = "打开设置";
            hint = "在 Windows 设置中允许通知访问";
        }
        if (!_exampleMode && _notifications.ErrorDetail is { } error)
        {
            notification = "暂不可用"; warning = true; hint = error;
        }
        NotificationStatus.Text = notification;
        NotificationStatus.Foreground = Brush(warning ? "#D7BE96" : granted ? "#B4D1BB" : "#B8B8C4");
        NotificationBadge.Background = Brush(warning ? "#30291F" : granted ? "#202C25" : "#25252D");
        NotificationHint.Text = hint;
        SetVisible(NotificationHint, hint.Length > 0);
        PermissionButton.Content = action;
        SetVisible(PermissionButton, needsAction);

        bool mediaConnected = _media.Status.StartsWith("已连接", StringComparison.Ordinal);
        MediaStatus.Text = _exampleMode ? "示例" : mediaConnected ? (_sessions.Count > 0 ? "已连接" : "待播放") : "未连接";
        MediaStatus.Foreground = Brush(mediaConnected ? "#B4D1BB" : "#B8B8C4");
        MediaBadge.Background = Brush(mediaConnected ? "#202C25" : "#25252D");
    }
    private void Settings_Changed(object sender, RoutedEventArgs e)
    {
        _settings.HideContent = PrivacyCheck.IsChecked == true;
        _settings.Quiet = QuietCheck.IsChecked == true;
        if (_settings.Quiet) _scheduler.Clear();
        _settings.Save(); Render();
    }
    private void AutoCollapse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string value && int.TryParse(value, out int seconds) && seconds is >= 0 and <= 5)
        {
            _settings.AutoCollapseSeconds = seconds;
            ApplyAutoCollapseSetting();
            if (!_demo) _settings.Save();
            Render();
        }
        e.Handled = true;
    }
    private void ApplyAutoCollapseSetting()
    {
        int seconds = _settings.AutoCollapseSeconds;
        _inactivity.Configure(seconds == 0 ? null : TimeSpan.FromSeconds(seconds), _clock.Elapsed);
        _liveScheduler.ExpireVisibleItems = _scheduler.ExpireVisibleItems = seconds != 0;
        if (_view == IslandView.Automatic && _scheduler.TotalCount == 0) _inactivity.Stop();
        RenderAutoCollapseOptions();
    }
    private void RenderAutoCollapseOptions()
    {
        if (_renderedAutoCollapseSeconds == _settings.AutoCollapseSeconds) return;
        _renderedAutoCollapseSeconds = _settings.AutoCollapseSeconds;
        foreach (Button button in AutoCollapseOptions.Children)
        {
            bool selected = (string)button.Tag == _settings.AutoCollapseSeconds.ToString();
            button.Background = Brush(selected ? "#34343D" : "#141418");
            button.Foreground = Brush(selected ? "#F2F2F4" : "#9D9DAA");
            System.Windows.Automation.AutomationProperties.SetItemStatus(button, selected ? "已选择" : "");
        }
    }
    private void Startup_Changed(object sender, RoutedEventArgs e)
    {
        try { AppSettings.SetStartup(StartupCheck.IsChecked == true); }
        catch { StartupCheck.IsChecked = AppSettings.StartupEnabled; StatusDetail.Text = "开机启动设置失败"; StatusDetail.Visibility = Visibility.Visible; }
    }
    private void Screen_Click(object sender, RoutedEventArgs e)
    {
        var screens = Forms.Screen.AllScreens;
        int index = Array.FindIndex(screens, s => s.DeviceName == GetScreen().DeviceName);
        _screen = screens[(index + 1) % screens.Length];
        _settings.PrimaryScreen = false; _settings.ScreenDeviceName = _screen.DeviceName;
        _settings.Save(); Render(); PositionWindow();
    }
    private void Offset_Click(object sender, RoutedEventArgs e)
    {
        _settings.TopOffset = _settings.TopOffset switch { 12 => 0, 0 => 24, _ => 12 };
        _settings.Save(); Render(); PositionWindow();
    }
    private Forms.Screen GetScreen() => _settings.PrimaryScreen ? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0] : _screen ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
    private void PositionWindow()
    {
        if (new WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        var area = GetScreen().WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = Math.Min(792, Math.Max(160, area.Width / dpi.DpiScaleX - 24));
        double height = Math.Min(488, Math.Max(160, area.Height / dpi.DpiScaleY - _settings.TopOffset - 12));
        var bounds = NativeWindowBounds.Apply(this, area.Left, area.Top, area.Width, _settings.TopOffset,
            width, height, dpi.DpiScaleX, dpi.DpiScaleY);
        _hostWidth = bounds.Width / dpi.DpiScaleX; _hostHeight = bounds.Height / dpi.DpiScaleY;
        ContentLayout.Margin = new Thickness(0, Math.Round(Island.BorderThickness.Top * dpi.DpiScaleY) / dpi.DpiScaleY, 0, 0);
        UpdateSurfaceClip(Island.Width, Island.Height);
    }
    private void PositionFrame(double width, double height)
    {
        Island.Width = width; Island.Height = height;
        Island.CornerRadius = new CornerRadius(Math.Clamp(22 + (height - 44) * 0.05, 22, 32));
        UpdateSurfaceClip(width, height);
    }
    private void UpdateSurfaceClip(double width, double height)
    {
        double hostWidth = HoverSurface.ActualWidth > 0 ? HoverSurface.ActualWidth : _hostWidth;
        var border = Island.BorderThickness;
        double radius = Math.Max(0, Island.CornerRadius.TopLeft - Math.Max(border.Left, border.Top));
        _surfaceClip.Rect = new Rect((hostWidth - width) / 2 + border.Left, border.Top,
            Math.Max(0, width - border.Left - border.Right), Math.Max(0, height - border.Top - border.Bottom));
        _surfaceClip.RadiusX = radius; _surfaceClip.RadiusY = radius;
    }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() =>
    {
        if (_screen is not null) _screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _screen.DeviceName);
        _renderKey = ""; Render(); PositionWindow(); UpdateTrayIcon();
    });

    private void CreateTray()
    {
        _tray = new Forms.NotifyIcon { Text = ApplicationBrand.Name };
        UpdateTrayIcon();
        _tray.Visible = true;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("控制中心", null, (_, _) => Dispatcher.Invoke(() => ChangeView(IslandView.ControlCenter)));
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(Exit));
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(() => ChangeView(IslandView.ControlCenter)); };
    }
    private void UpdateTrayIcon()
    {
        if (_tray is null) return;
        var size = Forms.SystemInformation.SmallIconSize;
        if (_trayIcon?.Size == size) return;
        var icon = ApplicationBrand.CreateTrayIcon(size);
        var previous = _trayIcon;
        _tray.Icon = icon;
        _trayIcon = icon;
        previous?.Dispose();
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Exit();
    private void Exit()
    {
        if (_closing) return;
        _closing = true; _timer.Stop(); _stop.Cancel(); _topmost?.Dispose(); _contentTransition?.Dispose(); _motion?.Dispose();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
        _plugins?.Dispose();
        _notifications.Dispose(); _media.Dispose(); _tray?.ContextMenuStrip?.Dispose(); _tray?.Dispose(); _trayIcon?.Dispose();
        System.Windows.Application.Current.Shutdown();
    }
    private static void SetVisible(UIElement element, bool visible) => element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    private static SolidColorBrush Brush(string hex) { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); return brush; }
    private static SolidColorBrush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private sealed partial class CardView
    {
        public string Id { get; } = "";
        public string SessionId { get; } = "";
        public double CardWidth { get; set; }
        public double SourceWidth => Math.Max(50, CardWidth - 58 - (IsMedia || IsPlugin ? 0 : 32));
        public Thickness Divider { get; set; }
        public double CoverSize { get; set; } = 92;
        public double TitleSize { get; set; } = 22;
        public Geometry CoverClip => new RectangleGeometry(new Rect(0, 0, CoverSize, CoverSize), 17, 17);
        public string Source { get; }
        public string Title { get; }
        public string Body { get; }
        public string Glyph { get; }
        public bool IsMedia { get; }
        public bool CanTogglePlayPause { get; }
        public bool CanGoNext { get; }
        public bool CanGoPrevious { get; }
        public string PlayPauseLabel { get; } = "播放";
        public Geometry PlayPauseGeometry { get; } = Geometry.Parse("M 7,4 L 20,12 L 7,20 Z");
        public SolidColorBrush StatusColor { get; }
        public SolidColorBrush Accent { get; }
        public SolidColorBrush AccentBackground { get; }
        public Visibility MediaVisibility => IsMedia ? Visibility.Visible : Visibility.Collapsed;
        public Visibility DismissVisibility => IsMedia || IsPlugin ? Visibility.Collapsed : Visibility.Visible;
        public BitmapSource? Artwork { get; }
        public Brush AmbientBrush { get; }
        public string Key => $"{Id}:{SessionId}:{Title}:{Body}:{Artwork?.GetHashCode()}:{PlayPauseLabel}:{CanTogglePlayPause}:{CanGoNext}:{CanGoPrevious}:{PluginKey}";
        public CardView(MediaSessionSnapshot session, BitmapSource? image, AlbumPalette palette, bool busy)
        {
            IsMedia = true; SessionId = session.SessionId; Source = session.AppName;
            Title = session.Title; Body = session.Artist; Glyph = "\uE8D6"; Artwork = image;
            CanTogglePlayPause = session.CanTogglePlayPause && !busy;
            CanGoNext = session.CanGoNext && !busy; CanGoPrevious = session.CanGoPrevious && !busy;
            PlayPauseLabel = session.IsPlaying ? "暂停" : "播放";
            PlayPauseGeometry = Geometry.Parse(session.IsPlaying ? "M 6,4 L 10,4 L 10,20 L 6,20 Z M 14,4 L 18,4 L 18,20 L 14,20 Z" : "M 7,4 L 20,12 L 7,20 Z");
            StatusColor = session.IsPlaying ? FrozenBrush(palette.Accent) : Brush("#70707D");
            Accent = FrozenBrush(palette.Foreground); AccentBackground = FrozenBrush(palette.Surface);
            var ambient = new RadialGradientBrush
            {
                GradientOrigin = new System.Windows.Point(0.05, 0.2), Center = new System.Windows.Point(0.05, 0.2), RadiusX = 1.1, RadiusY = 1.1,
                GradientStops = new GradientStopCollection { new GradientStop(Color.FromArgb(185, palette.Surface.R, palette.Surface.G, palette.Surface.B), 0), new GradientStop(Color.FromArgb(0, palette.Surface.R, palette.Surface.G, palette.Surface.B), 1) }
            };
            ambient.Freeze(); AmbientBrush = ambient;
        }
        public CardView(IslandEvent item, bool privacy, BitmapImage? image)
        {
            Id = item.Id; Source = item.AppName; Title = privacy ? "收到新消息" : item.Title; Body = privacy ? "内容已隐藏" : item.Body;
            Glyph = "\uE8F2"; Artwork = image; StatusColor = Brush("#9CACBD"); Accent = Brush("#B8C9DE"); AccentBackground = Brush("#1D2530");
            AmbientBrush = Brush("#0D1015");
        }
    }
}
