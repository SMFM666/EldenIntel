using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace EnemyIntel.App;

public sealed class TrackingText : FrameworkElement
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(TrackingText),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(TrackingText),
        new FrameworkPropertyMetadata(SystemFonts.MessageFontFamily, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(TrackingText),
        new FrameworkPropertyMetadata(19d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(TrackingText),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CharacterSpacingProperty = DependencyProperty.Register(
        nameof(CharacterSpacing), typeof(double), typeof(TrackingText),
        new FrameworkPropertyMetadata(0.45d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public FontFamily FontFamily { get => (FontFamily)GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => (double)GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double CharacterSpacing { get => (double)GetValue(CharacterSpacingProperty); set => SetValue(CharacterSpacingProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => MeasureAndDraw(null);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        _ = MeasureAndDraw(drawingContext);
    }

    private Size MeasureAndDraw(DrawingContext? drawingContext)
    {
        if (string.IsNullOrEmpty(Text)) return default;
        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var x = 0d;
        var height = 0d;
        for (var index = 0; index < Text.Length; index++)
        {
            var glyph = new FormattedText(
                Text[index].ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                typeface, FontSize, Foreground, pixelsPerDip);
            drawingContext?.DrawText(glyph, new Point(x, 0));
            x += glyph.WidthIncludingTrailingWhitespace;
            height = Math.Max(height, glyph.Height);
            if (index < Text.Length - 1 && Text[index] != ' ' && Text[index + 1] != ' ')
                x += CharacterSpacing;
        }
        return new Size(x, height);
    }
}
