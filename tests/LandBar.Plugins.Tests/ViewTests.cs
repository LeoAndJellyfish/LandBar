using System.Diagnostics;
using System.Text.Json;
using LandBar.Plugins;

internal static class ViewTests
{
    internal static async Task Lifecycle()
    {
        using var data = new TestDirectory(); data.Add("views", "views");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("views"));
        await Until(() => manager.Views.Count == 3);
        Equal(1, manager.Cards.Count);
        var view = manager.Views.Single(view => view.Id == "panel");
        Equal("view:views:panel", view.StableKey);
        Equal("plugins.page", view.Slot); Equal("column", view.Root.Type);
        Equal("页面内容", view.Root.Children[0].Text);
        var ring = view.Root.Children[1].Children[0];
        Equal("ring", ring.Type); Equal(0.75, ring.Value); Equal("75%", ring.Text);
        var sprite = manager.Views.Single(view => view.Id == "pixels").Root.Sprite!;
        Equal(2, sprite.PixelWidth); Equal(2, sprite.PixelHeight); Equal(2, sprite.Frames.Count);
        Equal("#00000000", sprite.Palette[0]); Equal(6.0, sprite.Fps);
        True(!await manager.ViewActionAsync("views", "missing", "refresh"));
        True(!await manager.ViewActionAsync("views", "panel", "unknown"));
        True(await manager.ViewActionAsync("views", "panel", "refresh"));
        await Until(() => manager.Views.Single(view => view.Id == "panel").Root.Children[0].Text == "已刷新");
        True(await manager.ViewActionAsync("views", "panel", "remove"));
        await Until(() => manager.Views.Count == 2);
        Equal(1, manager.Cards.Count);
        manager.Expire(DateTimeOffset.MaxValue);
        Equal(2, manager.Views.Count);
        await manager.StopAsync("views"); Equal(0, manager.Views.Count); Equal(0, manager.Cards.Count);
    }

    internal static async Task NoOp()
    {
        using var data = new TestDirectory(); data.Add("same", "views");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("same"); await Until(() => manager.Views.Count == 3);
        await Task.Delay(30);
        int changes = 0; manager.Changed += () => Interlocked.Increment(ref changes);
        var original = manager.Views.Single(view => view.Id == "panel");
        True(await manager.ViewActionAsync("same", "panel", "duplicate"));
        await Task.Delay(150);
        Equal(0, changes);
        Equal(original.UpdatedAt, manager.Views.Single(view => view.Id == "panel").UpdatedAt);
        try { ((IList<PluginNode>)original.Root.Children).Add(new() { Type = "text" }); throw new Exception("Expected immutable children"); }
        catch (NotSupportedException) { }
        var sprite = manager.Views.Single(view => view.Id == "pixels").Root.Sprite!;
        try { ((IList<string>)sprite.Frames)[0] = "1111"; throw new Exception("Expected immutable frames"); }
        catch (NotSupportedException) { }
        var snapshot = manager.Views.ToArray(); snapshot[0] = original with { Slot = "idle.left" };
        True(manager.Views.Single(view => view.Id == "panel").Slot == "plugins.page");
    }

    internal static async Task Isolation()
    {
        using var data = new TestDirectory(); data.Add("first", "views"); data.Add("second", "views");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await Task.WhenAll(manager.StartAsync("first"), manager.StartAsync("second"));
        await Until(() => manager.Views.Count == 6);
        Equal(6, manager.Views.Select(view => view.StableKey).Distinct().Count());
        await manager.StopAsync("first"); Equal(3, manager.Views.Count);
        True(manager.Views.All(view => view.PluginId == "second"));
        True(!await manager.ViewActionAsync("first", "panel", "refresh"));
        True(await manager.ViewActionAsync("second", "panel", "refresh"));
        await Until(() => manager.Views.Single(view => view.Id == "panel").Root.Children[0].Text == "已刷新");
    }

    internal static async Task Slots()
    {
        using var data = new TestDirectory(); data.Add("slots", "view-slots");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("slots"); await Until(() => manager.Views.Count == PluginSlots.All.Count);
        True(PluginSlots.All.All(slot => manager.Views.Any(view => view.Slot == slot)));
        Equal(0, manager.Cards.Count);
    }

    internal static async Task Validation()
    {
        foreach (string issue in new[] { "slot", "node", "field", "color", "width", "height", "font", "weight", "align", "ring", "button",
                     "leafchildren", "nodeid", "duplicateid", "duplicatefield", "spacing", "padding", "thickness", "childrenkind" })
            await Invalid("view-invalid-" + issue);
    }

    internal static async Task Bounds()
    {
        foreach (string mode in new[] { "view-depth8", "view-count128" })
        {
            using var data = new TestDirectory(); data.Add("bounds", mode);
            await using var manager = new PluginManager(data.Root); manager.Discover();
            True(await manager.StartAsync("bounds")); await Until(() => manager.Views.Count == 1);
            Equal(PluginState.Running, manager.Plugins.Single().State);
        }
        await Invalid("view-depth9"); await Invalid("view-count129");
    }

    internal static async Task Sprites()
    {
        foreach (string issue in new[] { "width", "height", "palette0", "palette17", "color", "frames0", "frames17", "length", "index", "fps", "negativefps", "fractional" })
            await Invalid("view-sprite-" + issue);
    }

    internal static async Task ViewLimits()
    {
        await Invalid("view-many13");
        using var data = new TestDirectory();
        for (int i = 0; i < 5; i++) data.Add("many" + i, i == 4 ? "view-many1" : "view-many12");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        for (int i = 0; i < 4; i++)
        {
            True(await manager.StartAsync("many" + i)); int count = (i + 1) * 12;
            await Until(() => manager.Views.Count == count);
        }
        Equal(48, manager.Views.Count);
        await manager.StartAsync("many4");
        await Until(() => manager.Plugins.Single(plugin => plugin.Id == "many4").State == PluginState.Faulted);
        Equal(48, manager.Views.Count);
    }

    internal static async Task Withdrawal()
    {
        foreach (string action in new[] { "fault", "exit", "close" })
        {
            using var data = new TestDirectory(); data.Add("withdraw", "views");
            await using var manager = new PluginManager(data.Root); manager.Discover();
            await manager.StartAsync("withdraw"); await Until(() => manager.Views.Count == 3);
            True(await manager.ViewActionAsync("withdraw", "panel", action));
            await Until(() => manager.Views.Count == 0);
            Equal(0, manager.Cards.Count);
            await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        }
        using (var data = new TestDirectory())
        {
            string directory = data.Add("removed", "views");
            await using var manager = new PluginManager(data.Root); manager.Discover();
            await manager.StartAsync("removed"); await Until(() => manager.Views.Count == 3);
            File.Delete(Path.Combine(directory, "plugin.json")); manager.Discover();
            Equal(0, manager.Views.Count); Equal(0, manager.Cards.Count);
        }
        using (var data = new TestDirectory())
        {
            data.Add("disposed", "views");
            var manager = new PluginManager(data.Root); manager.Discover();
            await manager.StartAsync("disposed"); await Until(() => manager.Views.Count == 3);
            await manager.DisposeAsync(); Equal(0, manager.Views.Count);
        }
    }

    internal static async Task Context()
    {
        using var data = new TestDirectory(); data.Add("context", "views");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(!await manager.SendContextAsync("context", "plugins.page", new { value = "disabled" }));
        await manager.StartAsync("context"); await Until(() => manager.Views.Count == 3);
        True(!await manager.SendContextAsync("context", "settings.page", new { value = "undeclared" }));
        True(!await manager.SendContextAsync("context", "plugins.page", new { value = new string('x', 17000) }));
        var cyclic = new List<object>(); cyclic.Add(cyclic);
        True(!await manager.SendContextAsync("context", "plugins.page", cyclic));
        True(!await manager.SendContextAsync("context", "plugins.page", new ThrowingContext()));
        Equal(PluginState.Running, manager.Plugins.Single().State);
        True(await manager.SendContextAsync("context", "plugins.page", new { value = "选定页面数据" }));
        await Until(() => manager.Views.Single(view => view.Id == "panel").Root.Children[0].Text == "选定页面数据");
        await manager.StopAsync("context");
        True(!await manager.SendContextAsync("context", "plugins.page", new { value = "stopped" }));
    }

    internal static async Task IdleBounds()
    {
        foreach (string suffix in new[] { "leftmax", "contentmax", "pagemax" })
        {
            using var data = new TestDirectory(); data.Add("idle", "view-idle-" + suffix);
            await using var manager = new PluginManager(data.Root); manager.Discover();
            True(await manager.StartAsync("idle")); await Until(() => manager.Views.Count == 1);
            Equal(PluginState.Running, manager.Plugins.Single().State);
        }
        foreach (string suffix in new[] { "leftwide", "righttall", "contentwide", "contenttall" })
            await Invalid("view-idle-" + suffix);
    }

    internal static async Task Tabs()
    {
        using var data = new TestDirectory(); data.Add("tabs", "view-tabs");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("tabs")); await Until(() => manager.Views.Count == 2);
        Equal(0, manager.Cards.Count);
        True(manager.Views.All(view => view.Slot == "activity.tab"));
        Equal("自定义页面", manager.Views.Single(view => view.Id == "main").Title);
        Equal("开发者😀", manager.Views.Single(view => view.Id == "second").Title);
        var first = manager.Views.Single(view => view.Id == "main");
        int changes = 0; manager.Changed += () => Interlocked.Increment(ref changes);
        True(await manager.ViewActionAsync("tabs", "main", "duplicate"));
        await Task.Delay(150); Equal(0, changes);
        Equal(first.UpdatedAt, manager.Views.Single(view => view.Id == "main").UpdatedAt);
        True(await manager.ViewActionAsync("tabs", "main", "rename"));
        await Until(() => manager.Views.Single(view => view.Id == "main").Title == "页面 · 更新" &&
            Volatile.Read(ref changes) > 0);
        True(changes > 0); Equal(first.StableKey, manager.Views.Single(view => view.Id == "main").StableKey);
        Equal("任务页面", manager.Views.Single(view => view.Id == "main").Root.Children[0].Text);
        await manager.StopAsync("tabs"); Equal(0, manager.Views.Count);
    }

    internal static async Task Placement()
    {
        using var data = new TestDirectory(); data.Add("positions", "view-placement-valid");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("positions")); await Until(() => manager.Views.Count == 5);
        var left = manager.Views.Single(view => view.Id == "fixed-left");
        var right = manager.Views.Single(view => view.Id == "fixed-right");
        var either = manager.Views.Single(view => view.Id == "either");
        Equal("idle.widget", either.Slot); Equal("right", PluginViewPlacement.DefaultSide(either));
        True(PluginViewPlacement.IsSideWidget(left)); True(PluginViewPlacement.SupportsSide(left, "left"));
        True(!PluginViewPlacement.SupportsSide(left, "right")); True(!PluginViewPlacement.SupportsSide(right, "left"));
        True(PluginViewPlacement.SupportsSide(right, "right"));
        True(PluginViewPlacement.CanUseSlot(either, "idle.left") && PluginViewPlacement.CanUseSlot(either, "idle.right"));
        True(!PluginViewPlacement.SupportsSide(either, "auto") && !PluginViewPlacement.CanUseSlot(either, "media.page"));
        True(either.Placement!.AllowedSides.SequenceEqual(new[] { "left", "right" }));
        True(PluginViewPlacement.CanUseSlot(manager.Views.Single(view => view.Id == "legacy-left"), "idle.right"));
        True(PluginViewPlacement.CanUseSlot(manager.Views.Single(view => view.Id == "legacy-right"), "idle.right"));
        try { ((IList<string>)either.Placement.AllowedSides)[0] = "right"; throw new Exception("Expected immutable sides"); }
        catch (NotSupportedException) { }
        var page = either with { Slot = "media.page", Placement = null };
        True(!PluginViewPlacement.IsSideWidget(page)); True(PluginViewPlacement.DefaultSide(page) is null);
        True(PluginViewPlacement.CanUseSlot(page, "media.page") && !PluginViewPlacement.CanUseSlot(page, "idle.left"));
        True(!PluginViewPlacement.CanUseSlot(either with { Placement = null }, "idle.right"));
    }

    internal static async Task PlacementValidation()
    {
        foreach (string issue in new[] { "missing", "legacy-left-missing", "legacy-right-missing", "null", "string", "array",
                     "empty-object", "missing-default", "missing-allowed", "invalid-default", "default-case", "invalid-side", "duplicate-side",
                     "empty-sides", "three-sides", "allowed-type", "allowed-item-type", "default-not-member", "extra-field", "duplicate-default",
                     "duplicate-allowed", "legacy-mismatch", "page", "content", "tab" })
            await Invalid("view-placement-invalid-" + issue);
    }

    internal static async Task PlacementUpdates()
    {
        using var data = new TestDirectory(); data.Add("updates", "view-placement-updates");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("updates")); await Until(() => manager.Views.Count == 1); await Task.Delay(30);
        int changes = 0; manager.Changed += () => Interlocked.Increment(ref changes);
        var original = manager.Views.Single();
        True(await manager.ViewActionAsync("updates", "widget", "duplicate")); await Task.Delay(150);
        Equal(0, changes); Equal(original.UpdatedAt, manager.Views.Single().UpdatedAt);
        True(await manager.ViewActionAsync("updates", "widget", "reorder")); await Task.Delay(150);
        Equal(0, changes); Equal(original.UpdatedAt, manager.Views.Single().UpdatedAt);
        True(await manager.ViewActionAsync("updates", "widget", "lock"));
        await Until(() => !PluginViewPlacement.SupportsSide(manager.Views.Single(), "right") && Volatile.Read(ref changes) > 0);
        Equal(original.StableKey, manager.Views.Single().StableKey);
        Equal(JsonSerializer.Serialize(original.Root), JsonSerializer.Serialize(manager.Views.Single().Root));
        True(await manager.ViewActionAsync("updates", "widget", "unlock"));
        await Until(() => PluginViewPlacement.SupportsSide(manager.Views.Single(), "right"));
        int beforeDefault = Volatile.Read(ref changes);
        True(await manager.ViewActionAsync("updates", "widget", "default"));
        await Until(() => PluginViewPlacement.DefaultSide(manager.Views.Single()) == "right" && Volatile.Read(ref changes) > beforeDefault);
        True(PluginViewPlacement.SupportsSide(manager.Views.Single(), "left"));
    }

    internal static async Task TabTitles()
    {
        foreach (string suffix in new[] { "valid24", "unicode" })
        {
            using var data = new TestDirectory(); data.Add("titles", "view-title-" + suffix);
            await using var manager = new PluginManager(data.Root); manager.Discover();
            True(await manager.StartAsync("titles")); await Until(() => manager.Views.Count == 1);
            Equal(24, manager.Views.Single().Title!.Length);
        }
        foreach (string suffix in new[] { "missing", "empty", "blank", "long", "lf", "cr", "tab", "nul", "separator", "unicode-long", "other-long" })
            await Invalid("view-title-" + suffix);
    }

    internal static async Task PixelSample()
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Bundled Windows PowerShell pixel sample requires Windows.");
            return;
        }
        using var data = new TestDirectory();
        string source = Path.Combine(AppContext.BaseDirectory, "Samples", "pixel-pet");
        string directory = Path.Combine(data.Root, "pixel-pet"); Directory.CreateDirectory(directory);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)));
        byte[] script = File.ReadAllBytes(Path.Combine(directory, "plugin.ps1"));
        True(script.Length >= 3 && script[0] == 0xEF && script[1] == 0xBB && script[2] == 0xBF);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("pixel-pet")); await Until(() => manager.Views.Count == 2);
        Equal(0, manager.Cards.Count);
        var avatar = manager.Views.Single(view => view.Id == "avatar");
        Equal("idle.widget", avatar.Slot); Equal(28.0, avatar.Root.Width); Equal(28.0, avatar.Root.Height);
        Equal("left", PluginViewPlacement.DefaultSide(avatar));
        True(PluginViewPlacement.CanUseSlot(avatar, "idle.left") && PluginViewPlacement.CanUseSlot(avatar, "idle.right"));
        Equal(16, avatar.Root.Sprite!.PixelWidth); Equal(16, avatar.Root.Sprite.PixelHeight);
        Equal(4, avatar.Root.Sprite.Palette.Count); Equal(4, avatar.Root.Sprite.Frames.Count); Equal(4.0, avatar.Root.Sprite.Fps);
        True(avatar.Root.Sprite.Frames.All(frame => frame.Length == 256));
        var tab = manager.Views.Single(view => view.Id == "pet");
        Equal("activity.tab", tab.Slot); Equal("像素伙伴", tab.Title);
        Equal("配色：薄荷", tab.Root.Children[2].Text);
        Equal("1 / 3", tab.Root.Children[1].Children[1].Text);
        Equal("next", tab.Root.Children[3].ActionId);
        var running = (System.Collections.IDictionary)typeof(PluginManager).GetField("_running", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(manager)!;
        object runtime = running["pixel-pet"]!;
        int pid = ((Process)runtime.GetType().GetProperty("Process", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(runtime)!).Id;
        True(await manager.ViewActionAsync("pixel-pet", "pet", "next"));
        await Until(() => manager.Views.Single(view => view.Id == "pet").Root.Children[2].Text == "配色：晴蓝");
        Equal("#B5D5F0", manager.Views.Single(view => view.Id == "avatar").Root.Sprite!.Palette[1]);
        Equal("2 / 3", manager.Views.Single(view => view.Id == "pet").Root.Children[1].Children[1].Text);
        await manager.StopAsync("pixel-pet"); Equal(0, manager.Views.Count);
        await Until(() => ProcessExited(pid));
    }

    private static async Task Invalid(string mode)
    {
        using var data = new TestDirectory(); data.Add("invalid-view", mode);
        await using var manager = new PluginManager(data.Root); manager.Discover();
        await manager.StartAsync("invalid-view");
        await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
        Equal(0, manager.Views.Count); Equal(0, manager.Cards.Count);
    }

    private static async Task Until(Func<bool> predicate)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate()) { if (watch.Elapsed > TimeSpan.FromSeconds(6)) throw new TimeoutException("View condition did not become true."); await Task.Delay(15); }
    }
    private static void True(bool condition) { if (!condition) throw new InvalidOperationException("Expected true"); }
    private static bool ProcessExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return process.HasExited; }
        catch (ArgumentException) { return true; }
    }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}, got {actual}");
    }

    private sealed class ThrowingContext
    {
        public string Value => throw new IOException("Host context serialization fixture.");
    }
}

internal static class ViewFixture
{
    internal static async Task<int> Run(string mode)
    {
        if (mode.StartsWith("view-placement-", StringComparison.Ordinal)) return await PlacementFixture.Run(mode);
        if (mode == "view-tabs")
        {
            object tree = new { type = "column", children = new object[]
            {
                new { type = "text", text = "任务页面" },
                new { type = "button", text = "改名", actionId = "rename" },
                new { type = "button", text = "重复", actionId = "duplicate" }
            }};
            await View(tree, "activity.tab", "main", "自定义页面");
            await View(new { type = "text", text = "另一个页面" }, "activity.tab", "second", "开发者😀");
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var root = json.RootElement;
                if (root.GetProperty("type").GetString() == "shutdown") return 0;
                if (root.GetProperty("type").GetString() == "viewAction")
                {
                    if (root.GetProperty("viewId").GetString() != "main") return 14;
                    await View(tree, "activity.tab", "main", root.GetProperty("actionId").GetString() == "rename" ? "页面 · 更新" : "自定义页面");
                }
            }
            return 0;
        }
        if (mode.StartsWith("view-title-", StringComparison.Ordinal))
        {
            string suffix = mode["view-title-".Length..];
            string? title = suffix switch
            {
                "empty" => "", "blank" => "   ", "valid24" => new string('中', 24),
                "unicode" => string.Concat(Enumerable.Repeat("😀", 12)), "unicode-long" => string.Concat(Enumerable.Repeat("😀", 13)),
                "lf" => "一\n二", "cr" => "一\r二", "tab" => "一\t二", "nul" => "一\0二", "separator" => "一\u2028二",
                _ => new string('x', 25)
            };
            if (suffix == "missing") await Send(new { type = "view", id = "main", slot = "activity.tab", root = new { type = "text", text = "Title" } });
            else await View(new { type = "text", text = "Title" }, suffix == "other-long" ? "plugins.page" : "activity.tab", "main", title);
            return await Wait();
        }
        if (mode.StartsWith("view-idle-", StringComparison.Ordinal))
        {
            string suffix = mode["view-idle-".Length..];
            string slot = suffix.StartsWith("content", StringComparison.Ordinal) ? "idle.content" : suffix == "pagemax" ? "plugins.page" : suffix == "righttall" ? "idle.right" : "idle.left";
            double width = suffix == "pagemax" ? 720 : slot == "idle.content" ? 400 : 64;
            double height = suffix == "pagemax" ? 420 : 36;
            if (suffix.EndsWith("wide", StringComparison.Ordinal)) width++;
            if (suffix.EndsWith("tall", StringComparison.Ordinal)) height++;
            await View(new { type = "text", text = "Slot", width, height }, slot);
            return await Wait();
        }
        if (mode.StartsWith("view-invalid-", StringComparison.Ordinal))
        {
            string issue = mode["view-invalid-".Length..];
            if (issue == "duplicatefield")
                Console.WriteLine("{\"type\":\"view\",\"id\":\"panel\",\"slot\":\"plugins.page\",\"root\":{\"type\":\"text\",\"text\":\"first\",\"text\":\"second\"}}");
            else
            {
                var node = new Dictionary<string, object?> { ["type"] = "text", ["text"] = "Page" };
                switch (issue)
                {
                    case "node": node["type"] = "xaml"; break;
                    case "field": node["xaml"] = "arbitrary"; break;
                    case "color": node["color"] = "red"; break;
                    case "width": node["width"] = 721; break;
                    case "height": node["height"] = 421; break;
                    case "font": node["fontSize"] = 11; break;
                    case "weight": node["weight"] = "bold"; break;
                    case "align": node["align"] = "arbitrary"; break;
                    case "ring": node["type"] = "ring"; node["value"] = 1.1; break;
                    case "button": node["type"] = "button"; break;
                    case "leafchildren": node["children"] = Array.Empty<object>(); break;
                    case "nodeid": node["id"] = "../../escape"; break;
                    case "spacing": node["spacing"] = 33; break;
                    case "padding": node["padding"] = -1; break;
                    case "thickness": node["thickness"] = 17; break;
                    case "childrenkind": node["type"] = "column"; node["children"] = "not-array"; break;
                    case "duplicateid": node = new() { ["type"] = "row", ["children"] = new[] { new { type = "text", id = "same" }, new { type = "text", id = "same" } } }; break;
                }
                await View(node, issue == "slot" ? "undeclared.slot" : "plugins.page");
            }
            await Task.Delay(60000); return 0;
        }
        if (mode.StartsWith("view-sprite-", StringComparison.Ordinal))
        {
            var sprite = Sprite();
            switch (mode["view-sprite-".Length..])
            {
                case "width": sprite["pixelWidth"] = 33; break;
                case "height": sprite["pixelHeight"] = 0; break;
                case "palette0": sprite["palette"] = Array.Empty<string>(); break;
                case "palette17": sprite["palette"] = Enumerable.Repeat("#FFFFFF", 17).ToArray(); break;
                case "color": sprite["palette"] = new[] { "url(file)" }; break;
                case "frames0": sprite["frames"] = Array.Empty<string>(); break;
                case "frames17": sprite["frames"] = Enumerable.Repeat("0110", 17).ToArray(); break;
                case "length": sprite["frames"] = new[] { "01" }; break;
                case "index": sprite["frames"] = new[] { "0230" }; break;
                case "fps": sprite["fps"] = 13; break;
                case "negativefps": sprite["fps"] = -1; break;
                case "fractional": sprite["pixelWidth"] = 1.5; break;
            }
            await View(sprite, "idle.left"); await Task.Delay(60000); return 0;
        }
        if (mode.StartsWith("view-depth", StringComparison.Ordinal))
        {
            int depth = int.Parse(mode["view-depth".Length..]);
            object node = new { type = "text", text = "Leaf" };
            for (int i = 1; i < depth; i++) node = new { type = "column", children = new[] { node } };
            await View(node); return await Wait();
        }
        if (mode.StartsWith("view-count", StringComparison.Ordinal))
        {
            int count = int.Parse(mode["view-count".Length..]);
            await View(new { type = "row", children = Enumerable.Range(0, count - 1).Select(i => new { type = "text", text = "t" }).ToArray() });
            return await Wait();
        }
        if (mode.StartsWith("view-many", StringComparison.Ordinal))
        {
            int count = int.Parse(mode["view-many".Length..]);
            for (int i = 0; i < count; i++) await View(new { type = "text", text = "Page" }, id: "page" + i);
            return await Wait();
        }
        if (mode == "view-slots")
        {
            for (int i = 0; i < PluginSlots.All.Count; i++) await View(new { type = "text", text = "Slot" }, PluginSlots.All[i], "slot" + i);
            return await Wait();
        }
        await Send(new { type = "upsert", id = "legacy", title = "旧卡片", body = "仍然可用" });
        await View(Panel());
        await View(Sprite(), "idle.left", "pixels");
        await View(new { type = "ring", value = 0.75, text = "75%" }, "idle.right", "ring");
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (root.GetProperty("type").GetString() == "shutdown") return 0;
            if (root.GetProperty("type").GetString() == "context")
            {
                if (root.GetProperty("slot").GetString() != "plugins.page") return 11;
                await View(Panel(root.GetProperty("data").GetProperty("value").GetString()!));
            }
            if (root.GetProperty("type").GetString() == "viewAction")
            {
                if (root.GetProperty("viewId").GetString() != "panel") return 12;
                switch (root.GetProperty("actionId").GetString())
                {
                    case "refresh": await View(Panel("已刷新")); break;
                    case "duplicate": await View(Panel()); break;
                    case "remove": await Send(new { type = "removeView", id = "panel" }); break;
                    case "fault": Console.WriteLine("{bad-json"); await Task.Delay(60000); return 0;
                    case "exit": return 9;
                    case "close": Console.Out.Flush(); NativeOutput.Close(); await Task.Delay(60000); return 0;
                }
            }
        }
        return 0;
    }

    private static object Panel(string title = "页面内容") => new
    {
        type = "column", id = "root", width = 300, height = 240, spacing = 8, padding = 12, cornerRadius = 16, background = "#101014",
        children = new object[]
        {
            new { type = "text", text = title, color = "#FFFFFF", fontSize = 20, weight = "semibold" },
            new { type = "row", children = new object[] { new { type = "ring", value = 0.75, text = "75%", color = "#BEDDC9", trackColor = "#25252B", thickness = 3 }, new { type = "spacer", width = 8 } } },
            new { type = "row", children = new[] { "refresh", "duplicate", "remove", "fault", "exit", "close" }.Select(id => new { type = "button", id, actionId = id, text = id }).ToArray() }
        }
    };
    private static Dictionary<string, object?> Sprite() => new()
    {
        ["type"] = "sprite", ["pixelWidth"] = 2, ["pixelHeight"] = 2,
        ["palette"] = new[] { "#00000000", "#BEDDC9" }, ["frames"] = new[] { "0110", "1001" }, ["fps"] = 6
    };
    private static Task View(object root, string slot = "plugins.page", string id = "panel", string? title = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["type"] = "view", ["id"] = id, ["slot"] = slot, ["root"] = root,
            ["title"] = title ?? (slot == "activity.tab" ? id : null)
        };
        if (slot is "idle.widget" or "idle.left" or "idle.right")
        {
            string side = slot == "idle.right" ? "right" : "left";
            message["placement"] = new { defaultSide = side, allowedSides = new[] { side } };
        }
        return Send(message);
    }
    private static async Task Send(object value) { await Console.Out.WriteLineAsync(JsonSerializer.Serialize(value)); await Console.Out.FlushAsync(); }
    private static async Task<int> Wait()
    {
        while (await Console.In.ReadLineAsync() is { } line)
        {
            using var json = JsonDocument.Parse(line);
            if (json.RootElement.GetProperty("type").GetString() == "shutdown") return 0;
        }
        return 0;
    }
}
