using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace ShareLink;

internal static class AppDialog
{
    internal static bool Show(Window owner, string title, string message, string confirm = "确定", string? cancel = "取消", FrameworkElement? extra = null, bool danger = false, CancellationToken cancellation = default)
    {
        if (cancellation.IsCancellationRequested) return false;
        var window = new Window { Owner = owner, Title = title, Width = 500, SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false, FontFamily = owner.FontFamily, Foreground = owner.Foreground, FontSize = 17 };
        var transform = new ScaleTransform(1, 1);
        var frame = new Border { Background = Brushes.White, CornerRadius = new CornerRadius(24), Padding = new Thickness(28),
            Margin = new Thickness(20), RenderTransformOrigin = new Point(.5, .5), RenderTransform = transform,
            Effect = new DropShadowEffect { Color = Color.FromRgb(32, 45, 66), BlurRadius = 25, Opacity = .17, ShadowDepth = 5 } };
        var panel = new StackPanel(); frame.Child = panel;
        var heading = new Grid(); heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock { Text = title, FontSize = 25, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        panel.Children.Add(heading);
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = message, TextAlignment = TextAlignment.Left, TextWrapping = TextWrapping.Wrap, FontSize = 17, LineHeight = 24, Foreground = (Brush)owner.FindResource("Muted") });
        if (extra is not null) { extra.Margin = new Thickness(0, 18, 0, 0); details.Children.Add(extra); }
        panel.Children.Add(new ScrollViewer { Content = details, MaxHeight = Math.Max(160, Math.Min(390, SystemParameters.WorkArea.Height - 260)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 18, 0, 24) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        bool completing = false;
        void Complete(bool result)
        {
            if (completing) return; completing = true;
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(100));
            fade.Completed += (_, _) => { if (window.IsVisible) window.DialogResult = result; };
            window.BeginAnimation(UIElement.OpacityProperty, fade);
        }
        if (cancel is not null)
        {
            var back = new Button { Content = cancel, MinWidth = 100, MinHeight = 44, Margin = new Thickness(0, 0, 12, 0) };
            back.Click += (_, _) => Complete(false); actions.Children.Add(back);
        }
        var primary = new Button { Content = confirm, Style = (Style)owner.FindResource("Primary"), MinWidth = 110, MinHeight = 44, IsDefault = true };
        if (danger) primary.Background = new SolidColorBrush(Color.FromRgb(192, 79, 79));
        primary.Click += (_, _) => Complete(true); actions.Children.Add(primary); panel.Children.Add(actions);
        window.Content = frame;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Complete(false); } };
        heading.MouseLeftButtonDown += (_, e) => { if (e.Source is TextBlock && e.LeftButton == MouseButtonState.Pressed) window.DragMove(); };
        window.Loaded += (_, _) =>
        {
            window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new QuadraticEase() });
            primary.Focus();
        };
        using var registration = cancellation.Register(() => window.Dispatcher.BeginInvoke(new Action(() => {
            if (window.IsVisible) Complete(false);
        })));
        return window.ShowDialog() == true;
    }
}
