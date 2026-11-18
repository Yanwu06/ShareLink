using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
namespace ShareLink;
public partial class MainWindow
{
    internal static string[] CheckSendTarget(string output)
    {
        var checks=new List<string>();void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        var w=new MainWindow(Path.Combine(output,"target-ui-fixture"),desktopTest:true);
        w.UpdateSendTargetPresentation();
        Check("unconnected target has no device icon or send prefix",w.TargetDeviceIcon.Visibility==Visibility.Collapsed&&w.TargetPrefix.Visibility==Visibility.Collapsed&&w.TargetLabel.Text=="请连接接收设备");
        w.RenderDiagnostic(Path.Combine(output, "unconnected-centered.png")); w.LayoutSendTarget(); w.RenderDiagnostic(Path.Combine(output, "unconnected-centered.png"));
        double promptCenter = w.TargetLabel.TranslatePoint(new Point(w.TargetLabel.ActualWidth / 2, 0), w.SendButton).X;
        Check("unconnected prompt centers across the whole button including arrow area", Math.Abs(promptCenter - w.SendButton.ActualWidth / 2) <= 1);
        using var session=new NearbySession{Authenticated=true,Name="Generated peer"};w.nearbySession=session;w.selectedIsNearby=true;
        foreach(var entry in new[]{("computer","电脑"),("phone","手机"),("tablet","平板"),("unknown","未知类型设备")}){
            w.phoneInfo=JsonSerializer.SerializeToElement(new{deviceType=entry.Item1});w.UpdateSendTargetPresentation();
            Check("authenticated target renders actual type "+entry.Item1,w.TargetDeviceIcon.Visibility==Visibility.Visible&&(string)w.TargetDeviceIcon.Tag==entry.Item1&&AutomationProperties.GetItemStatus(w.SendButton)=="接收设备："+entry.Item2&&w.TargetLabel.Text=="Generated peer");
        }
        Check("target device icon sits between send prefix and name",System.Windows.Controls.Grid.GetColumn(w.TargetPrefix)<System.Windows.Controls.Grid.GetColumn(w.TargetDeviceIcon)&&System.Windows.Controls.Grid.GetColumn(w.TargetDeviceIcon)<System.Windows.Controls.Grid.GetColumn(w.TargetNameViewport));
        foreach (int width in new[] { 940, 1180, 1440 }) {
            void Render(string suffix) {
                string image = Path.Combine(output, $"send-target-{width}-{suffix}.png");
                w.RenderDiagnostic(image, width, 800); w.LayoutSendTarget(); w.RenderDiagnostic(image, width, 800);
            }
            session.Name = "冰霜"; w.UpdateSendTargetPresentation(); Render("short");
            Check($"short target stays centered at {width}px", w.TargetGroup.HorizontalAlignment == HorizontalAlignment.Center && Math.Abs(w.TargetGroup.ActualWidth - w.TargetContentHost.ActualWidth) > 1);
            Check($"wider send button fits inside footer at {width}px", w.SendButton.ActualWidth >= 220 && w.SendButton.TranslatePoint(new Point(w.SendButton.ActualWidth, 0), w.SendBar).X <= w.SendBar.ActualWidth);
            session.Name = "用于验证设备全称滚动显示的电脑名称_ABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789_TAIL-9Z";
            w.UpdateSendTargetPresentation(); Render("long");
            Check($"long target pins prefix and icon left at {width}px", w.TargetGroup.HorizontalAlignment == HorizontalAlignment.Left && w.TargetLabel.ActualWidth > w.TargetNameViewport.ActualWidth && w.TargetNameViewport.ClipToBounds);
            var nameClip = System.Windows.Controls.Primitives.LayoutInformation.GetLayoutClip(w.TargetLabel);
            Check($"full device name is laid out without its own narrow clip at {width}px", nameClip is null || nameClip.Bounds.Width >= w.TargetLabel.ActualWidth - 1);
            double prefixLeft = w.TargetPrefix.TranslatePoint(new Point(), w.SendBar).X;
            w.TargetNameTranslation.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
            w.TargetNameTranslation.X = -(w.TargetLabel.ActualWidth - w.TargetNameViewport.ActualWidth);
            w.RenderDiagnostic(Path.Combine(output, $"send-target-{width}-tail.png"), width, 800);
            double tailRight = w.TargetLabel.TranslatePoint(new Point(w.TargetLabel.ActualWidth, 0), w.TargetNameViewport).X;
            Check($"actual name tail reaches viewport while prefix remains fixed at {width}px", Math.Abs(tailRight - w.TargetNameViewport.ActualWidth) <= 1 && w.TargetPrefix.TranslatePoint(new Point(), w.SendBar).X == prefixLeft);
            session.Name = "冰霜"; w.UpdateSendTargetPresentation(); Render("short-restored");
            Check($"short target restores position after long name at {width}px", w.TargetNameTranslation.X == 0 && w.TargetGroup.HorizontalAlignment == HorizontalAlignment.Center);
        }
        w.discoveryEnabled = false; w.UpdatePresentation();
        Check("disabled discovery really disables refresh", !w.RefreshDevicesButton.IsEnabled);
        w.RenderDiagnostic(Path.Combine(output, "refresh-discovery-off.png"));
        var refreshBody = (System.Windows.FrameworkElement)w.RefreshDevicesButton.Template.FindName("Body", w.RefreshDevicesButton);
        Check("disabled refresh retains full opacity and transparent surface", refreshBody.Opacity == 1 && ((System.Windows.Media.SolidColorBrush)w.RefreshDevicesButton.Background).Color.A == 0);
        w.pairing=true;w.UpdateSendTargetPresentation();Check("pairing hides the old icon until authenticated connection presentation",w.TargetDeviceIcon.Visibility==Visibility.Collapsed&&w.TargetLabel.Text=="正在连接设备…");w.pairing=false;
        session.Dispose();w.UpdateSendTargetPresentation();Check("lost authentication immediately removes the previous device icon",w.TargetDeviceIcon.Visibility==Visibility.Collapsed&&w.TargetPrefix.Visibility==Visibility.Collapsed);
        var applied=new List<bool>();var visibility=new AdvertisementVisibility(v=>applied.Add(v));visibility.Set(true);visibility.Set(true);visibility.Set(false);visibility.Set(false);visibility.Set(true);
        Check("advertising changes are idempotent across connected and disconnected states",applied.SequenceEqual(new[]{true,false,true}));
        bool failed=true;int attempts=0;var retry=new AdvertisementVisibility(_=>{attempts++;if(failed)throw new IOException("generated failure");});try{retry.Set(false);}catch(IOException){}failed=false;retry.Set(false);
        Check("failed advertising update is retried rather than remembered as success",attempts==2);
        w.nearbySession=null;return checks.ToArray();
    }
}
