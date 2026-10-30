using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using LandBar.Core;
using Windows.Media.Control;

namespace LandBar.Services;

/// <summary>Keeps native media sessions independently of short-lived song notifications.</summary>
public sealed class WindowsMediaSource : IDisposable
{
    private sealed class SessionHandle(string id, long identity, GlobalSystemMediaTransportControlsSession session)
    {
        public string Id { get; } = id;
        public long Identity { get; } = identity;
        public GlobalSystemMediaTransportControlsSession Session { get; set; } = session;
        public string? ObservedTrack { get; set; }
        public bool HasEmittedPlayingTrack { get; set; }
        public MediaSessionSnapshot? LastSnapshot { get; set; }
    }

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0, 1);
    private readonly object _sessionsLock = new();
    private readonly Dictionary<long, SessionHandle> _handles = new();
    private IReadOnlyList<MediaSessionSnapshot> _snapshot = Array.Empty<MediaSessionSnapshot>();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private long _nextSessionId;
    private int _running;
    private int _disposed;

    public event Action<IslandEvent>? EventReceived;
    public event Action<string>? StatusChanged;
    public event Action<IReadOnlyList<MediaSessionSnapshot>>? SessionsChanged;
    public string Status { get; private set; } = "尚未连接";
    public IReadOnlyList<MediaSessionSnapshot> Sessions => Volatile.Read(ref _snapshot);
    public bool IsPlaying => Sessions.Any(session => session.IsPlaying);
    public string? ActiveAppId => Sessions.FirstOrDefault(session => session.IsCurrent)?.AppId;
    public string? ActiveSessionId => Sessions.FirstOrDefault(session => session.IsCurrent)?.SessionId;

    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0 || Volatile.Read(ref _disposed) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var retryDelay = TimeSpan.FromSeconds(1);
                try
                {
                    if (_manager is null)
                    {
                        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync()
                            .AsTask(token).WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                        _manager.SessionsChanged += ManagerSessionsChanged;
                        _manager.CurrentSessionChanged += ManagerCurrentSessionChanged;
                    }
                    await RefreshAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    ResetManager();
                    Publish(Array.Empty<MediaSessionSnapshot>());
                    SetStatus($"媒体暂不可用 · 0x{exception.HResult:X8}");
                    retryDelay = TimeSpan.FromSeconds(5);
                }
                await _wake.WaitAsync(retryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            ResetManager();
            Publish(Array.Empty<MediaSessionSnapshot>());
            Interlocked.Exchange(ref _running, 0);
        }
    }

    private async Task RefreshAsync(CancellationToken token)
    {
        await _refreshGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var manager = _manager;
            if (manager is null) return;
            var nativeSessions = manager.GetSessions();
            var current = manager.GetCurrentSession();
            long? currentIdentity = current is null ? null : NativeIdentity(current);
            var seen = new HashSet<long>();
            var snapshots = new List<MediaSessionSnapshot>();
            foreach (var session in nativeSessions)
            {
                token.ThrowIfCancellationRequested();
                SessionHandle? handle = null;
                try
                {
                    var identity = NativeIdentity(session);
                    seen.Add(identity);
                    lock (_sessionsLock)
                    {
                        if (!_handles.TryGetValue(identity, out handle))
                        {
                            handle = new SessionHandle($"media-session-{++_nextSessionId}", identity, session);
                            _handles.Add(identity, handle);
                            session.PlaybackInfoChanged += PlaybackInfoChanged;
                            session.MediaPropertiesChanged += MediaPropertiesChanged;
                        }
                        handle.Session = session;
                    }
                    var properties = await session.TryGetMediaPropertiesAsync()
                        .AsTask(token).WaitAsync(TimeSpan.FromSeconds(4), token).ConfigureAwait(false);
                    var title = properties.Title?.Trim();
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    var appId = session.SourceAppUserModelId;
                    var artist = properties.Artist?.Trim() ?? "";
                    var album = properties.AlbumTitle?.Trim() ?? "";
                    var trackKey = $"{title}\n{artist}\n{album}";
                    var playback = session.GetPlaybackInfo();
                    var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    var controls = playback.Controls;
                    bool trackChanged = handle.ObservedTrack != trackKey;
                    if (trackChanged)
                    {
                        handle.ObservedTrack = trackKey;
                        handle.HasEmittedPlayingTrack = false;
                    }
                    string? artwork = !trackChanged ? handle.LastSnapshot?.ArtworkPath : null;
                    if (artwork is null && properties.Thumbnail is not null)
                    {
                        try
                        {
                            artwork = await WinRtArtworkCache.SaveAsync(properties.Thumbnail,
                                $"{appId}\n{trackKey}", token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                        catch (Exception exception) { TraceFailure("artwork", exception); }
                    }
                    var snapshot = new MediaSessionSnapshot(handle.Id, appId, FriendlyAppName(appId), title,
                        artist, artwork, playing, identity == currentIdentity,
                        controls.IsPlayPauseToggleEnabled || (playing ? controls.IsPauseEnabled : controls.IsPlayEnabled),
                        controls.IsNextEnabled, controls.IsPreviousEnabled);
                    handle.LastSnapshot = snapshot;
                    snapshots.Add(snapshot);
                    // A resume updates the live snapshot. A song not previously played still alerts once.
                    if (playing && !handle.HasEmittedPlayingTrack)
                    {
                        handle.HasEmittedPlayingTrack = true;
                        Emit(new IslandEvent($"media:{handle.Id}:{Guid.NewGuid():N}", IslandEventKind.Media,
                            snapshot.AppName, title, string.IsNullOrEmpty(artist) ? "正在播放" : artist,
                            DateTimeOffset.Now, handle.Id, artwork));
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    TraceFailure("session", exception);
                    if (handle?.LastSnapshot is { } previous)
                        snapshots.Add(previous with { IsCurrent = handle.Identity == currentIdentity });
                }
            }
            lock (_sessionsLock)
            {
                foreach (var stale in _handles.Keys.Where(identity => !seen.Contains(identity)).ToArray())
                {
                    Unsubscribe(_handles[stale].Session);
                    _handles.Remove(stale);
                }
            }
            token.ThrowIfCancellationRequested();
            Publish(snapshots.OrderBy(snapshot => snapshot.SessionId, StringComparer.Ordinal).ToArray());
            SetStatus(snapshots.Count == 0 ? "已连接 · 等待音乐播放" : "已连接 · 系统媒体");
        }
        finally { _refreshGate.Release(); }
    }

    public Task<bool> TogglePlayPauseAsync(string? sessionId = null) =>
        ControlAsync(sessionId, session =>
        {
            var info = session.GetPlaybackInfo();
            if (info.Controls.IsPlayPauseToggleEnabled) return session.TryTogglePlayPauseAsync().AsTask();
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            if (playing && info.Controls.IsPauseEnabled) return session.TryPauseAsync().AsTask();
            if (!playing && info.Controls.IsPlayEnabled) return session.TryPlayAsync().AsTask();
            return Task.FromResult(false);
        });

    public Task<bool> NextAsync(string? sessionId = null) => ControlAsync(sessionId, session =>
        session.GetPlaybackInfo().Controls.IsNextEnabled ? session.TrySkipNextAsync().AsTask() : Task.FromResult(false));

    public Task<bool> PreviousAsync(string? sessionId = null) => ControlAsync(sessionId, session =>
        session.GetPlaybackInfo().Controls.IsPreviousEnabled ? session.TrySkipPreviousAsync().AsTask() : Task.FromResult(false));

    private async Task<bool> ControlAsync(string? sessionId,
        Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        try
        {
            if (_stop.IsCancellationRequested || _manager is not { } manager) return false;
            GlobalSystemMediaTransportControlsSession? session;
            if (sessionId is null) session = manager.GetCurrentSession();
            else
            {
                SessionHandle? target;
                lock (_sessionsLock) target = _handles.Values.FirstOrDefault(handle => handle.Id == sessionId);
                if (target is null) return false;
                session = manager.GetSessions().FirstOrDefault(item => NativeIdentity(item) == target.Identity);
            }
            if (session is null) return false;
            bool success = await action(session).WaitAsync(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
            if (success)
            {
                await Task.Delay(80, _stop.Token).ConfigureAwait(false);
                await RefreshAsync(_stop.Token).ConfigureAwait(false);
                Wake();
            }
            return success;
        }
        catch (Exception exception) { TraceFailure("control", exception); return false; }
    }

    private static long NativeIdentity(GlobalSystemMediaTransportControlsSession session)
    {
        // IUnknown provides canonical identity, independent of projection wrapper or app ID.
        var native = ((WinRT.IWinRTObject)session).NativeObject;
        var iid = new Guid("00000000-0000-0000-C000-000000000046");
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(native.ThisPtr, in iid, out var identity));
        try { return identity.ToInt64(); }
        finally { Marshal.Release(identity); GC.KeepAlive(session); }
    }

    private void Publish(IReadOnlyList<MediaSessionSnapshot> value)
    {
        if (Sessions.SequenceEqual(value)) return;
        var immutable = Array.AsReadOnly(value.ToArray());
        Volatile.Write(ref _snapshot, immutable);
        if (SessionsChanged is not { } handlers) return;
        foreach (Action<IReadOnlyList<MediaSessionSnapshot>> handler in handlers.GetInvocationList())
            try { handler(immutable); } catch (Exception exception) { TraceFailure("snapshot subscriber", exception); }
    }

    private void ManagerSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args) => Wake();
    private void ManagerCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Wake();
    private void PlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Wake();
    private void MediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Wake();
    private void Wake()
    {
        if (_stop.IsCancellationRequested) return;
        try { _wake.Release(); } catch (SemaphoreFullException) { }
    }

    private void ResetManager()
    {
        var manager = _manager;
        _manager = null;
        if (manager is not null)
        {
            try { manager.SessionsChanged -= ManagerSessionsChanged; } catch { }
            try { manager.CurrentSessionChanged -= ManagerCurrentSessionChanged; } catch { }
        }
        lock (_sessionsLock)
        {
            foreach (var handle in _handles.Values) Unsubscribe(handle.Session);
            _handles.Clear();
        }
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession session)
    {
        try { session.PlaybackInfoChanged -= PlaybackInfoChanged; } catch { }
        try { session.MediaPropertiesChanged -= MediaPropertiesChanged; } catch { }
    }

    private static string FriendlyAppName(string appId)
    {
        if (appId.Contains("cloudmusic", StringComparison.OrdinalIgnoreCase)) return "网易云音乐";
        if (appId.Contains("qqmusic", StringComparison.OrdinalIgnoreCase)) return "QQ 音乐";
        if (appId.Contains("spotify", StringComparison.OrdinalIgnoreCase)) return "Spotify";
        if (appId.Contains("music.ui", StringComparison.OrdinalIgnoreCase) || appId.Contains("zunemusic", StringComparison.OrdinalIgnoreCase)) return "媒体播放器";
        if (appId.Contains("chrome", StringComparison.OrdinalIgnoreCase)) return "Chrome";
        if (appId.Contains("msedge", StringComparison.OrdinalIgnoreCase)) return "Microsoft Edge";
        try
        {
            var info = Windows.ApplicationModel.AppInfo.GetFromAppUserModelId(appId);
            if (!string.IsNullOrWhiteSpace(info?.DisplayInfo?.DisplayName)) return info.DisplayInfo.DisplayName;
        }
        catch { }
        var name = Path.GetFileNameWithoutExtension(appId);
        return string.IsNullOrWhiteSpace(name) ? "音乐" : name;
    }

    private void Emit(IslandEvent item)
    {
        if (EventReceived is not { } handlers) return;
        foreach (Action<IslandEvent> handler in handlers.GetInvocationList())
            try { handler(item); } catch (Exception exception) { TraceFailure("event subscriber", exception); }
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        if (StatusChanged is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(status); } catch (Exception exception) { TraceFailure("status subscriber", exception); }
    }

    private static void TraceFailure(string operation, Exception exception) =>
        Debug.WriteLine($"LandBar media {operation}: 0x{exception.HResult:X8}");

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
    }
}
