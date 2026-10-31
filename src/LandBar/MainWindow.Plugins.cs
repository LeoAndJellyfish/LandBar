using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using LandBar.Core;
using LandBar.Plugins;

namespace LandBar;

public partial class MainWindow
{
    private PluginManager? _plugins;
    private readonly Dictionary<string, IslandEvent> _pluginEvents = new(StringComparer.Ordinal);
    private readonly HashSet<string> _changingPlugins = new(StringComparer.Ordinal);
    private int _pluginRefreshPending;
    private bool _pluginsTab;
    private string _pluginListKey = "";
    private static string PluginsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LandBar", "Plugins");
    private IReadOnlyList<PluginCard> CurrentPluginCards => _exampleMode ? Array.Empty<PluginCard>() : _plugins?.Cards ?? Array.Empty<PluginCard>();

    private void InitializePlugins()
    {
        try
        {
            ConnectPlugins(new PluginManager(PluginsDirectory));
            _plugins!.Discover();
            _ = StartEnabledPluginsAsync();
        }
        catch (Exception ex)
        {
            ShowPluginHint("插件目录不可用：" + ex.GetType().Name);
            Services.AppSettings.WriteDiagnostic(ex.GetType().Name);
        }
    }

    private void ConnectPlugins(PluginManager manager)
    {
        _plugins = manager;
        manager.Changed += QueuePluginRefresh;
    }

    private async Task StartEnabledPluginsAsync()
    {
        foreach (string id in (_settings.EnabledPluginIds ?? []).Distinct(StringComparer.Ordinal).Take(8))
        {
            if (_closing) break;
            try { await _plugins!.StartAsync(id); }
            catch (Exception ex) { Services.AppSettings.WriteDiagnostic(ex.GetType().Name); }
        }
    }

    private void QueuePluginRefresh()
    {
        if (_closing || Interlocked.Exchange(ref _pluginRefreshPending, 1) != 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _pluginRefreshPending, 0);
            if (_closing) return;
            SynchronizePluginCards();
        });
    }

    private void SynchronizePluginCards()
    {
        var cards = _plugins?.Cards ?? Array.Empty<PluginCard>();
        var keys = cards.Select(card => card.StableKey).ToHashSet(StringComparer.Ordinal);
        foreach (string key in _pluginEvents.Keys.Where(key => !keys.Contains(key)).ToArray())
        {
            _liveScheduler.Remove(key);
            _pluginEvents.Remove(key);
            _hiddenHistory.Remove(key);
        }
        bool newActivity = false;
        foreach (var card in cards)
        {
            string source = _plugins?.Plugins.FirstOrDefault(plugin => plugin.Id == card.PluginId)?.Name ?? card.PluginId;
            bool existed = _pluginEvents.TryGetValue(card.StableKey, out var previous);
            var value = new IslandEvent(card.StableKey, IslandEventKind.Plugin, source, card.Title, card.Body,
                previous?.CreatedAt ?? DateTimeOffset.UtcNow, card.PluginId);
            _pluginEvents[card.StableKey] = value;
            if (existed) _liveScheduler.Update(value);
            else if (!_settings.Quiet)
            {
                _liveScheduler.Push(value);
                newActivity = true;
            }
        }
        if (_exampleMode) return;
        if (newActivity) RecordInteraction();
        Render();
    }

    private PluginCard? FindPluginCard(string key) => CurrentPluginCards.FirstOrDefault(card => card.StableKey == key);
    private CardView FromPlugin(PluginCard card)
    {
        string source = _plugins?.Plugins.FirstOrDefault(plugin => plugin.Id == card.PluginId)?.Name ?? card.PluginId;
        return new CardView(card, source, _settings.HideContent);
    }

    private void PluginsTab_Click(object sender, RoutedEventArgs e)
    {
        _activePluginTabKey = PluginTabs.FirstOrDefault()?.Key;
        _pluginsTab = true; _notificationsTab = false; _page = 0;
        RecordInteraction(); Render(); e.Handled = true;
    }

    private void PluginsSettings_Click(object sender, RoutedEventArgs e)
    {
        ChangeView(IslandView.PluginSettings); e.Handled = true;
    }

    private void RenderPluginManagement()
    {
        var plugins = _plugins?.Plugins ?? Array.Empty<PluginStatus>();
        string key = string.Join("|", plugins.Select(plugin => $"{plugin.Id}:{plugin.Name}:{plugin.Description}:{plugin.State}:{plugin.Detail}:{_changingPlugins.Contains(plugin.Id)}:{_settings.EnabledPluginIds?.Contains(plugin.Id)}"));
        if (key != _pluginListKey)
        {
            _pluginListKey = key;
            PluginList.ItemsSource = plugins.Select(plugin => new
            {
                plugin.Id, plugin.Name, plugin.Description,
                StatusText = PluginStateText(plugin.State) + (plugin.State is PluginState.Faulted or PluginState.Invalid ? " · " + plugin.Detail : ""),
                ToggleText = plugin.State is PluginState.Running or PluginState.Starting || _settings.EnabledPluginIds?.Contains(plugin.Id) == true ? "停用" : "启用",
                CanToggle = !_demo && !_changingPlugins.Contains(plugin.Id) && plugin.State != PluginState.Invalid
            }).ToArray();
        }
        SetVisible(PluginsEmptyHint, plugins.Count == 0);
        RenderPluginViewChoices();
    }

    private static string PluginStateText(PluginState state) => state switch
    {
        PluginState.Starting => "启动中",
        PluginState.Running => "运行中",
        PluginState.Stopped => "已停止",
        PluginState.Faulted => "运行失败",
        PluginState.Invalid => "清单无效",
        _ => "未启用"
    };

    private async void TogglePlugin_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_demo || _plugins is null || (sender as FrameworkElement)?.Tag is not string id || !_changingPlugins.Add(id)) return;
        Render();
        try
        {
            _settings.EnabledPluginIds ??= [];
            var plugin = _plugins.Plugins.FirstOrDefault(item => item.Id == id);
            if (plugin?.State is PluginState.Running or PluginState.Starting || _settings.EnabledPluginIds.Contains(id))
            {
                _settings.EnabledPluginIds.RemoveAll(item => item == id);
                _settings.Save();
                await _plugins.StopAsync(id);
            }
            else if (await _plugins.StartAsync(id))
            {
                _settings.EnabledPluginIds.Add(id);
                _settings.Save();
            }
        }
        catch (Exception ex) { ShowPluginHint("插件操作失败：" + ex.GetType().Name); }
        finally { _changingPlugins.Remove(id); if (!_closing) { RecordInteraction(); Render(); } }
    }

    private async void PluginAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_plugins is null || (sender as FrameworkElement)?.Tag is not PluginActionView action) return;
        RecordInteraction();
        try { await _plugins.ActionAsync(action.PluginId, action.CardId, action.Id); }
        catch (Exception ex) { Services.AppSettings.WriteDiagnostic(ex.GetType().Name); }
    }

    private void OpenPluginsFolder_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        try
        {
            Directory.CreateDirectory(PluginsDirectory);
            Process.Start(new ProcessStartInfo(PluginsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex) { ShowPluginHint("目录打开失败：" + ex.GetType().Name); }
    }

    private void RefreshPlugins_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_demo) { ShowPluginHint("请结束预览后管理本地插件。"); return; }
        try { _plugins?.Discover(); Render(); }
        catch (Exception ex) { ShowPluginHint("刷新失败：" + ex.GetType().Name); }
    }

    private void InstallPluginExample_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_demo) { ShowPluginHint("请结束预览后安装示例。"); return; }
        string exampleId = (sender as FrameworkElement)?.Tag as string ?? "focus-timer";
        string[] files = exampleId switch
        {
            "focus-timer" or "pixel-pet" => ["plugin.json", "plugin.ps1", "README.md"],
            "codex-balance" => ["plugin.json", "plugin.py", "README.md"],
            _ => []
        };
        if (files.Length == 0) return;
        try
        {
            string source = Path.Combine(AppContext.BaseDirectory, "PluginExamples", exampleId);
            string destination = Path.Combine(PluginsDirectory, exampleId);
            if (Directory.Exists(destination))
            { ShowPluginHint("已有同名插件，现有文件已保留。更新示例请先备份并移走原目录。"); return; }
            if (files.Any(name => !File.Exists(Path.Combine(source, name))))
            { ShowPluginHint("示例文件缺失，请重新解压完整发布包。"); return; }
            Directory.CreateDirectory(PluginsDirectory);
            string staging = Path.Combine(PluginsDirectory, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                foreach (string name in files) File.Copy(Path.Combine(source, name), Path.Combine(staging, name));
                Directory.Move(staging, destination);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    foreach (string name in files) File.Delete(Path.Combine(staging, name));
                    Directory.Delete(staging);
                }
            }
            _plugins?.Discover();
            ShowPluginHint("示例已安装。点击“启用”开始运行。"); Render();
        }
        catch (Exception ex) { ShowPluginHint("安装失败：" + ex.GetType().Name); }
    }

    private void ShowPluginHint(string text)
    {
        PluginManagementHint.Text = text;
        SetVisible(PluginManagementHint, true);
        RecordInteraction();
    }

    private sealed record PluginActionView(string PluginId, string CardId, string Id, string Label);

    private sealed partial class CardView
    {
        public bool IsPlugin { get; }
        public double Progress { get; }
        public Visibility PluginVisibility => IsPlugin ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ProgressVisibility { get; } = Visibility.Collapsed;
        public IReadOnlyList<PluginActionView> PluginActions { get; } = Array.Empty<PluginActionView>();
        public string PluginKey { get; } = "";

        public CardView(PluginCard card, string source, bool privacy)
        {
            IsPlugin = true; Id = card.StableKey; Source = source;
            Title = privacy ? "插件活动" : card.Title; Body = privacy ? "内容已隐藏" : card.Body;
            Glyph = card.Icon switch { "timer" => "\uE916", "check" => "\uE73E", "download" => "\uE896", "code" => "\uE943", "music" => "\uE8D6", "calendar" => "\uE787", "info" => "\uE946", _ => "\uE8A5" };
            StatusColor = Brush("#BEDDC9"); Accent = Brush("#BEDDC9"); AccentBackground = Brush("#1C2923");
            AmbientBrush = Brush("#0D1310");
            Progress = card.Progress ?? 0;
            ProgressVisibility = card.Progress.HasValue ? Visibility.Visible : Visibility.Collapsed;
            PluginActions = card.Actions.Select(action => new PluginActionView(card.PluginId, card.Id, action.Id, action.Label)).ToArray();
            PluginKey = $"{card.Progress}:{card.Icon}:" + string.Join(";", card.Actions.Select(action => action.Id + ":" + action.Label));
        }
    }
}
