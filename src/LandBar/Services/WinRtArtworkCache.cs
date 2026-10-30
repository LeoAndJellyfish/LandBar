using System.IO;
using System.Security.Cryptography;
using System.Text;
using Windows.Storage.Streams;

namespace LandBar.Services;

internal static class WinRtArtworkCache
{
    private const int MaximumBytes = 5 * 1024 * 1024;
    private static readonly string DirectoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "Artwork");

    public static async Task<string?> SaveAsync(IRandomAccessStreamReference reference, string key,
        CancellationToken token)
    {
        Directory.CreateDirectory(DirectoryPath);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var path = Path.Combine(DirectoryPath, hash + ".img");
        if (File.Exists(path)) return path;
        using var stream = await reference.OpenReadAsync().AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        if (stream.Size == 0 || stream.Size > MaximumBytes) return null;
        using var reader = new DataReader(stream);
        var size = checked((uint)stream.Size);
        var loaded = await reader.LoadAsync(size).AsTask(token).WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
        if (loaded != size) return null;
        var bytes = new byte[size];
        reader.ReadBytes(bytes);
        await File.WriteAllBytesAsync(path, bytes, token).ConfigureAwait(false);
        // A fixed cache ceiling avoids indefinitely retaining old album artwork.
        foreach (var stale in new DirectoryInfo(DirectoryPath).EnumerateFiles("*.img")
                     .OrderByDescending(file => file.LastWriteTimeUtc).Skip(48))
            try { stale.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return path;
    }
}
