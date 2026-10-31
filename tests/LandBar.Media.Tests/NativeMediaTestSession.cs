using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Windows.Media;
using Windows.Storage;
using Windows.Storage.Streams;

/// <summary>
/// The producer owns its Win32 windows on an STA dispatcher and its MTA-only SMTC objects on
/// worker threads. The parent exercises real cross-process GSMTC without touching other players.
/// </summary>
internal static class NativeMediaFixtureHost
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static Task<int> RunHostAsync()
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                application.Startup += (_, _) => _ = Task.Run(() => ReadCommandsAsync(application));
                completion.TrySetResult(application.Run());
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Fixture host: {exception.GetType().Name} 0x{exception.HResult:X8}");
                completion.TrySetResult(1);
            }
        }) { Name = "LandBar SMTC fixture windows" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static async Task ReadCommandsAsync(Application application)
    {
        var sessions = new Dictionary<int, OwnedSession>();
        int result = 0;
        try
        {
            WriteResponse(true);
            // Console's synchronized reader can block synchronously even through ReadLineAsync.
            // Never run that blocking read on the HWND dispatcher after an awaited UI operation.
            while (await Task.Run(Console.ReadLine).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 16 * 1024)
                    throw new InvalidDataException("Fixture command exceeds the line limit.");
                bool exit = false;
                try
                {
                    var command = JsonSerializer.Deserialize<FixtureCommand>(line, JsonOptions)
                        ?? throw new InvalidDataException("Missing fixture command.");
                    exit = command.Op == "exit";
                    await ApplyCommandAsync(command).ConfigureAwait(false);
                    // Flush the exit acknowledgement before stopping the window dispatcher.
                    WriteResponse(true);
                }
                catch (Exception exception)
                {
                    WriteResponse(false, exception);
                    exit = false;
                }
                if (exit) break;
            }
        }
        catch (Exception exception)
        {
            result = 1;
            Console.Error.WriteLine($"Fixture input: {exception.GetType().Name} 0x{exception.HResult:X8}");
        }
        finally
        {
            // EOF also reclaims every owned session when the parent exits unexpectedly.
            await CloseAllAsync().ConfigureAwait(false);
            await application.Dispatcher.InvokeAsync(() => application.Shutdown(result));
        }

        async Task ApplyCommandAsync(FixtureCommand command)
        {
            if (command.Op == "exit") { await CloseAllAsync().ConfigureAwait(false); return; }
            if (command.Index is < 0 or > 1) throw new InvalidDataException("Invalid fixture index.");
            if (command.Op == "close")
            {
                if (sessions.Remove(command.Index, out var closed)) await closed.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (command.Op is not ("create" or "update"))
                throw new InvalidDataException("Invalid fixture operation.");
            if (string.IsNullOrWhiteSpace(command.Title) || command.Artist is null || command.Album is null)
                throw new InvalidDataException("Incomplete fixture metadata.");
            if (command.Op == "create")
            {
                if (sessions.ContainsKey(command.Index)) throw new InvalidDataException("Fixture already exists.");
                var created = await OwnedSession.CreateAsync(application.Dispatcher).ConfigureAwait(false);
                try
                {
                    await created.UpdateAsync(command).ConfigureAwait(false);
                    await created.SetPlayingAsync().ConfigureAwait(false);
                    sessions.Add(command.Index, created);
                }
                catch { await created.DisposeAsync().ConfigureAwait(false); throw; }
            }
            else
            {
                if (!sessions.TryGetValue(command.Index, out var existing))
                    throw new InvalidDataException("Fixture does not exist.");
                await existing.UpdateAsync(command).ConfigureAwait(false);
            }
        }

        async Task CloseAllAsync()
        {
            foreach (var session in sessions.Values)
            {
                try { await session.DisposeAsync().ConfigureAwait(false); }
                catch (Exception exception)
                {
                    result = 1;
                    Console.Error.WriteLine($"Fixture cleanup: {exception.GetType().Name} 0x{exception.HResult:X8}");
                }
            }
            sessions.Clear();
        }
    }

    private static void WriteResponse(bool ok, Exception? exception = null)
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            ok,
            error = exception is null ? null : $"{exception.GetType().Name} 0x{exception.HResult:X8}"
        }, JsonOptions));
        Console.Out.Flush();
    }

    private sealed record FixtureCommand(string? Op, int Index, string? Title, string? Artist,
        string? Album, string? ArtworkPath);

    private sealed class OwnedSession(Window window, SystemMediaTransportControls controls) : IAsyncDisposable
    {
        private readonly object _gate = new();
        private bool _disposed;
        private HwndSource? _windowSource;
        private HwndSourceHook? _windowHook;
        private SystemMediaTransportControls Controls { get; } = controls;

        public static async Task<OwnedSession> CreateAsync(Dispatcher dispatcher)
        {
            var (window, handle) = await dispatcher.InvokeAsync(() =>
            {
                var window = new Window
                {
                    Title = "LandBar isolated media fixture",
                    Width = 160,
                    Height = 80,
                    Left = -20000,
                    Top = -20000,
                    ShowInTaskbar = false,
                    ShowActivated = false,
                    WindowStyle = WindowStyle.ToolWindow,
                    ResizeMode = ResizeMode.NoResize
                };
                window.Show();
                return (window, new WindowInteropHelper(window).Handle);
            }).Task.ConfigureAwait(false);
            try
            {
                // SystemMediaTransportControls declares ThreadingModel.MTA. Only the HWND belongs
                // to the STA; creating this WinRT object there produces RPC_E_WRONG_THREAD reads.
                var owned = await Task.Run(() =>
                {
                    var controls = SystemMediaTransportControlsInterop.GetForWindow(handle);
                    var session = new OwnedSession(window, controls);
                    controls.IsPlayEnabled = true;
                    controls.IsPauseEnabled = true;
                    controls.IsNextEnabled = true;
                    controls.IsPreviousEnabled = true;
                    controls.IsEnabled = true;
                    controls.ButtonPressed += session.OnButtonPressed;
                    return session;
                }).ConfigureAwait(false);
                await dispatcher.InvokeAsync(() =>
                {
                    owned._windowSource = HwndSource.FromHwnd(handle)
                        ?? throw new InvalidOperationException("Fixture window source is missing.");
                    owned._windowHook = owned.OnWindowMessage;
                    owned._windowSource.AddHook(owned._windowHook);
                }).Task.ConfigureAwait(false);
                return owned;
            }
            catch { await dispatcher.InvokeAsync(window.Close); throw; }
        }

        public async Task UpdateAsync(FixtureCommand command)
        {
            var file = command.ArtworkPath is null ? null : await Task.Run(() =>
                StorageFile.GetFileFromPathAsync(command.ArtworkPath).AsTask()).ConfigureAwait(false);
            // ConfigureAwait(false) alone can inline on the STA that completed a dispatcher
            // operation. Each WinRT access therefore enters an explicit thread-pool callback.
            await Task.Run(() =>
            {
                RandomAccessStreamReference? artwork = file is null ? null : RandomAccessStreamReference.CreateFromFile(file);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    var updater = Controls.DisplayUpdater;
                    updater.Type = MediaPlaybackType.Music;
                    updater.MusicProperties.Title = command.Title!;
                    updater.MusicProperties.Artist = command.Artist!;
                    updater.MusicProperties.AlbumTitle = command.Album!;
                    updater.Thumbnail = artwork;
                    updater.Update();
                }
            }).ConfigureAwait(false);
        }

        public Task SetPlayingAsync() => Task.Run(() =>
        {
            lock (_gate) Controls.PlaybackStatus = MediaPlaybackStatus.Playing;
        });

        private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
        {
            var button = args.Button;
            if (button == SystemMediaTransportControlsButton.Play) QueuePlaybackCommand(46);
            if (button == SystemMediaTransportControlsButton.Pause) QueuePlaybackCommand(47);
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            const int WmAppCommand = 0x0319;
            if (message != WmAppCommand) return IntPtr.Zero;
            int command = (int)((lParam.ToInt64() >> 16) & 0x0FFF);
            if (command is not (14 or 46 or 47)) return IntPtr.Zero;
            // Win32 transport commands can target this HWND directly. Consume them here so
            // DefWindowProc cannot forward the test's request to the desktop shell.
            handled = true;
            QueuePlaybackCommand(command);
            return new IntPtr(1);
        }

        private void QueuePlaybackCommand(int command)
        {
            _ = Task.Run(() =>
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    Controls.PlaybackStatus = command switch
                    {
                        46 => MediaPlaybackStatus.Playing,
                        47 => MediaPlaybackStatus.Paused,
                        14 => Controls.PlaybackStatus == MediaPlaybackStatus.Playing
                            ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Playing,
                        _ => Controls.PlaybackStatus
                    };
                }
            });
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Task.Run(() =>
                {
                    lock (_gate)
                    {
                        if (_disposed) return;
                        _disposed = true;
                        Controls.ButtonPressed -= OnButtonPressed;
                        Controls.IsEnabled = false;
                        Controls.PlaybackStatus = MediaPlaybackStatus.Closed;
                        Controls.DisplayUpdater.ClearAll();
                        Controls.DisplayUpdater.Update();
                    }
                }).ConfigureAwait(false);
            }
            finally
            {
                await window.Dispatcher.InvokeAsync(() =>
                {
                    if (_windowSource is not null && _windowHook is not null)
                        _windowSource.RemoveHook(_windowHook);
                    window.Close();
                });
            }
        }
    }
}
