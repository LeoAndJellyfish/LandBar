using System.Windows;
using System.Windows.Controls;
using LandBar.Core;
using LandBar.Plugins;
using LandBar.Presentation;

namespace LandBar;

public partial class MainWindow
{
    // A shared catalog lets built-in and plugin components participate in the same selection.
    private sealed record CompactComponent(string Key, string Title, bool Available, PluginView? View = null);
    private CompactComponent? _standaloneComponent;
    private readonly PluginViewPresenter _measureStandalone = new();
    private string _shrinkChoicesKey = "";
    private string PreferredCompactComponentKey => string.IsNullOrWhiteSpace(_settings.ShrinkComponent)
        ? "builtin:clock" : _settings.ShrinkComponent;

    private IReadOnlyList<CompactComponent> CompactComponents(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        var result = new List<CompactComponent>
        {
            new("builtin:clock", "时钟", true),
            new("builtin:media", "播放器", sessions.Count > 0)
        };
        var views = _plugins?.Views ?? Array.Empty<PluginView>();
        foreach (var view in views.Where(PluginViewPlacement.CanUseStandalone))
        {
            string source = _plugins?.Plugins.FirstOrDefault(plugin => plugin.Id == view.PluginId)?.Name ?? view.PluginId;
            string title = view.Title ?? (views.Count(other => other.PluginId == view.PluginId && PluginViewPlacement.CanUseStandalone(other)) > 1
                ? source + " · " + view.Id : source);
            result.Add(new(view.StableKey, title, !_exampleMode && _settings.EnablePluginViews, view));
        }
        return result;
    }

    private CompactComponent ResolveStandaloneComponent(IReadOnlyList<MediaSessionSnapshot> sessions)
    {
        var components = CompactComponents(sessions);
        // Keep the user's preference through provider stop/restart and media-session gaps.
        return components.FirstOrDefault(component => component.Key == PreferredCompactComponentKey && component.Available)
            ?? components[0];
    }

    private CompactWidgetLayout CalculateStandaloneLayout(CompactComponent component, double availableWidth)
    {
        double body = component.Key == "builtin:clock" ? 68 : component.Key == "builtin:media" ? 28
            : MeasureCompactView(_measureStandalone, component.View, component.View?.Slot == "idle.content" ? 400 : 64);
        double componentHeight = component.View is null ? 28 : Math.Min(36, _measureStandalone.DesiredSize.Height);
        // Tall widgets need extra horizontal room for the capsule's rounded corners.
        // Include a two-DIP rasterization allowance; a 28-DIP ring still occupies 44 × 44.
        double inset = Math.Max(8, Math.Ceiling(22 - Math.Sqrt(22 * 22 - componentHeight * componentHeight / 4) + 2));
        double edges = inset * 2;
        double width = Math.Clamp(Math.Ceiling(body) + edges, 44, Math.Min(424, availableWidth));
        return new(width, width - edges, 0, 0, false, false, CompactMediaDetail.Clock);
    }

    private void RenderShrinkOptions()
    {
        ShrinkModeCheck.IsChecked = _settings.ShrinkMode;
        SetVisible(ShrinkComponentSection, _settings.ShrinkMode);
        var components = CompactComponents(OrderedSessions).ToList();
        if (!components.Any(component => component.Key == PreferredCompactComponentKey))
            components.Add(new(PreferredCompactComponentKey, "所选插件组件", false));
        string key = _settings.ShrinkMode + ":" + PreferredCompactComponentKey + ":" +
            string.Join("|", components.Select(component => component.Key + ":" + component.Title + ":" + component.Available));
        if (_shrinkChoicesKey == key) return;
        _shrinkChoicesKey = key;
        ShrinkComponentOptions.ItemsSource = components.Select(component =>
        {
            bool selected = component.Key == PreferredCompactComponentKey;
            return new
            {
                component.Key,
                Title = component.Title + (component.Available ? "" : " · 暂不可用"),
                Status = selected ? "已选择" : "",
                SelectedVisibility = selected ? Visibility.Visible : Visibility.Collapsed,
                Background = Brush(selected ? "#26382F" : "#1D1D23"),
                Foreground = Brush(selected ? "#D4EFDE" : "#CCCCD6")
            };
        }).ToArray();
        var preferred = components.First(component => component.Key == PreferredCompactComponentKey);
        ShrinkStatus.Text = preferred.Available ? "" : "暂时显示时钟，组件恢复后自动切回。";
        SetVisible(ShrinkStatus, !preferred.Available);
    }

    private void ShrinkMode_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShrinkMode = ShrinkModeCheck.IsChecked == true;
        if (!_demo) _settings.Save();
        RecordInteraction(); Render(); e.Handled = true;
    }

    private void ShrinkComponent_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        if (!CompactComponents(OrderedSessions).Any(component => component.Key == key)) return;
        _settings.ShrinkComponent = key;
        if (!_demo) _settings.Save();
        RecordInteraction(); Render(); e.Handled = true;
    }
}
