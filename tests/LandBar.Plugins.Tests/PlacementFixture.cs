using System.Text.Json;

internal static class PlacementFixture
{
    internal static async Task<int> Run(string mode)
    {
        if (mode == "view-placement-valid")
        {
            await Send(View("fixed-left", "idle.widget", Declaration("left", "left")));
            await Send(View("fixed-right", "idle.widget", Declaration("right", "right")));
            await Send(View("either", "idle.widget", Declaration("right", "right", "left")));
            await Send(View("legacy-left", "idle.left", Declaration("left", "left", "right")));
            await Send(View("legacy-right", "idle.right", Declaration("right", "right")));
            return await Wait();
        }
        if (mode == "view-placement-updates")
        {
            object root = new
            {
                type = "row", width = 64, height = 36,
                children = new[] { "duplicate", "reorder", "lock", "unlock", "default" }
                    .Select(action => new { type = "button", actionId = action, text = action }).ToArray()
            };
            object declaration = Declaration("left", "left", "right");
            await Send(View("widget", "idle.widget", declaration, root));
            while (await Console.In.ReadLineAsync() is { } line)
            {
                using var json = JsonDocument.Parse(line);
                var message = json.RootElement;
                if (message.GetProperty("type").GetString() == "shutdown") return 0;
                if (message.GetProperty("type").GetString() != "viewAction") continue;
                declaration = message.GetProperty("actionId").GetString() switch
                {
                    "reorder" => Declaration("left", "right", "left"),
                    "lock" => Declaration("left", "left"),
                    "unlock" => Declaration("left", "left", "right"),
                    "default" => Declaration("right", "left", "right"),
                    _ => declaration
                };
                await Send(View("widget", "idle.widget", declaration, root));
            }
            return 0;
        }
        string issue = mode["view-placement-invalid-".Length..];
        string slot = issue switch
        {
            "legacy-left-missing" or "legacy-mismatch" => "idle.left",
            "legacy-right-missing" => "idle.right",
            "page" => "media.page", "content" => "idle.content", "tab" => "activity.tab",
            _ => "idle.widget"
        };
        var view = View("invalid", slot, Declaration("left", "left", "right"));
        if (issue.EndsWith("missing", StringComparison.Ordinal)) view.Remove("placement");
        switch (issue)
        {
            case "null": view["placement"] = null; break;
            case "string": view["placement"] = "left"; break;
            case "array": view["placement"] = new[] { "left", "right" }; break;
            case "empty-object": view["placement"] = new { }; break;
            case "missing-default": view["placement"] = new { allowedSides = new[] { "left" } }; break;
            case "missing-allowed": view["placement"] = new { defaultSide = "left" }; break;
            case "invalid-default": view["placement"] = Declaration("auto", "left", "right"); break;
            case "default-case": view["placement"] = Declaration("Left", "left", "right"); break;
            case "invalid-side": view["placement"] = Declaration("left", "left", "center"); break;
            case "duplicate-side": view["placement"] = Declaration("left", "left", "left"); break;
            case "empty-sides": view["placement"] = Declaration("left"); break;
            case "three-sides": view["placement"] = Declaration("left", "left", "right", "left"); break;
            case "allowed-type": view["placement"] = new { defaultSide = "left", allowedSides = "left" }; break;
            case "allowed-item-type": view["placement"] = new { defaultSide = "left", allowedSides = new object[] { 1 } }; break;
            case "default-not-member": view["placement"] = Declaration("right", "left"); break;
            case "extra-field": view["placement"] = new { defaultSide = "left", allowedSides = new[] { "left" }, auto = true }; break;
            case "legacy-mismatch": view["placement"] = Declaration("right", "left", "right"); break;
        }
        if (issue is "duplicate-default" or "duplicate-allowed")
        {
            string extra = issue == "duplicate-default" ? "\"defaultSide\":\"left\"," : "\"allowedSides\":[\"left\"],";
            Console.WriteLine("{\"type\":\"view\",\"id\":\"invalid\",\"slot\":\"idle.widget\",\"placement\":{" + extra +
                "\"defaultSide\":\"left\",\"allowedSides\":[\"left\",\"right\"]},\"root\":{\"type\":\"text\",\"text\":\"位置\"}}");
            await Console.Out.FlushAsync();
        }
        else await Send(view);
        return await Wait();
    }

    private static object Declaration(string defaultSide, params string[] allowedSides) => new { defaultSide, allowedSides };
    private static Dictionary<string, object?> View(string id, string slot, object placement, object? root = null) => new()
    {
        ["type"] = "view", ["id"] = id, ["slot"] = slot, ["placement"] = placement,
        ["root"] = root ?? new { type = "text", text = "位置", width = 28, height = 28 },
        ["title"] = slot == "activity.tab" ? "位置" : null
    };
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
