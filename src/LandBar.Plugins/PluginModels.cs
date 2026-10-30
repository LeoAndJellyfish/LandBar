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

internal sealed record PluginManifest(
    string Id, string Name, string Version, string Description,
    string Command, string[] Arguments, int ProtocolVersion);
