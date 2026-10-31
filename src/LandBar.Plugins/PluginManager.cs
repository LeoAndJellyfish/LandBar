using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace LandBar.Plugins;

/// <summary>
/// Runs explicitly enabled, trusted local programs outside the UI process. This is process fault
/// isolation, not an operating-system security sandbox. Callbacks can arrive on background threads.
/// </summary>
public sealed class PluginManager : IDisposable, IAsyncDisposable
{
    private const int MaximumDiscovered = 128;
    private sealed record Definition(PluginManifest Manifest, string DirectoryPath);
    private sealed class Runtime(Definition definition, Process process, WindowsProcessScope? scope)
    {
        internal Definition Definition { get; } = definition;
        internal Process Process { get; } = process;
        internal WindowsProcessScope? Scope { get; } = scope;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal SemaphoreSlim Writer { get; } = new(1, 1);
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Completion { get; set; } = Task.CompletedTask;
        internal PluginState State { get; set; } = PluginState.Starting;
        internal string? Detail { get; set; }
        internal bool StopRequested { get; set; }
    }

    private readonly string _root;
    private readonly object _sync = new();
    private Dictionary<string, Definition> _definitions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Runtime> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginStatus> _lastStatus = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginCard> _cards = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PluginView> _views = new(StringComparer.Ordinal);
    private IReadOnlyList<PluginStatus> _invalid = [];
    private bool _disposed;

    public PluginManager(string root) => _root = Path.GetFullPath(root);
    public string RootDirectory => _root;
    public event Action? Changed;

    public IReadOnlyList<PluginStatus> Plugins
    {
        get
        {
            lock (_sync)
                return _definitions.Values.Select(definition =>
                {
                    string id = definition.Manifest.Id;
                    if (_running.TryGetValue(id, out var runtime))
                        return Status(runtime.Definition, runtime.State, runtime.Detail);
                    return _lastStatus.TryGetValue(id, out var status) ? status : Status(definition, PluginState.Disabled);
                }).Concat(_invalid).OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    public IReadOnlyList<PluginCard> Cards
    {
        get { lock (_sync) return _cards.Values.OrderBy(card => card.PluginId).ThenBy(card => card.Id).ToArray(); }
    }

    public IReadOnlyList<PluginView> Views
    {
        get { lock (_sync) return _views.Values.OrderBy(view => view.PluginId).ThenBy(view => view.Id).ToArray(); }
    }

    /// <summary>Refreshes manifests without launching programs. Running, unchanged IDs keep their process.</summary>
    public IReadOnlyList<PluginStatus> Discover()
    {
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_root);
        PluginProtocol.RejectLinks(_root);
        var discovered = new Dictionary<string, Definition>(StringComparer.Ordinal);
        var invalid = new List<PluginStatus>();
        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        string[] directories = Directory.EnumerateDirectories(_root).Take(MaximumDiscovered + 1).ToArray();
        if (directories.Length > MaximumDiscovered)
            invalid.Add(new("invalid-directory-limit", "插件目录数量超限", "", "", PluginState.Invalid,
                "最多扫描 128 个插件目录，请整理插件文件夹后刷新。", _root));
        foreach (string directory in directories.Take(MaximumDiscovered).Order(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(Path.Combine(directory, "plugin.json"))) continue;
            try
            {
                var manifest = PluginProtocol.ReadManifest(directory);
                var definition = new Definition(manifest, directory);
                if (duplicates.Contains(manifest.Id))
                    invalid.Add(Status(definition, PluginState.Invalid, "插件 id 重复。"));
                else if (!discovered.TryAdd(manifest.Id, definition))
                {
                    duplicates.Add(manifest.Id);
                    invalid.Add(Status(discovered[manifest.Id], PluginState.Invalid, "插件 id 重复。"));
                    invalid.Add(Status(definition, PluginState.Invalid, "插件 id 重复。"));
                    discovered.Remove(manifest.Id);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                          KeyNotFoundException or InvalidOperationException or FormatException or ArgumentException)
            {
                invalid.Add(new("invalid-" + Path.GetFileName(directory), Path.GetFileName(directory), "", "",
                    PluginState.Invalid, "插件清单无效：" + FriendlyError(error), directory));
            }
        }
        Runtime[] removed;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            removed = _running.Where(pair => !discovered.TryGetValue(pair.Key, out var definition) ||
                !string.Equals(definition.DirectoryPath, pair.Value.Definition.DirectoryPath,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .Select(pair => pair.Value).ToArray();
            _definitions = discovered;
            _invalid = invalid.AsReadOnly();
            foreach (string id in _lastStatus.Keys.Where(id => !discovered.ContainsKey(id)).ToArray()) _lastStatus.Remove(id);
            foreach (var runtime in removed)
            {
                runtime.StopRequested = true;
                RemoveCards(runtime.Definition.Manifest.Id);
            }
        }
        foreach (var runtime in removed) _ = StopRuntimeAsync(runtime);
        Notify();
        return Plugins;
    }

    /// <summary>Call only after the user explicitly enables a discovered plugin.</summary>
    public async Task<bool> StartAsync(string id)
    {
        Runtime runtime;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_definitions.TryGetValue(id, out var definition)) return false;
            if (_running.TryGetValue(id, out var existing)) return existing.State == PluginState.Running;
            if (_running.Count >= PluginProtocol.MaximumRunning)
            {
                _lastStatus[id] = Status(definition, PluginState.Faulted, "最多同时运行八个插件。");
                runtime = null!;
            }
            else
            {
                try
                {
                    // Revalidate immediately before execution, including a folder replaced after discovery.
                    PluginManifest manifest = PluginProtocol.ReadManifest(definition.DirectoryPath);
                    if (manifest.Id != id) throw new InvalidDataException("插件 id 已更改，请刷新列表。");
                    definition = new(manifest, definition.DirectoryPath);
                    string executable = !Path.IsPathRooted(manifest.Command) && (manifest.Command.Contains('/') || manifest.Command.Contains('\\'))
                        ? Path.GetFullPath(Path.Combine(definition.DirectoryPath, manifest.Command)) : manifest.Command;
                    var info = new ProcessStartInfo(executable)
                    {
                        WorkingDirectory = definition.DirectoryPath,
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                        StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
                        StandardErrorEncoding = Encoding.UTF8
                    };
                    foreach (string argument in manifest.Arguments) info.ArgumentList.Add(argument);
                    info.Environment["LANDBAR_PROTOCOL_VERSION"] = PluginProtocol.Version.ToString();
                    var process = new Process { StartInfo = info };
                    try { if (!process.Start()) throw new InvalidOperationException("插件进程启动失败。"); }
                    catch { process.Dispose(); throw; }
                    WindowsProcessScope? scope;
                    try { scope = WindowsProcessScope.Attach(process); }
                    catch
                    {
                        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                        process.Dispose();
                        throw;
                    }
                    runtime = new(definition, process, scope);
                    _running.Add(id, runtime);
                    runtime.Completion = RunAsync(runtime);
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    _lastStatus[id] = Status(definition, PluginState.Faulted, "启动失败：" + FriendlyError(error));
                    runtime = null!;
                }
            }
        }
        Notify();
        if (runtime is null) return false;
        try
        {
            if (!await WriteAsync(runtime, new { type = "hello", protocolVersion = PluginProtocol.Version,
                features = new[] { "cards", "views", "contexts", "tabs", "sprites" } }).ConfigureAwait(false))
                return false;
            return await runtime.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Fault(runtime, "插件未在五秒内完成协议握手。");
            await StopRuntimeAsync(runtime).ConfigureAwait(false);
            return false;
        }
    }

    public async Task StopAsync(string id)
    {
        Runtime? runtime;
        lock (_sync)
        {
            if (!_running.TryGetValue(id, out runtime))
            {
                if (_definitions.TryGetValue(id, out var definition)) _lastStatus[id] = Status(definition, PluginState.Disabled);
                RemoveCards(id);
            }
            else
            {
                runtime.StopRequested = true;
                runtime.State = PluginState.Stopped;
                RemoveCards(id);
            }
        }
        Notify();
        if (runtime is not null) await StopRuntimeAsync(runtime).ConfigureAwait(false);
    }

    public async Task StopAllAsync()
    {
        string[] ids;
        lock (_sync) ids = _running.Keys.ToArray();
        await Task.WhenAll(ids.Select(StopAsync)).ConfigureAwait(false);
    }

    public async Task<bool> ActionAsync(string pluginId, string cardId, string actionId)
    {
        Runtime? runtime;
        lock (_sync)
        {
            if (!_running.TryGetValue(pluginId, out runtime) || runtime.State != PluginState.Running ||
                !_cards.TryGetValue($"plugin:{pluginId}:{cardId}", out var card) ||
                !card.Actions.Any(action => action.Id == actionId)) return false;
        }
        return await WriteAsync(runtime, new { type = "action", cardId, actionId }).ConfigureAwait(false);
    }

    public async Task<bool> ViewActionAsync(string pluginId, string viewId, string actionId)
    {
        Runtime? runtime;
        lock (_sync)
        {
            if (!_running.TryGetValue(pluginId, out runtime) || runtime.State != PluginState.Running ||
                !_views.TryGetValue($"view:{pluginId}:{viewId}", out var view) ||
                !PluginProtocol.HasViewAction(view.Root, actionId)) return false;
        }
        return await WriteAsync(runtime, new { type = "viewAction", viewId, actionId }).ConfigureAwait(false);
    }

    /// <summary>The host selects a provider first, then explicitly supplies that slot's required data.</summary>
    public async Task<bool> SendContextAsync(string pluginId, string slot, object? data)
    {
        Runtime? runtime;
        lock (_sync)
        {
            if (!_running.TryGetValue(pluginId, out runtime) || runtime.State != PluginState.Running ||
                !_views.Values.Any(view => view.PluginId == pluginId && view.Slot == slot)) return false;
        }
        string text;
        try
        {
            text = JsonSerializer.Serialize(new { type = "context", slot, data }, new JsonSerializerOptions { MaxDepth = 24 });
            if (text.Length > PluginProtocol.MaximumLineLength || Encoding.UTF8.GetByteCount(text) > PluginProtocol.MaximumLineLength)
                return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return false;
        }
        return await WriteSerializedAsync(runtime, text).ConfigureAwait(false);
    }

    public void Expire(DateTimeOffset now)
    {
        bool changed;
        lock (_sync)
        {
            string[] expired = _cards.Values.Where(card => card.ExpiresAt <= now).Select(card => card.StableKey).ToArray();
            changed = expired.Length > 0;
            foreach (string key in expired) _cards.Remove(key);
        }
        if (changed) Notify();
    }

    private async Task RunAsync(Runtime runtime)
    {
        // Process pipes use synchronous OS handles on Windows. Async-over-sync reads would
        // occupy two thread-pool workers per idle provider and delay writes and handshake timers.
        // The eight-provider limit bounds these dedicated readers to sixteen threads.
        Task stderr = Task.Factory.StartNew(() => DrainError(runtime), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task stdout = Task.Factory.StartNew(() => ReadMessages(runtime), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            await runtime.Process.WaitForExitAsync(runtime.Cancellation.Token).ConfigureAwait(false);
            // Allow the reader to consume final output, without waiting for inherited descendant pipes.
            await Task.WhenAny(stdout, Task.Delay(200)).ConfigureAwait(false);
            if (!runtime.StopRequested && runtime.State != PluginState.Faulted)
            {
                if (runtime.State == PluginState.Starting) Fault(runtime, "插件在完成握手前退出。");
                else if (runtime.Process.ExitCode != 0) Fault(runtime, "插件异常退出。");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { Fault(runtime, "插件进程连接已中断。"); }
        finally
        {
            runtime.Cancellation.Cancel();
            Kill(runtime);
            // Cancellation may have ended the first wait before the OS signaled process exit.
            // Observe that signal before disposing Process or completing a stop request.
            try { await runtime.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
            // Killing the owned process tree closes inherited pipes and unblocks the dedicated readers.
            try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
            lock (_sync)
            {
                string id = runtime.Definition.Manifest.Id;
                if (_running.TryGetValue(id, out var current) && ReferenceEquals(current, runtime))
                {
                    RemoveCards(id);
                    _running.Remove(id);
                    _lastStatus[id] = Status(runtime.Definition,
                        runtime.State == PluginState.Faulted ? PluginState.Faulted : PluginState.Stopped,
                        runtime.State == PluginState.Faulted ? runtime.Detail : "插件已停止。");
                }
            }
            runtime.Ready.TrySetResult(false);
            runtime.Process.Dispose();
            Notify();
        }
    }

    private void ReadMessages(Runtime runtime)
    {
        var line = new StringBuilder();
        long window = Stopwatch.GetTimestamp();
        int messages = 0;
        try
        {
            while (!runtime.Cancellation.IsCancellationRequested)
            {
                // Read() consumes StreamReader's internal buffer without waiting to fill a caller
                // buffer past a complete JSON line while the provider waits for our response.
                int next = runtime.Process.StandardOutput.Read();
                if (next < 0)
                {
                    // ExitProcess closes stdout shortly before the process handle is signaled.
                    // A bounded grace distinguishes normal exit from a provider closing stdout
                    // while continuing to run; this reader owns a dedicated thread.
                    if (!runtime.StopRequested && !runtime.Process.WaitForExit(200))
                    {
                        Fault(runtime, "插件输出通道已关闭。");
                        runtime.Cancellation.Cancel();
                        Kill(runtime);
                    }
                    return;
                }
                char character = (char)next;
                if (character == '\n')
                {
                    if (Stopwatch.GetElapsedTime(window) >= TimeSpan.FromSeconds(1)) { window = Stopwatch.GetTimestamp(); messages = 0; }
                    if (++messages > 60) throw new InvalidDataException("插件消息超过每秒 60 条。");
                    string message = line.ToString().TrimEnd('\r');
                    line.Clear();
                    HandleMessage(runtime, message);
                }
                else
                {
                    if (line.Length >= PluginProtocol.MaximumLineLength) throw new InvalidDataException("插件消息超过 16 KiB。");
                    line.Append(character);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (!runtime.StopRequested)
            {
                Fault(runtime, "插件协议错误：" + FriendlyError(error));
                runtime.Cancellation.Cancel();
                Kill(runtime);
            }
        }
    }

    private void HandleMessage(Runtime runtime, string message)
    {
        using var json = PluginProtocol.Parse(message);
        var root = json.RootElement;
        string type = PluginProtocol.Text(root, "type", 20);
        bool changed = false;
        lock (_sync)
        {
            if (runtime.StopRequested || runtime.State == PluginState.Faulted ||
                !_running.TryGetValue(runtime.Definition.Manifest.Id, out var active) || !ReferenceEquals(active, runtime)) return;
            if (runtime.State == PluginState.Starting)
            {
                if (type != "hello" || root.GetProperty("protocolVersion").GetInt32() != PluginProtocol.Version)
                    throw new InvalidDataException("插件必须先确认 protocolVersion 1。");
                runtime.State = PluginState.Running;
                runtime.Ready.TrySetResult(true);
                changed = true;
            }
            else if (type == "upsert")
            {
                var card = PluginProtocol.Card(runtime.Definition.Manifest.Id, root, DateTimeOffset.UtcNow);
                if (_cards.TryGetValue(card.StableKey, out var existing))
                {
                    changed = card.Title != existing.Title || card.Body != existing.Body || card.Icon != existing.Icon ||
                        card.Progress != existing.Progress || !card.Actions.SequenceEqual(existing.Actions);
                    // Identical payloads renew TTL without causing a UI rebind or resetting inactivity.
                    _cards[card.StableKey] = changed ? card : existing with { ExpiresAt = card.ExpiresAt };
                }
                else
                {
                    if (_cards.Count >= PluginProtocol.MaximumCards ||
                        _cards.Values.Count(value => value.PluginId == card.PluginId) >= PluginProtocol.MaximumCardsPerPlugin)
                        throw new InvalidDataException("插件卡片数量超限。");
                    _cards.Add(card.StableKey, card);
                    changed = true;
                }
            }
            else if (type == "remove")
                changed = _cards.Remove($"plugin:{runtime.Definition.Manifest.Id}:{PluginProtocol.CardId(root)}");
            else if (type == "view")
            {
                var view = PluginProtocol.View(runtime.Definition.Manifest.Id, root, DateTimeOffset.UtcNow);
                if (_views.TryGetValue(view.StableKey, out var existing))
                {
                    changed = view.Slot != existing.Slot || view.Title != existing.Title || !PluginProtocol.SameNode(view.Root, existing.Root);
                    if (changed) _views[view.StableKey] = view;
                }
                else
                {
                    if (_views.Count >= PluginProtocol.MaximumViews ||
                        _views.Values.Count(value => value.PluginId == view.PluginId) >= PluginProtocol.MaximumViewsPerPlugin)
                        throw new InvalidDataException("插件页面数量超限。");
                    _views.Add(view.StableKey, view);
                    changed = true;
                }
            }
            else if (type == "removeView")
                changed = _views.Remove($"view:{runtime.Definition.Manifest.Id}:{PluginProtocol.CardId(root)}");
            else throw new InvalidDataException("插件消息类型不受支持。");
        }
        if (changed) Notify();
    }

    private static void DrainError(Runtime runtime)
    {
        var buffer = new char[1024];
        try
        {
            while (!runtime.Cancellation.IsCancellationRequested &&
                   runtime.Process.StandardError.Read(buffer, 0, buffer.Length) > 0) { }
        }
        catch (Exception) { }
    }

    private Task<bool> WriteAsync(Runtime runtime, object value) => WriteSerializedAsync(runtime, JsonSerializer.Serialize(value));

    private async Task<bool> WriteSerializedAsync(Runtime runtime, string text)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(runtime.Cancellation.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(1));
        bool entered = false;
        try
        {
            await runtime.Writer.WaitAsync(deadline.Token).ConfigureAwait(false);
            entered = true;
            await runtime.Process.StandardInput.WriteLineAsync(text.AsMemory(), deadline.Token).ConfigureAwait(false);
            await runtime.Process.StandardInput.FlushAsync(deadline.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            if (!runtime.StopRequested) { Fault(runtime, "插件输入通道已关闭。"); runtime.Cancellation.Cancel(); Kill(runtime); }
            return false;
        }
        finally { if (entered) runtime.Writer.Release(); }
    }

    private async Task StopRuntimeAsync(Runtime runtime)
    {
        runtime.Ready.TrySetResult(false);
        if (!runtime.Cancellation.IsCancellationRequested)
        {
            await WriteAsync(runtime, new { type = "shutdown" }).ConfigureAwait(false);
            await Task.WhenAny(runtime.Completion, Task.Delay(500)).ConfigureAwait(false);
        }
        runtime.Cancellation.Cancel();
        Kill(runtime);
        try { await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
    }

    private void Fault(Runtime runtime, string detail)
    {
        lock (_sync)
        {
            if (!_running.TryGetValue(runtime.Definition.Manifest.Id, out var active) || !ReferenceEquals(active, runtime)) return;
            runtime.State = PluginState.Faulted; runtime.Detail = detail; RemoveCards(runtime.Definition.Manifest.Id);
        }
        runtime.Ready.TrySetResult(false);
        Notify();
    }

    private static void Kill(Runtime runtime)
    {
        // Closing the owned Windows Job atomically reclaims the entire tree. Enumerating the tree
        // with Process.Kill can race another cleanup closing that job and produce invalid handles.
        if (runtime.Scope is { } scope) { scope.Dispose(); return; }
        try { if (!runtime.Process.HasExited) runtime.Process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
    }

    private void RemoveCards(string pluginId)
    {
        foreach (string key in _cards.Values.Where(card => card.PluginId == pluginId).Select(card => card.StableKey).ToArray()) _cards.Remove(key);
        foreach (string key in _views.Values.Where(view => view.PluginId == pluginId).Select(view => view.StableKey).ToArray()) _views.Remove(key);
    }

    private static PluginStatus Status(Definition definition, PluginState state, string? detail = null) =>
        new(definition.Manifest.Id, definition.Manifest.Name, definition.Manifest.Version,
            definition.Manifest.Description, state, detail, definition.DirectoryPath);

    private static string FriendlyError(Exception error) => error switch
    {
        InvalidDataException => error.Message,
        JsonException or KeyNotFoundException or FormatException or InvalidOperationException => "JSON 字段或格式无效。",
        UnauthorizedAccessException => "无法访问插件文件。",
        FileNotFoundException => "插件文件不存在。",
        _ => error.GetType().Name
    };

    private void Notify()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch { /* A subscriber cannot strand plugin cleanup. */ }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; }
        await StopAllAsync().ConfigureAwait(false);
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
