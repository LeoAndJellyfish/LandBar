using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace LandBar.Presentation;

/// <summary>Aligns the clock's visible numerals while retaining WPF's text formatting and automation.</summary>
public sealed class InkCenteredClock : TextBlock
{
    private readonly TranslateTransform _inkOffset = new();
    private bool _inkDirty = true;
    private double _naturalInkCenter;

    static InkCenteredClock()
    {
        foreach (var property in new[]
        {
            TextProperty, FontFamilyProperty, FontSizeProperty, FontStyleProperty,
            FontWeightProperty, FontStretchProperty, FlowDirectionProperty, LanguageProperty,
            PaddingProperty, System.Windows.Documents.Typography.NumeralAlignmentProperty
        })
            property.OverrideMetadata(typeof(InkCenteredClock), new FrameworkPropertyMetadata(InkChanged));
    }

    public InkCenteredClock()
    {
        RenderTransform = _inkOffset;
        LayoutUpdated += (_, _) => UpdateInkOffset();
    }

    private static void InkChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args) =>
        ((InkCenteredClock)sender)._inkDirty = true;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _inkDirty = true;
        UpdateInkOffset();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _inkDirty = true;
        UpdateInkOffset();
    }

    private void UpdateInkOffset()
    {
        if (!IsMeasureValid || ActualHeight <= 0) return;
        if (_inkDirty)
        {
            _inkDirty = false;
            if (string.IsNullOrEmpty(Text))
            {
                _naturalInkCenter = ActualHeight / 2;
                _inkOffset.Y = 0;
                return;
            }
            var text = new FormattedText(Text, Language.GetSpecificCulture(), FlowDirection,
                new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var ink = text.BuildGeometry(new Point()).Bounds;
            // TextBlock remains responsible for drawing, including its tabular-number setting.
            // Align the two formatters' baselines before locating the actual glyph center.
            _naturalInkCenter = ink.IsEmpty ? ActualHeight / 2
                : BaselineOffset - text.Baseline + ink.Y + ink.Height / 2;
        }

        double center = ActualHeight / 2;
        if (VerticalAlignment == VerticalAlignment.Center && VisualTreeHelper.GetParent(this) is not null)
        {
            var slot = LayoutInformation.GetLayoutSlot(this);
            // Layout rounding can offset an odd-height line box by half a physical pixel.
            // Use the allocated row's center rather than retaining that line-box offset.
            if (!slot.IsEmpty && slot.Height > 0)
                center = slot.Y + slot.Height / 2 - VisualTreeHelper.GetOffset(this).Y;
        }
        double offset = center - _naturalInkCenter;
        if (Math.Abs(_inkOffset.Y - offset) > .000001) _inkOffset.Y = offset;
    }
}
