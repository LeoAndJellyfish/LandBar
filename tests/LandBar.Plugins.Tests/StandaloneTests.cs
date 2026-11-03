using System.Diagnostics;
using System.Text.Json;
using LandBar.Plugins;

internal static class StandaloneTests
{
    internal static async Task Eligibility()
    {
        using var data = new TestDirectory(); data.Add("standalone", "view-standalone-valid");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("standalone")); await Until(() => manager.Views.Count == 12);
        foreach (var view in manager.Views)
        {
            bool expected = !view.Id.StartsWith("off", StringComparison.Ordinal);
            True(view.AllowStandalone == expected);
            True(PluginViewPlacement.CanUseStandalone(view) == expected);
            if (PluginViewPlacement.IsSideWidget(view))
            {
                True(PluginViewPlacement.SupportsSide(view, view.Slot == "idle.right" ? "right" : "left"));
                True(!PluginViewPlacement.SupportsSide(view, view.Slot == "idle.right" ? "left" : "right"));
            }
        }
        var original = manager.Views.First(view => view.Id.StartsWith("on", StringComparison.Ordinal));
        foreach (string slot in new[] { "activity.tab", "media.page", "notifications.page", "plugins.page",
                     "controlCenter.page", "settings.page", "pluginManagement.page", "unknown" })
            True(!PluginViewPlacement.CanUseStandalone(original with { Slot = slot }));
    }

    internal static async Task Validation()
    {
        foreach (string issue in new[] { "null", "string", "number", "array", "object", "duplicate" }
                     .Concat(new[] { "activity.tab", "media.page", "notifications.page", "plugins.page",
                         "controlCenter.page", "settings.page", "pluginManagement.page" }
                         .SelectMany(slot => new[] { "page-true-" + slot, "page-false-" + slot })))
        {
            using var data = new TestDirectory(); data.Add("invalid", "view-standalone-invalid-" + issue);
            await using var manager = new PluginManager(data.Root); manager.Discover();
            await manager.StartAsync("invalid");
            await Until(() => manager.Plugins.Single().State == PluginState.Faulted);
            True(manager.Views.Count == 0);
        }
    }

    internal static async Task Updates()
    {
        using var data = new TestDirectory(); data.Add("updates", "view-standalone-updates");
        await using var manager = new PluginManager(data.Root); manager.Discover();
        True(await manager.StartAsync("updates")); await Until(() => manager.Views.Count == 1); await Task.Delay(30);
        int changes = 0; manager.Changed += () => Interlocked.Increment(ref changes);
        var original = manager.Views.Single();
        True(await manager.ViewActionAsync("updates", "widget", "explicit")); await Task.Delay(150);
        True(changes == 0 && manager.Views.Single().UpdatedAt == original.UpdatedAt);
        True(await manager.ViewActionAsync("updates", "widget", "disable"));
        await Until(() => !manager.Views.Single().AllowStandalone && Volatile.Read(ref changes) > 0);
        var disabled = manager.Views.Single();
        True(original.StableKey == disabled.StableKey);
        True(JsonSerializer.Serialize(original.Root) == JsonSerializer.Serialize(disabled.Root));
        int before = Volatile.Read(ref changes);
        True(await manager.ViewActionAsync("updates", "widget", "disable")); await Task.Delay(150);
        True(changes == before && manager.Views.Single().UpdatedAt == disabled.UpdatedAt);
        True(await manager.ViewActionAsync("updates", "widget", "default"));
        await Until(() => PluginViewPlacement.CanUseStandalone(manager.Views.Single()) && Volatile.Read(ref changes) > before);
    }

    private static void True(bool value) { if (!value) throw new InvalidOperationException("Expected true"); }
    private static async Task Until(Func<bool> predicate)
    {
        var clock = Stopwatch.StartNew();
        while (!predicate() && clock.ElapsedMilliseconds < 6000) await Task.Delay(20);
        if (!predicate()) throw new TimeoutException("Standalone fixture condition was not reached.");
    }
}

internal static class StandaloneFixture
{
    internal static async Task<int> Run(string mode)
    {
        if (mode == "view-standalone-valid")
        {
            int i = 0;
            foreach (string slot in new[] { "idle.widget", "idle.left", "idle.right", "idle.content" })
            {
                await Send(View("default" + i, slot));
                var on = View("on" + i, slot); on["allowStandalone"] = true; await Send(on);
                var off = View("off" + i++, slot); off["allowStandalone"] = false; await Send(off);
            }
            return await Wait();
        }
        if (mode == "view-standalone-updates")
        {
            var widget = View("widget", "idle.widget");
            widget["root"] = new { type = "row", width = 64, height = 36,
                children = new[] { "explicit", "disable", "default" }
                    .Select(action => new { type = "button", text = action, actionId = action }).ToArray() };
            await Send(widget);
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var message = json.RootElement;
                if (message.GetProperty("type").GetString() == "shutdown") return 0;
                if (message.GetProperty("type").GetString() != "viewAction") continue;
                string? action = message.GetProperty("actionId").GetString();
                if (action == "default") widget.Remove("allowStandalone");
                else widget["allowStandalone"] = action != "disable";
                await Send(widget);
            }
            return 0;
        }
        string issue = mode["view-standalone-invalid-".Length..];
        string slotName = issue.StartsWith("page-", StringComparison.Ordinal) ? issue.Split('-', 3)[2] : "idle.widget";
        var invalid = View("invalid", slotName);
        invalid["allowStandalone"] = issue switch
        {
            "null" => null, "string" => "true", "number" => 1, "array" => new[] { true }, "object" => new { eligible = true },
            _ => !issue.StartsWith("page-false-", StringComparison.Ordinal)
        };
        if (issue == "duplicate")
        {
            string serialized = JsonSerializer.Serialize(invalid);
            await Console.Out.WriteLineAsync(serialized[..^1] + ",\"allowStandalone\":false}"); await Console.Out.FlushAsync();
        }
        else await Send(invalid);
        return await Wait();
    }

    private static Dictionary<string, object?> View(string id, string slot)
    {
        var view = new Dictionary<string, object?>
        {
            ["type"] = "view", ["id"] = id, ["slot"] = slot,
            ["root"] = new { type = "text", text = "组件", width = 28, height = 28 }
        };
        if (slot is "idle.widget" or "idle.left" or "idle.right")
        {
            string side = slot == "idle.right" ? "right" : "left";
            view["placement"] = new { defaultSide = side, allowedSides = new[] { side } };
        }
        if (slot == "activity.tab") view["title"] = "页面";
        return view;
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
