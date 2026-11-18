using System.Collections;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShareLink.BrowserSharing;

// Canvas gives the complete name its natural width; only the outer viewport clips it.
public sealed class MarqueeText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(MarqueeText), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsMeasure, Changed));
    public static readonly DependencyProperty PausedProperty = DependencyProperty.Register(nameof(Paused), typeof(bool), typeof(MarqueeText), new PropertyMetadata(false, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool Paused { get => (bool)GetValue(PausedProperty); set => SetValue(PausedProperty, value); }
    private readonly TextBlock label = new() { TextWrapping = TextWrapping.NoWrap };
    private readonly TranslateTransform translation = new();
    private readonly Canvas canvas = new() { ClipToBounds = false };
    private readonly Border viewport = new() { ClipToBounds = true };
    private double naturalWidth;
    private string motionKey = "";
    public MarqueeText()
    {
        Focusable = false;
        label.RenderTransform = translation; canvas.Children.Add(label); viewport.Child = canvas;
        AddVisualChild(viewport); AddLogicalChild(viewport);
        Loaded += (_, _) => { SystemParameters.StaticPropertyChanged += SystemChanged; Restart(); };
        Unloaded += (_, _) => { SystemParameters.StaticPropertyChanged -= SystemChanged; motionKey = ""; MarqueeMotion.Apply(label, translation, naturalWidth, false); };
        IsVisibleChanged += (_, _) => Restart();
    }
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => index == 0 ? viewport : throw new ArgumentOutOfRangeException(nameof(index));
    protected override IEnumerator LogicalChildren => new[] { viewport }.GetEnumerator();
    protected override AutomationPeer OnCreateAutomationPeer() => new NamePeer(this);
    private sealed class NamePeer(MarqueeText owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(MarqueeText);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => ((MarqueeText)Owner).Text;
        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var text = (MarqueeText)sender;
        if (e.Property == TextProperty) { text.label.Text = text.Text; text.InvalidateMeasure(); }
        text.Restart();
    }
    private void SystemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) Restart(); }
    protected override Size MeasureOverride(Size available)
    {
        label.FontFamily = FontFamily; label.FontSize = FontSize; label.FontWeight = FontWeight;
        label.FontStyle = FontStyle; label.FontStretch = FontStretch; label.Foreground = Foreground;
        label.FlowDirection = FlowDirection; label.Language = Language;
        naturalWidth = Math.Ceiling(new FormattedText(Text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch), FontSize, Foreground ?? Brushes.Black,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace) + 2;
        label.Width = naturalWidth; label.Measure(new Size(naturalWidth, double.PositiveInfinity));
        double height = Math.Ceiling(label.DesiredSize.Height);
        double width = double.IsInfinity(available.Width) ? naturalWidth : Math.Min(naturalWidth, available.Width);
        canvas.Height = height; viewport.Measure(new Size(Math.Max(0, width), height));
        return new Size(Math.Max(0, width), height);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        viewport.Arrange(new Rect(finalSize));
        Restart(finalSize.Width);
        return finalSize;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (label is null) return;
        if (e.Property == ForegroundProperty) label.Foreground = Foreground;
        if (e.Property == FontSizeProperty || e.Property == FontFamilyProperty || e.Property == FontWeightProperty || e.Property == FontStyleProperty || e.Property == FontStretchProperty || e.Property == FlowDirectionProperty || e.Property == LanguageProperty) InvalidateMeasure();
    }
    private void Restart(double? arrangedWidth = null)
    {
        double available = arrangedWidth ?? ActualWidth;
        bool active = IsLoaded && IsVisible && !Paused && SystemParameters.ClientAreaAnimation && available > 0 && naturalWidth > available + 1;
        string key = $"{Text}|{naturalWidth}|{available}|{active}";
        if (key == motionKey) return;
        motionKey = key;
        MarqueeMotion.Apply(label, translation, naturalWidth, active);
    }
}
