using LandBar.Core;

var failures = new List<string>();
var tests = new (string Name, Action Run)[]
{
    ("Two visible cards and deterministic rotation", Rotation),
    ("Previous and next are inverses for three, five, and six cards", NavigationInverse),
    ("Previous navigation preserves exposure and works while paused", PreviousPreservesExposure),
    ("Notification IDs stay deduplicated after dismissal", Deduplication),
    ("Songs replace the same app without losing other apps", MediaReplacement),
    ("Capacity and history remain bounded", BoundedCapacity),
    ("Hover pause freezes exposure and rotation", Pause),
    ("Queued events receive their entire eight seconds", ExposureFairness),
    ("Large ticks match fine grained ticks", LongTick),
    ("Dismiss fills the vacant slot immediately", Dismiss),
    ("Clear prevents polling from resurrecting dismissed notifications", Clear),
    ("Single cards do not rotate or wait", SingleCard),
    ("Invalid configuration and elapsed duration are rejected", Validation),
    ("Random source arrivals and subsecond ticks remain bounded", InterleavedSources),
    ("Inactivity expires at exactly five seconds and resets only on activity", InactivityDeadline),
    ("Inactivity stops after collapse and rearms on the next opening", InactivityRearm),
    ("Never expiry preserves cards while rotation and manual clear continue", NeverExpiry),
    ("Re-enabling expiry resumes the retained display budget", ResumeExpiry),
    ("Configurable inactivity supports one to five seconds and never", InactivityOptions),
    ("Clearing notifications removes mixed active, waiting and history entries only", ClearNotificationsMixed),
    ("Clearing notifications retains media display budgets and notification deduplication", ClearNotificationsBudgets),
    ("Clearing notification history alone retains the current media rotation", ClearNotificationsHistoryOnly),
    ("Clearing notifications remains available while paused or expiry is disabled", ClearNotificationsPaused),
    ("Plugin updates retain exposure and never reopen collapsed cards", PluginUpdates),
    ("Provider withdrawal removes active and historical cards without clearing other sources", PluginWithdrawal),
    ("Compact widgets consume only their occupied side tracks", CompactLayoutChecks.OccupiedTracks),
    ("Removing and restoring compact widgets reclaims and restores their exact widths", CompactLayoutChecks.RemovalAndRestoration),
    ("Compact narrow layouts preserve whole widgets and recover media detail", CompactLayoutChecks.Constrained),
    ("Compact layout budgets remain finite and bounded across display widths", CompactLayoutChecks.Budgets)
};

foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures.Add(name); Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures.Count}/{tests.Length} passed");
return failures.Count == 0 ? 0 : 1;

static IslandEvent Notification(string id) => new(id, IslandEventKind.Notification, "微信", id, "测试消息", DateTimeOffset.UnixEpoch);
static IslandEvent Song(string id, string app = "music") => new(id, IslandEventKind.Media, "音乐", id, "演唱者", DateTimeOffset.UnixEpoch, app);
static string Ids(IslandScheduler scheduler) => string.Join(",", scheduler.VisibleItems.Select(x => x.Id));
static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected '{expected}', got '{actual}'");
}
static void True(bool value)
{
    if (!value) throw new InvalidOperationException("Expected true");
}
static void PluginUpdates()
{
    var scheduler = new IslandScheduler();
    var card = new IslandEvent("plugin:timer:focus", IslandEventKind.Plugin, "专注", "25:00", "", DateTimeOffset.UnixEpoch);
    scheduler.Push(card);
    scheduler.Tick(TimeSpan.FromSeconds(7));
    scheduler.Update(card with { Title = "24:53" });
    Equal("24:53", scheduler.VisibleItems.Single().Title);
    Equal(1, scheduler.History.Count);
    scheduler.Tick(TimeSpan.FromSeconds(1));
    Equal(0, scheduler.TotalCount);
    scheduler.Update(card with { Title = "24:52" });
    Equal(0, scheduler.TotalCount);
    Equal("24:52", scheduler.History.Single().Title);
    scheduler.Push(card with { Id = "plugin:timer:next" });
    scheduler.Clear();
    scheduler.Update(card with { Id = "plugin:timer:next", Title = "Updated" });
    Equal(0, scheduler.TotalCount);
}
static void PluginWithdrawal()
{
    var scheduler = new IslandScheduler();
    scheduler.Push(Notification("notice"));
    scheduler.Push(new("plugin:timer:a", IslandEventKind.Plugin, "专注", "计时", "", DateTimeOffset.UnixEpoch));
    scheduler.Push(Song("song"));
    scheduler.ClearNotifications();
    Equal(2, scheduler.TotalCount);
    True(scheduler.Remove("plugin:timer:a"));
    Equal("song", scheduler.VisibleItems.Single().Id);
    True(scheduler.History.All(item => item.Kind != IslandEventKind.Plugin));
    True(!scheduler.Remove("plugin:timer:a"));
}
static void InactivityDeadline()
{
    var idle = new IslandInactivityTimer();
    Equal(false, idle.IsExpired(TimeSpan.FromHours(1)));
    idle.Reset(TimeSpan.FromSeconds(10));
    Equal(false, idle.IsExpired(TimeSpan.FromMilliseconds(14999)));
    Equal(true, idle.IsExpired(TimeSpan.FromSeconds(15)));
    // A late interaction starts a complete new reading/interaction interval.
    idle.Reset(TimeSpan.FromMilliseconds(14999));
    Equal(false, idle.IsExpired(TimeSpan.FromSeconds(15)));
    Equal(false, idle.IsExpired(TimeSpan.FromMilliseconds(19998)));
    Equal(true, idle.IsExpired(TimeSpan.FromMilliseconds(19999)));
}
static void InactivityRearm()
{
    var idle = new IslandInactivityTimer();
    idle.Reset(TimeSpan.Zero);
    idle.Stop();
    Equal(false, idle.IsExpired(TimeSpan.FromHours(1)));
    idle.Reset(TimeSpan.FromHours(2));
    Equal(false, idle.IsExpired(TimeSpan.FromHours(2) + TimeSpan.FromMilliseconds(4999)));
    Equal(true, idle.IsExpired(TimeSpan.FromHours(2) + TimeSpan.FromSeconds(5)));
}
static void NeverExpiry()
{
    var q = new IslandScheduler { ExpireVisibleItems = false };
    foreach (var id in new[] { "a", "b", "c" }) q.Push(Notification(id));
    q.Tick(TimeSpan.FromSeconds(4)); Equal("c,a", Ids(q));
    q.Tick(TimeSpan.FromSeconds(4)); Equal("b,c", Ids(q));
    q.Tick(TimeSpan.FromSeconds(4)); Equal("a,b", Ids(q)); Equal(3, q.TotalCount);
    q.Tick(TimeSpan.FromHours(1)); Equal(3, q.TotalCount); Equal(3, q.History.Count);
    True(q.Dismiss("a")); Equal(2, q.TotalCount);
    q.Clear(); Equal(0, q.TotalCount); Equal(false, q.Push(Notification("b")));
}
static void ResumeExpiry()
{
    var q = new IslandScheduler(); q.Push(Notification("a"));
    q.Tick(TimeSpan.FromSeconds(3));
    q.ExpireVisibleItems = false;
    q.Tick(TimeSpan.FromHours(1)); Equal(1, q.TotalCount);
    q.ExpireVisibleItems = true;
    q.Tick(TimeSpan.FromMilliseconds(4999)); Equal(1, q.TotalCount);
    q.Tick(TimeSpan.FromMilliseconds(1)); Equal(0, q.TotalCount);
}
static void InactivityOptions()
{
    var idle = new IslandInactivityTimer();
    foreach (int seconds in Enumerable.Range(1, 5))
    {
        var now = TimeSpan.FromSeconds(100 + seconds);
        idle.Configure(TimeSpan.FromSeconds(seconds), now);
        Equal(false, idle.IsExpired(now + TimeSpan.FromSeconds(seconds) - TimeSpan.FromTicks(1)));
        Equal(true, idle.IsExpired(now + TimeSpan.FromSeconds(seconds)));
    }
    idle.Configure(null, TimeSpan.FromSeconds(200));
    Equal<TimeSpan?>(null, idle.Deadline);
    Equal(false, idle.IsExpired(TimeSpan.FromDays(2)));
    idle.Reset(TimeSpan.FromSeconds(205));
    Equal<TimeSpan?>(null, idle.Deadline);
    idle.Configure(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(210));
    Equal<TimeSpan?>(TimeSpan.FromSeconds(212), idle.Deadline);
    Throws<ArgumentOutOfRangeException>(() => idle.Configure(TimeSpan.Zero, TimeSpan.Zero));
}
static void Rotation()
{
    var q = new IslandScheduler();
    foreach (var id in new[] { "a", "b", "c", "d", "e" }) q.Push(Notification(id));
    Equal("a,b", Ids(q)); Equal(3, q.PendingCount); Equal(3, q.PageCount);
    q.Tick(TimeSpan.FromMilliseconds(3999)); Equal("a,b", Ids(q));
    q.Tick(TimeSpan.FromMilliseconds(1)); Equal("c,d", Ids(q));
    q.NextPage(); Equal("e,a", Ids(q));
}
static void NavigationInverse()
{
    foreach (var count in new[] { 3, 5, 6 })
    {
        var q = new IslandScheduler();
        for (var i = 0; i < count; i++) q.Push(Notification(i.ToString()));
        // Check every ring position, including the odd-length wraparound.
        for (var position = 0; position < count; position++)
        {
            var before = Ids(q);
            var page = q.CurrentPage;
            q.NextPage(); q.PreviousPage();
            Equal(before, Ids(q)); Equal(page, q.CurrentPage);
            q.PreviousPage(); q.NextPage();
            Equal(before, Ids(q)); Equal(page, q.CurrentPage);
            Equal(count, q.TotalCount); Equal(count - 2, q.PendingCount);
            q.NextPage();
        }
    }

    var single = new IslandScheduler(maxVisible: 1);
    foreach (var id in new[] { "a", "b", "c" }) single.Push(Notification(id));
    single.PreviousPage(); Equal("c", Ids(single));
    single.NextPage(); Equal("a", Ids(single));
    var pair = new IslandScheduler(); pair.Push(Notification("a")); pair.Push(Notification("b"));
    pair.PreviousPage(); Equal("a,b", Ids(pair));
}
static void PreviousPreservesExposure()
{
    var q = new IslandScheduler();
    foreach (var id in new[] { "a", "b", "c" }) q.Push(Notification(id));
    q.Tick(TimeSpan.FromSeconds(3));
    q.SetPaused(true); q.PreviousPage(); Equal("b,c", Ids(q));
    q.Tick(TimeSpan.FromHours(1)); Equal("b,c", Ids(q));
    q.NextPage(); Equal("a,b", Ids(q));
    q.SetPaused(false);
    // Two visible cards still have their original three seconds of exposure.
    // Use explicit navigation before each rotation boundary to keep them visible.
    q.Tick(TimeSpan.FromSeconds(3)); q.PreviousPage(); q.NextPage();
    q.Tick(TimeSpan.FromSeconds(2)); Equal("c", Ids(q)); Equal(1, q.TotalCount);
}
static void Deduplication()
{
    var q = new IslandScheduler();
    True(q.Push(Notification("a"))); Equal(false, q.Push(Notification("a")));
    q.Dismiss("a"); Equal(false, q.Push(Notification("a"))); Equal(0, q.TotalCount);
    // The finite cache permits reuse of a genuinely old OS notification ID.
    for (var i = 0; i < 256; i++) q.Push(Notification($"next-{i}"));
    True(q.Push(Notification("a")));
}
static void MediaReplacement()
{
    var q = new IslandScheduler();
    q.Push(Song("song-a")); q.Push(Notification("chat")); q.Push(Song("other-app", "other"));
    q.Tick(TimeSpan.FromSeconds(3));
    True(q.Push(Song("song-b"))); Equal(3, q.TotalCount); Equal("song-b,chat", Ids(q));
    Equal(false, q.Push(Song("song-b"))); True(q.Push(Song("song-a")));
    Equal(3, q.TotalCount); Equal("song-a,chat", Ids(q));
    var paused = new IslandScheduler();
    paused.Push(Song("old")); paused.Tick(TimeSpan.FromSeconds(7)); paused.Push(Song("new"));
    paused.Tick(TimeSpan.FromSeconds(7)); Equal(1, paused.TotalCount);
    paused.Tick(TimeSpan.FromSeconds(1)); Equal(0, paused.TotalCount);
}
static void BoundedCapacity()
{
    var q = new IslandScheduler();
    for (var i = 0; i < 100; i++) q.Push(Notification(i.ToString()));
    Equal(30, q.TotalCount); Equal(28, q.PendingCount); Equal(30, q.History.Count);
    Equal(70, q.DroppedCount); Equal("0,1", Ids(q));
    q.NextPage(); Equal("72,73", Ids(q));
    q.ClearHistory(); Equal(0, q.History.Count);
}
static void Pause()
{
    var q = new IslandScheduler();
    q.Push(Notification("a")); q.Push(Notification("b")); q.Push(Notification("c"));
    q.Tick(TimeSpan.FromSeconds(3)); q.SetPaused(true); q.Tick(TimeSpan.FromHours(1));
    Equal("a,b", Ids(q)); Equal(3, q.TotalCount);
    q.SetPaused(false); q.Tick(TimeSpan.FromSeconds(1)); Equal("c,a", Ids(q));
    q.SetPaused(true); q.NextPage(); Equal("b,c", Ids(q));
}
static void ExposureFairness()
{
    var q = new IslandScheduler();
    const int count = 29;
    var exposure = Enumerable.Range(0, count).ToDictionary(i => i.ToString(), _ => 0);
    for (var i = 0; i < count; i++) q.Push(Notification(i.ToString()));
    for (var seconds = 0; seconds < 200 && q.TotalCount > 0; seconds++)
    {
        foreach (var item in q.VisibleItems) exposure[item.Id]++;
        q.Tick(TimeSpan.FromSeconds(1));
    }
    Equal(0, q.TotalCount);
    foreach (var (id, seconds) in exposure)
        if (seconds != 8) throw new InvalidOperationException($"Card {id} received {seconds}s, expected 8s");
}
static void LongTick()
{
    var coarse = new IslandScheduler(); var fine = new IslandScheduler();
    foreach (var id in Enumerable.Range(0, 7).Select(i => i.ToString()))
    { coarse.Push(Notification(id)); fine.Push(Notification(id)); }
    coarse.Tick(TimeSpan.FromSeconds(13.5));
    for (var i = 0; i < 135; i++) fine.Tick(TimeSpan.FromMilliseconds(100));
    Equal(Ids(fine), Ids(coarse)); Equal(fine.TotalCount, coarse.TotalCount);
    coarse.Tick(TimeSpan.FromDays(1)); Equal(0, coarse.TotalCount);
}
static void Dismiss()
{
    var q = new IslandScheduler();
    foreach (var id in new[] { "a", "b", "c", "d" }) q.Push(Notification(id));
    True(q.Dismiss("a")); Equal("b,c", Ids(q));
    True(q.Dismiss("d")); Equal(2, q.TotalCount);
    Equal(false, q.Dismiss("missing"));
}
static void Clear()
{
    var q = new IslandScheduler(); q.Push(Notification("a")); q.Clear();
    Equal(0, q.TotalCount); Equal(0, q.CurrentPage); Equal(false, q.Push(Notification("a")));
    q.Tick(TimeSpan.FromDays(1)); Equal(0, q.TotalCount);
}
static void ClearNotificationsMixed()
{
    var q = new IslandScheduler();
    q.Push(Notification("visible-chat"));
    q.Push(Song("song-a", "player-a"));
    q.Push(Notification("queued-chat"));
    q.Push(Song("song-b", "player-b"));
    q.Push(Song("song-c", "player-c"));
    q.Push(Notification("dismissed-chat"));
    q.Dismiss("dismissed-chat");
    long revision = q.Revision;
    q.ClearNotifications();
    Equal("song-a,song-b", Ids(q)); Equal(3, q.TotalCount); Equal(1, q.PendingCount);
    Equal(2, q.PageCount); Equal(1, q.CurrentPage); Equal(revision + 1, q.Revision);
    Equal("song-a,song-b,song-c", string.Join(",", q.History.Select(item => item.Id)));
    q.NextPage(); Equal("song-c,song-a", Ids(q));
    revision = q.Revision;
    q.ClearNotifications();
    Equal(revision, q.Revision); Equal("song-c,song-a", Ids(q)); Equal(2, q.CurrentPage);

    var notifications = new IslandScheduler();
    foreach (var id in new[] { "a", "b", "c", "d", "e" }) notifications.Push(Notification(id));
    notifications.NextPage(); notifications.NextPage(); Equal(3, notifications.CurrentPage);
    notifications.ClearNotifications();
    Equal(0, notifications.TotalCount); Equal(0, notifications.History.Count); Equal(0, notifications.CurrentPage);
    notifications.Tick(TimeSpan.FromHours(1)); Equal(0, notifications.TotalCount);
    revision = notifications.Revision; notifications.ClearNotifications(); Equal(revision, notifications.Revision);
}
static void ClearNotificationsBudgets()
{
    var q = new IslandScheduler();
    q.Push(Song("existing-song", "player-a"));
    q.Push(Notification("old-chat"));
    q.Push(Song("queued-song", "player-b"));
    q.Tick(TimeSpan.FromSeconds(3));
    q.ClearNotifications();
    Equal("existing-song,queued-song", Ids(q));
    Equal(false, q.Push(Notification("old-chat")));
    q.Tick(TimeSpan.FromMilliseconds(4999)); Equal(2, q.TotalCount);
    q.Tick(TimeSpan.FromMilliseconds(1)); Equal("queued-song", Ids(q));
    q.Tick(TimeSpan.FromMilliseconds(2999)); Equal(1, q.TotalCount);
    q.Tick(TimeSpan.FromMilliseconds(1)); Equal(0, q.TotalCount);
    True(q.Push(Notification("new-chat"))); Equal("new-chat", Ids(q));
}
static void ClearNotificationsHistoryOnly()
{
    var q = new IslandScheduler();
    q.Push(Notification("historic-chat")); q.Dismiss("historic-chat");
    foreach (var id in new[] { "a", "b", "c" }) q.Push(Song(id, $"player-{id}"));
    q.Tick(TimeSpan.FromSeconds(3));
    q.ClearNotifications();
    Equal("a,b", Ids(q)); Equal(3, q.TotalCount);
    q.Tick(TimeSpan.FromSeconds(1)); Equal("c,a", Ids(q));
    Equal(false, q.Push(Notification("historic-chat")));
}
static void ClearNotificationsPaused()
{
    var q = new IslandScheduler { ExpireVisibleItems = false };
    q.Push(Notification("first")); q.Push(Song("song-a", "player-a"));
    q.Push(Notification("second")); q.Push(Song("song-b", "player-b"));
    q.SetPaused(true);
    q.ClearNotifications();
    True(q.IsPaused); Equal(false, q.ExpireVisibleItems); Equal("song-a,song-b", Ids(q));
    q.Tick(TimeSpan.FromDays(1)); Equal("song-a,song-b", Ids(q));
    q.SetPaused(false); q.Tick(TimeSpan.FromMinutes(1)); Equal(2, q.TotalCount);
    Equal(2, q.History.Count);
}
static void SingleCard()
{
    var q = new IslandScheduler(); q.Push(Notification("a"));
    q.Tick(TimeSpan.FromSeconds(7.99)); Equal("a", Ids(q));
    q.Tick(TimeSpan.FromSeconds(.01)); Equal(0, q.TotalCount);
}
static void Validation()
{
    Throws<ArgumentOutOfRangeException>(() => new IslandScheduler(maxVisible: 3));
    Throws<ArgumentOutOfRangeException>(() => new IslandScheduler(capacity: 1));
    Throws<ArgumentOutOfRangeException>(() => new IslandScheduler(displayDuration: TimeSpan.Zero));
    Throws<ArgumentOutOfRangeException>(() => new IslandScheduler().Tick(TimeSpan.FromSeconds(-1)));
    Throws<ArgumentException>(() => new IslandScheduler().Push(Notification("")));
}
static void InterleavedSources()
{
    var q = new IslandScheduler();
    var random = new Random(831524);
    for (var operation = 0; operation < 200_000; operation++)
    {
        switch (random.Next(12))
        {
            case 0: case 1: case 2:
                q.Push(Notification($"notification-{random.Next(500)}")); break;
            case 3:
                q.Push(Song($"track-{random.Next(20)}", $"app-{random.Next(4)}")); break;
            case 4:
                q.SetPaused(random.Next(2) == 0); break;
            case 5: q.NextPage(); break;
            case 6: q.PreviousPage(); break;
            case 7:
                if (q.VisibleItems.Count > 0) q.Dismiss(q.VisibleItems[0].Id);
                break;
            case 8: q.Clear(); break;
            default:
                q.Tick(TimeSpan.FromTicks(random.NextInt64(0, TimeSpan.FromSeconds(30).Ticks)));
                break;
        }
        True(q.TotalCount <= 30);
        True(q.VisibleItems.Count <= 2);
        Equal(q.TotalCount, q.VisibleItems.Count + q.PendingCount);
        True(q.History.Count <= 30);
    }
    q.SetPaused(false); q.Tick(TimeSpan.FromDays(1)); Equal(0, q.TotalCount);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); } catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}");
}
