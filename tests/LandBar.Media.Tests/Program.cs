using LandBar.Services;
using Windows.Media;
using Windows.Media.Playback;

// Two metadata-only sessions are owned by this test. No audio is emitted and no existing player is controlled.
var prefix = $"LandBar isolated test {Guid.NewGuid():N}";
var players = new List<MediaPlayer>();
using var source = new WindowsMediaSource();
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var runner = source.StartAsync(stop.Token);
int emitted = 0;
source.EventReceived += item => { if (item.Title.StartsWith(prefix)) Interlocked.Increment(ref emitted); };
var checks = new List<string>();

try
{
    foreach (var suffix in new[] { "A", "B" })
    {
        var player = new MediaPlayer { IsMuted = true };
        players.Add(player);
        player.CommandManager.IsEnabled = false;
        var controls = player.SystemMediaTransportControls;
        controls.IsEnabled = true;
        controls.IsPlayEnabled = true;
        controls.IsPauseEnabled = true;
        controls.IsNextEnabled = true;
        controls.IsPreviousEnabled = true;
        controls.DisplayUpdater.Type = MediaPlaybackType.Music;
        controls.DisplayUpdater.MusicProperties.Title = prefix + suffix;
        controls.DisplayUpdater.MusicProperties.Artist = "LandBar test";
        controls.DisplayUpdater.Update();
        controls.PlaybackStatus = MediaPlaybackStatus.Playing;
        controls.ButtonPressed += (sender, args) =>
        {
            if (args.Button == SystemMediaTransportControlsButton.Pause) sender.PlaybackStatus = MediaPlaybackStatus.Paused;
            if (args.Button == SystemMediaTransportControlsButton.Play) sender.PlaybackStatus = MediaPlaybackStatus.Playing;
        };
    }

    await Until(() => Own().Length == 2, "two owned media sessions registered");
    var initial = Own();
    Check(initial.Select(session => session.SessionId).Distinct().Count() == 2, "Two sessions have distinct IDs");
    Check(initial.Select(session => session.AppId).Distinct().Count() == 1, "Two sessions share one source app ID");
    Check(initial.All(session => session.IsPlaying), "Both playing sessions are visible");
    Check(initial.All(session => session.CanTogglePlayPause), "Capabilities permit play/pause");
    int eventsBeforePause = Volatile.Read(ref emitted);
    var target = initial.Single(session => session.Title == prefix + "A");
    var untouched = initial.Single(session => session.Title == prefix + "B");
    Check(await source.TogglePlayPauseAsync(target.SessionId), "Exact session pause request succeeds");
    await Until(() => Own().Single(session => session.SessionId == target.SessionId).IsPlaying == false, "pause state arrives");
    Check(Own().Length == 2, "Paused session remains discoverable");
    Check(Own().Single(session => session.SessionId == untouched.SessionId).IsPlaying, "Other session remains playing");
    Check(await source.TogglePlayPauseAsync(target.SessionId), "Exact session resume request succeeds");
    await Until(() => Own().Single(session => session.SessionId == target.SessionId).IsPlaying, "resume state arrives");
    Check(Volatile.Read(ref emitted) == eventsBeforePause, "Pause/resume does not emit a second song alert");
    await Task.Delay(1200, stop.Token);
    Check(Own().Select(session => session.SessionId).Order().SequenceEqual(initial.Select(session => session.SessionId).Order()), "Session IDs remain stable across polls");
    players[0].Dispose();
    players.RemoveAt(0);
    await Until(() => Own().All(session => session.SessionId != target.SessionId), "closed session removed");
    Check(!await source.TogglePlayPauseAsync(target.SessionId), "Closed session ID never falls through to another player");
    Check(!await source.NextAsync("missing-session") && !await source.PreviousAsync("missing-session"), "Unknown transport IDs are rejected");
    Console.WriteLine($"{checks.Count}/{checks.Count} media integration checks passed");
    return 0;
}
catch (Exception exception)
{
    Console.WriteLine($"FAIL {exception.GetType().Name}: {exception.Message}");
    return 1;
}
finally
{
    foreach (var player in players) player.Dispose();
    stop.Cancel();
    await runner;
}

LandBar.Core.MediaSessionSnapshot[] Own() => source.Sessions.Where(session => session.Title.StartsWith(prefix)).ToArray();
void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}
async Task Until(Func<bool> condition, string name)
{
    for (int i = 0; i < 40 && !condition(); i++) await Task.Delay(150, stop.Token);
    if (!condition()) throw new TimeoutException(name);
}
