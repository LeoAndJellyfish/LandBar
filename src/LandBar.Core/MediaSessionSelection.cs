namespace LandBar.Core;

/// <summary>
/// Orders the current media snapshot without changing playback or hiding paused sources.
/// Explicit user selection wins while that session exists; otherwise playing sources lead.
/// </summary>
public static class MediaSessionSelection
{
    public static IReadOnlyList<MediaSessionSnapshot> Order(
        IEnumerable<MediaSessionSnapshot> sessions,
        string? preferredSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        var snapshot = sessions.ToArray();
        foreach (var session in snapshot)
        {
            ArgumentNullException.ThrowIfNull(session);
            ArgumentException.ThrowIfNullOrWhiteSpace(session.SessionId);
        }

        // Duplicate IDs can occur during a source refresh. Prefer the live playback state,
        // then use content to choose a deterministic representative independent of input order.
        var unique = snapshot.GroupBy(session => session.SessionId, StringComparer.Ordinal)
            .Select(group => group.OrderBy(PlaybackPriority)
                .ThenBy(session => session.AppName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(session => session.AppName, StringComparer.Ordinal)
                .ThenBy(session => session.AppId, StringComparer.Ordinal)
                .ThenBy(session => session.Title, StringComparer.Ordinal)
                .ThenBy(session => session.Artist, StringComparer.Ordinal)
                .ThenBy(session => session.ArtworkPath, StringComparer.Ordinal)
                .First());

        return unique
            .OrderBy(session => string.Equals(session.SessionId, preferredSessionId,
                StringComparison.Ordinal) ? -1 : PlaybackPriority(session))
            .ThenBy(session => session.AppName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(session => session.AppName, StringComparer.Ordinal)
            .ThenBy(session => session.SessionId, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PlaybackPriority(MediaSessionSnapshot session) =>
        session.IsPlaying ? session.IsCurrent ? 0 : 1 : session.IsCurrent ? 2 : 3;
}
