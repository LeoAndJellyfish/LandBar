namespace LandBar.Plugins;

public enum PluginState { Disabled, Starting, Running, Stopped, Faulted, Invalid }

public sealed record PluginStatus(
    string Id, string Name, string Version, string Description,
    PluginState State, string? Detail, string DirectoryPath);

public sealed record PluginAction(string Id, string Label);

public sealed record PluginCard(
    string PluginId, string Id, string Title, string Body, string Icon,
    double? Progress, IReadOnlyList<PluginAction> Actions,
    DateTimeOffset UpdatedAt, DateTimeOffset? ExpiresAt)
{
    public string StableKey => $"plugin:{PluginId}:{Id}";
}

/// <summary>Declarative UI owned by one provider run; selecting a view remains a host/user decision.</summary>
public sealed record PluginView(string PluginId, string Id, string Slot, PluginNode Root, DateTimeOffset UpdatedAt, string? Title = null)
{
    public string StableKey => $"view:{PluginId}:{Id}";
}

public sealed record PluginSprite(
    int PixelWidth, int PixelHeight, IReadOnlyList<string> Palette, IReadOnlyList<string> Frames, double Fps);

public sealed record PluginNode
{
    public required string Type { get; init; }
    public string? Id { get; init; }
    public string Text { get; init; } = "";
    public string? Color { get; init; }
    public string? Background { get; init; }
    public double? Width { get; init; }
    public double? Height { get; init; }
    public double FontSize { get; init; } = 15;
    public string Weight { get; init; } = "normal";
    public string Align { get; init; } = "start";
    public double Spacing { get; init; }
    public double Padding { get; init; }
    public double CornerRadius { get; init; }
    public double? Value { get; init; }
    public string? TrackColor { get; init; }
    public double Thickness { get; init; } = 3;
    public string? ActionId { get; init; }
    public IReadOnlyList<PluginNode> Children { get; init; } = Array.Empty<PluginNode>();
    public PluginSprite? Sprite { get; init; }
}

public static class PluginSlots
{
    public static IReadOnlyList<string> All { get; } = Array.AsReadOnly(new[]
    {
        "idle.left", "idle.right", "idle.content", "media.page", "notifications.page", "plugins.page",
        "controlCenter.page", "settings.page", "pluginManagement.page", "activity.tab"
    });
}

internal sealed record PluginManifest(
    string Id, string Name, string Version, string Description,
    string Command, string[] Arguments, int ProtocolVersion);
