using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;
namespace ShareLink;
internal sealed class PcCheckDiscovery : IDisposable
{
    private BluetoothLEAdvertisementWatcher? watcher;
    private readonly Action? restartScan;
    private int generation;
    private readonly object gate=new();
    private readonly Dictionary<string,NearbyPhone> peers=new(StringComparer.OrdinalIgnoreCase);
    private string localHint="";
    private readonly Dictionary<string,string> names=new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,long> nameAttempts=new(StringComparer.OrdinalIgnoreCase);
    private sealed record SharedPeer(NearbyPhone Peer, string Hint = "", long RetryAt = 0);
    private readonly Dictionary<ulong,SharedPeer> sharedPeers = [];
    private readonly Func<NearbyPhone,Task<PcPublicMetadata?>> metadataReader;
    private readonly Func<NearbyPhone,Task<string?>> nameReader;
    private readonly Func<long> clock;
    internal Func<bool> CanReadName { get; set; } = () => true;
    private bool disposed,readingName;
    private TaskCompletionSource? metadataIdle;
    internal Task WaitForMetadataIdleAsync(TimeSpan timeout)
    {
        lock(gate)return readingName && metadataIdle is not null ? metadataIdle.Task.WaitAsync(timeout) : Task.CompletedTask;
    }
    private int received,matched,sharedReceived,metadataAttempts,metadataSucceeded;
    private string metadataStage="idle";
    private long metadataStarted;
    internal object Diagnostics { get { lock(gate) return new { state=State,error=Error,received=Received,matched=Matched,sharedAdvertisements=sharedReceived,sharedCandidates=sharedPeers.Count,resolved=peers.Count,reading=readingName,canRead=CanReadName(),metadataAttempts,metadataSucceeded,metadataStage,pendingMs=readingName?Math.Max(0,clock()-metadataStarted):0 }; } }
    public string State=>watcher?.Status.ToString()??"not-started";
    public int Received=>Volatile.Read(ref received);
    public int Matched=>Volatile.Read(ref matched);
    public string Error{get;private set;}="";
    public PcCheckDiscovery(Func<NearbyPhone,Task<string?>>? nameReader=null,Func<long>? clock=null,Func<NearbyPhone,Task<PcPublicMetadata?>>? metadataReader=null,Action? restartScan=null){this.restartScan=restartScan;this.metadataReader=metadataReader??(peer=>PcDeviceName.ReadSharedAsync(peer,stage=>{lock(gate)metadataStage=stage;}));this.nameReader=nameReader??PcDeviceName.ReadAsync;this.clock=clock??(()=>Environment.TickCount64);}
    public void SetLocalFingerprint(string fingerprint){lock(gate){localHint=PcServiceIdentity.Hint(PcServiceIdentity.Create(fingerprint));peers.Remove(localHint);}}
    public static bool IsPcServiceData(ReadOnlySpan<byte> value)=>value.Length>=16&&PcServiceIdentity.Hint(new Guid(value[..16])).Length==16;
    public void Start()=>RestartWatcher();
    private void RestartWatcher()
    {
        if(disposed)return;
        var old=watcher;watcher=null;
        if(old is not null){old.Received-=Found;old.Stopped-=Stopped;try{old.Stop();}catch{}}
        if(restartScan is not null){restartScan();return;}
        watcher=new(){ScanningMode=BluetoothLEScanningMode.Active,AllowExtendedAdvertisements=true};
        watcher.Received+=Found;watcher.Stopped+=Stopped;watcher.Start();
    }
    private void Stopped(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementWatcherStoppedEventArgs args)
    {
        if(sender==watcher&&args.Error!=BluetoothError.Success)Error=args.Error.ToString();
    }
    public void Refresh()
    {
        lock(gate){if(disposed)return;generation++;peers.Clear();sharedPeers.Clear();nameAttempts.Clear();Error="";}
        RestartWatcher();
    }
    private void Found(BluetoothLEAdvertisementWatcher sender,BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if(sender!=watcher||disposed)return;
        Interlocked.Increment(ref received);var services=args.Advertisement.ServiceUuids.ToList();
        foreach(var section in args.Advertisement.GetSectionsByType(0x21)){
            using var data=DataReader.FromBuffer(section.Data);if(data.UnconsumedBufferLength<16||data.UnconsumedBufferLength>64)continue;
            byte[] bytes=new byte[data.UnconsumedBufferLength];data.ReadBytes(bytes);if(IsPcServiceData(bytes))services.Add(new Guid(bytes.AsSpan(0,16)));
        }
        if (services.Contains(NearbyInvitation.ServiceUuid)) Interlocked.Increment(ref sharedReceived);
        if (services.Contains(NearbyInvitation.ServiceUuid)) RecordShared(args.BluetoothAddress,args.RawSignalStrengthInDBm,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),args.BluetoothAddressType);
        foreach(var service in services.Distinct())if(PcServiceIdentity.Hint(service).Length>0){Interlocked.Increment(ref matched);Record(args.BluetoothAddress,args.Advertisement.LocalName,args.RawSignalStrengthInDBm,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),args.BluetoothAddressType,service);}
        _=RefreshNamesAsync();
    }
    internal void Record(ulong address,string name,short rssi,long now,BluetoothAddressType type,Guid service)
    {
        RecordCandidate(address,name,rssi,now,type,service,PcServiceIdentity.Hint(service));
    }
    private void RecordCandidate(ulong address,string name,short rssi,long now,BluetoothAddressType type,Guid service,string hint)
    {
        if(hint.Length==0)return;
        lock(gate){if(disposed||hint==localHint||peers.Count>=64&&!peers.ContainsKey(hint))return;
            string displayName=names.TryGetValue(hint,out var resolvedName)?resolvedName:!string.IsNullOrWhiteSpace(name)?name:peers.TryGetValue(hint,out var previous)?previous.Name:"ShareLink 电脑";
            peers[hint]=new(address,displayName,rssi,now,type,FingerprintPrefix:hint){DeviceType="computer",ControlService=service};}
    }
    internal void RecordShared(ulong address,short rssi,long now,BluetoothAddressType type)
    {
        lock(gate) {
            if(disposed)return;
            foreach(var key in sharedPeers.Where(p=>now-p.Value.Peer.SeenAtMs>6000).Select(p=>p.Key).ToArray())sharedPeers.Remove(key);
            if(sharedPeers.Count>=64&&!sharedPeers.ContainsKey(address))return;
            sharedPeers.TryGetValue(address,out var old);
            var peer=new NearbyPhone(address,"",rssi,now,type){DeviceType="computer",ControlService=NearbyInvitation.ServiceUuid};
            sharedPeers[address]=new(peer,old?.Hint??"",old?.RetryAt??0);
            if(old?.Hint.Length==16)RecordCandidate(address,"",rssi,now,type,NearbyInvitation.ServiceUuid,old.Hint);
        }
    }
    private void RecordSharedMetadata(ulong address,PcPublicMetadata metadata)
    {
        if(!PcDeviceName.ValidMetadata(metadata))return;
        lock(gate) {
            if(disposed||!sharedPeers.TryGetValue(address,out var current))return;
            string hint=PcServiceIdentity.Hint(PcServiceIdentity.Create(metadata.Fingerprint));
            sharedPeers[address]=current with{Hint=hint};RememberName(hint,metadata.Name);nameAttempts[hint]=clock()+30000;
            var p=current.Peer;RecordCandidate(p.Address,metadata.Name,p.Rssi,p.SeenAtMs,p.AddressType,NearbyInvitation.ServiceUuid,hint);
        }
    }
    internal void RememberName(string hint,string name)
    {
        if(hint.Length!=16||!hint.All(Uri.IsHexDigit)||!PcDeviceName.Valid(name))return;
        lock(gate){
            if(disposed)return;
            if(names.Count>=64&&!names.ContainsKey(hint))names.Remove(names.Keys.First());
            names[hint]=name;
            if(peers.TryGetValue(hint,out var peer))peers[hint]=peer with{Name=name};
        }
    }
    internal NearbyPhone? Resolve(NearbyPhone candidate)=>Snapshot().FirstOrDefault(p=>string.Equals(p.FingerprintPrefix,candidate.FingerprintPrefix,StringComparison.OrdinalIgnoreCase));
    internal async Task RefreshNamesAsync()
    {
        NearbyPhone? candidate; bool sharedLookup;int lookupGeneration;
        lock(gate) {
            if(disposed||readingName||!CanReadName())return;
            long now=clock(); Snapshot();
            candidate=sharedPeers.Values.Where(p=>p.Hint.Length==0&&now>=p.RetryAt).OrderByDescending(p=>p.Peer.Rssi).Select(p=>p.Peer).FirstOrDefault();
            sharedLookup=candidate is not null;
            candidate??=Snapshot().OrderBy(p=>names.ContainsKey(p.FingerprintPrefix)?1:0).FirstOrDefault(p=>!nameAttempts.TryGetValue(p.FingerprintPrefix,out long at)||now>=at);
            if(candidate is null)return;
            readingName=true;metadataIdle=new(TaskCreationOptions.RunContinuationsAsynchronously);lookupGeneration=generation;metadataStarted=now;
            if(sharedLookup){metadataAttempts++;metadataStage="starting";}
            if(sharedLookup)sharedPeers[candidate.Address]=sharedPeers[candidate.Address] with{RetryAt=long.MaxValue};
            else nameAttempts[candidate.FingerprintPrefix]=long.MaxValue;
        }
        bool success=false;
        try {
            if(sharedLookup) {
                var metadata=await metadataReader(candidate);
                if(!PcDeviceName.ValidMetadata(metadata))return;
                lock(gate){if(disposed||lookupGeneration!=generation)return;RecordSharedMetadata(candidate.Address,metadata!);metadataSucceeded++;metadataStage="success";success=true;}
            } else {
                string? name=await nameReader(candidate);
                if(name is null||!PcDeviceName.Valid(name))return;
                lock(gate){if(disposed||lookupGeneration!=generation)return;RememberName(candidate.FingerprintPrefix,name);success=true;}
            }
        } catch (Exception ex) { lock(gate) if(sharedLookup) metadataStage=$"exception:{ex.GetType().Name}:0x{ex.HResult:X8}"; /* Public metadata cannot change pairing or start a hotspot. */ }
        finally {
            lock(gate) {
                readingName=false;metadataIdle?.TrySetResult();
                if(!disposed&&lookupGeneration==generation&&sharedLookup&&sharedPeers.TryGetValue(candidate.Address,out var p))sharedPeers[candidate.Address]=p with{RetryAt=clock()+(success?30000:2000)};
                else if(!disposed&&lookupGeneration==generation&&!sharedLookup&&peers.ContainsKey(candidate.FingerprintPrefix))nameAttempts[candidate.FingerprintPrefix]=clock()+(success?30000:2000);
                else if(lookupGeneration==generation&&!sharedLookup)nameAttempts.Remove(candidate.FingerprintPrefix);
            }
        }
    }
    public NearbyPhone[] Snapshot()=>SnapshotAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    internal NearbyPhone[] SnapshotAt(long now){lock(gate){foreach(var address in sharedPeers.Where(p=>now-p.Value.Peer.SeenAtMs>6000).Select(p=>p.Key).ToArray())sharedPeers.Remove(address);foreach(var hint in peers.Where(p=>now-p.Value.SeenAtMs>6000).Select(p=>p.Key).ToArray()){peers.Remove(hint);nameAttempts.Remove(hint);}return peers.Values.OrderByDescending(p=>p.Rssi).ToArray();}}
    public void Dispose(){lock(gate){disposed=true;generation++;peers.Clear();names.Clear();nameAttempts.Clear();sharedPeers.Clear();}var old=watcher;watcher=null;if(old is not null){old.Received-=Found;old.Stopped-=Stopped;try{old.Stop();}catch{}}}
}
