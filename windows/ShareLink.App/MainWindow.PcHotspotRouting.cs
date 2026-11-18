using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
namespace ShareLink;
public partial class MainWindow
{
    private Task? pcJoining;
    private CancellationTokenSource? pcJoinLifetime;
    private JsonElement? pcJoinedEndpoint,pcJoinCredentials,pcJoinOffer;
    private string pcJoinError="",pcJoinPullId="",pcPreparedSendId="";
    private bool pcUsingLocalHotspot;
    private bool PcLinkBusy=>pcAccessPoint is not null||pcStarting is not null||pcJoining is not null||pcUsingLocalHotspot;
    private async Task<JsonElement> PcJoinCommandAsync(JsonElement request)
    {
        static JsonElement J(object o)=>JsonSerializer.SerializeToElement(o);
        switch(Text(request,"op")) {
            case "hotspot_join_start":
                if(pcProtocol?.Authenticated!=true||pcAccessPoint is not null||pcStarting is not null)throw new InvalidOperationException("当前连接不能切换热点角色。");
                var credentials=request.GetProperty("credentials");PcHotspotPolicy.Validate(credentials,pcProtocol.Fingerprint);
                JsonElement? offer=request.TryGetProperty("offer",out var incoming)&&incoming.ValueKind==JsonValueKind.Object?incoming.Clone():null;
                var approval=offer is JsonElement offered?PhoneReceiveApproval.Create(offered,pcProtocol.Fingerprint):null;
                // A receiver must know the authenticated manifest before leaving its Wi-Fi.
                // Legacy role changes remain available when this computer is the sender.
                if(approval is null&&pcOffer is null)throw new InvalidOperationException("接收文件清单尚未就绪，请重新发送。");
                if(pcJoining is null) {
                    if(receivePromptActive||closing)throw new InvalidOperationException("请先处理当前接收请求。");
                    pcJoinCredentials=credentials.Clone();pcJoinOffer=offer;pcJoinedEndpoint=null;pcJoinError="";pcJoinPullId="";pcJoinLifetime=new();
                    var protocol=pcProtocol;var lifetime=pcJoinLifetime;
                    // Return pending before showing a modal dialog, keeping encrypted cancellation responsive.
                    pcJoining=Dispatcher.InvokeAsync(()=>JoinPcHotspotAsync(credentials.Clone(),approval,protocol,lifetime),DispatcherPriority.Background).Task.Unwrap();
                } else if(pcJoinCredentials?.GetRawText()!=credentials.GetRawText() || (pcJoinOffer is JsonElement pending ? approval is null||!approval.MatchesOffer(pending,pcProtocol.Fingerprint) : approval is not null))throw new InvalidOperationException("已有另一个临时热点连接。");
                return J(new{ok=true,pending=pcJoinedEndpoint is null});
            case "hotspot_join_status":return J(new{ok=true,pending=pcJoining is not null&&pcJoinedEndpoint is null&&pcJoinError.Length==0,endpoint=pcJoinedEndpoint,error=pcJoinError});
            case "hotspot_join_stop":await StopPcJoinAsync();return J(new{ok=true});
            default:throw new InvalidOperationException("未知热点角色操作。");
        }
    }
    private async Task WatchPcJoinConsentAsync(PcControlProtocol protocol,CancellationTokenSource lifetime)
    {
        try {
            while(!lifetime.IsCancellationRequested) {
                await Task.Delay(500,lifetime.Token);
                if(closing||!ReferenceEquals(pcProtocol,protocol)||!protocol.Authenticated){lifetime.Cancel();return;}
            }
        } catch(OperationCanceledException) when(lifetime.IsCancellationRequested) {}
    }
    private async Task JoinPcHotspotAsync(JsonElement credentials,PhoneReceiveApproval? approval,PcControlProtocol protocol,CancellationTokenSource lifetime)
    {
        var token=lifetime.Token;
        if(approval is not null)receivePromptActive=true;
        Task watcher=Task.CompletedTask;
        try {
            var settings=PcHotspotPolicy.Validate(credentials,protocol.Fingerprint);
            token.ThrowIfCancellationRequested();
            if(approval is not null) {
                watcher=WatchPcJoinConsentAsync(protocol,lifetime);
                bool accepted=AppDialog.Show(this,"接收文件",approval.Message(protocol.Name,downloadDir,true),"接收","拒绝",cancellation:token);
                if(!accepted)throw new OperationCanceledException("已拒绝接收文件。");
            }
            token.ThrowIfCancellationRequested();
            if(closing||!ReferenceEquals(pcProtocol,protocol)||!protocol.Authenticated||approval is not null&&!approval.MatchesOffer(pcJoinOffer!.Value,protocol.Fingerprint))throw new OperationCanceledException("接收请求已结束或内容已变化。");
            if(wifi is null)throw new InvalidOperationException("Wi-Fi直连尚未初始化。");
            uint? wired=NativeWifi.WiredInternetInterface();
            var before=await pcWifiReport();
            if(approval is null&&!wired.HasValue&&before.Interfaces.Any(i=>i.Connected)&&!AppDialog.Show(this,"需要切换无线网络",TemporaryWifiNotice.Message,"继续连接","取消",cancellation:token))throw new OperationCanceledException("已保留原无线网络。");
            token.ThrowIfCancellationRequested();
            await wifi.JoinAsync(settings.Ssid,settings.Password,"wpa2-psk",token);
            var deadline=DateTime.UtcNow.AddSeconds(15);string? local=null;
            while(DateTime.UtcNow<deadline) {
                token.ThrowIfCancellationRequested();
                if(wired.HasValue&&NativeWifi.InternetInterface()!=wired.Value)throw new InvalidOperationException("临时Wi-Fi改变了原上网出口，正在恢复。");
                try {local=wifi.DirectAddress(settings.Owner);break;}catch{await Task.Delay(250,token);}
            }
            if(local is null)throw new TimeoutException("未取得临时热点内的文件地址。");
            token.ThrowIfCancellationRequested();core!.Call(new{op="direct_bind",address=local});
            var info=core.Call(new{op="snapshot"}).GetProperty("info");
            if(approval is null) {
                string accessDirectory=Path.Combine(stateDir,"native-hotspot-client");Directory.CreateDirectory(accessDirectory);
                await ShareLink.BrowserSharing.NearbyHotspot.EnsureClientAccessAsync(IPAddress.Parse(local),(int)Number(info,"port"),wifi.JoinedHotspot(settings.Ssid,settings.Owner),accessDirectory,()=>AppDialog.Show(this,"允许对方访问文件通道？","Windows需要一次管理员确认，仅允许本次临时热点内的文件连接。","允许访问","取消"),token);
            } else {
                token.ThrowIfCancellationRequested();
                if(!ReferenceEquals(pcProtocol,protocol)||!protocol.Authenticated)throw new OperationCanceledException("接收连接已结束。");
                var peer=new Peer(protocol.Fingerprint,protocol.Name,settings.Owner.ToString(),settings.Port,protocol.Fingerprint,true){DeviceType="computer"};
                var pull=await Task.Run(()=>core.Call(new{op="pull",peer=PeerPayload(peer),verificationCode=protocol.Fingerprint[..16],token=approval.Token}));
                pcJoinPullId=Text(pull,"id");
                if(pcJoinPullId.Length==0)throw new InvalidOperationException("未能创建接收任务。");
                approvedPhoneReceives.Add(pcJoinPullId,approval);
                BusinessActivity();
            }
            token.ThrowIfCancellationRequested();
            pcJoinedEndpoint=JsonSerializer.SerializeToElement(new{address=local,port=(int)Number(info,"port"),fingerprint=Text(info,"fingerprint"),pullStarted=approval is not null});
            StatusMessage.Text=approval is null?"已加入对方电脑临时热点，等待文件连接。":"已确认接收，正在核对文件信息。";
        } catch(Exception ex) {
            if(pcJoinPullId.Length>0){approvedPhoneReceives.Remove(pcJoinPullId);try{core?.Call(new{op="cancel",id=pcJoinPullId});}catch{}}
            pcJoinError=ex is OperationCanceledException?"临时热点连接已取消。":ex.Message;
            try {core?.Call(new{op="direct_bind",address=(string?)null});if(wifi?.HasRecovery==true)await wifi.RestoreAsync();}catch(Exception recovery){pcJoinError+=" 原网络恢复未完成："+recovery.Message;}
        } finally {
            if(approval is not null){lifetime.Cancel();await watcher;receivePromptActive=false;UpdateSelection();}
        }
    }
    private async Task StopPcJoinAsync()
    {
        pcJoinLifetime?.Cancel();if(pcJoining is not null)await pcJoining;
        var deadline=DateTime.UtcNow.AddSeconds(25);while(PcHasTasks()&&DateTime.UtcNow<deadline)await Task.Delay(100);
        if(PcHasTasks())throw new InvalidOperationException("文件任务尚未结束，暂不释放临时网络。");
        if(pcJoining is not null||wifi?.HasRecovery==true) {
            core?.Call(new{op="direct_bind",address=(string?)null});
            if(wifi?.HasRecovery==true)await wifi.RestoreAsync();
        }
        pcJoinLifetime?.Dispose();pcJoinLifetime=null;pcJoining=null;pcJoinCredentials=null;pcJoinOffer=null;pcJoinedEndpoint=null;pcJoinError="";pcJoinPullId="";
    }
    private bool PreparedPcSendActive()=>pcPreparedSendId.Length>0&&core!.Call(new{op="snapshot"}).GetProperty("transfers").EnumerateArray().Any(t=>Text(t,"id")==pcPreparedSendId&&Text(t,"state") is not("completed" or "failed" or "cancelled" or "rejected"));
    private void CancelPreparedPcSend()
    {
        if(pcPreparedSendId.Length>0)try{core?.Call(new{op="cancel",id=pcPreparedSendId});}catch{}
    }
    private async Task<Peer?> TryPcHotspotRoleAsync(NearbySession session,SelectedFile[]? outgoingFiles=null)
    {
        if(connectedPhone is null||!IsNativePc(connectedPhone))return null;
        var status=await SecureAsync(new{op="status",mergedReceiveApproval=true});
        if(!status.TryGetProperty("capabilities",out var capabilities)||!capabilities.TryGetProperty("hotspotRoleSelection",out var support)||support.ValueKind!=JsonValueKind.True)return null;
        // An older receiver cannot approve a manifest before its join operation.
        if(outgoingFiles is not null&&(!capabilities.TryGetProperty("mergedReceiveApproval",out var merged)||merged.ValueKind!=JsonValueKind.True))return null;
        var local=await Task.Run(NativeWifi.Collect);networkReport=local;
        if(!status.TryGetProperty("report",out var remote)||!PcHotspotPolicy.PreferLocal(JsonSerializer.SerializeToElement(local.Payload),remote))return null;
        pcUsingLocalHotspot=true;temporaryNetwork=true;RestoreNetworkButton.Visibility=Visibility.Visible;
        StatusMessage.Text="优先由已连接5GHz Wi-Fi的本机开启临时热点…";
        pcStarting??=StartPcAccessPointAsync();await pcStarting;
        if(pcCredentials is null)throw new InvalidOperationException(pcHotspotError.Length>0?pcHotspotError:"本机临时热点未就绪。");
        JsonElement? offer=null;
        if(outgoingFiles is not null) {
            var prepared=await Task.Run(()=>core!.Call(new{op="prepare_pull",peerName=session.Name,peerDeviceType="computer",files=outgoingFiles.Select(f=>new{path=f.Path,name=f.Name})}));
            pcPreparedSendId=Text(prepared,"id");
            offer=JsonSerializer.SerializeToElement(new{id=pcPreparedSendId,token=Text(prepared,"token"),files=outgoingFiles.Select(f=>new{name=f.Name,size=f.Size})});
            PhoneReceiveApproval.Create(offer.Value,Text(core!.Call(new{op="snapshot"}).GetProperty("info"),"fingerprint"));
            var row=new TransferView(pcPreparedSendId);transferViews[pcPreparedSendId]=row;transferRows.Insert(0,row);EmptyTransfers.Visibility=Visibility.Collapsed;
            await RefreshState();
        }
        try {
            await SecureAsync(new{op="hotspot_join_start",credentials=pcCredentials,offer});
            var deadline=DateTime.UtcNow.AddSeconds(65);
            while(DateTime.UtcNow<deadline) {
                if(pcPreparedSendId.Length>0&&!PreparedPcSendActive()) {
                    var completed=core!.Call(new{op="snapshot"}).GetProperty("transfers").EnumerateArray().Any(t=>Text(t,"id")==pcPreparedSendId&&Text(t,"state")=="completed");
                    if(!completed)throw new OperationCanceledException("发送请求已结束。");
                }
                var result=await SecureAsync(new{op="hotspot_join_status"});
                string error=Text(result,"error");if(error.Length>0)throw new InvalidOperationException(error);
                if(result.TryGetProperty("endpoint",out var endpoint)&&endpoint.ValueKind==JsonValueKind.Object) {
                    if(!Text(endpoint,"fingerprint").Equals(session.Fingerprint,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("对方文件服务身份与加密连接不一致。");
                    string host=Text(endpoint,"address");int port=(int)Number(endpoint,"port");
                    if(!IPAddress.TryParse(host,out var address)||address.AddressFamily!=System.Net.Sockets.AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||port is <1 or >65535)throw new InvalidOperationException("对方文件地址无效。");
                    if(pcPreparedSendId.Length>0) {
                        if(!endpoint.TryGetProperty("pullStarted",out var started)||started.ValueKind!=JsonValueKind.True)throw new InvalidOperationException("对方尚未确认接收文件。");
                        // The outbound pull is pinned to our certificate and one native offer token.
                        // Do not start a duplicate push or probe a channel that may already be finished.
                        selectedPeer=new Peer(session.Fingerprint,session.Name,host,port,session.Fingerprint,true){DeviceType="computer"};
                    } else {
                        var probe=await Task.Run(()=>core!.Call(new{op="probe",host,port}));var peer=probe.GetProperty("peer");
                        if(!Text(peer,"fingerprint").Equals(session.Fingerprint,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("对方文件通道证书不一致。");
                        selectedPeer=new Peer(Text(peer,"id"),session.Name,host,port,session.Fingerprint,true){DeviceType="computer"};
                    }
                    StatusMessage.Text="本机临时热点已连接，文件通道已核验。";return selectedPeer;
                }
                await Task.Delay(500);
            }
            throw new TimeoutException("对方未能及时加入本机临时热点。");
        } catch {
            CancelPreparedPcSend();
            try{await SecureAsync(new{op="hotspot_join_stop"});}catch{}
            throw;
        }
    }
    private async Task StopLocalPcClientHotspotAsync()
    {
        if(!pcUsingLocalHotspot)return;
        if(nearbySession?.Authenticated==true)await SecureAsync(new{op="hotspot_join_stop"});
        await StopPcAccessPointAsync();pcUsingLocalHotspot=false;
    }
}
