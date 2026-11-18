using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using ShareLink.BrowserSharing;
namespace ShareLink;

public partial class MainWindow
{
    private readonly bool pcNativeTest;
    private PcCheckDiscovery? pcDiscovery;
    private Dictionary<string,string> pcDisplayNames=new(StringComparer.OrdinalIgnoreCase);
    private PcControlProtocol? pcProtocol;
    private PcGattHost? pcRadio;
    private NearbyHotspot? pcAccessPoint;
    private Task? pcStarting;
    private JsonElement? pcCredentials,pcOffer;
    private string pcHotspotError="";
    private bool pcRefreshing,pcCloseInProgress,pcClosed,pcDiscoveryChanging;
    private readonly CancellationTokenSource pcLifetime=new();
    private readonly object pcAdmissionGate = new();
    private volatile bool pcInboundReserved;
    private string pcRadioStatus = "";
    private Func<Task<WifiReport>> pcWifiReport = () => Task.Run(NativeWifi.Collect);
    private bool PcSessionOwned => pcInboundReserved || pcProtocol?.HasSession == true;
    private bool PcPairing => PcSessionOwned && !HostedPcConnected;
    private bool HostedPcConnected => pcProtocol?.Authenticated == true;
    private static bool IsNativePc(NearbyPhone peer) => peer.ControlService is Guid service && (PcServiceIdentity.Hint(service).Length == 16 || service == NearbyInvitation.ServiceUuid);
    private bool CanAcceptPc => discoveryEnabled && core is not null && !closing && !pcCloseInProgress && !storageChanging && !modeChanging && !browserSharing && !pairing && !sending && !hasActiveTransfers && !receivePromptActive && nearbySession is null && !PcSessionOwned && !PcHasTasks();
    private bool ReservePcPeer() { lock (pcAdmissionGate) { if (!CanAcceptPc) return false; pcInboundReserved = true; return true; } }
    private void ReleasePcPeer() { lock (pcAdmissionGate) pcInboundReserved = false; }
    private bool TryBeginNearbyConnection()
    {
        lock (pcAdmissionGate) {
            if (core is null || pairing || PcSessionOwned || nearbySession?.Authenticated == true || sending || closing || pcCloseInProgress || storageChanging || modeChanging || hasActiveTransfers || transferRows.Any(t => t.Active) || (!diagnostic && !discoveryEnabled)) return false;
            pairing = true; return true;
        }
    }
    private async Task InitializePcNativeAsync(Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider? sharedProvider = null)
    {
        if (pcDiscovery is not null || pcRadio is not null) return;
        pcStarting=null;pcCredentials=null;pcHotspotError="";pcOffer=null;
        pcRadioStatus = "";
        pcDisplayNames=PcDisplayNames.Read(stateDir);
        pcDiscovery=new(){CanReadName=()=>CanReadPcMetadata};
        foreach(var known in pcDisplayNames)pcDiscovery.RememberName(PcServiceIdentity.Hint(PcServiceIdentity.Create(known.Key)),known.Value);
        pcDiscovery.SetLocalFingerprint(Text(core!.Call(new{op="snapshot"}).GetProperty("info"),"fingerprint"));
        try { pcDiscovery.Start(); } catch (Exception ex) { pcRadioStatus="电脑扫描未开启："+ex.Message; pcDiscovery.Dispose(); pcDiscovery=null; }
        pcProtocol=new(core!,secureCommand:(request)=>Dispatcher.InvokeAsync(()=>PcCommandAsync(request)).Task.Unwrap(),ready:()=>CanAcceptPc,reservePeer:ReservePcPeer,releasePeer:ReleasePcPeer,clickToConnect:true);
        pcRadio=new(pcProtocol);
        try { await pcRadio.StartAsync(sharedProvider); }
        catch (Exception ex) {
            pcRadioStatus="电脑接收服务未开启："+ex.Message+"；仍可选择附近设备连接。";
            await pcRadio.DisposeAsync(); pcRadio=null; pcProtocol=null;
        }
        if (pcNativeTest) NetworkSummary.Text="本轮隔离电脑原生热点测试";
    }
    private bool PcHasTasks()=>core is not null&&core.Call(new{op="snapshot"}).GetProperty("transfers").EnumerateArray().Any(t=>Text(t,"state") is not ("completed" or "failed" or "cancelled" or "rejected"));
    private void PrunePcOffer()
    {
        if(pcOffer is null||core is null)return;
        string id=Text(pcOffer.Value,"id");
        if(!core.Call(new{op="snapshot"}).GetProperty("transfers").EnumerateArray().Any(t=>Text(t,"id")==id&&Text(t,"state") is not("completed" or "failed" or "cancelled" or "rejected")))pcOffer=null;
    }
    private async Task<JsonElement> PcCommandAsync(JsonElement request)
    {
        static JsonElement J(object o)=>JsonSerializer.SerializeToElement(o);
        PrunePcOffer();
        switch(Text(request,"op")){
            case "status": networkReport=await pcWifiReport();return J(new{ok=true,info=core!.Call(new{op="snapshot"}).GetProperty("info"),report=networkReport.Payload,capabilities=new{automaticHotspot=true,mergedReceiveApproval=true,hotspotRoleSelection=pcOffer is not null || (request.TryGetProperty("mergedReceiveApproval",out var merged)&&merged.ValueKind==JsonValueKind.True)},hotspotState=pcHotspotError.Length>0?"failed":pcCredentials is not null?"ready":pcStarting is not null?"starting":"idle",pullOffer=pcOffer});
            case "hotspot_join_start":case "hotspot_join_status":case "hotspot_join_stop":return await PcJoinCommandAsync(request);
            case "personal_hotspot":return J(new{ok=true,personalHotspotActive=false});
            case "hotspot_start":if(pcJoining is not null)return J(new{ok=false,error="当前正在加入对方热点"});if(pcHotspotError.Length>0)return J(new{ok=false,error=pcHotspotError});pcStarting??=StartPcAccessPointAsync();return J(new{ok=true,pending=pcCredentials is null,credentials=pcCredentials});
            case "hotspot_stop":await StopPcAccessPointAsync();return J(new{ok=true});
            case "pull_consumed":case "pull_reject":
                if(pcOffer is null||Text(pcOffer.Value,"id")!=Text(request,"id"))return J(new{ok=false,error="待发送任务已变化"});
                if(Text(request,"op")=="pull_reject")core!.Call(new{op="cancel",id=Text(pcOffer.Value,"id")});pcOffer=null;return J(new{ok=true});
            case "disconnect":
                if(PcHasTasks())return J(new{ok=false,error="请先完成或取消当前传输"});
                await StopPcJoinAsync();await StopPcAccessPointAsync();pcOffer=null;return J(new{ok=true});
            default:return J(new{ok=false,error="不支持的加密控制操作"});
        }
    }
    private async Task StartPcAccessPointAsync()
    {
        try{
            pcAccessPoint=new();await pcAccessPoint.StartAsync(pcLifetime.Token);var owner=await pcAccessPoint.WaitForAddressAsync(pcLifetime.Token);
            core!.Call(new{op="direct_bind",address=owner.ToString()});var info=core.Call(new{op="snapshot"}).GetProperty("info");
            Directory.CreateDirectory(Path.Combine(stateDir,"native-hotspot"));
            await pcAccessPoint.EnsureAccessAsync(owner,(int)Number(info,"port"),Path.Combine(stateDir,"native-hotspot"),()=>AppDialog.Show(this,"允许对方访问文件通道？","Windows需要一次管理员确认，仅允许临时热点内的文件连接。","允许访问","取消"),pcLifetime.Token);
            pcCredentials=JsonSerializer.SerializeToElement(new{ssid=pcAccessPoint.Ssid,password=pcAccessPoint.Password,securityType="wpa2-psk",addresses=new[]{owner.ToString()},port=(int)Number(info,"port"),fingerprint=Text(info,"fingerprint"),transport="wifi-direct",frequencyMhz=0});
            StatusMessage.Text="电脑临时热点已就绪，等待对方文件连接。";
        }catch(Exception ex){pcHotspotError=ex.Message;StatusMessage.Text="临时热点未就绪："+ex.Message;try{if(pcAccessPoint is not null)await pcAccessPoint.DisposeAsync();}catch{}}
    }
    private async Task StopPcAccessPointAsync()
    {
        if(pcStarting is not null)await pcStarting;
        var until=DateTime.UtcNow.AddSeconds(25);while(PcHasTasks()&&DateTime.UtcNow<until)await Task.Delay(100);
        if(PcHasTasks())throw new InvalidOperationException("文件仍在传输，保留热点等待任务结束");
        if(pcAccessPoint is not null)await pcAccessPoint.DisposeAsync();
        if(pcAccessPoint is not null)core?.Call(new{op="direct_bind",address=(string?)null});
        pcAccessPoint=null;pcStarting=null;pcCredentials=null;pcHotspotError="";
    }
    private async Task RefreshPcNativeAsync()
    {
        if(pcRefreshing||pcCloseInProgress||closing||core is null)return;pcRefreshing=true;
        try{
            if(HostedPcConnected)RememberPcDisplayName(pcProtocol!.Fingerprint,pcProtocol.Name);
            else if(nearbySession?.Authenticated==true)RememberPcDisplayName(nearbySession.Fingerprint,nearbySession.Name);
            await RefreshNetworkAsync();
            if(pcDiscovery is not null)_=pcDiscovery.RefreshNamesAsync();
            RefreshNearbyList();
            if (pcNativeTest || isolatedDesktopRoot is not null) File.WriteAllText(Path.Combine(stateDir,"pc-native-state-private.json"),JsonSerializer.Serialize(new{hosted=HostedPcConnected,client=nearbySession?.Authenticated==true,hostAdvertisement=pcRadio?.AdvertisementState,sharedAdvertisement=pcRadio?.SharedAdvertisement,phoneAdvertisement=nearbyInvitation?.AdvertisementState,phoneDiscovery=nearbyDiscovery?.Status,pcDiscovery=pcDiscovery?.State,pcDiscoveryDiagnostics=pcDiscovery?.Diagnostics,pcGattDiagnostics=pcRadio?.Diagnostics,pcHasSession=pcProtocol?.HasSession,pcInboundReserved,hotspotReady=pcCredentials is not null,hotspotError=pcHotspotError,receivePromptActive,networkPolling,polling,sending,temporaryNetwork,temporaryTransferId,networkRestoring=networkRestoration.Pending is not null,networkRecovery=wifi?.HasRecovery,pcUsingLocalHotspot,snapshot=core.Call(new{op="snapshot"}),at=DateTimeOffset.Now}));
        }catch(Exception ex){StatusMessage.Text="电脑连接状态更新失败："+ex.Message;}finally{pcRefreshing=false;}
    }
    private void RememberPcDisplayName(string fingerprint,string name)
    {
        if(!PcDisplayNames.Valid(fingerprint,name))return;
        pcDiscovery?.RememberName(PcServiceIdentity.Hint(PcServiceIdentity.Create(fingerprint)),name);
        if(pcDisplayNames.TryGetValue(fingerprint,out var old)&&old==name)return;
        pcDisplayNames[fingerprint]=name;
        if(pcDisplayNames.Count>64)pcDisplayNames.Remove(pcDisplayNames.Keys.First());
        try{PcDisplayNames.Save(stateDir,pcDisplayNames);}catch{/* Display cache failure cannot affect transfers. */}
    }
    private async Task<string> OfferHostedPcFilesAsync()
    {
        PrunePcOffer();
        if(core is null||pcProtocol?.Authenticated!=true||pcOffer is not null)throw new InvalidOperationException("请先连接电脑并结束已有待发送任务");
        sending=true;UpdateSelection();var selected=files.ToArray();
        try{
            var result=await Task.Run(()=>core.Call(new{op="prepare_pull",peerName=pcProtocol.Name,peerDeviceType="computer",files=selected.Select(f=>new{path=f.Path,name=f.Name})}));
            string id=Text(result,"id");if(id.Length==0||Text(result,"token").Length==0)throw new InvalidOperationException("未创建待发送任务");
            pcOffer=JsonSerializer.SerializeToElement(new{id,token=Text(result,"token"),files=selected.Select(f=>new{name=f.Name,size=f.Size})});
            var row=new TransferView(id);transferViews[id]=row;transferRows.Insert(0,row);foreach(var file in selected)files.Remove(file);
            BusinessActivity();StatusMessage.Text="已发送请求，等待对方确认接收。";await RefreshState();return id;
        }finally{sending=false;UpdateSelection();}
    }
    private async Task PausePcNativeAsync()
    {
        await StopPcJoinAsync();
        await StopPcAccessPointAsync();
        if(pcRadio is not null)await pcRadio.DisposeAsync();pcRadio=null;pcProtocol=null;
        pcDiscovery?.Dispose();pcDiscovery=null;ReleasePcPeer();
    }
    private async Task<bool> ClosePcNativeAsync()
    {
        if(pcCloseInProgress)return false;
        if(PcHasTasks()&&!AppDialog.Show(this,"退出 ShareLink？","还有文件任务。退出会取消这些任务并正常恢复本次临时网络。","取消任务并退出","继续传输"))return false;
        pcCloseInProgress=true;timer.Stop();
        try{
            var idleUntil=DateTime.UtcNow.AddSeconds(25);
            while(polling||networkPolling||pcRefreshing||pcDiscoveryChanging) { if(DateTime.UtcNow>=idleUntil)throw new InvalidOperationException("连接状态仍在更新，请稍后正常退出"); await Task.Delay(40); }
            if(core is not null){
                var snapshot=core.Call(new{op="snapshot"});
                // Durable intent must succeed before ordinary native cancellation begins.
                foreach(string id in ExitHistory.RememberActive(snapshot))core.Call(new{op="cancel",id});
            }
            var until=DateTime.UtcNow.AddSeconds(15);while(PcHasTasks()&&DateTime.UtcNow<until)await Task.Delay(100);if(PcHasTasks())throw new InvalidOperationException("任务取消尚未完成，请稍后正常结束");
            pcLifetime.Cancel();await PausePcNativeAsync();
            restoringForClose=true;
            try { await RestoreNetworkAsync(); } finally { restoringForClose=false; }
            if(wifi?.HasRecovery==true)throw new InvalidOperationException("原网络尚未恢复，请重试正常退出");
            await NotifyPcDisconnectForCloseAsync();
            DisableDiscovery();pcClosed=true;return true;
        }catch(Exception ex){StatusMessage.Text="退出清理尚未完成："+ex.Message;pcCloseInProgress=false;timer.Start();return false;}
    }
}
