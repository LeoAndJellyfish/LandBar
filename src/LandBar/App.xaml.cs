using System.Threading;
using System.Windows;

namespace LandBar;

public partial class App : System.Windows.Application
{
    private Mutex? _instance;
    protected override async void OnStartup(StartupEventArgs e)
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = e.Args.Contains("--software-rendering")
            ? System.Windows.Interop.RenderMode.SoftwareOnly : System.Windows.Interop.RenderMode.Default;
        base.OnStartup(e);
        if (e.Args.Contains("--test-toast") || e.Args.Contains("--clear-test-toast"))
        {
            try
            {
                if (e.Args.Contains("--clear-test-toast")) global::Windows.UI.Notifications.ToastNotificationManager.History.Remove("landbar-test");
                else
                {
                    var xml = new Windows.Data.Xml.Dom.XmlDocument();
                    xml.LoadXml("<toast><visual><binding template='ToastGeneric'><text>LandBar 通知测试</text><text>这是一条本地测试通知。</text></binding></visual><audio silent='true'/></toast>");
                    var toast = new Windows.UI.Notifications.ToastNotification(xml) { Tag = "landbar-test", ExpirationTime = DateTimeOffset.Now.AddMinutes(1) };
                    global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Show(toast);
                    await Task.Delay(1500);
                    var notifier = global::Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier();
                    var snapshot = await global::Windows.UI.Notifications.Management.UserNotificationListener.Current.GetNotificationsAsync(global::Windows.UI.Notifications.NotificationKinds.Toast);
                    var result = new { notifierSetting = notifier.Setting.ToString(), ownHistoryCount = global::Windows.UI.Notifications.ToastNotificationManager.History.GetHistory().Count, snapshotCount = snapshot.Count, testToastCount = snapshot.Count(n => n.Notification.Visual.GetBinding(global::Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric)?.GetTextElements().Any(t => t.Text == "LandBar 通知测试") == true) };
                    File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "notification-test-result.json"), System.Text.Json.JsonSerializer.Serialize(result));
                }
            }
            catch (Exception ex) { Services.AppSettings.WriteDiagnostic(ex.GetType().Name); Environment.ExitCode = 1; }
            Shutdown(); return;
        }
        _instance = new Mutex(true, "Local\\LandBar.Desktop.Singleton", out bool created);
        if (!created) { Shutdown(); return; }
        DispatcherUnhandledException += (_, args) =>
        {
            // Log exception type only; notification content stays in memory.
            Services.AppSettings.WriteDiagnostic(args.Exception.GetType().Name);
            args.Handled = true;
        };
        var capturePath = e.Args.FirstOrDefault(arg => arg.StartsWith("--capture=", StringComparison.Ordinal))?[10..];
        var window = new MainWindow(e.Args.Contains("--demo"), e.Args.Contains("--smoke"), e.Args.Contains("--preview"), capturePath);
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
