using Windows.Devices.Bluetooth;
namespace ShareLink;
internal static class PcSharedDiscoveryChecks
{
    internal static async Task<string[]> RunAsync()
    {
        var checks=new List<string>();
        void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        string local="0102030405060708"+new string('a',48),remote="1112131415161718"+new string('b',48),other="2122232425262728"+new string('c',48);
        long clock=1000,seen=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); int reads=0;
        var pending=new TaskCompletionSource<PcPublicMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var scanner=new PcCheckDiscovery(metadataReader:_=>{reads++;return pending.Task;},clock:()=>clock)) {
            scanner.SetLocalFingerprint(local);scanner.RecordShared(1,-40,seen,BluetoothAddressType.Public);
            Check("fixed invitation advertisement stays hidden until PC metadata is valid",scanner.Snapshot().Length==0);
            var lookup=scanner.RefreshNamesAsync();await scanner.RefreshNamesAsync();Check("shared identity lookup is bounded to one pending read",reads==1);
            scanner.RecordShared(1,-41,seen+1,BluetoothAddressType.Public);pending.SetResult(new(remote,"Generated peer"));await lookup;
            var peer=scanner.Snapshot().Single();
            Check("shared metadata produces a PC with the legacy invitation service as its control service",peer.Name=="Generated peer"&&peer.DeviceType=="computer"&&peer.ControlService==NearbyInvitation.ServiceUuid&&peer.FingerprintPrefix==remote[..16]);
            scanner.RecordShared(2,-42,seen+2,BluetoothAddressType.Random);await scanner.RefreshNamesAsync();
            Check("shared public and random addresses merge only after matching identity metadata",scanner.Snapshot() is [{Address:2}]);
            scanner.RecordShared(2,-43,seen+3,BluetoothAddressType.Random);
            Check("later fixed advertisements renew the resolved row without losing its name",scanner.Snapshot().Single().Name=="Generated peer");
        }
        var metadata=new Dictionary<ulong,PcPublicMetadata?>{[1]=new(local,"Same name"),[2]=new(remote,"Same name"),[3]=new(other,"Same name"),[4]=null};
        using(var scanner=new PcCheckDiscovery(metadataReader:p=>Task.FromResult(metadata[p.Address]))) {
            scanner.SetLocalFingerprint(local);
            for(ulong address=1;address<=4;address++){scanner.RecordShared(address,-40,seen,BluetoothAddressType.Public);await scanner.RefreshNamesAsync();}
            Check("shared service excludes the local identity and retains two same-name PCs",scanner.Snapshot().Length==2);
            Check("an old phone-invitation-only service cannot become a PC without identity metadata",scanner.Snapshot().All(p=>p.Address!=4));
            scanner.SnapshotAt(seen+7000);Check("shared rows expire on real broadcast loss",scanner.Snapshot().Length==0);
        }
        reads=0;clock=1000;
        using(var scanner=new PcCheckDiscovery(metadataReader:_=>{reads++;return Task.FromResult<PcPublicMetadata?>(reads==1?null:new(remote,"Retry name"));},clock:()=>clock)) {
            scanner.RecordShared(1,-40,seen,BluetoothAddressType.Public);scanner.CanReadName=()=>false;await scanner.RefreshNamesAsync();
            Check("pairing pauses shared-service metadata reads",reads==0);
            scanner.CanReadName=()=>true;await scanner.RefreshNamesAsync();clock=2999;await scanner.RefreshNamesAsync();
            Check("failed shared lookup waits two seconds before retry",reads==1&&scanner.Snapshot().Length==0);
            clock=3000;await scanner.RefreshNamesAsync();Check("shared lookup recovers on the bounded retry",reads==2&&scanner.Snapshot().Single().Name=="Retry name");
        }
        var slowMetadata=new TaskCompletionSource<PcPublicMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var scanner=new PcCheckDiscovery(metadataReader:_=>slowMetadata.Task)) {
            scanner.RecordShared(1,-40,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),BluetoothAddressType.Random);
            var lookup=scanner.RefreshNamesAsync();scanner.CanReadName=()=>false;
            var drained=scanner.WaitForMetadataIdleAsync(TimeSpan.FromSeconds(2));
            Check("formal connection waits for pending metadata lease to end",!drained.IsCompleted);
            bool timedOut=false;try{await scanner.WaitForMetadataIdleAsync(TimeSpan.FromMilliseconds(20));}catch(TimeoutException){timedOut=true;}
            Check("metadata drain is bounded and does not falsely permit a connection",timedOut&&!drained.IsCompleted);
            slowMetadata.SetResult(new(remote,"Fresh service"));await lookup;await drained;
            Check("completed public metadata releases the formal connection gate",scanner.WaitForMetadataIdleAsync(TimeSpan.FromSeconds(1)).IsCompletedSuccessfully);
        }
        Check("malformed public identity metadata is rejected",!PcDeviceName.ValidMetadata(new("bad","Name"))&&!PcDeviceName.ValidMetadata(new(remote,"bad\nname")));
        return checks.ToArray();
    }
}
