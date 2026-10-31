using System.IO;
using System.Security.Cryptography;
using LandBar.Core;
using LandBar.Services;
using Windows.Media.Control;
using Windows.Storage;
using Windows.Storage.Streams;

if (args.Contains("--fixture-host")) return await NativeMediaFixtureHost.RunHostAsync();

var checks = new List<string>();
var temporaryDirectory = Path.Combine(Path.GetTempPath(), "LandBar-media-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    if (!args.Contains("--media-only")) await TestArtworkCache();
    if (!args.Contains("--cache-only")) await TestNativeSessions();
    Console.WriteLine($"{checks.Count}/{checks.Count} media checks passed");
    return 0;
}
catch (Exception exception)
{
    Console.WriteLine($"FAIL {exception.GetType().Name}: {exception.Message}");
    return 1;
}
finally
{
    // Every path below is owned by this invocation; the user's artwork cache is never cleared.
    Directory.Delete(temporaryDirectory, recursive: true);
}

async Task TestArtworkCache()
{
    var cacheDirectory = Path.Combine(temporaryDirectory, "cache");
    var firstBytes = BitmapBytes(0x00336699);
    var secondBytes = BitmapBytes(0x00CC7744);
    var first = await ReferenceFor("cache-first.bmp", firstBytes);
    var second = await ReferenceFor("cache-second.bmp", secondBytes);
    var firstPath = await WinRtArtworkCache.SaveAsync(first, CancellationToken.None, cacheDirectory);
    var secondPath = await WinRtArtworkCache.SaveAsync(second, CancellationToken.None, cacheDirectory);
    Check(firstPath is not null && secondPath is not null, "Non-empty thumbnails are saved");
    Check(firstPath != secondPath, "Changed thumbnail bytes receive an independent immutable path");
    Check(File.ReadAllBytes(firstPath!).SequenceEqual(firstBytes) && File.ReadAllBytes(secondPath!).SequenceEqual(secondBytes),
        "Both cached files contain the exact corresponding thumbnail bytes");
    Check(Path.GetFileName(secondPath) == "content-" + Convert.ToHexString(SHA256.HashData(secondBytes)) + ".img",
        "Artwork identity comes from the content hash");
    var initialWrite = File.GetLastWriteTimeUtc(firstPath!);
    Check(await WinRtArtworkCache.SaveAsync(first, CancellationToken.None, cacheDirectory) == firstPath,
        "Unchanged thumbnail bytes keep the same path");
    Check(File.GetLastWriteTimeUtc(firstPath!) == initialWrite, "Unchanged content is not rewritten");
    var duplicate = await ReferenceFor("same-content-different-source.bmp", firstBytes);
    Check(await WinRtArtworkCache.SaveAsync(duplicate, CancellationToken.None, cacheDirectory) == firstPath,
        "Identical content from another source shares the immutable entry");

    var empty = await ReferenceFor("empty.img", []);
    Check(await WinRtArtworkCache.SaveAsync(empty, CancellationToken.None, cacheDirectory) is null,
        "An empty thumbnail does not create an artwork entry");
    var oversized = await ReferenceFor("oversized.img", new byte[5 * 1024 * 1024 + 1]);
    Check(await WinRtArtworkCache.SaveAsync(oversized, CancellationToken.None, cacheDirectory) is null,
        "Oversized thumbnails are rejected");

    var beforeCancellation = Directory.GetFiles(cacheDirectory).Order().ToArray();
    using (var cancellation = new CancellationTokenSource())
    {
        cancellation.Cancel();
        var canceled = false;
        try { await WinRtArtworkCache.SaveAsync(second, cancellation.Token, cacheDirectory); }
        catch (OperationCanceledException) { canceled = true; }
        Check(canceled, "Cancellation is observed even when the artwork is already cached");
    }
    Check(Directory.GetFiles(cacheDirectory).Order().SequenceEqual(beforeCancellation),
        "Cancellation leaves the cache unchanged");
    var concurrentBytes = BitmapBytes(0x006A347F);
    var concurrentReference = await ReferenceFor("cache-concurrent.bmp", concurrentBytes);
    var concurrent = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
        WinRtArtworkCache.SaveAsync(concurrentReference, CancellationToken.None, cacheDirectory)));
    Check(concurrent[0] is not null && concurrent.All(path => path == concurrent[0]) &&
        File.ReadAllBytes(concurrent[0]!).SequenceEqual(concurrentBytes),
        "Concurrent first saves agree on one complete content file");
    Check(!Directory.EnumerateFiles(cacheDirectory, "*.tmp").Any(), "Cache operations leave no temporary files");
}

async Task TestNativeSessions()
{
    // The producer lives in another process, matching a real desktop player and its COM boundary.
    var prefix = $"LandBar isolated test {Guid.NewGuid():N}";
    await using var fixture = await NativeMediaFixtureClient.StartAsync();
    using var source = new WindowsMediaSource();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
    int emitted = 0;
    source.EventReceived += item => { if (item.Title.StartsWith(prefix)) Interlocked.Increment(ref emitted); };
    Task runner = Task.CompletedTask;
    var firstBytes = BitmapBytes(0x001C9C66);
    var secondBytes = BitmapBytes(0x00C14A44);
    var updatedBytes = BitmapBytes(0x00416ABB);
    var finalBytes = BitmapBytes(0x00A28443);
    var first = await WriteArtwork("session-first.bmp", firstBytes);
    var second = await WriteArtwork("session-second.bmp", secondBytes);
    var updated = await WriteArtwork("session-updated.bmp", updatedBytes);
    var final = await WriteArtwork("session-final.bmp", finalBytes);
    const string artist = "LandBar test";
    const string album = "Isolated artwork regression";
    try
    {
        await fixture.CreateAsync(0, prefix + "A", artist, album, first);
        await fixture.CreateAsync(1, prefix + "B", artist, album, second);

        runner = source.StartAsync(stop.Token);
        await Until(() => Own().Length == 2, "two owned media sessions registered");
        var initial = Own();
        Check(initial.Select(session => session.SessionId).Distinct().Count() == 2, "Two sessions have distinct IDs");
        Check(initial.Select(session => session.AppId).Distinct().Count() == 1, "Two sessions share one source app ID");
        Check(initial.All(session => session.IsPlaying), "Both playing sessions are visible");
        Check(initial.All(session => session.CanTogglePlayPause), "Capabilities permit play/pause");
        var target = initial.Single(session => session.Title == prefix + "A");
        var untouched = initial.Single(session => session.Title == prefix + "B");
        await Until(() => HasBytes(Find(target.SessionId), firstBytes) && HasBytes(Find(untouched.SessionId), secondBytes),
            "each session receives its initial thumbnail");
        Check(HasBytes(Find(target.SessionId), firstBytes) && HasBytes(Find(untouched.SessionId), secondBytes),
            "Initial thumbnails belong to the correct independent sessions");

        int eventsBeforePause = Volatile.Read(ref emitted);
        Check(await FromSta(() => source.TogglePlayPauseAsync(target.SessionId)), "STA caller can pause the exact native session");
        await Until(() => Find(target.SessionId)?.IsPlaying == false, "pause state arrives");
        Check(Own().Length == 2, "Paused session remains discoverable");
        Check(Find(untouched.SessionId)?.IsPlaying == true, "Other session remains playing");
        Check(await source.TogglePlayPauseAsync(target.SessionId), "Exact session resume request succeeds");
        await Until(() => Find(target.SessionId)?.IsPlaying == true, "resume state arrives");
        Check(Volatile.Read(ref emitted) == eventsBeforePause, "Pause/resume does not emit a second song alert");

        var originalPath = Find(target.SessionId)!.ArtworkPath;
        await fixture.UpdateAsync(0, prefix + "A", artist, album, updated);
        await Until(() => HasBytes(Find(target.SessionId), updatedBytes), "thumbnail-only metadata update arrives");
        Check(Find(target.SessionId)!.ArtworkPath != originalPath, "Thumbnail-only update replaces the image URI");
        Check(Find(target.SessionId)!.Title == target.Title && Find(target.SessionId)!.Artist == target.Artist,
            "Artwork can change while the title and artist stay unchanged");
        Check(HasBytes(Find(untouched.SessionId), secondBytes), "Updating one thumbnail does not alter the other session");
        Check(Volatile.Read(ref emitted) == eventsBeforePause, "Artwork-only updates do not emit another song alert");

        await fixture.UpdateAsync(0, prefix + "C", artist, album, updated);
        await Until(() => Find(target.SessionId)?.Title == prefix + "C" && HasBytes(Find(target.SessionId), updatedBytes),
            "next track arrives with the provider's previous thumbnail");
        int eventsAfterTrack = Volatile.Read(ref emitted);
        Check(eventsAfterTrack == eventsBeforePause + 1, "Changing a track emits exactly one song alert");
        await fixture.UpdateAsync(0, prefix + "C", artist, album, final);
        await Until(() => HasBytes(Find(target.SessionId), finalBytes), "late cover for the new track arrives");
        Check(HasBytes(Find(target.SessionId), finalBytes), "Late artwork replaces the old cover initially supplied for a new track");
        Check(Volatile.Read(ref emitted) == eventsAfterTrack, "Late artwork correction does not repeat the track alert");

        await fixture.UpdateAsync(0, prefix + "C", artist, album, null);
        await Until(() => Find(target.SessionId)?.ArtworkPath is null, "withdrawn thumbnail is cleared");
        Check(Find(target.SessionId) is { ArtworkPath: null }, "Withdrawing a thumbnail clears the previous image");

        // Equal app IDs and track metadata must still preserve independent provider thumbnails.
        await fixture.UpdateAsync(0, prefix + "shared", artist, album, first);
        await fixture.UpdateAsync(1, prefix + "shared", artist, album, second);
        await Until(() => Own().All(session => session.Title == prefix + "shared") &&
            HasBytes(Find(target.SessionId), firstBytes) && HasBytes(Find(untouched.SessionId), secondBytes),
            "matching metadata sessions retain their own images");
        Check(Find(target.SessionId)!.ArtworkPath != Find(untouched.SessionId)!.ArtworkPath,
            "Equal source, title, artist and album do not merge different covers");

        await Task.Delay(1200, stop.Token);
        Check(Own().Select(session => session.SessionId).Order().SequenceEqual(initial.Select(session => session.SessionId).Order()),
            "Session IDs remain stable across polls");
        await fixture.CloseAsync(0);
        // Windows may recreate the surviving native COM session when another HWND closes.
        // Identify that survivor by its independent artwork rather than its previous IUnknown.
        await Until(() => Own().Length == 1 && Find(target.SessionId) is null && Own()[0].IsPlaying && HasBytes(Own()[0], secondBytes),
            "closed session is removed while the other session stays playing");
        Check(Own().Length == 1 && Own()[0].IsPlaying && HasBytes(Own()[0], secondBytes),
            "Closing one native window preserves the other playing session");
        Check(!await source.TogglePlayPauseAsync(target.SessionId), "Closed session ID never falls through to another player");
        Check(!await source.NextAsync("missing-session") && !await source.PreviousAsync("missing-session"),
            "Unknown transport IDs are rejected");
        await fixture.DisposeAsync();
        await Until(() => Own().Length == 0, "producer shutdown removes every owned session");
        Check(fixture.HasExited && Own().Length == 0, "Fixture process exits cleanly and releases every media session");
    }
    finally
    {
        try { await fixture.DisposeAsync(); }
        finally { stop.Cancel(); await runner; }
    }

    MediaSessionSnapshot[] Own() => source.Sessions.Where(session => session.Title.StartsWith(prefix)).ToArray();
    MediaSessionSnapshot? Find(string id) => Own().SingleOrDefault(session => session.SessionId == id);
    async Task Until(Func<bool> condition, string name)
    {
        for (int i = 0; i < 120 && !condition(); i++) await Task.Delay(150, stop.Token);
        if (!condition())
        {
            var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            int nativeOwned = 0, failedProperties = 0;
            foreach (var session in manager.GetSessions())
            {
                try
                {
                    var properties = await session.TryGetMediaPropertiesAsync();
                    if (properties.Title.StartsWith(prefix)) nativeOwned++;
                }
                catch { failedProperties++; }
            }
            throw new TimeoutException($"{name} (source: {source.Status}; native={manager.GetSessions().Count}; owned={nativeOwned}; propertyFailures={failedProperties})");
        }
    }
}

async Task<RandomAccessStreamReference> ReferenceFor(string name, byte[] bytes)
{
    var path = await WriteArtwork(name, bytes);
    return RandomAccessStreamReference.CreateFromFile(await StorageFile.GetFileFromPathAsync(path));
}

async Task<string> WriteArtwork(string name, byte[] bytes)
{
    var path = Path.Combine(temporaryDirectory, name);
    await File.WriteAllBytesAsync(path, bytes);
    return path;
}

static byte[] BitmapBytes(uint color)
{
    // Valid 2 x 2, 32-bit BMP; a per-invocation nonce avoids reusing another test's disk entry.
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream);
    writer.Write((ushort)0x4D42); writer.Write(70); writer.Write(0); writer.Write(54);
    writer.Write(40); writer.Write(2); writer.Write(2); writer.Write((ushort)1); writer.Write((ushort)32);
    writer.Write(0); writer.Write(16); writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(0);
    writer.Write(color | 0xFF000000); writer.Write(color | 0xFF000000);
    var nonce = Guid.NewGuid().ToByteArray();
    writer.Write(BitConverter.ToUInt32(nonce, 0) | 0xFF000000);
    writer.Write(BitConverter.ToUInt32(nonce, 4) | 0xFF000000);
    return stream.ToArray();
}

static bool HasBytes(MediaSessionSnapshot? session, byte[] expected)
{
    try { return session?.ArtworkPath is { } path && File.ReadAllBytes(path).SequenceEqual(expected); }
    catch (IOException) { return false; }
}

void Check(bool value, string name)
{
    if (!value) throw new InvalidOperationException(name);
    checks.Add(name);
    Console.WriteLine("PASS " + name);
}

static Task<T> FromSta<T>(Func<Task<T>> action)
{
    var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
    var thread = new Thread(() =>
    {
        try { completion.TrySetResult(action().GetAwaiter().GetResult()); }
        catch (Exception exception) { completion.TrySetException(exception); }
    }) { IsBackground = true, Name = "LandBar STA media caller" };
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    return completion.Task;
}
