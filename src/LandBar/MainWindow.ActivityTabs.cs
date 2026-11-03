using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LandBar;

public partial class MainWindow
{
    private sealed class ActivityTabChoice(string key, string title) : INotifyPropertyChanged
    {
        public string Key { get; } = key;
        private string _title = title;
        private bool _selected;
        public string Title => _title;
        public Brush Background => Brush(_selected ? "#24242B" : "#08080A");
        public Brush Foreground => Brush(_selected ? "#F2F2F4" : "#92929F");
        public bool IsTabStop => _selected;
        public string Status => _selected ? "已选择" : "";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update(string title, bool selected)
        {
            if (_title != title) { _title = title; Changed(nameof(Title)); }
            if (_selected == selected) return;
            _selected = selected;
            foreach (string property in new[] { nameof(Background), nameof(Foreground), nameof(IsTabStop), nameof(Status) }) Changed(property);
        }
        private void Changed(string property) => PropertyChanged?.Invoke(this, new(property));
    }

    private readonly ObservableCollection<ActivityTabChoice> _activityPluginTabs = [];
    private bool _activityTabsNeedCenter, _activityTabsCenterQueued;
    private string _activityTabLayoutKey = "";
    private string? _activityTabFocusKey;
    private string SelectedActivityTabKey => _pluginsTab && _activePluginTabKey is { } key ? key
        : _notificationsTab ? "builtin:notifications" : "builtin:media";

    private void ConfigureActivityTabs()
    {
        PluginTabItems.ItemsSource = _activityPluginTabs;
        ActivityTabs.LayoutUpdated += ActivityTabs_LayoutUpdated;
        ActivityTabScroll.SizeChanged += (_, _) => RequestActivityTabCenter();
        ActivityTabs.SizeChanged += (_, _) => RequestActivityTabCenter();
        ContentLayout.IsHitTestVisibleChanged += (_, _) =>
        {
            if (ContentLayout.IsHitTestVisible && _activityTabFocusKey is not null) RequestActivityTabCenter();
        };
        ActivityTabScroll.ScrollChanged += (_, args) =>
        {
            if (args.ViewportWidthChange != 0 || args.ExtentWidthChange != 0) RequestActivityTabCenter();
        };
        PluginTabItems.ItemContainerGenerator.StatusChanged += (_, _) => RequestActivityTabCenter();
        ActivityTabs.AddHandler(FrameworkElement.RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, args) =>
        {
            // WPF's focus scrolling competes with our selected-tab positioning.
            args.Handled = true;
            RequestActivityTabCenter();
        }));
    }

    private void NormalizePluginTabSelection()
    {
        if (!_pluginsTab) return;
        var tabs = PluginTabs;
        if (tabs.Any(tab => tab.Key == _activePluginTabKey)) return;
        _activePluginTabKey = tabs.FirstOrDefault()?.Key;
        if (_activePluginTabKey is null) _pluginsTab = false;
    }

    private void SyncActivityTabChoices(IReadOnlyList<PluginTab> tabs)
    {
        var keys = tabs.Select(tab => tab.Key).ToHashSet(StringComparer.Ordinal);
        for (int index = _activityPluginTabs.Count - 1; index >= 0; index--)
            if (!keys.Contains(_activityPluginTabs[index].Key)) _activityPluginTabs.RemoveAt(index);
        for (int index = 0; index < tabs.Count; index++)
        {
            var tab = tabs[index];
            var choice = _activityPluginTabs.FirstOrDefault(item => item.Key == tab.Key);
            if (choice is null) { choice = new(tab.Key, tab.Title); _activityPluginTabs.Insert(index, choice); }
            else if (_activityPluginTabs.IndexOf(choice) != index) _activityPluginTabs.Move(_activityPluginTabs.IndexOf(choice), index);
            choice.Update(tab.Title, SelectedActivityTabKey == tab.Key);
        }
        MediaTab.IsTabStop = SelectedActivityTabKey == "builtin:media";
        NotificationsTab.IsTabStop = SelectedActivityTabKey == "builtin:notifications";
        System.Windows.Automation.AutomationProperties.SetItemStatus(MediaTab, MediaTab.IsTabStop ? "已选择" : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(NotificationsTab, NotificationsTab.IsTabStop ? "已选择" : "");
        string layoutKey = SelectedActivityTabKey + ":" + MediaTab.Content + ":" +
            string.Join("|", tabs.Select(tab => tab.Key + ":" + tab.Title));
        if (layoutKey == _activityTabLayoutKey) return;
        _activityTabLayoutKey = layoutKey;
        RequestActivityTabCenter();
    }

    private void RequestActivityTabCenter()
    {
        if (_closing) return;
        _activityTabsNeedCenter = true;
        if (_activityTabsCenterQueued) return;
        _activityTabsCenterQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _activityTabsCenterQueued = false;
            CenterActivityTab();
        }, DispatcherPriority.Loaded);
    }

    private void ActivityTabs_LayoutUpdated(object? sender, EventArgs e)
    {
        if (_activityTabsNeedCenter) CenterActivityTab();
    }

    private List<Button> ActivityTabButtons() => new[] { MediaTab, NotificationsTab }
        .Concat(DescendantTabButtons(PluginTabItems)).Where(button => button.IsVisible).ToList();

    private static IEnumerable<Button> DescendantTabButtons(DependencyObject node)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is Button button) yield return button;
            else foreach (var descendant in DescendantTabButtons(child)) yield return descendant;
        }
    }

    private string ActivityButtonKey(Button button) => button == MediaTab ? "builtin:media"
        : button == NotificationsTab ? "builtin:notifications" : (string)button.Tag;

    private void CenterActivityTab()
    {
        if (_closing || !_activityTabsNeedCenter || !ActivityTabs.IsVisible || !ActivityTabs.IsArrangeValid ||
            ActivityTabScroll.ViewportWidth <= 0) return;
        var buttons = ActivityTabButtons();
        var selected = buttons.FirstOrDefault(button => ActivityButtonKey(button) == SelectedActivityTabKey);
        if (selected is null || buttons.Any(button => !button.IsArrangeValid)) return;
        // A legal plugin page may be only 320 DIP wide. Its title must fit the smaller
        // tab viewport as a complete button, with text trimming confined to the label.
        double maximumTitleWidth = Math.Max(1, Math.Min(140, ActivityTabScroll.ViewportWidth - 2));
        bool titleSizeChanged = false;
        foreach (var button in buttons.Where(button => button != MediaTab && button != NotificationsTab))
            if (Math.Abs(button.MaxWidth - maximumTitleWidth) > .25)
            {
                button.MaxWidth = maximumTitleWidth;
                titleSizeChanged = true;
            }
        if (titleSizeChanged) return;
        double selectedCenter = selected.TranslatePoint(new Point(selected.ActualWidth / 2, 0), ActivityTabs).X;
        double firstLeft = buttons[0].TranslatePoint(new Point(), ActivityTabs).X;
        double lastRight = buttons[^1].TranslatePoint(new Point(buttons[^1].ActualWidth, 0), ActivityTabs).X;
        // Show navigation only when centering the selection leaves other tabs outside the strip.
        bool navigation = selectedCenter - firstLeft > ActivityTabNavigation.ActualWidth / 2 + .5 ||
            lastRight - selectedCenter > ActivityTabNavigation.ActualWidth / 2 + .5;
        var navigationVisibility = navigation ? Visibility.Visible : Visibility.Collapsed;
        if (ActivityTabsPrevious.Visibility != navigationVisibility || ActivityTabsNext.Visibility != navigationVisibility)
        {
            ActivityTabsPrevious.Visibility = ActivityTabsNext.Visibility = navigationVisibility;
            return;
        }
        ActivityTabsPrevious.IsEnabled = selected != buttons[0];
        ActivityTabsNext.IsEnabled = selected != buttons[^1];
        double viewport = ActivityTabScroll.ViewportWidth;
        double leading = Math.Max(0, (viewport - buttons[0].ActualWidth) / 2);
        double trailing = Math.Max(0, (viewport - buttons[^1].ActualWidth) / 2);
        if (Math.Abs(ActivityTabsLeadingSpace.Width - leading) > .25 || Math.Abs(ActivityTabsTrailingSpace.Width - trailing) > .25)
        {
            ActivityTabsLeadingSpace.Width = leading;
            ActivityTabsTrailingSpace.Width = trailing;
            return;
        }
        double offset = Math.Clamp(selectedCenter - viewport / 2, 0, ActivityTabScroll.ScrollableWidth);
        if (Math.Abs(ActivityTabScroll.HorizontalOffset - offset) > .25)
        {
            ActivityTabScroll.ScrollToHorizontalOffset(offset);
            return;
        }
        _activityTabsNeedCenter = false;
        if (_activityTabFocusKey == SelectedActivityTabKey && ContentLayout.IsHitTestVisible)
        {
            _activityTabFocusKey = null;
            selected.Focus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(selected), selected);
        }
    }

    private void SelectActivityTab(string key, bool focus = false)
    {
        if (key != "builtin:media" && key != "builtin:notifications" && !PluginTabs.Any(tab => tab.Key == key)) return;
        _pluginsTab = key is not ("builtin:media" or "builtin:notifications");
        _notificationsTab = key == "builtin:notifications";
        if (_pluginsTab) _activePluginTabKey = key;
        _page = 0;
        if (focus) _activityTabFocusKey = key;
        _activityTabLayoutKey = "";
        RecordInteraction(); Render();
    }

    private void NavigateActivityTab(int direction, bool focus = false)
    {
        var keys = new[] { "builtin:media", "builtin:notifications" }.Concat(PluginTabs.Select(tab => tab.Key)).ToArray();
        int current = Array.IndexOf(keys, SelectedActivityTabKey);
        int target = Math.Clamp(current + direction, 0, keys.Length - 1);
        if (target == current)
        {
            if (focus) _activityTabFocusKey = keys[target];
            RequestActivityTabCenter();
            return;
        }
        SelectActivityTab(keys[target], focus);
    }

    private void ActivityTabsPrevious_Click(object sender, RoutedEventArgs e) { NavigateActivityTab(-1); e.Handled = true; }
    private void ActivityTabsNext_Click(object sender, RoutedEventArgs e) { NavigateActivityTab(1); e.Handled = true; }
    private void ActivityTabs_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right) NavigateActivityTab(e.Key == Key.Left ? -1 : 1, focus: true);
        else if (e.Key == Key.Home) SelectActivityTab("builtin:media", focus: true);
        else if (e.Key == Key.End) SelectActivityTab(PluginTabs.LastOrDefault()?.Key ?? "builtin:notifications", focus: true);
        else return;
        e.Handled = true;
    }
}
