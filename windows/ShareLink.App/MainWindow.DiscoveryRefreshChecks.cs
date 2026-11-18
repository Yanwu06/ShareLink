using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckDiscoveryRefreshAsync(string output)
    {
        var checks=new List<string>();void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        string local="0102030405060708"+new string('a',48),remote="1112131415161718"+new string('b',48);long seen=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int restarts=0,reads=0;var pending=new TaskCompletionSource<PcPublicMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var scan=new PcCheckDiscovery(metadataReader:_=>{reads++;return reads==1?pending.Task:Task.FromResult<PcPublicMetadata?>(new(remote,"Fresh peer"));},restartScan:()=>restarts++)){
            scan.SetLocalFingerprint(local);scan.RecordShared(1,-40,seen,BluetoothAddressType.Random);var oldRead=scan.RefreshNamesAsync();
            scan.Refresh();scan.RecordShared(1,-40,seen+1,BluetoothAddressType.Random);await scan.RefreshNamesAsync();
            Check("refresh restarts PC scan without overlapping pending metadata requests",restarts==1&&reads==1);
            pending.SetResult(new(remote,"Obsolete peer"));await oldRead;
            Check("a metadata result from before refresh cannot revive a stale row",scan.Snapshot().Length==0);
            await scan.RefreshNamesAsync();Check("fresh metadata after refresh restores the restarted peer",scan.Snapshot().Single().Name=="Fresh peer"&&reads==2);
            scan.Refresh();scan.Record(2,"Self",-40,seen+2,BluetoothAddressType.Public,PcServiceIdentity.Create(local));
            Check("refresh retains own-identity exclusion and removes PC cached rows",restarts==2&&scan.Snapshot().Length==0);
            scan.Dispose();scan.Refresh();Check("a disposed PC scanner cannot be restarted by refresh",restarts==2);
        }
        var owner=new MainWindow(Path.Combine(output,"refresh-normal-ui"),desktopTest:true);
        owner.TaskbarItemInfo=new System.Windows.Shell.TaskbarItemInfo();
        using var native=new NativeCore(owner.CoreConfiguration());using var mobile=new NearbyBluetooth();
        restarts=0;using var pc=new PcCheckDiscovery(restartScan:()=>restarts++);
        owner.core=native;owner.pcDiscovery=pc;owner.nearbyDiscovery=mobile;owner.pcRefreshing=true;owner.discoveryEnabled=true;
        try{
            string pair=Text(native.Call(new{op="pair_begin"}),"pairId");var ended=new NearbySession{PairId=pair};ended.Dispose();owner.nearbySession=ended;
            Check("ended client object cannot block public PC metadata reads",owner.CanReadPcMetadata);
            pc.Record(1,"Old PC",-40,seen,BluetoothAddressType.Public,PcServiceIdentity.Create(remote));mobile.RecordAdvertisement(2,"",-40,seen,BluetoothAddressType.Public,false,fingerprintPrefix:remote[..16],advertisedName:"Old phone");
            await owner.RefreshDevicesAsync();
            Check("normal refresh releases dead session and clears both device lists",owner.nearbySession is null&&pc.Snapshot().Length==0&&mobile.Snapshot().Length==0&&restarts==1&&owner.discoveryEnabled);
            pc.Record(3,"Fresh PC",-40,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),BluetoothAddressType.Random,PcServiceIdentity.Create(remote));owner.RefreshNearbyList();
            Check("a restarted PC appears in ordinary UI without toggling discovery",((NearbyPhone[])owner.NearbyList.ItemsSource).Single().Name=="Fresh PC");
            var healthy=new NearbySession{Authenticated=true};owner.nearbySession=healthy;await owner.RefreshDevicesAsync();
            Check("refresh preserves an authenticated live connection",ReferenceEquals(owner.nearbySession,healthy)&&healthy.Authenticated&&restarts==1);healthy.Dispose();owner.nearbySession=null;
            string source=Path.Combine(output,"refresh-active-generated.bin");await File.WriteAllBytesAsync(source,new byte[32]);
            string task=Text(native.Call(new{op="prepare_pull",peerName="Generated PC",peerDeviceType="computer",files=new[]{new{path=source,name=Path.GetFileName(source)}}}),"id");
            ended=new NearbySession();ended.Dispose();owner.nearbySession=ended;await owner.RefreshDevicesAsync();await owner.ReleaseEndedNearbySessionAsync();
            Check("fresh native active task blocks refresh and session teardown before UI catches up",restarts==1&&ReferenceEquals(owner.nearbySession,ended));
            await owner.RefreshState();Check("normal refresh fixture completed its real snapshot update",!owner.StatusMessage.Text.StartsWith("刷新状态失败"));Check("active ordinary UI disables refresh and discovery shutdown",!owner.RefreshDevicesButton.IsEnabled&&!owner.DiscoveryButton.IsEnabled);
            native.Call(new{op="cancel",id=task});await owner.RefreshState();await owner.RefreshDevicesAsync();
            Check("refresh recovers after task ends without changing the discovery switch",restarts==2&&owner.nearbySession is null&&owner.discoveryEnabled&&owner.RefreshDevicesButton.IsEnabled);
            owner.discoveryEnabled=false;await owner.RefreshDevicesAsync();Check("refresh does not turn disabled discovery on",restarts==2&&!owner.discoveryEnabled);
        }finally{owner.timer.Stop();owner.core=null;owner.pcDiscovery=null;owner.nearbyDiscovery=null;owner.nearbySession?.Dispose();owner.nearbySession=null;}
        return checks.ToArray();
    }
}
