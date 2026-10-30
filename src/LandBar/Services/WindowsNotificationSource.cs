using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LandBar.Core;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace LandBar.Services;

/// <summary>
/// Reads new Windows toast notifications without touching another application's data.
/// Snapshot polling deliberately avoids the unpackaged NotificationChanged subscription bug.
/// </summary>
public sealed class WindowsNotificationSource : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<uint, string> _known = new();
    private UserNotificationListener? _listener;
    private bool _hasBaseline;
    private int _running;

    public event Action<IslandEvent>? EventReceived;
    public event Action<string>? StatusChanged;
    public string Status { get; private set; } = "尚未连接";
    public UserNotificationListenerAccessStatus AccessStatus { get; private set; } = UserNotificationListenerAccessStatus.Unspecified;
    public string? ErrorDetail { get; private set; }

    /// <summary>Must present the system consent prompt on the WPF UI thread.</summary>
    public async Task<bool> RequestAccessAsync()
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is not null && !dispatcher.CheckAccess())
                return await dispatcher.InvokeAsync(RequestAccessAsync).Task.Unwrap();

            _listener ??= UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            AccessStatus = access;
            ErrorDetail = null;
            SetStatus(DescribeAccess(access));
            return access == UserNotificationListenerAccessStatus.Allowed;
        }
        catch (Exception exception)
        {
            ErrorDetail = DescribeError(exception);
            SetStatus(ErrorDetail);
            return false;
        }
    }

    /// <summary>Runs until cancellation. Existing history is a baseline and is never replayed.</summary>
    public Task StartAsync(CancellationToken cancellationToken) =>
        Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        var token = linked.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var retryDelay = TimeSpan.FromSeconds(1);
                try
                {
                    _listener ??= UserNotificationListener.Current;
                    var access = _listener.GetAccessStatus();
                    AccessStatus = access;
                    if (access != UserNotificationListenerAccessStatus.Allowed)
                    {
                        // Re-granting permission starts a fresh baseline rather than replaying history.
                        _hasBaseline = false;
                        _known.Clear();
                        ErrorDetail = null;
                        SetStatus(DescribeAccess(access));
                        retryDelay = TimeSpan.FromSeconds(3);
                    }
                    else
                    {
                        var snapshot = await _listener.GetNotificationsAsync(NotificationKinds.Toast)
                            .AsTask(token).WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
                        var currentIds = new HashSet<uint>();
                        foreach (var notification in snapshot.OrderBy(item => item.CreationTime))
                        {
                            token.ThrowIfCancellationRequested();
                            currentIds.Add(notification.Id);
                            try
                            {
                                var binding = notification.Notification.Visual
                                    .GetBinding(KnownNotificationBindings.ToastGeneric);
                                var text = binding?.GetTextElements()
                                    .Select(element => element.Text?.Trim())
                                    .Where(value => !string.IsNullOrWhiteSpace(value))
                                    .Select(value => value!)
                                    .ToArray() ?? [];
                                var appId = notification.AppInfo?.AppUserModelId ?? "";
                                var appName = notification.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
                                var signature = $"{notification.CreationTime.UtcTicks}\n{appId}\n{string.Join('\n', text)}";
                                var changed = !_known.TryGetValue(notification.Id, out var previous) || previous != signature;
                                _known[notification.Id] = signature;
                                bool selfTest = text.FirstOrDefault() == "LandBar 通知测试";
                                if (!_hasBaseline || !changed || text.Length == 0 ||
                                    (!selfTest && (appName.Equals("LandBar", StringComparison.OrdinalIgnoreCase) ||
                                    appId.StartsWith("LandBar", StringComparison.OrdinalIgnoreCase)))) continue;

                                var title = text.Length > 1 ? text[0] : appName;
                                var body = text.Length > 1 ? string.Join("\n", text.Skip(1)) : text[0];
                                // Some messengers update an existing toast while retaining ID and CreationTime.
                                var revision = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature)))[..16];
                                Emit(new IslandEvent($"toast:{notification.Id}:{notification.CreationTime.UtcTicks}:{revision}",
                                    IslandEventKind.Notification, appName, title, body,
                                    notification.CreationTime, appId));
                            }
                            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                            catch (Exception exception)
                            {
                                // A toast may disappear between snapshot retrieval and content access.
                                Debug.WriteLine($"LandBar notification parsing: 0x{exception.HResult:X8}");
                            }
                        }
                        foreach (var stale in _known.Keys.Where(id => !currentIds.Contains(id)).ToArray())
                            _known.Remove(stale);
                        _hasBaseline = true;
                        ErrorDetail = null;
                        SetStatus("已连接 · Windows 通知");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception exception)
                {
                    ErrorDetail = DescribeError(exception);
                    SetStatus(ErrorDetail);
                    retryDelay = TimeSpan.FromSeconds(5);
                }
                await Task.Delay(retryDelay, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { Interlocked.Exchange(ref _running, 0); }
    }

    private static string DescribeAccess(UserNotificationListenerAccessStatus access) => access switch
    {
        UserNotificationListenerAccessStatus.Allowed => "已授权 · Windows 通知",
        UserNotificationListenerAccessStatus.Denied => "通知访问已关闭，请在 Windows 设置中允许访问",
        _ => "等待授权 · 请点击启用通知"
    };

    private static string DescribeError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "通知权限不足，请授权或安装带身份的应用包",
        COMException when exception.HResult == unchecked((int)0x80073D54) => "请安装带身份的应用包以启用通知访问",
        TimeoutException => "Windows 通知响应超时，正在重试",
        _ => $"通知暂不可用 · 0x{exception.HResult:X8}"
    };

    private void Emit(IslandEvent item)
    {
        if (EventReceived is not { } handlers) return;
        foreach (Action<IslandEvent> handler in handlers.GetInvocationList())
            try { handler(item); }
            catch (Exception exception) { Debug.WriteLine($"LandBar notification subscriber: {exception.GetType().Name}"); }
    }

    private void SetStatus(string status)
    {
        if (Status == status) return;
        Status = status;
        if (StatusChanged is not { } handlers) return;
        foreach (Action<string> handler in handlers.GetInvocationList())
            try { handler(status); }
            catch (Exception exception) { Debug.WriteLine($"LandBar notification status: {exception.GetType().Name}"); }
    }

    public void Dispose() => _stop.Cancel();
}
