using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LandBar.Services;

public sealed class AppSettings
{
    private int _autoCollapseSeconds = 5;
    /// <summary>Zero keeps the island open; older settings files retain the five-second default.</summary>
    public int AutoCollapseSeconds
    {
        get => _autoCollapseSeconds;
        set => _autoCollapseSeconds = value is >= 0 and <= 5 ? value : 5;
    }
    public bool HideContent { get; set; }
    public bool Quiet { get; set; }
    public bool PrimaryScreen { get; set; } = true;
    public string? ScreenDeviceName { get; set; }
    public int TopOffset { get; set; } = 12;
    public bool OnboardingComplete { get; set; }
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar");
    public static AppSettings Load()
    {
        try { return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path.Combine(Folder, "settings.json"))) ?? new(); }
        catch { return new(); }
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "settings.json"), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
    public static void WriteDiagnostic(string type)
    {
        try { Directory.CreateDirectory(Folder); File.AppendAllText(Path.Combine(Folder, "diagnostics.log"), $"{DateTimeOffset.Now:u} {type}\n"); } catch { }
    }
    public static bool StartupEnabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("LandBar") != null; }
    }
    public static void SetStartup(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled) key.SetValue("LandBar", $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue("LandBar", false);
    }
}
