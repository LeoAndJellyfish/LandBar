using System.IO;
using System.Security.Cryptography;
using Windows.Storage.Streams;

namespace LandBar.Services;

internal static class WinRtArtworkCache
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "Artwork");

    public static async Task<string?> SaveAsync(IRandomAccessStreamReference reference, CancellationToken token,
        string? cacheDirectory = null)
    {
        token.ThrowIfCancellationRequested();
        using var stream = await reference.OpenReadAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size > MaximumBytes) return null;
        using var reader = new DataReader(stream);
        var size = checked((uint)stream.Size);
        var loaded = await reader.LoadAsync(size).AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        if (loaded != size) return null;
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        token.ThrowIfCancellationRequested();

        // Providers can update their thumbnail after publishing the title. The image bytes, rather
        // than track metadata, own this immutable path; WPF can safely cache it by URI as well.
        var directory = cacheDirectory ?? DirectoryPath;
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var path = Path.Combine(directory, "content-" + hash + ".img");
        if (File.Exists(path)) return path;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{hash}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            try { File.Move(temporaryPath, path); }
            catch (IOException) when (File.Exists(path)) { /* A concurrent reader saved identical bytes. */ }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        TrimCache(directory, path);
        return path;
    }

    private static void TrimCache(string directory, string currentPath)
    {
        // Only scan on a new image, keeping stable playback free of repeated directory walks.
        try
        {
            foreach (var stale in new DirectoryInfo(directory).EnumerateFiles("*.img")
                         .Where(file => !string.Equals(file.FullName, currentPath, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(file => file.LastWriteTimeUtc).Skip(47))
                try { stale.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
