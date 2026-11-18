using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace ShareLink;

public static class Motion
{
    private static readonly DependencyProperty ExpandedProperty = DependencyProperty.RegisterAttached("Expanded", typeof(bool), typeof(Motion), new PropertyMetadata(false));
    private static readonly DependencyProperty RevisionProperty = DependencyProperty.RegisterAttached("Revision", typeof(int), typeof(Motion), new PropertyMetadata(0));

    internal static BitmapSource? CapturePage(FrameworkElement page)
    {
        if (!page.IsVisible || page.ActualWidth < 1 || page.ActualHeight < 1 || !SystemParameters.ClientAreaAnimation) return null;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.ActualWidth), (int)Math.Ceiling(page.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page); bitmap.Freeze(); return bitmap;
    }
    internal static void SwitchPage(FrameworkElement page, Image overlay, BitmapSource? previous, int direction)
    {
        overlay.BeginAnimation(UIElement.OpacityProperty, null);
        if (!SystemParameters.ClientAreaAnimation || previous is null) {
            overlay.Source = null; overlay.Visibility = Visibility.Collapsed; page.Opacity = 1; page.RenderTransform = Transform.Identity; return;
        }
        overlay.Source = previous; overlay.Visibility = Visibility.Visible;
        var exit = new TranslateTransform(); overlay.RenderTransform = exit;
        var enter = new TranslateTransform(); page.RenderTransform = enter;
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        exit.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, -24 * direction, TimeSpan.FromMilliseconds(240)) { EasingFunction = easing });
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220));
        fade.Completed += (_, _) => { overlay.Visibility = Visibility.Collapsed; overlay.Source = null; };
        overlay.BeginAnimation(UIElement.OpacityProperty, fade);
        enter.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(24 * direction, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = easing });
        page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280)));
    }

    internal static void SetExpanded(FrameworkElement element, bool expanded, bool animate)
    {
        if ((bool)element.GetValue(ExpandedProperty) == expanded) return;
        element.SetValue(ExpandedProperty, expanded);
        int revision = (int)element.GetValue(RevisionProperty) + 1;
        element.SetValue(RevisionProperty, revision);
        double from = element.Visibility == Visibility.Visible ? element.ActualHeight : 0;
        double opacity = element.Opacity;
        element.BeginAnimation(FrameworkElement.HeightProperty, null);
        element.BeginAnimation(UIElement.OpacityProperty, null);
        if (!animate || !SystemParameters.ClientAreaAnimation) {
            element.Height = double.NaN; element.Opacity = 1;
            element.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            element.IsHitTestVisible = expanded;
            return;
        }
        element.Visibility = Visibility.Visible;
        element.IsHitTestVisible = expanded;
        double target = 0;
        if (expanded && element is ContentControl { Content: FrameworkElement content }) {
            double width = Math.Max(1, element.ActualWidth > 0 ? element.ActualWidth : ((FrameworkElement)element.Parent).ActualWidth);
            content.Measure(new Size(width, double.PositiveInfinity));
            target = Math.Min(element.MaxHeight, content.DesiredSize.Height);
        }
        element.Height = from;
        var height = new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(expanded ? 260 : 220)) {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        height.Completed += (_, _) => {
            if ((int)element.GetValue(RevisionProperty) != revision) return;
            element.BeginAnimation(FrameworkElement.HeightProperty, null);
            element.Height = double.NaN;
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            element.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        };
        element.BeginAnimation(FrameworkElement.HeightProperty, height);
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(expanded && from == 0 ? 0 : opacity,
            expanded ? 1 : 0, TimeSpan.FromMilliseconds(expanded ? 220 : 180)));
    }

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached("Progress", typeof(double), typeof(Motion), new PropertyMetadata(0d, ProgressChanged));
    public static double GetProgress(DependencyObject target) => (double)target.GetValue(ProgressProperty);
    public static void SetProgress(DependencyObject target, double value) => target.SetValue(ProgressProperty, value);
    private static void ProgressChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ProgressBar bar) return;
        double value = Math.Clamp((double)e.NewValue, 0, 100);
        if (!bar.IsVisible) { bar.Value = value; return; }
        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(bar.Value, value, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    public static readonly DependencyProperty RevealProperty = DependencyProperty.RegisterAttached("Reveal", typeof(bool), typeof(Motion), new PropertyMetadata(false, RevealChanged));
    public static bool GetReveal(DependencyObject target) => (bool)target.GetValue(RevealProperty);
    public static void SetReveal(DependencyObject target, bool value) => target.SetValue(RevealProperty, value);
    private static void RevealChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not FrameworkElement element || !(bool)e.NewValue) return;
        element.Loaded += (_, _) => Reveal(element);
    }
    internal static void Reveal(FrameworkElement element)
    {
        if (!element.IsVisible) return;
        var move = new TranslateTransform(); element.RenderTransform = move;
        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
    }
}
