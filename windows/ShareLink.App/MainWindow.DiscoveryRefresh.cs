using System.Windows.Media;
using System.Windows.Media.Animation;
namespace ShareLink;
public partial class MainWindow
{
    private bool CanReadPcMetadata => !closing && !pcCloseInProgress && !storageChanging && !pairing && !sending && !hasActiveTransfers && !PcSessionOwned && nearbySession?.Authenticated != true;
    private bool CanRefreshDiscovery => discoveryEnabled && !closing && !pcCloseInProgress && !storageChanging && !pairing && !sending && !hasActiveTransfers && !modeChanging && !pcDiscoveryChanging && !PcSessionOwned && nearbySession?.Authenticated != true && !PcLinkBusy;
    private async Task ReleaseEndedNearbySessionAsync()
    {
        if(core is null || nearbySession is not {Authenticated:false} ended || pairing || sending || hasActiveTransfers || PcHasTasks())return;
        // A dead client object must not block public PC metadata reads indefinitely.
        nearbySession=null;connectedPhone=null;selectedIsNearby=false;selectedPeer=null;
        if(ended.PairId.Length>0)try{await Task.Run(()=>core.Call(new{op="pair_close",pairId=ended.PairId}));}catch{/* Already closed native keys need no second close. */}
        ended.Dispose();
    }
    private async Task RefreshDevicesAsync()
    {
        if(!CanRefreshDiscovery || core is null || PcHasTasks())return;
        pcDiscoveryChanging=true;UpdatePresentation();
        var started=DateTime.UtcNow;
        RefreshRotation.BeginAnimation(RotateTransform.AngleProperty,new DoubleAnimation(0,360,TimeSpan.FromMilliseconds(850)){RepeatBehavior=RepeatBehavior.Forever,EasingFunction=new CubicEase{EasingMode=EasingMode.EaseInOut}});
        try{
            await ReleaseEndedNearbySessionAsync();
            nearbyDiscovery?.Refresh();pcDiscovery?.Refresh();
            BusinessActivity();RefreshNearbyList();
            StatusMessage.Text="正在重新搜索附近设备…";
        }catch(Exception ex){StatusMessage.Text="刷新设备未完成："+ex.Message;}
        finally{var remaining=TimeSpan.FromMilliseconds(850)-(DateTime.UtcNow-started);if(remaining>TimeSpan.Zero)await Task.Delay(remaining);RefreshRotation.BeginAnimation(RotateTransform.AngleProperty,null);RefreshRotation.Angle=0;pcDiscoveryChanging=false;UpdatePresentation();}
    }
    private async Task NotifyPcDisconnectForCloseAsync()
    {
        if(nearbySession?.Authenticated!=true || connectedPhone is null || !IsNativePc(connectedPhone))return;
        // Tasks and the temporary network have already been released before this call.
        // End the peer's logical session even if Windows retains the physical BLE link.
        try{await SecureAsync(new{op="disconnect"});}catch{/* An absent peer cannot prevent local normal exit. */}
    }
}
