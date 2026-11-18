using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using ShareLink.BrowserSharing;

namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckMarqueeAsync(string output)
    {
        var checks = new List<string>();
        void Check(string name, bool ok) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        var control = new MarqueeText { Text = "用于验证长名称完整滚出再淡入的设备_ABCDEFGHIJKLMNOPQRSTUVWXYZ_TAIL-9Z", FontSize = 17, Width = 220, ToolTip = "Full name" };
        var panel = new StackPanel(); panel.Children.Add(control);
        var window = new Window { Content = panel, Width = 300, Height = 140, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -30000, Top = -30000 };
        TextBlock Label() => (TextBlock)((Canvas)((Border)VisualTreeHelper.GetChild(control, 0)).Child).Children[0];
        try {
            window.Show(); window.UpdateLayout();
            var label = Label(); var translation = (TranslateTransform)label.RenderTransform;
            double width = label.ActualWidth;
            var clip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(label);
            Check("generic marquee lays out the whole long name beyond its sole clipping viewport", width > control.ActualWidth && ((Border)VisualTreeHelper.GetChild(control, 0)).ClipToBounds && (clip is null || clip.Bounds.Width >= width - 1));
            var clock = MarqueeMotion.Apply(label, translation, width, true)!;
            double exit = MarqueeMotion.ExitSeconds(width), cycle = MarqueeMotion.CycleSeconds(width);
            void Seek(double seconds) { clock.Controller!.SeekAlignedToLastTick(TimeSpan.FromSeconds(seconds), TimeSeekOrigin.BeginTime); }
            void Capture(string name) {
                var drawing = new DrawingVisual(); using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 240, 44)); dc.DrawRectangle(new VisualBrush(control) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(10, 8, control.ActualWidth, control.ActualHeight)); }
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(240, 44, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, "marquee-" + name + ".png")); encoder.Save(file);
            }
            Seek(.8); Check("name holds still and opaque for the first second", translation.X == 0 && label.Opacity == 1); Capture("hold");
            Seek(1 + (width - control.ActualWidth) / (width + 4) * (exit - 1));
            Check("full tail reaches the viewport before leaving", Math.Abs(translation.X + width - control.ActualWidth) < 1); Capture("tail");
            double first = 1 + (exit - 1) * .25, second = 1 + (exit - 1) * .5;
            Seek(first); double quarter = translation.X;
            Seek(second); Check("leftward travel uses the phone constant 80 DIP per second", Math.Abs(translation.X - 2 * quarter) < .01 && Math.Abs((width + 4) / (exit - 1) - 80) < .1);
            Seek(exit + .1); Check("entire name exits leaving an empty viewport for 200 milliseconds", translation.X <= -width && label.Opacity == 0); Capture("empty");
            Seek(exit + .2 + .13); Check("name returns at the original position with an intermediate ease-out fade", translation.X == 0 && label.Opacity > .5 && label.Opacity < 1); Capture("fade-in");
            Seek(cycle + .5); Check("next cycle starts opaque and still without reverse scrolling", translation.X == 0 && label.Opacity == 1); Capture("returned");
            Seek(3 * cycle + .5); Check("position and opacity remain synchronized across repeated cycles", translation.X == 0 && label.Opacity == 1);
            MarqueeMotion.Apply(label, translation, width, false);
            control.Text = "冰霜"; window.UpdateLayout();
            Check("short replacement cancels motion and restores full opacity", Label().Opacity == 1 && ((TranslateTransform)Label().RenderTransform).X == 0 && !Label().HasAnimatedProperties);
            control.Text = "再次验证长名称_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_TAIL-9Z"; window.UpdateLayout();
            control.Paused = true;
            Check("paused marquee cancels both animations and restores the complete start", !Label().HasAnimatedProperties && !Label().RenderTransform.HasAnimatedProperties && Label().Opacity == 1 && ((TranslateTransform)Label().RenderTransform).X == 0);
            control.Paused = false; control.Visibility = Visibility.Collapsed;
            Check("hidden marquee stops and restores opacity", !Label().HasAnimatedProperties && !Label().RenderTransform.HasAnimatedProperties && Label().Opacity == 1);
            control.Visibility = Visibility.Visible; window.UpdateLayout();
            if (SystemParameters.ClientAreaAnimation) Check("showing a long name restarts its cycle", Label().HasAnimatedProperties && Label().RenderTransform.HasAnimatedProperties);
            control.Width = 1200; window.Width = 1300; window.UpdateLayout();
            Check("widening to fit stops auto-scroll", !Label().HasAnimatedProperties && !Label().RenderTransform.HasAnimatedProperties && ((TranslateTransform)Label().RenderTransform).X == 0);
            control.Width = 220; window.UpdateLayout();
            panel.Children.Remove(control);
            await Task.Delay(30);
            Check("unloaded marquee releases both animation clocks", !label.HasAnimatedProperties && !translation.HasAnimatedProperties && label.Opacity == 1);
        } finally { window.Close(); }
        var legacy = new MainWindow(Path.Combine(output, "legacy-marquee-fixture"));
        var peer = new Peer("generated", "用于连续滚动的长设备名称_ABCDEFGHIJKLMNOPQRSTUVWXYZ_TAIL-9Z", "127.0.0.1", 1, new string('0', 64));
        legacy.knownPeers[peer.Id] = peer; legacy.RefreshNearbyList();
        var original = legacy.PeerList.ItemsSource;
        for (int i = 0; i < 20; i++) legacy.RefreshNearbyList();
        Check("unchanged periodic LAN snapshots retain marquee row instances", ReferenceEquals(original, legacy.PeerList.ItemsSource));
        legacy.knownPeers[peer.Id] = peer with { Name = "Updated generated name" }; legacy.RefreshNearbyList();
        Check("changed LAN names refresh instead of preserving stale text", !ReferenceEquals(original, legacy.PeerList.ItemsSource) && ((Peer[])legacy.PeerList.ItemsSource).Single().Name == "Updated generated name");
        legacy.knownPeers.Clear(); legacy.RefreshNearbyList();
        Check("departed LAN peers remove their marquee rows", ((Peer[])legacy.PeerList.ItemsSource).Length == 0);
        legacy.timer.Stop();
        return checks.ToArray();
    }
}
