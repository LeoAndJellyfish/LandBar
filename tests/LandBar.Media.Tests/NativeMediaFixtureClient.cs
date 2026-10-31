using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;

/// <summary>Owns the isolated Win32 media producer; all transport commands use the real GSMTC API.</summary>
internal sealed class NativeMediaFixtureClient : IAsyncDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stderr;
    private bool _disposed;
    public bool HasExited => _process.HasExited;

    private NativeMediaFixtureClient(Process process)
    {
        _process = process;
        _stderr = process.StandardError.ReadToEndAsync();
    }

    public static async Task<NativeMediaFixtureClient> StartAsync()
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("No test executable path");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--fixture-host");
        var client = new NativeMediaFixtureClient(Process.Start(start) ?? throw new InvalidOperationException("Fixture process did not start"));
        try
        {
            await client.ReadAcknowledgementAsync();
            return client;
        }
        catch { await client.DisposeAsync(); throw; }
    }

    public Task CreateAsync(int index, string title, string artist, string album, string? artworkPath) =>
        SendAsync(new { op = "create", index, title, artist, album, artworkPath });
    public Task UpdateAsync(int index, string title, string artist, string album, string? artworkPath) =>
        SendAsync(new { op = "update", index, title, artist, album, artworkPath });
    public Task CloseAsync(int index) => SendAsync(new { op = "close", index });

    private async Task SendAsync(object command)
    {
        await _process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(command));
        await _process.StandardInput.FlushAsync();
        await ReadAcknowledgementAsync();
    }

    private async Task ReadAcknowledgementAsync()
    {
        var line = await _process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(8));
        if (line is null)
            throw new IOException("Fixture exited before acknowledging: " + await _stderr.WaitAsync(TimeSpan.FromSeconds(2)));
        using var document = JsonDocument.Parse(line);
        if (!document.RootElement.GetProperty("ok").GetBoolean())
            throw new InvalidOperationException("Fixture command failed: " + document.RootElement.GetProperty("error").GetString());
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (!_process.HasExited)
            {
                try { await SendAsync(new { op = "exit" }); }
                finally { _process.StandardInput.Close(); }
                await _process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(8));
            }
            if (_process.ExitCode != 0)
                throw new InvalidOperationException("Fixture exited with code " + _process.ExitCode + ": " + await _stderr);
        }
        finally
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }
        }
    }
}
