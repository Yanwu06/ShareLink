using System.Windows;
using System.Windows.Media;
namespace ShareLink;
public partial class MainWindow
{
    private void TransferNav_Click(object sender,RoutedEventArgs e)=>ShowHistory(false);
    private void DiscoveryInfo_Click(object sender,RoutedEventArgs e)=>DiscoveryInfoPopup.IsOpen=!DiscoveryInfoPopup.IsOpen;
    private void UpdateNavigation()
    {
        TransferNavButton.Tag=!showingHistory&&!showingSettings?"True":"False";HistoryButton.Tag=showingHistory?"True":"False";SettingsButton.Tag=showingSettings?"True":"False";
        PageTitle.Text=showingHistory?"传输记录":showingSettings?"设置":"文件分享";BrowserShareButton.Visibility=showingHistory||showingSettings?Visibility.Collapsed:Visibility.Visible;
        DiscoveryStatusLabel.Text=discoveryEnabled?HostedPcConnected||nearbySession?.Authenticated==true?"设备已连接":"发现已开启":"发现已关闭";
        DiscoveryStatusDot.Fill=new SolidColorBrush(discoveryEnabled?Color.FromRgb(49,184,107):Color.FromRgb(153,163,178));
        if(showingHistory||showingSettings||browserSharing)DiscoveryInfoPopup.IsOpen=false;
    }
}
