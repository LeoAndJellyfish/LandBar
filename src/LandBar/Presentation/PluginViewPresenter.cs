using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LandBar.Plugins;

namespace LandBar.Presentation;

/// <summary>Renders the bounded plugin view model without loading plugin code or XAML into WPF.</summary>
public sealed class PluginViewPresenter : Border, IDisposable
{
    private PluginView? _view;
    private double _maximumWidth, _maximumHeight;
    private readonly List<PluginSpriteImage> _sprites = [];
    public event Action<string>? ActionRequested;
    public PluginView? View => _view;

    public void Present(PluginView? view, double maximumWidth, double maximumHeight)
    {
        if (ReferenceEquals(view, _view) && maximumWidth == _maximumWidth && maximumHeight == _maximumHeight) return;
        DisposeSprites();
        _view = view; _maximumWidth = maximumWidth; _maximumHeight = maximumHeight;
        MaxWidth = maximumWidth;
        MaxHeight = maximumHeight <= 36 ? maximumHeight : double.PositiveInfinity;
        ClipToBounds = true;
        Child = view is null ? null : Build(view.Root, maximumWidth, maximumHeight <= 36 ? maximumHeight : 420);
    }

    private FrameworkElement Build(PluginNode node, double availableWidth, double availableHeight)
    {
        double width = Math.Min(node.Width ?? availableWidth, availableWidth);
        FrameworkElement element;
        switch (node.Type)
        {
            case "column":
                var column = new StackPanel();
                foreach (var child in node.Children)
                {
                    var item = Build(child, Math.Max(1, width - node.Padding * 2), availableHeight);
                    if (column.Children.Count > 0) item.Margin = new Thickness(0, node.Spacing, 0, 0);
                    column.Children.Add(item);
                }
                element = column;
                break;
            case "row":
                var row = new Grid();
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    double leadingSpace = i > 0 ? node.Spacing : 0;
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = child.Width is { } childWidth
                        ? new GridLength(Math.Min(childWidth, width) + leadingSpace)
                        : child.Type is "ring" or "sprite" or "button" ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                    var item = Build(child, Math.Max(1, width - node.Padding * 2), availableHeight);
                    if (i > 0) item.Margin = new Thickness(leadingSpace, 0, 0, 0);
                    Grid.SetColumn(item, i); row.Children.Add(item);
                }
                element = row;
                break;
            case "text":
                element = new TextBlock { Text = node.Text, FontSize = node.FontSize,
                    FontWeight = node.Weight == "semibold" ? FontWeights.SemiBold : FontWeights.Normal,
                    Foreground = ColorBrush(node.Color, "#F2F2F4"), TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis, LineHeight = node.FontSize * 1.45 };
                break;
            case "button":
                var button = new Button { Content = new TextBlock { Text = node.Text, TextTrimming = TextTrimming.CharacterEllipsis },
                    FontSize = node.FontSize, MinHeight = 34, Padding = new Thickness(12, 5, 12, 5),
                    Foreground = ColorBrush(node.Color, "#F2F2F4"), ToolTip = node.Text };
                System.Windows.Automation.AutomationProperties.SetName(button, node.Text);
                button.Click += (_, args) => { args.Handled = true; if (node.ActionId is { } action) ActionRequested?.Invoke(action); };
                element = button;
                break;
            case "ring":
                var ring = new Grid { Width = node.Width ?? 32, Height = node.Height ?? node.Width ?? 32 };
                ring.Children.Add(new PluginRing { Value = node.Value ?? 0, Thickness = node.Thickness,
                    Foreground = ColorBrush(node.Color, "#BEDDC9"), Track = ColorBrush(node.TrackColor, "#292D30") });
                ring.Children.Add(new TextBlock { Text = node.Text, FontSize = node.FontSize,
                    Foreground = ColorBrush(node.Color, "#BEDDC9"), HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center });
                element = ring;
                break;
            case "sprite":
                var sprite = new PluginSpriteImage(node.Sprite!);
                sprite.Width = Math.Min(sprite.Width, availableWidth);
                sprite.Height = Math.Min(sprite.Height, availableHeight);
                _sprites.Add(sprite); element = sprite;
                break;
            default:
                element = new Border { Width = node.Width ?? 1, Height = node.Height ?? 1 };
                break;
        }
        element.MaxWidth = Math.Max(1, availableWidth - node.Padding * 2);
        if (node.Width is { } explicitWidth) element.Width = Math.Max(1, Math.Min(explicitWidth, availableWidth) - node.Padding * 2);
        if (node.Height is { } explicitHeight) element.Height = Math.Max(1, Math.Min(explicitHeight, availableHeight) - node.Padding * 2);
        element.VerticalAlignment = VerticalAlignment.Center;
        element.HorizontalAlignment = node.Align switch { "center" => HorizontalAlignment.Center, "end" => HorizontalAlignment.Right,
            "stretch" => HorizontalAlignment.Stretch, _ => HorizontalAlignment.Left };
        if (node.Background is not null || node.Padding > 0 || node.CornerRadius > 0)
            return new Border { Background = ColorBrush(node.Background, "#00000000"), Padding = new Thickness(node.Padding),
                CornerRadius = new CornerRadius(node.CornerRadius), Child = element, MaxWidth = Math.Max(1, availableWidth),
                Width = node.Width.HasValue ? Math.Min(node.Width.Value, availableWidth) : double.NaN,
                Height = node.Height.HasValue ? Math.Min(node.Height.Value, availableHeight) : double.NaN,
                HorizontalAlignment = element.HorizontalAlignment, VerticalAlignment = VerticalAlignment.Center };
        return element;
    }

    private static SolidColorBrush ColorBrush(string? text, string fallback)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(text ?? fallback));
        brush.Freeze(); return brush;
    }
    private void DisposeSprites() { foreach (var sprite in _sprites) sprite.Dispose(); _sprites.Clear(); }
    public void Dispose() { DisposeSprites(); _view = null; Child = null; }
}

public sealed class PluginRing : FrameworkElement
{
    public double Value { get; init; }
    public double Thickness { get; init; } = 3;
    public Brush Foreground { get; init; } = Brushes.White;
    public Brush Track { get; init; } = Brushes.DimGray;
    protected override void OnRender(DrawingContext context)
    {
        double radius = Math.Max(0, (Math.Min(ActualWidth, ActualHeight) - Thickness) / 2);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var track = new Pen(Track, Thickness); track.Freeze();
        context.DrawEllipse(null, track, center, radius, radius);
        if (Value <= 0 || radius <= 0) return;
        var pen = new Pen(Foreground, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }; pen.Freeze();
        if (Value >= 1) { context.DrawEllipse(null, pen, center, radius, radius); return; }
        double angle = Value * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var drawing = geometry.Open())
        {
            drawing.BeginFigure(new Point(center.X, center.Y - radius), false, false);
            drawing.ArcTo(new Point(center.X + Math.Sin(angle) * radius, center.Y - Math.Cos(angle) * radius),
                new Size(radius, radius), 0, Value > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze(); context.DrawGeometry(null, pen, geometry);
    }
}

public sealed class PluginSpriteImage : Image, IDisposable
{
    private readonly IReadOnlyList<BitmapSource> _frames;
    private readonly DispatcherTimer _timer;
    private int _frame;
    public int FrameIndex => _frame;
    public bool IsAnimating => _timer.IsEnabled;
    public PluginSpriteImage(PluginSprite sprite)
    {
        var frames = new List<BitmapSource>();
        foreach (string frame in sprite.Frames)
        {
            var pixels = new byte[sprite.PixelWidth * sprite.PixelHeight * 4];
            for (int i = 0; i < frame.Length; i++)
            {
                int index = Convert.ToInt32(frame[i].ToString(), 16);
                var color = (Color)ColorConverter.ConvertFromString(sprite.Palette[index]);
                pixels[i * 4] = (byte)(color.B * color.A / 255); pixels[i * 4 + 1] = (byte)(color.G * color.A / 255);
                pixels[i * 4 + 2] = (byte)(color.R * color.A / 255); pixels[i * 4 + 3] = color.A;
            }
            var bitmap = BitmapSource.Create(sprite.PixelWidth, sprite.PixelHeight, 96, 96, PixelFormats.Pbgra32, null,
                pixels, sprite.PixelWidth * 4); bitmap.Freeze(); frames.Add(bitmap);
        }
        _frames = frames; Source = frames[0]; Width = sprite.PixelWidth * 2; Height = sprite.PixelHeight * 2;
        Stretch = Stretch.Uniform; RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / sprite.Fps) };
        _timer.Tick += (_, _) => { _frame = (_frame + 1) % _frames.Count; Source = _frames[_frame]; };
        Loaded += (_, _) => UpdateAnimation(); Unloaded += (_, _) => _timer.Stop();
        IsVisibleChanged += (_, _) => UpdateAnimation();
    }
    private void UpdateAnimation()
    {
        if (IsLoaded && IsVisible && _frames.Count > 1 && SystemParameters.ClientAreaAnimation) _timer.Start(); else _timer.Stop();
    }
    public void Dispose() => _timer.Stop();
}
