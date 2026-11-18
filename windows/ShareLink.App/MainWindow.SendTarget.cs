using System.Windows;
using System.Windows.Automation;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
namespace ShareLink;
public partial class MainWindow
{
    private void UpdateSendTargetPresentation()
    {
        bool nearby=selectedIsNearby&&nearbySession?.Authenticated==true;
        string name=HostedPcConnected?pcProtocol!.Name:nearby?nearbySession!.Name:modules.LanEnabled?selectedPeer?.Name??"":"";
        bool connected=!pairing&&!PcPairing&&name.Length>0;
        string type=HostedPcConnected?"computer":nearby?Text(phoneInfo,"deviceType"):selectedPeer?.DeviceType??"unknown";
        if(nearby&&type.Length==0)type=connectedPhone?.DeviceType??"unknown";
        type=type.Trim().ToLowerInvariant();
        TargetDeviceIcon.Tag=connected?type:"unknown";
        TargetDeviceIcon.Visibility=TargetPrefix.Visibility=connected?Visibility.Visible:Visibility.Collapsed;
        TargetLabel.Text=connected?name:pairing||PcPairing?"正在连接设备…":"请连接接收设备";
        System.Windows.Controls.Grid.SetColumnSpan(TargetContentHost, connected ? 1 : 2);
        TargetContentHost.Margin = new Thickness(0, 0, connected ? 12 : 0, 0);
        QueueSendTargetLayout();
        string label=connected?"发送到："+name:TargetLabel.Text;
        SendButton.ToolTip=label;AutomationProperties.SetName(SendButton,label);
        string device=type switch{"computer" or "pc" or "2in1"=>"电脑","phone"=>"手机","tablet"=>"平板",_=>"未知类型设备"};
        AutomationProperties.SetItemStatus(SendButton,connected?"接收设备："+device:"");
    }
    private bool sendTargetLayoutPending;
    private string sendTargetLayoutKey = "";
    private void SendTarget_SizeChanged(object sender, SizeChangedEventArgs e) => QueueSendTargetLayout();
    private void SendTarget_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => QueueSendTargetLayout();
    private void QueueSendTargetLayout()
    {
        if (sendTargetLayoutPending || TargetLabel is null) return;
        sendTargetLayoutPending = true;
        Dispatcher.InvokeAsync(() => { sendTargetLayoutPending = false; LayoutSendTarget(); }, DispatcherPriority.Loaded);
    }
    private void LayoutSendTarget()
    {
        double available = TargetContentHost.ActualWidth;
        if (available <= 0) return;
        bool connected = TargetPrefix.Visibility == Visibility.Visible;
        double WidthOf(System.Windows.Controls.TextBlock text) => new FormattedText(text.Text,
            CultureInfo.CurrentUICulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;
        double nameWidth = Math.Ceiling(WidthOf(TargetLabel)) + 1;
        double fixedWidth = connected ? Math.Ceiling(WidthOf(TargetPrefix)) + TargetDeviceIcon.Width + TargetDeviceIcon.Margin.Left + TargetDeviceIcon.Margin.Right : 0;
        bool overflow = connected && nameWidth + fixedWidth > available;
        double viewport = Math.Max(0, Math.Min(nameWidth, available - fixedWidth));
        TargetGroup.Width = overflow ? available : Math.Min(available, fixedWidth + nameWidth);
        TargetGroup.HorizontalAlignment = overflow ? HorizontalAlignment.Left : HorizontalAlignment.Center;
        TargetNameViewport.Width = viewport;
        TargetLabel.Width = nameWidth;
        string key = $"{TargetLabel.Text}|{connected}|{Math.Round(available, 2)}|{TargetContentHost.IsVisible}|{SystemParameters.ClientAreaAnimation}";
        if (key == sendTargetLayoutKey) return;
        sendTargetLayoutKey = key;
        // Only the device name moves; prefix, device icon and arrow stay fixed.
        ShareLink.BrowserSharing.MarqueeMotion.Apply(TargetLabel, TargetNameTranslation, nameWidth,
            overflow && TargetContentHost.IsVisible && SystemParameters.ClientAreaAnimation);
    }
    private void UpdateDiscoverability()
    {
        bool visible=discoveryEnabled&&!HostedPcConnected&&nearbySession?.Authenticated!=true&&!closing&&!pcCloseInProgress&&!browserSharing;
        try{nearbyInvitation?.SetDiscoverable(visible);pcRadio?.SetDiscoverable(visible);}
        catch(Exception ex){StatusMessage.Text="对外发现状态更新未完成："+ex.Message;}
    }
}
