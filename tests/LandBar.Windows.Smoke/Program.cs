using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Media.Control;
using Windows.UI.Notifications.Management;
using LandBar.Services;

// Read only integration diagnostics. Never requests consent or retrieves notification content.
var result = new Dictionary<string, object?>
{
    ["osVersion"] = Environment.OSVersion.VersionString,
    ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
};
try
{
    var package = Package.Current;
    result["packageIdentity"] = package.Id.Name;
    result["packageVersion"] = package.Id.Version.ToString();
}
catch (Exception exception)
{
    result["packageIdentity"] = null;
    result["packageIdentityHResult"] = $"0x{exception.HResult:X8}";
}
try
{
    var listener = UserNotificationListener.Current;
    result["notificationApi"] = "available";
    result["notificationAccess"] = listener.GetAccessStatus().ToString();
}
catch (Exception exception)
{
    result["notificationApi"] = "unavailable";
    result["notificationHResult"] = $"0x{exception.HResult:X8}";
}
var mediaConnected = false;
try
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(timeout.Token);
    result["mediaApi"] = "connected";
    result["mediaSessionCount"] = manager.GetSessions().Count;
    result["hasCurrentMediaSession"] = manager.GetCurrentSession() is not null;
    mediaConnected = true;
}
catch (Exception exception)
{
    result["mediaApi"] = "unavailable";
    result["mediaHResult"] = $"0x{exception.HResult:X8}";
}
if (args.Contains("--exercise-sources"))
{
    using var notifications = new WindowsNotificationSource();
    using var media = new WindowsMediaSource();
    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(6));
    var process = System.Diagnostics.Process.GetCurrentProcess();
    var beforeCpu = process.TotalProcessorTime;
    var beforeMemory = process.WorkingSet64;
    var notificationEvents = 0;
    var mediaEvents = 0;
    notifications.EventReceived += _ => Interlocked.Increment(ref notificationEvents);
    media.EventReceived += _ => Interlocked.Increment(ref mediaEvents);
    await Task.WhenAll(notifications.StartAsync(stop.Token), media.StartAsync(stop.Token));
    process.Refresh();
    result["sourceExerciseSeconds"] = 6;
    result["notificationSourceStatus"] = notifications.Status;
    result["mediaSourceStatus"] = media.Status;
    result["notificationEventCount"] = notificationEvents;
    result["mediaEventCount"] = mediaEvents;
    result["cpuMilliseconds"] = (process.TotalProcessorTime - beforeCpu).TotalMilliseconds;
    result["workingSetMiB"] = Math.Round(process.WorkingSet64 / 1048576d, 1);
    result["workingSetGrowthMiB"] = Math.Round((process.WorkingSet64 - beforeMemory) / 1048576d, 1);
}
Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
return mediaConnected ? 0 : 1;
