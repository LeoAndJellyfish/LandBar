namespace LandBar.Core;

/// <summary>An immutable view of one native media session, including paused sessions.</summary>
public sealed record MediaSessionSnapshot(
    string SessionId,
    string AppId,
    string AppName,
    string Title,
    string Artist,
    string? ArtworkPath,
    bool IsPlaying,
    bool IsCurrent,
    bool CanTogglePlayPause,
    bool CanGoNext,
    bool CanGoPrevious);
