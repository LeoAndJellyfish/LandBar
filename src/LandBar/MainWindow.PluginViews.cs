using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;

namespace LandBar;

public partial class MainWindow
{
    private string? _activePluginTabKey;
    private string _pluginTabsKey = "", _pluginChoicesKey = "";
    private bool _forceBuiltinPluginManagement;
    private readonly Dictionary<string, string> _pluginContextKeys = new(StringComparer.Ordinal);
    private readonly PluginViewPresenter _measureLeft = new(), _measureRight = new(), _measureCenter = new();
    private CompactWidgetLayout _compactLayout = CompactWidgetLayout.Calculate(680, 112, 0, 0);
    private IReadOnlyList<PluginView> CurrentPluginViews => _exampleMode || !_settings.EnablePluginViews
        ? Array.Empty<PluginView>() : _plugins?.Views ?? Array.Empty<PluginView>();
    private sealed record PluginTab(string Key, string Title, string PluginId, PluginView? View);
    private IReadOnlyList<PluginTab> PluginTabs => CurrentPluginViews.Where(view => view.Slot == "activity.tab")
        .Select(view => new PluginTab(view.StableKey, view.Title ?? view.Id, view.PluginId, view))
        .Concat(CurrentPluginCards.GroupBy(card => card.PluginId).Select(group => new PluginTab("cards:" + group.Key,
            _plugins?.Plugins.FirstOrDefault(plugin => plugin.Id == group.Key)?.Name ?? group.Key, group.Key, null)))
        .ToArray();
    private PluginTab? ActivePluginTab => PluginTabs.FirstOrDefault(tab => tab.Key == _activePluginTabKey);
    private IReadOnlyList<PluginCard> ActivePluginCards => CurrentPluginCards
        .Where(card => ActivePluginTab?.PluginId == card.PluginId).ToArray();

    private PluginView? SelectedPluginView(string slot)
    {
        return BoundPluginView(slot, CurrentPluginViews);
    }

    private PluginView? BoundPluginView(string slot, IReadOnlyList<PluginView> views)
    {
        if (_settings.PluginViewBindings?.TryGetValue(slot, out var key) != true) return null;
        var view = views.FirstOrDefault(view => view.StableKey == key && PluginViewPlacement.CanUseSlot(view, slot));
        if (view is null || slot is not ("idle.left" or "idle.right")) return view;
        string other = slot == "idle.left" ? "idle.right" : "idle.left";
        // Recover duplicate persisted bindings without rendering the same widget twice.
        if (_settings.PluginViewBindings.GetValueOrDefault(other) == key && PluginViewPlacement.CanUseSlot(view, other) &&
            PluginViewPlacement.DefaultSide(view) != slot[5..]) return null;
        return view;
    }

    private PluginView? PagePluginView()
    {
        if (_view == IslandView.Activity)
            return _pluginsTab ? ActivePluginTab?.View ?? SelectedPluginView("plugins.page")
                : SelectedPluginView(_notificationsTab ? "notifications.page" : "media.page");
        return _view switch
        {
            IslandView.ControlCenter => SelectedPluginView("controlCenter.page"),
            IslandView.Settings => SelectedPluginView("settings.page"),
            IslandView.PluginSettings when !_forceBuiltinPluginManagement => SelectedPluginView("pluginManagement.page"),
            _ => null
        };
    }

    private void RenderPluginTabs()
    {
        var activeContexts = CurrentPluginViews.Select(view => view.PluginId + ":" + view.Slot).ToHashSet(StringComparer.Ordinal);
        foreach (string contextKey in _pluginContextKeys.Keys.Where(key => !activeContexts.Contains(key)).ToArray()) _pluginContextKeys.Remove(contextKey);
        var tabs = PluginTabs;
        if (_pluginsTab && !tabs.Any(tab => tab.Key == _activePluginTabKey))
        {
            _activePluginTabKey = tabs.FirstOrDefault()?.Key;
            if (_activePluginTabKey is null) _pluginsTab = false;
        }
        string key = _activePluginTabKey + ":" + _pluginsTab + ":" + string.Join("|", tabs.Select(tab => tab.Key + ":" + tab.Title));
        if (key == _pluginTabsKey) return;
        _pluginTabsKey = key;
        PluginTabItems.ItemsSource = tabs.Select(tab => new
        {
            tab.Key, tab.Title,
            Background = Brush(_pluginsTab && tab.Key == _activePluginTabKey ? "#24242B" : "#08080A"),
            Foreground = Brush(_pluginsTab && tab.Key == _activePluginTabKey ? "#F2F2F4" : "#92929F")
        }).ToArray();
    }

    private void PluginTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        _activePluginTabKey = key; _pluginsTab = true; _notificationsTab = false; _page = 0;
        RecordInteraction(); Render(); e.Handled = true;
    }

    private CompactWidgetLayout CalculateCompactPluginLayout(double builtinWidth, double availableWidth, int mediaCount)
    {
        var content = SelectedPluginView("idle.content");
        var left = SelectedPluginView("idle.left"); var right = SelectedPluginView("idle.right");
        return CompactWidgetLayout.Calculate(Math.Max(80, availableWidth), builtinWidth,
            MeasureCompactView(_measureLeft, left, 64), MeasureCompactView(_measureRight, right, 64),
            content is null ? null : MeasureCompactView(_measureCenter, content, 400), mediaCount);
    }

    private double MeasureCompactView(PluginViewPresenter presenter, PluginView? view, double maximumWidth)
    {
        presenter.SetValue(TextElement.FontFamilyProperty, FontFamily);
        VisualTreeHelper.SetRootDpi(presenter, VisualTreeHelper.GetDpi(this));
        presenter.Present(view, maximumWidth, 36);
        if (view is null) return 0;
        presenter.Measure(new Size(maximumWidth, 36));
        return Math.Clamp(Math.Ceiling(presenter.DesiredSize.Width), 1, maximumWidth);
    }

    private void PresentPluginViews(bool compact, bool activity, PluginView? page, double width, double height)
    {
        CompactLeftColumn.Width = CompactRightColumn.Width = new GridLength(compact ? _compactLayout.SideWidth : 0);
        PresentSlot(PluginIdleLeft, compact && _compactLayout.ShowLeft ? SelectedPluginView("idle.left") : null, 64, 36);
        PresentSlot(PluginIdleRight, compact && _compactLayout.ShowRight ? SelectedPluginView("idle.right") : null, 64, 36);
        var idleContent = compact ? SelectedPluginView("idle.content") : null;
        PresentSlot(PluginIdleContent, idleContent, Math.Max(1, _compactLayout.CenterWidth), 36);
        SetVisible(BuiltinCompactView, idleContent is null);
        bool customActivity = activity && _view == IslandView.Activity && page is not null;
        SetVisible(ActivityOverrideScroll, customActivity);
        if (customActivity)
        {
            SetVisible(EventItems, false); SetVisible(EmptyState, false); SetVisible(PageControls, false);
            SetVisible(ClearNotificationsButton, false);
        }
        else SetVisible(EventItems, activity);
        PresentSlot(ActivityOverrideContent, customActivity ? page : null, Math.Max(1, width - 44), Math.Max(1, height - 62));
        bool customPage = !activity && !compact && page is not null;
        SetVisible(PageOverrideView, customPage);
        if (customPage)
        {
            SetVisible(ControlCenterView, false); SetVisible(SettingsScroll, false); SetVisible(PluginManagementScroll, false);
            OverridePageTitle.Text = page?.Title ?? SlotTitle(page!.Slot);
        }
        PresentSlot(PageOverrideContent, customPage ? page : null, Math.Max(1, width - 32), Math.Max(1, height - 68));
        if (_view != IslandView.PluginSettings) _forceBuiltinPluginManagement = false;
        SendPluginContexts(page);
    }

    private void PresentSlot(PluginViewPresenter presenter, PluginView? view, double width, double height)
    {
        presenter.Present(view, width, height);
        SetVisible(presenter, view is not null);
        presenter.Tag = view?.StableKey;
    }

    private void PluginIdle_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        var widget = CurrentPluginViews.FirstOrDefault(view => view.StableKey == key);
        var tab = PluginTabs.FirstOrDefault(tab => tab.PluginId == widget?.PluginId);
        if (tab is null) OpenActivity();
        else
        {
            _activePluginTabKey = tab.Key; _pluginsTab = true; _notificationsTab = false;
            ChangeView(IslandView.Activity);
        }
        e.Handled = true;
    }

    private void ConfigureViewPresenters()
    {
        foreach (var presenter in new[] { PluginIdleLeft, PluginIdleRight, PluginIdleContent, ActivityOverrideContent, PageOverrideContent })
            presenter.ActionRequested += async action =>
            {
                if (presenter.View is not { } view || _plugins is null) return;
                RecordInteraction();
                await _plugins.ViewActionAsync(view.PluginId, view.Id, action);
            };
    }

    private static string SlotTitle(string slot) => slot switch
    {
        "idle.widget" or "idle.left" or "idle.right" => "收起态组件", "idle.content" => "收起态 · 主体",
        "media.page" => "媒体页面", "notifications.page" => "通知页面", "plugins.page" => "卡片页面",
        "activity.tab" => "独立标签页", "controlCenter.page" => "控制中心", "settings.page" => "设置页面",
        "pluginManagement.page" => "插件管理", _ => slot
    };

    private void RenderPluginViewChoices()
    {
        var views = _plugins?.Views ?? Array.Empty<PluginView>();
        SetVisible(PluginViewsSection, views.Count > 0);
        PluginViewsToggle.Content = _settings.EnablePluginViews ? "停用插件界面" : "启用插件界面";
        string key = _settings.EnablePluginViews + ":" + string.Join("|", views.Select(view => view.StableKey + ":" + view.Slot + ":" + view.Title +
            ":" + view.Placement?.DefaultSide + ":" + string.Join(",", view.Placement?.AllowedSides ?? []))) +
            ":" + string.Join("|", (_settings.PluginViewBindings ?? []).OrderBy(pair => pair.Key).Select(pair => pair.Key + pair.Value));
        if (key == _pluginChoicesKey) return;
        _pluginChoicesKey = key;
        PluginViewChoices.ItemsSource = views.Where(view => view.Slot != "activity.tab").Select(view =>
        {
            bool widget = PluginViewPlacement.IsSideWidget(view);
            bool left = BoundPluginView("idle.left", views)?.StableKey == view.StableKey;
            bool right = BoundPluginView("idle.right", views)?.StableKey == view.StableKey;
            string name = _plugins?.Plugins.FirstOrDefault(plugin => plugin.Id == view.PluginId)?.Name ?? view.PluginId;
            string hint = view.Placement?.AllowedSides.Count == 1
                ? "固定" + (view.Placement.DefaultSide == "left" ? "左侧" : "右侧") : "左右可选";
            return new
            {
                Key = view.StableKey, Title = widget ? view.Title ??
                    (views.Count(other => other.PluginId == view.PluginId && PluginViewPlacement.IsSideWidget(other)) > 1
                        ? name + " · " + view.Id : name) : view.Title ?? SlotTitle(view.Slot),
                Source = widget ? (view.Title is null ? "收起态组件" : name) + " · " + hint : name,
                PositionHint = widget ? hint : "",
                LeftVisibility = PluginViewPlacement.SupportsSide(view, "left") ? Visibility.Visible : Visibility.Collapsed,
                RightVisibility = PluginViewPlacement.SupportsSide(view, "right") ? Visibility.Visible : Visibility.Collapsed,
                LeftTag = view.StableKey + "|left", RightTag = view.StableKey + "|right",
                LeftBackground = Brush(left && _settings.EnablePluginViews ? "#31463B" : "#24242B"),
                RightBackground = Brush(right && _settings.EnablePluginViews ? "#31463B" : "#24242B"),
                LeftForeground = Brush(left && _settings.EnablePluginViews ? "#D4EFDE" : "#CCCCD6"),
                RightForeground = Brush(right && _settings.EnablePluginViews ? "#D4EFDE" : "#CCCCD6"),
                UseVisibility = widget ? Visibility.Collapsed : Visibility.Visible,
                ToggleText = BoundPluginView(view.Slot, views)?.StableKey == view.StableKey ? "还原" : "使用"
            };
        }).ToArray();
    }

    /// <summary>Moves one widget atomically and preserves the displaced widget when it can swap sides.</summary>
    private bool TryAssignPluginSide(string key, string side)
    {
        var views = _plugins?.Views ?? Array.Empty<PluginView>();
        var view = views.FirstOrDefault(view => view.StableKey == key);
        if (view is null || !PluginViewPlacement.SupportsSide(view, side)) return false;
        string target = "idle." + side, other = side == "left" ? "idle.right" : "idle.left";
        string otherSide = side == "left" ? "right" : "left";
        var moving = BoundPluginView(other, views)?.StableKey == key;
        var displaced = BoundPluginView(target, views);
        _settings.PluginViewBindings ??= new(StringComparer.Ordinal);
        if (_settings.PluginViewBindings.GetValueOrDefault(other) == key) _settings.PluginViewBindings.Remove(other);
        if (moving && displaced is not null && displaced.StableKey != key && PluginViewPlacement.SupportsSide(displaced, otherSide))
            _settings.PluginViewBindings[other] = displaced.StableKey;
        _settings.PluginViewBindings[target] = key;
        return true;
    }

    private void SelectPluginSide_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (_demo || (sender as FrameworkElement)?.Tag is not string tag) return;
        int separator = tag.LastIndexOf('|');
        if (separator < 0) return;
        string key = tag[..separator], side = tag[(separator + 1)..], slot = "idle." + side;
        if (_settings.EnablePluginViews && BoundPluginView(slot, _plugins?.Views ?? Array.Empty<PluginView>())?.StableKey == key)
        {
            // Clicking the highlighted side withdraws this widget from both persisted side entries.
            foreach (string entry in new[] { "idle.left", "idle.right" })
                if (_settings.PluginViewBindings.GetValueOrDefault(entry) == key) _settings.PluginViewBindings.Remove(entry);
        }
        else if (!TryAssignPluginSide(key, side)) return;
        _settings.EnablePluginViews = true;
        _settings.Save(); RecordInteraction(); Render();
    }

    private void SelectPluginView_Click(object sender, RoutedEventArgs e)
    {
        if (_demo || (sender as FrameworkElement)?.Tag is not string key) return;
        var view = _plugins?.Views.FirstOrDefault(view => view.StableKey == key);
        if (view is null) return;
        _settings.PluginViewBindings ??= new(StringComparer.Ordinal);
        if (SelectedPluginView(view.Slot)?.StableKey == key) _settings.PluginViewBindings.Remove(view.Slot);
        else { _settings.EnablePluginViews = true; _settings.PluginViewBindings[view.Slot] = key; }
        _settings.Save(); RecordInteraction(); Render(); e.Handled = true;
    }

    private void TogglePluginViews_Click(object sender, RoutedEventArgs e)
    {
        if (_demo) return;
        _settings.EnablePluginViews = !_settings.EnablePluginViews;
        _settings.Save(); RecordInteraction(); Render(); e.Handled = true;
    }

    private void RestoreBuiltinViews()
    {
        _settings.EnablePluginViews = false;
        if (!_demo) _settings.Save();
        _activePluginTabKey = null; _pluginsTab = false;
        ChangeView(IslandView.ControlCenter);
    }

    private void OpenBuiltinPluginManagement()
    {
        _forceBuiltinPluginManagement = true; ChangeView(IslandView.PluginSettings);
    }

    private void SendPluginContexts(PluginView? view)
    {
        if (view is null || _exampleMode || _plugins is null) return;
        object data = view.Slot switch
        {
            "media.page" => new { sessions = OrderedSessions.Take(8).Select(session => new
                { session.SessionId, AppName = ShortText(session.AppName, 80), Title = ShortText(session.Title, 160), Artist = ShortText(session.Artist, 120), session.IsPlaying,
                    session.CanTogglePlayPause, session.CanGoNext, session.CanGoPrevious }).ToArray() },
            "notifications.page" => new { notifications = NotificationHistory.Take(8).Select(item => new
                { item.Id, AppName = ShortText(item.AppName, 80), Title = _settings.HideContent ? "新通知" : ShortText(item.Title, 160),
                    Body = _settings.HideContent ? "内容已隐藏" : item.Body[..Math.Min(item.Body.Length, 240)], item.CreatedAt }).ToArray() },
            _ => new { quiet = _settings.Quiet, hideContent = _settings.HideContent, autoCollapseSeconds = _settings.AutoCollapseSeconds }
        };
        string key = view.PluginId + ":" + view.Slot;
        string content = JsonSerializer.Serialize(data);
        if (_pluginContextKeys.TryGetValue(key, out var previous) && previous == content) return;
        _pluginContextKeys[key] = content;
        _ = _plugins.SendContextAsync(view.PluginId, view.Slot, data);
    }

    private void DisposePluginViews()
    {
        foreach (var presenter in new[] { PluginIdleLeft, PluginIdleRight, PluginIdleContent, ActivityOverrideContent, PageOverrideContent }) presenter.Dispose();
        _measureLeft.Dispose(); _measureRight.Dispose(); _measureCenter.Dispose();
    }
    private static string ShortText(string value, int limit) => value[..Math.Min(value.Length, limit)];
}
