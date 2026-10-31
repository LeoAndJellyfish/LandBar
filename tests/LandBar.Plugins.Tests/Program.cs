using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LandBar.Plugins;

if (args.Length >= 2 && args[0] == "--fixture") return await Fixture(args[1]);

var tests = new (string Name, Func<Task> Run)[]
{
    ("Discovery never executes disabled plugins", DisabledDiscovery),
    ("Handshake, upsert, action, removal and shutdown use real pipes", FullLifecycle),
    ("Updates retain identity and identical payloads skip UI changes", UpdateIdentity),
    ("Same card IDs remain scoped to each plugin", ScopedCards),
    ("Expired cards are withdrawn and renewed TTL is retained", Expiry),
    ("Hot discovery retains running instances and removes deleted providers", Rediscovery),
    ("Invalid manifests, protocol versions and executable escapes are rejected", InvalidManifests),
    ("Duplicate IDs disable every conflicting provider", DuplicateIds),
    ("Bad JSON faults one plugin while another keeps running", BadJsonIsolation),
    ("Wrong handshake and messages before hello are rejected", HandshakeValidation),
    ("Oversized ASCII, UTF8, unterminated and deep JSON messages are bounded", Oversize),
    ("Malformed card fields and action bounds are rejected", CardValidation),
    ("Message rate limits contain flooding providers", RateLimit),
    ("Per-plugin and aggregate card counts stay bounded", CardLimits),
    ("Running process count is bounded without consuming thread-pool workers", ProcessLimit),
    ("Unexpected exit clears cards and permits explicit restart", UnexpectedExit),
    ("Explicit stop kills an uncooperative owned process and clears cards", ForcedStop),
    ("Disposal closes every owned plugin process", Disposal),
    ("Stderr is drained without blocking JSONL or actions", StderrDrain),
    ("Relative executable paths resolve inside the plugin directory", RelativeExecutable),
    ("Synchronous disposal is safe on a non-pumping UI context", UiDisposal),
    ("A provider that closes stdout is reclaimed without hanging", ClosedOutput),
    ("A silent handshake times out and reclaims the provider", HandshakeTimeout),
    ("Forced stop reclaims the owned process tree", ProcessTree),
    ("Discovery bounds the number of local directories", DiscoveryLimit),
    ("Windows Job lifetime reclaims descendants after normal and abnormal parent exit", ParentExitTree),
    ("Bundled Windows PowerShell 5.1 sample displays Chinese, responds to actions and exits", BundledSample),
    ("Declarative views preserve their nodes and dispatch validated actions", ViewTests.Lifecycle),
    ("Unchanged views skip refreshes and expose immutable trees", ViewTests.NoOp),
    ("View identity and withdrawal remain scoped to each provider", ViewTests.Isolation),
    ("Every supported host slot is accepted without creating notification cards", ViewTests.Slots),
    ("Invalid declarative node schemas fault only their provider", ViewTests.Validation),
    ("Node counts and nesting enforce their precise inclusive limits", ViewTests.Bounds),
    ("Sprite palettes, pixel indices, frame sizes and frame rates stay bounded", ViewTests.Sprites),
    ("Per-provider and aggregate view counts stay bounded", ViewTests.ViewLimits),
    ("View contributions disappear on fault, EOF, exit, removal and disposal", ViewTests.Withdrawal),
    ("Selected-slot context uses real pipes and rejects invalid or oversized data", ViewTests.Context),
    ("Idle slot root dimensions retain their inclusive display limits", ViewTests.IdleBounds),
    ("Providers publish independent named tabs and title-only updates", ViewTests.Tabs),
    ("Tab titles enforce single-line UTF16 limits with Chinese and emoji", ViewTests.TabTitles),
    ("Bundled pixel animation sample publishes views and handles palette changes", ViewTests.PixelSample)
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception error) { failures++; Console.Error.WriteLine("FAIL " + test.Name + ": " + error); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static async Task DisabledDiscovery()
{
    using var data = new TestDirectory();
    data.Add("disabled");
    await using var manager = new PluginManager(data.Root);
    var plugins = manager.Discover();
    Equal(1, plugins.Count);
    Equal(PluginState.Disabled, plugins[0].State);
    await Task.Delay(150);
    True(!File.Exists(Path.Combine(data.Root, "disabled", "started.txt")));
    Equal(0, manager.Cards.Count);
    True(!await manager.StartAsync("unknown"));
}

static async Task FullLifecycle()
{
    using var data = new TestDirectory();
    string directory = data.Add("timer");
    await using var manager = new PluginManager(data.Root);
    manager.Discover();
    True(await manager.StartAsync("timer"));
    await Until(() => manager.Cards.Count == 1);
    var card = manager.Cards.Single();
    Equal("plugin:timer:focus", card.StableKey);
    Equal("专注计时", card.Title);
    Equal("timer", card.Icon);
    True(File.Exists(Path.Combine(directory, "started.txt")));
    True(!await manager.ActionAsync("timer", "focus", "missing"));
    True(!await manager.ActionAsync("timer", "missing", "toggle"));
    True(await manager.ActionAsync("timer", "focus", "toggle"));
    await Until(() => manager.Cards.SingleOrDefault()?.Title == "已暂停");
    True(await manager.ActionAsync("timer", "focus", "remove"));
    await Until(() => manager.Cards.Count == 0);
    await manager.StopAsync("timer");
    True(File.Exists(Path.Combine(directory, "shutdown.txt")));
    Equal(PluginState.Stopped, manager.Plugins.Single().State);
}

static async Task UpdateIdentity()
{
    using var data = new TestDirectory(); data.Add("updates", "duplicate");
    await using var manager = new PluginManager(data.Root);
    manager.Discover(); int changes = 0; manager.Changed += () => Interlocked.Increment(ref changes);
    True(await manager.StartAsync("updates"));
    await Until(() => manager.Cards.Count == 1);
    await Task.Delay(200);
    var first = manager.Cards.Single();
    int oldChanges = changes;
    True(await manager.ActionAsync("updates", "focus", "duplicate"));
    await Task.Delay(150);
    Equal(oldChanges, changes);
    Equal(first.UpdatedAt, manager.Cards.Single().UpdatedAt);
    True(await manager.ActionAsync("updates", "focus", "toggle"));
    await Until(() => manager.Cards.Single().Title == "已暂停");
    Equal(first.StableKey, manager.Cards.Single().StableKey);
    True(manager.Cards.Single().UpdatedAt >= first.UpdatedAt);
}

static async Task ScopedCards()
{
    using var data = new TestDirectory(); data.Add("first"); data.Add("second");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    await Task.WhenAll(manager.StartAsync("first"), manager.StartAsync("second"));
    await Until(() => manager.Cards.Count == 2);
    Equal(2, manager.Cards.Select(card => card.StableKey).Distinct().Count());
    await manager.StopAsync("first");
    Equal("second", manager.Cards.Single().PluginId);
}

static async Task Expiry()
{
    using var data = new TestDirectory(); data.Add("ttl", "ttl");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("ttl")); await Until(() => manager.Cards.Count == 1);
    var deadline = manager.Cards.Single().ExpiresAt!.Value;
    await Task.Delay(60);
    True(await manager.ActionAsync("ttl", "focus", "duplicate"));
    await Until(() => manager.Cards.Single().ExpiresAt > deadline);
    manager.Expire(deadline);
    Equal(1, manager.Cards.Count);
    manager.Expire(deadline.AddSeconds(2));
    Equal(0, manager.Cards.Count);
}

static async Task Rediscovery()
{
    using var data = new TestDirectory(); string directory = data.Add("refresh");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("refresh")); await Until(() => manager.Cards.Count == 1);
    string started = File.ReadAllText(Path.Combine(directory, "started.txt"));
    manager.Discover(); Equal(PluginState.Running, manager.Plugins.Single().State);
    Equal(started, File.ReadAllText(Path.Combine(directory, "started.txt")));
    File.Delete(Path.Combine(directory, "plugin.json"));
    manager.Discover(); Equal(0, manager.Plugins.Count); Equal(0, manager.Cards.Count);
    await Until(() => ProcessExited(int.Parse(started)));
}

static async Task InvalidManifests()
{
    using var data = new TestDirectory();
    data.Add("escape", command: "../outside.exe");
    data.Add("badversion", version: 2);
    data.Add("BAD-ID");
    string malformed = data.Add("malformed"); File.WriteAllText(Path.Combine(malformed, "plugin.json"), "{");
    string huge = data.Add("huge"); File.WriteAllText(Path.Combine(huge, "plugin.json"), new string(' ', 17000));
    await using var manager = new PluginManager(data.Root);
    True(manager.Discover().All(status => status.State == PluginState.Invalid));
    Equal(5, manager.Plugins.Count);
    True(!await manager.StartAsync("escape")); True(!await manager.StartAsync("badversion"));
}

static async Task DuplicateIds()
{
    using var data = new TestDirectory();
    data.Add("same", folder: "first"); data.Add("same", folder: "second"); data.Add("same", folder: "third");
    await using var manager = new PluginManager(data.Root);
    Equal(3, manager.Discover().Count);
    True(manager.Plugins.All(plugin => plugin.State == PluginState.Invalid));
    True(!await manager.StartAsync("same"));
}

static async Task BadJsonIsolation()
{
    using var data = new TestDirectory(); data.Add("good"); data.Add("bad", "badjson");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    await Task.WhenAll(manager.StartAsync("good"), manager.StartAsync("bad"));
    await Until(() => manager.Plugins.Single(status => status.Id == "bad").State == PluginState.Faulted);
    await Until(() => manager.Cards.Count == 1);
    Equal("good", manager.Cards.Single().PluginId);
    Equal(PluginState.Running, manager.Plugins.Single(status => status.Id == "good").State);
}

static async Task HandshakeValidation()
{
    foreach (string mode in new[] { "wronghello", "earlycard" })
    {
        using var data = new TestDirectory(); data.Add("handshake", mode);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(!await manager.StartAsync("handshake"));
        await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        Equal(0, manager.Cards.Count);
    }
}

static async Task Oversize()
{
    foreach (string mode in new[] { "oversize", "unicodeoversize", "unterminated", "deepjson" })
    {
        using var data = new TestDirectory(); data.Add("oversize", mode);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("oversize");
        await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        Equal(0, manager.Cards.Count);
    }
}

static async Task CardValidation()
{
    foreach (string mode in new[] { "badprogress", "badactions", "badtitle", "badicon", "duplicatefields" })
    {
        using var data = new TestDirectory(); data.Add("invalid", mode);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("invalid");
        await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        Equal(0, manager.Cards.Count);
    }
}

static async Task RateLimit()
{
    using var data = new TestDirectory(); data.Add("flood", "flood");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    await manager.StartAsync("flood");
    await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
    Equal(0, manager.Cards.Count);
}

static async Task CardLimits()
{
    using (var data = new TestDirectory())
    {
        data.Add("many", "ninecards");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("many");
        await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        Equal(0, manager.Cards.Count);
    }
    using (var data = new TestDirectory())
    {
        for (int i = 0; i < 5; i++) data.Add("cards" + i, "eightcards");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        for (int i = 0; i < 4; i++)
        {
            await manager.StartAsync("cards" + i);
            int expected = (i + 1) * 8;
            await Until(() => manager.Cards.Count == expected);
        }
        Equal(32, manager.Cards.Count);
        await manager.StartAsync("cards4");
        await Until(() => manager.Plugins.Single(plugin => plugin.Id == "cards4").State == PluginState.Faulted);
        Equal(32, manager.Cards.Count);
    }
}

static async Task ProcessLimit()
{
    // The owner gets its own small thread pool. Idle process pipes must not occupy those workers;
    // otherwise handshake writes and their deadlines cannot run when eight providers are enabled.
    var info = new ProcessStartInfo(Environment.ProcessPath!)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true
    };
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    info.ArgumentList.Add("--fixture"); info.ArgumentList.Add("processlimitowner");
    using var owner = Process.Start(info)!;
    Task<string> output = owner.StandardOutput.ReadToEndAsync();
    Task<string> error = owner.StandardError.ReadToEndAsync();
    try
    {
        await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        if (owner.ExitCode != 0)
            throw new InvalidOperationException("Bounded-thread-pool owner failed: " + await output + await error);
        True((await output).Contains("PROCESS_LIMIT_OK", StringComparison.Ordinal));
    }
    finally
    {
        if (!owner.HasExited)
        {
            // On Windows, terminating this owner closes the Jobs that own its fixture children.
            owner.Kill(entireProcessTree: !OperatingSystem.IsWindows());
            await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));
        }
    }
}

static async Task ProcessLimitOwner()
{
    ThreadPool.GetMinThreads(out _, out int minimumIo);
    ThreadPool.GetMaxThreads(out _, out int maximumIo);
    True(ThreadPool.SetMinThreads(2, minimumIo));
    True(ThreadPool.SetMaxThreads(4, maximumIo));
    using var data = new TestDirectory(); for (int i = 0; i < 9; i++) data.Add("process" + i, "nocard");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    for (int i = 0; i < 8; i++)
    {
        string id = "process" + i;
        if (!await manager.StartAsync(id))
        {
            var status = manager.Plugins.Single(plugin => plugin.Id == id);
            throw new InvalidOperationException($"{id} failed to start: {status.State}, {status.Detail}");
        }
    }
    True(!await manager.StartAsync("process8"));
    Equal(8, manager.Plugins.Count(plugin => plugin.State == PluginState.Running));
    Equal(PluginState.Faulted, manager.Plugins.Single(plugin => plugin.Id == "process8").State);
}

static async Task UnexpectedExit()
{
    using var data = new TestDirectory(); data.Add("exit", "exit");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    await manager.StartAsync("exit");
    await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
    Equal(0, manager.Cards.Count);
    await Task.Delay(60);
    await manager.StartAsync("exit");
    await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
}

static async Task ForcedStop()
{
    using var data = new TestDirectory(); string directory = data.Add("stubborn", "stubborn");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("stubborn")); await Until(() => manager.Cards.Count == 1);
    int pid = await ReadPidAsync(Path.Combine(directory, "started.txt"));
    var stopwatch = Stopwatch.StartNew(); await manager.StopAsync("stubborn");
    True(stopwatch.Elapsed < TimeSpan.FromSeconds(4));
    True(ProcessExited(pid)); Equal(0, manager.Cards.Count);
}

static async Task Disposal()
{
    using var data = new TestDirectory(); string directory = data.Add("dispose", "tree");
    var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("dispose")); await Until(() => manager.Cards.Count == 1);
    int pid = await ReadPidAsync(Path.Combine(directory, "started.txt"));
    int child = await ReadPidAsync(Path.Combine(directory, "child-pid.txt"));
    await Task.WhenAll(manager.StopAsync("dispose"), manager.DisposeAsync().AsTask());
    True(ProcessExited(pid)); Equal(0, manager.Cards.Count);
    await Until(() => ProcessExited(child));
    await manager.DisposeAsync();
    try { manager.Discover(); throw new Exception("Expected disposed check"); } catch (ObjectDisposedException) { }
}

static async Task StderrDrain()
{
    using var data = new TestDirectory(); data.Add("stderr", "stderr");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("stderr")); await Until(() => manager.Cards.Count == 1);
    True(await manager.ActionAsync("stderr", "focus", "toggle"));
    await Until(() => manager.Cards.Single().Title == "已暂停");
}

static async Task RelativeExecutable()
{
    using var data = new TestDirectory(); string directory = data.Add("relative");
    string source = Path.Combine(AppContext.BaseDirectory, "LandBar.Plugins.Tests" + (OperatingSystem.IsWindows() ? ".exe" : ""));
    string destination = Path.Combine(directory, Path.GetFileName(source));
    foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    data.WriteManifest(directory, "relative", "./" + Path.GetFileName(source), ["--fixture", "normal"]);
    await using var manager = new PluginManager(data.Root); manager.Discover();
    // The apphost locates its runtime through DOTNET_ROOT; executable resolution uses the plugin
    // directory rather than the LandBar working directory.
    True(await manager.StartAsync("relative")); await Until(() => manager.Cards.Count == 1);
}

static async Task UiDisposal()
{
    using var data = new TestDirectory(); data.Add("ui", "stubborn");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        SynchronizationContext.SetSynchronizationContext(new NeverPumpingContext());
        try { True(manager.StartAsync("ui").GetAwaiter().GetResult()); manager.Dispose(); done.SetResult(); }
        catch (Exception error) { done.SetException(error); }
    }) { IsBackground = true };
    thread.Start();
    await done.Task.WaitAsync(TimeSpan.FromSeconds(8));
    Equal(0, manager.Cards.Count);
}

static async Task ClosedOutput()
{
    using var data = new TestDirectory(); string directory = data.Add("closed", "closeoutput");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    await manager.StartAsync("closed");
    await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
    int pid = await ReadPidAsync(Path.Combine(directory, "started.txt"));
    await Until(() => ProcessExited(pid));
    Equal(0, manager.Cards.Count);
}

static async Task HandshakeTimeout()
{
    using var data = new TestDirectory(); string directory = data.Add("silent", "silenthello");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    var watch = Stopwatch.StartNew();
    True(!await manager.StartAsync("silent"));
    True(watch.Elapsed < TimeSpan.FromSeconds(8));
    Equal(PluginState.Faulted, manager.Plugins.Single().State);
    int pid = await ReadPidAsync(Path.Combine(directory, "started.txt"));
    await Until(() => ProcessExited(pid));
}

static async Task ProcessTree()
{
    using var data = new TestDirectory(); string directory = data.Add("tree", "tree");
    await using var manager = new PluginManager(data.Root); manager.Discover();
    True(await manager.StartAsync("tree"));
    string childFile = Path.Combine(directory, "child-pid.txt");
    int child = await ReadPidAsync(childFile);
    int parent = await ReadPidAsync(Path.Combine(directory, "started.txt"));
    await manager.StopAsync("tree");
    True(ProcessExited(parent));
    await Until(() => ProcessExited(child));
}

static async Task DiscoveryLimit()
{
    using var data = new TestDirectory();
    for (int i = 0; i < 130; i++) data.Add("folder" + i);
    await using var manager = new PluginManager(data.Root); manager.Discover();
    Equal(129, manager.Plugins.Count);
    True(manager.Plugins.Any(plugin => plugin.Id == "invalid-directory-limit" && plugin.State == PluginState.Invalid));
    Equal(0, manager.Cards.Count);
}

static async Task ParentExitTree()
{
    if (!OperatingSystem.IsWindows()) return; // Windows Job Objects back the Windows product lifecycle.
    foreach (string mode in new[] { "treeexit", "treecrash" })
    {
        using var data = new TestDirectory(); string directory = data.Add("tree", mode);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("tree");
        string childFile = Path.Combine(directory, "child-pid.txt");
        int child = await ReadPidAsync(childFile);
        int parent = await ReadPidAsync(Path.Combine(directory, "started.txt"));
        await Until(() => ProcessExited(parent));
        await Until(() => manager.Plugins.Single().State is PluginState.Stopped or PluginState.Faulted);
        await Until(() => ProcessExited(child));
        Equal(0, manager.Cards.Count);
        Equal(mode == "treeexit" ? PluginState.Stopped : PluginState.Faulted, manager.Plugins.Single().State);
    }
}

static async Task BundledSample()
{
    if (!OperatingSystem.IsWindows())
    {
        Console.WriteLine("SKIP Bundled Windows PowerShell 5.1 sample requires Windows.");
        return;
    }
    using var data = new TestDirectory();
    string source = Path.Combine(AppContext.BaseDirectory, "Samples", "focus-timer");
    string directory = Path.Combine(data.Root, "focus-timer");
    Directory.CreateDirectory(directory);
    foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
    byte[] script = File.ReadAllBytes(Path.Combine(directory, "plugin.ps1"));
    True(script.Length >= 3 && script[0] == 0xEF && script[1] == 0xBB && script[2] == 0xBF);
    using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "plugin.json"))))
        Equal("powershell.exe", manifest.RootElement.GetProperty("command").GetString());
    await using var manager = new PluginManager(data.Root);
    Equal("专注计时", manager.Discover().Single().Name);
    True(await manager.StartAsync("focus-timer"));
    await Until(() => manager.Cards.Count == 1);
    var card = manager.Cards.Single();
    Equal("focus", card.Id); Equal("专注计时", card.Title); Equal("timer", card.Icon);
    True(card.Body.Contains("进行中", StringComparison.Ordinal));
    Equal(3, card.Actions.Count);
    Equal("暂停", card.Actions.Single(action => action.Id == "toggle").Label);
    // Read the owned PID for the lifecycle assertion; the shipped sample and manifest stay unchanged.
    var running = (System.Collections.IDictionary)typeof(PluginManager).GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager)!;
    object runtime = running["focus-timer"]!;
    int pid = ((Process)runtime.GetType().GetProperty("Process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!).Id;
    True(await manager.ActionAsync("focus-timer", "focus", "toggle"));
    await Until(() => manager.Cards.SingleOrDefault()?.Actions.Single(action => action.Id == "toggle").Label == "继续");
    True(manager.Cards.Single().Body.Contains("已暂停", StringComparison.Ordinal));
    await manager.StopAsync("focus-timer");
    Equal(0, manager.Cards.Count);
    await Until(() => ProcessExited(pid));
}

static async Task Until(Func<bool> condition)
{
    var watch = Stopwatch.StartNew();
    while (!condition())
    {
        if (watch.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("Condition did not become true.");
        await Task.Delay(15);
    }
}
static async Task<int> ReadPidAsync(string path)
{
    int pid = 0;
    await Until(() =>
    {
        try
        {
            string text = File.ReadAllText(path);
            return text.EndsWith('\n') && int.TryParse(text, out pid) && pid > 0;
        }
        catch (IOException) { return false; }
    });
    return pid;
}
static bool ProcessExited(int pid)
{
    try { using var process = Process.GetProcessById(pid); return process.HasExited; }
    catch (ArgumentException) { return true; }
}
static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}");
}

static async Task<int> Fixture(string mode)
{
    Console.InputEncoding = new UTF8Encoding(false); Console.OutputEncoding = new UTF8Encoding(false);
    if (mode == "child") { await Task.Delay(60000); return 0; }
    if (mode == "processlimitowner")
    {
        await ProcessLimitOwner();
        Console.WriteLine("PROCESS_LIMIT_OK");
        return 0;
    }
    WritePid("started.txt", Environment.ProcessId);
    if (mode == "earlycard") { await SendCard(); return 0; }
    string? hello = await Console.In.ReadLineAsync();
    if (hello is null) return 2;
    using (var json = JsonDocument.Parse(hello))
        if (json.RootElement.GetProperty("type").GetString() != "hello") return 3;
    if (mode == "silenthello") { await Task.Delay(60000); return 0; }
    await Send(new { type = "hello", protocolVersion = mode == "wronghello" ? 2 : 1 });
    if (mode == "wronghello") { await Task.Delay(5000); return 0; }
    if (mode.StartsWith("view", StringComparison.Ordinal)) return await ViewFixture.Run(mode);
    if (mode == "closeoutput") { Console.Out.Flush(); NativeOutput.Close(); await Task.Delay(60000); return 0; }
    if (mode == "badjson") { Console.WriteLine("{bad"); await Task.Delay(5000); return 0; }
    if (mode == "oversize") { Console.WriteLine(new string('x', 20000)); await Task.Delay(5000); return 0; }
    if (mode == "unicodeoversize") { Console.WriteLine("{\"type\":\"upsert\",\"value\":\"" + new string('中', 7000) + "\"}"); await Task.Delay(5000); return 0; }
    if (mode == "unterminated") { Console.Write(new string('x', 20000)); Console.Out.Flush(); await Task.Delay(5000); return 0; }
    if (mode == "deepjson") { Console.WriteLine("{\"type\":\"upsert\",\"data\":" + new string('[', 20) + "0" + new string(']', 20) + "}"); await Task.Delay(5000); return 0; }
    if (mode == "duplicatefields") { Console.WriteLine("{\"type\":\"upsert\",\"type\":\"remove\",\"id\":\"focus\"}"); await Task.Delay(5000); return 0; }
    if (mode == "badprogress") { await Send(new { type = "upsert", id = "focus", title = "Title", body = "", progress = 2 }); await Task.Delay(5000); return 0; }
    if (mode == "badactions") { await Send(new { type = "upsert", id = "focus", title = "Title", body = "", actions = Enumerable.Range(0, 4).Select(i => new { id = "act" + i, label = "Action" }) }); await Task.Delay(5000); return 0; }
    if (mode == "badtitle") { await Send(new { type = "upsert", id = "focus", title = new string('x', 121), body = "" }); await Task.Delay(5000); return 0; }
    if (mode == "badicon") { await Send(new { type = "upsert", id = "focus", title = "Title", body = "", icon = "arbitrary" }); await Task.Delay(5000); return 0; }
    if (mode == "stderr") { Console.Error.Write(new string('e', 100000)); Console.Error.Flush(); }
    if (mode == "flood")
    {
        for (int i = 0; i < 100; i++) await Send(new { type = "remove", id = "nothing" });
        await Task.Delay(5000); return 0;
    }
    if (mode is "eightcards" or "ninecards")
    {
        for (int i = 0; i < (mode == "eightcards" ? 8 : 9); i++) await Send(new { type = "upsert", id = "card" + i, title = "Title", body = "" });
    }
    else if (mode != "nocard") await SendCard(ttl: mode == "ttl" ? 2 : null);
    if (mode == "exit") { await Task.Delay(120); return 7; }
    if (mode is "tree" or "treeexit" or "treecrash")
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet") info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("--fixture"); info.ArgumentList.Add("child");
        using var child = Process.Start(info)!;
        WritePid("child-pid.txt", child.Id);
        if (mode != "tree") { await Task.Delay(120); return mode == "treeexit" ? 0 : 9; }
        await Task.Delay(60000); return 0;
    }
    if (mode == "stubborn") { await Task.Delay(60000); return 0; }
    while (await Console.In.ReadLineAsync() is { } line)
    {
        using var json = JsonDocument.Parse(line);
        string type = json.RootElement.GetProperty("type").GetString()!;
        if (type == "shutdown") { File.WriteAllText("shutdown.txt", "ok"); return 0; }
        if (type == "action")
        {
            if (json.RootElement.GetProperty("cardId").GetString() != "focus") return 4;
            string action = json.RootElement.GetProperty("actionId").GetString()!;
            if (action == "remove") await Send(new { type = "remove", id = "focus" });
            else await SendCard(action == "toggle" ? "已暂停" : "专注计时", mode == "ttl" ? 2 : null);
        }
    }
    return 0;

    static Task SendCard(string title = "专注计时", double? ttl = null) => Send(new
    {
        type = "upsert", id = "focus", title, body = "本地测试", icon = "timer", progress = 0.5,
        ttlSeconds = ttl, actions = new[] { new { id = "toggle", label = "暂停" }, new { id = "remove", label = "移除" }, new { id = "duplicate", label = "刷新" } }
    });
    static async Task Send(object value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
    static void WritePid(string path, int pid)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        File.Move(temporary, path, overwrite: true);
    }
}

sealed class TestDirectory : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "LandBar-plugin-tests-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Root);
    public string Add(string id, string mode = "normal", string? command = null, int version = 1, string? folder = null)
    {
        string directory = Path.Combine(Root, folder ?? id); Directory.CreateDirectory(directory);
        string process = command ?? Environment.ProcessPath!;
        string[] arguments = process.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(process) == "dotnet"
            ? [Assembly.GetExecutingAssembly().Location, "--fixture", mode] : ["--fixture", mode];
        WriteManifest(directory, id, process, arguments, version);
        return directory;
    }
    public void WriteManifest(string directory, string id, string command, string[] arguments, int version = 1) =>
        File.WriteAllText(Path.Combine(directory, "plugin.json"), JsonSerializer.Serialize(new
        { id, name = id, version = "1.0.0", description = "Test provider", command, arguments, protocolVersion = version }));
    public void Dispose()
    {
        try { Directory.Delete(Root, true); } catch { }
    }
}

sealed class NeverPumpingContext : SynchronizationContext
{
    public override void Post(SendOrPostCallback callback, object? state) { }
}

static class NativeOutput
{
    internal static void Close()
    {
        if (OperatingSystem.IsWindows()) CloseHandle(GetStdHandle(-11));
        else CloseUnix(1);
    }
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("libc", EntryPoint = "close")] private static extern int CloseUnix(int descriptor);
}
