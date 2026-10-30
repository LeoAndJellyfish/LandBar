namespace LandBar.Core;

public enum IslandEventKind
{
    Notification,
    Media
}

public sealed record IslandEvent(
    string Id,
    IslandEventKind Kind,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset CreatedAt,
    string? AppId = null,
    string? ArtworkPath = null);
