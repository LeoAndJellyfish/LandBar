using LandBar.Core;

var tests = new (string Name, Action Run)[]
{
    ("A playing source leads a paused system current source", PlayingBeatsPausedCurrent),
    ("Concurrent playing sources are both preserved and current leads", ConcurrentPlayback),
    ("Paused sources remain available for explicit playback", PausedSources),
    ("Explicit session selection stays first until it disappears", SelectedSession),
    ("A missing preferred session falls back to live playback", ClosedSelection),
    ("Separate sessions from the same app remain distinct", SameApplication),
    ("Duplicate session IDs retain the strongest live state", DuplicateIds),
    ("Input ordering cannot cause card swaps", StableOrdering),
    ("Ordering preserves capabilities and source snapshots", PreserveSnapshot),
    ("Empty snapshots return an empty deck", EmptySnapshot),
    ("Invalid snapshots fail with explicit argument errors", InvalidInput)
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}: {exception.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static MediaSessionSnapshot Session(string id, string appName = "Player", bool playing = false,
    bool current = false, string? appId = null) =>
    new(id, appId ?? appName, appName, $"Track {id}", "Artist", null,
        playing, current, true, true, true);

static string Ids(IEnumerable<MediaSessionSnapshot> sessions) =>
    string.Join(",", sessions.Select(session => session.SessionId));

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'");
}

static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}

static void PlayingBeatsPausedCurrent()
{
    var sessions = new[] { Session("browser", "ego", current: true), Session("music", "网易云音乐", playing: true) };
    Equal("music,browser", Ids(MediaSessionSelection.Order(sessions)));
}

static void ConcurrentPlayback()
{
    var sessions = new[]
    {
        Session("music", "网易云音乐", playing: true),
        Session("browser", "ego", playing: true, current: true),
        Session("paused", "QQ 音乐")
    };
    var result = MediaSessionSelection.Order(sessions);
    Equal("browser,music,paused", Ids(result));
    Equal(2, result.Count(session => session.IsPlaying));
}

static void PausedSources()
{
    var sessions = new[] { Session("b", "B"), Session("c", "C", current: true), Session("a", "A") };
    Equal("c,a,b", Ids(MediaSessionSelection.Order(sessions)));
    Equal(3, MediaSessionSelection.Order(sessions).Count);
}

static void SelectedSession()
{
    var selected = Session("selected", "Browser");
    var current = Session("current", "Player", playing: true, current: true);
    Equal("selected,current", Ids(MediaSessionSelection.Order([current, selected], "selected")));
    Equal("current,selected", Ids(MediaSessionSelection.Order([current, selected])));
}

static void ClosedSelection()
{
    var old = Session("old");
    var live = Session("live", playing: true);
    var paused = Session("paused", current: true);
    Equal("old,live,paused", Ids(MediaSessionSelection.Order([live, old, paused], "old")));
    Equal("live,paused", Ids(MediaSessionSelection.Order([paused, live], "old")));
    Equal("", Ids(MediaSessionSelection.Order([], "old")));
}

static void SameApplication()
{
    var first = Session("tab-1", "Browser", playing: true, appId: "same-app");
    var second = Session("tab-2", "Browser", playing: true, appId: "same-app");
    Equal("tab-1,tab-2", Ids(MediaSessionSelection.Order([second, first])));
    Equal("tab-2,tab-1", Ids(MediaSessionSelection.Order([first, second], "tab-2")));
}

static void DuplicateIds()
{
    var paused = Session("same", "Player");
    var playing = paused with { IsPlaying = true };
    var current = playing with { IsCurrent = true };
    var other = Session("other", "Another player", playing: true);
    foreach (var permutation in Permutations(new[] { paused, current, playing, other }))
    {
        var result = MediaSessionSelection.Order(permutation);
        Equal("same,other", Ids(result));
        Equal(current, result[0]);
    }
}

static void StableOrdering()
{
    var values = new[]
    {
        Session("z", "Browser", playing: true),
        Session("a", "browser", playing: true),
        Session("b", "Music", playing: true),
        Session("p", "Paused", current: true),
        Session("q", "Paused")
    };
    string expected = Ids(MediaSessionSelection.Order(values));
    foreach (var permutation in Permutations(values))
        Equal(expected, Ids(MediaSessionSelection.Order(permutation)));
}

static void PreserveSnapshot()
{
    var readOnly = Session("locked") with { CanTogglePlayPause = false, CanGoNext = false, CanGoPrevious = false };
    var playing = Session("live", playing: true);
    var source = new[] { readOnly, playing };
    var result = MediaSessionSelection.Order(source);
    Equal("locked,live", Ids(source));
    Equal("live,locked", Ids(result));
    Equal(true, ReferenceEquals(readOnly, result[1]));
    Equal(false, result[1].CanTogglePlayPause);
    Equal(false, result[1].CanGoNext);
    Equal(false, result[1].CanGoPrevious);
    Equal(false, readOnly.IsPlaying);
    Equal(true, playing.IsPlaying);
}

static void EmptySnapshot() => Equal(0, MediaSessionSelection.Order([]).Count);

static void InvalidInput()
{
    Throws<ArgumentNullException>(() => MediaSessionSelection.Order(null!));
    Throws<ArgumentNullException>(() => MediaSessionSelection.Order([null!]));
    Throws<ArgumentException>(() => MediaSessionSelection.Order([Session(" ")]));
}

static IEnumerable<T[]> Permutations<T>(T[] items)
{
    if (items.Length == 0) { yield return []; yield break; }
    for (var i = 0; i < items.Length; i++)
    {
        var remainder = items.Where((_, index) => index != i).ToArray();
        foreach (var tail in Permutations(remainder))
            yield return [items[i], .. tail];
    }
}
