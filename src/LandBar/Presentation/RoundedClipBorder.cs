using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LandBar.Presentation;

/// <summary>Clips both live content and transition snapshots to the border's inner curve.</summary>
public sealed class RoundedClipBorder : Border
{
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (Child is { } child)
        {
            var inset = BorderThickness;
            double radius = Math.Max(0, CornerRadius.TopLeft - Math.Max(inset.Left, inset.Top));
            var clip = new RectangleGeometry(new Rect(child.RenderSize), radius, radius);
            clip.Freeze();
            child.Clip = clip;
        }
        return result;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == CornerRadiusProperty || e.Property == BorderThicknessProperty) InvalidateArrange();
    }
}
