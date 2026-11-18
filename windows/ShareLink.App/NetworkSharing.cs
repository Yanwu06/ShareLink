using System.IO;
using System.Net;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ShareLink;

public partial class MainWindow
{
    private NativeWifi? wifi;
    private NearbyBluetooth? nearbyDiscovery;
    private NearbyInvitation? nearbyInvitation;
    private NearbySession? nearbySession;
    private WifiReport networkReport = new(0, [], false, null);
    private JsonElement phoneInfo, phoneReport;
    private bool selectedIsNearby, networkPolling, temporaryNetwork, restoringForClose, closeAfterRestore;
    private volatile bool pairing;
    private string temporaryTransferId = "";
    private readonly HashSet<string> pulledOffers = [];
    private NearbyPhone? connectedPhone;
    private readonly Dictionary<ulong, (string Fingerprint, string Name, string DeviceType)> nearbyIdentities = [];
    private readonly Dictionary<string, (double Rate, long At)> directSamples = [];
    private long networkUpdated, phoneUpdated;
    private readonly SemaphoreSlim controlGate = new(1, 1);

    private async Task InitializeNetworkAsync()
    {
        wifi = new(stateDir);
        var settings = Path.Combine(stateDir, "network-preference.json");
        try { if (File.Exists(settings)) KeepNetwork.IsChecked = JsonDocument.Parse(File.ReadAllText(settings)).RootElement.GetProperty("keepCurrentNetwork").GetBoolean(); } catch { }
        if (wifi.HasRecovery)
        {
            NetworkDecision.Text = "正在恢复上次未完成会话的原网络…";
            try { await Task.Run(() => wifi.RestoreAsync()); NetworkDecision.Text = "已恢复原网络并清理临时配置。"; }
            catch (Exception ex) { NetworkDecision.Text = "原网络恢复未完成：" + ex.Message; RestoreNetworkButton.Visibility = Visibility.Visible; }
        }
        if (modules.Direct is null) return;
        await EnableDiscoveryAsync();
        await RefreshNetworkAsync();
    }

    private void NetworkPreference_Changed(object sender, RoutedEventArgs e)
    {
        if (diagnostic || KeepNetwork is null || !Directory.Exists(stateDir)) return;
        try { File.WriteAllText(Path.Combine(stateDir, "network-preference.json"), JsonSerializer.Serialize(new { keepCurrentNetwork = KeepNetwork.IsChecked == true })); } catch { }
    }

    private void RefreshNearbyList()
    {
        var phones = (nearbyDiscovery?.Snapshot() ?? []).Concat(pcDiscovery?.Snapshot() ?? []).ToList();
        if (nearbySession?.Authenticated != true && !HostedPcConnected) {
            connectedPhone = null;
            if (selectedIsNearby) { selectedIsNearby = false; selectedPeer = null; UpdateSelection(); }
        }
        if (HostedPcConnected) {
            phones.Clear(); phones.Add(new NearbyPhone(0,pcProtocol!.Name,0,0){Connected=true,DeviceType="computer"}); selectedIsNearby=true;
        }
        if (connectedPhone is not null && nearbySession?.Authenticated == true) {
            phones.Clear(); // Keep other scan candidates in the radio cache, outside the connected view.
            phones.Insert(0, connectedPhone with { Connected = true, Name = nearbySession.Name });
        }
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < phones.Count; i++)
        {
            var phone = phones[i];
            if (nearbyIdentities.TryGetValue(phone.Address, out var identity) && (phone.FingerprintPrefix.Length == 0 || identity.Fingerprint.StartsWith(phone.FingerprintPrefix, StringComparison.OrdinalIgnoreCase)))
            {
                phones[i] = phone with { Name = identity.Name, DeviceType = identity.DeviceType };
                hidden.Add(identity.Fingerprint);
                continue;
            }
            // Broadcast hints merge presentation only. Connections still probe
            // the full certificate and pin it to the encrypted BLE session.
            var match = ConnectionPolicy.UniquePresentationPeer(phone, knownPeers.Values);
            if (match is not null) { phones[i] = phone with { Name = match.Name, DeviceType = match.DeviceType }; hidden.Add(match.Fingerprint); }
        }
        Peer[] peers = modules.LanEnabled && nearbySession?.Authenticated != true ? knownPeers.Values.Where(p => !hidden.Contains(p.Fingerprint)).OrderBy(p => p.Name).ToArray() : [];
        // Preserve the same name controls while periodic snapshots are unchanged.
        if (PeerList.ItemsSource is not Peer[] previousPeers || !previousPeers.SequenceEqual(peers)) PeerList.ItemsSource = peers;
        string RowIdentity(NearbyPhone p) => (IsNativePc(p) ? "pc:" : "mobile:") + (p.FingerprintPrefix.Length > 0 ? p.FingerprintPrefix : p.Address.ToString());
        phones = phones.OrderBy(RowIdentity, StringComparer.OrdinalIgnoreCase).ToList();
        if (NearbyList.ItemsSource is not NearbyPhone[] previousPhones ||
            !previousPhones.Select(p => (Identity: RowIdentity(p), p.Display, p.Connected, p.DeviceType)).SequenceEqual(phones.Select(p => (Identity: RowIdentity(p), p.Display, p.Connected, p.DeviceType))))
            NearbyList.ItemsSource = phones.ToArray();
        NoPeers.Visibility = (!modules.LanEnabled || knownPeers.Count == 0) && phones.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoPeers.Text = "没有发现附近设备。\n\n请在手机开启 ShareLink 与蓝牙。\n\n连接后会自动建立临时 Wi-Fi。";
        BluetoothStatus.Text = (nearbyDiscovery?.Status ?? "蓝牙发现不可用") + "\n" + (nearbyInvitation?.Status ?? "") + "\n" + pcRadioStatus + "\n" + (pcRadio?.Status ?? "");
        UpdatePresentation();
    }

    private async Task RefreshNetworkAsync()
    {
        if (networkPolling || pairing || closing || storageChanging || (diagnostic && !pcNativeTest) || core is null) return;
        networkPolling = true;
        try
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - networkUpdated >= 5000)
            {
                networkReport = await Task.Run(NativeWifi.Collect);
                networkUpdated = now;
                NetworkSummary.Text = temporaryNetwork ? networkReport.Display + " · 本次临时直连\n协商 Mbps 不是文件速度。"
                    : "等待建立临时 Wi-Fi · 请通过蓝牙选择附近设备";
                await Task.Run(() => core.Call(new { op = "report_link", report = networkReport.Payload }));
            }
            if (nearbySession?.Authenticated == true && !sending && now - phoneUpdated >= 2500)
            {
                phoneUpdated = now;
                var status = await SecureAsync(new { op = "status", report = networkReport.Payload });
                nearbySession.AutomaticHotspot = ConnectionPolicy.SupportsAutomaticHotspot(status);
                if (status.TryGetProperty("info", out var updatedInfo))
                {
                    if (!string.Equals(Text(updatedInfo, "fingerprint"), nearbySession.Fingerprint, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("手机身份在当前会话中发生变化。");
                    phoneInfo = updatedInfo;
                }
                if (status.TryGetProperty("report", out var report)) phoneReport = report;
                if (status.TryGetProperty("pullOffer", out var offer) && offer.ValueKind == JsonValueKind.Object)
                    await ReceivePhoneOfferAsync(offer);
            }
        }
        catch (Exception ex)
        {
            BluetoothStatus.Text = "附近控制连接不可用：" + ex.Message;
            if (nearbySession?.Authenticated != true) selectedIsNearby = false;
        }
        finally
        {
            // Release a finished direct link even when the BLE status poll failed.
            if (temporaryTransferId.Length > 0 && transferViews.TryGetValue(temporaryTransferId, out var transfer) && !transfer.Active && !transferRows.Any(t => t.Active))
            {
                temporaryTransferId = "";
                try { await RestoreNetworkAsync(); }
                catch (Exception ex) { NetworkDecision.Text = "直连清理未完成：" + ex.Message; }
            }
            await ReleaseEndedNearbySessionAsync();
            networkPolling = false; UpdateSelection();
        }
    }

    private async void Nearby_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not NearbyPhone phone) return;
        if (phone.Connected && HostedPcConnected) { if (pairing || sending || hasActiveTransfers || PcHasTasks()) { ShowToast("请先完成或取消当前传输。"); return; } await CloseDiscoveryAsync(); await EnableDiscoveryAsync(); ShowToast("已断开连接，可重新选择设备。"); return; }
        if (phone.Connected) await DisconnectNearbyAsync();
        else {
            // Keep presentation stable while resolving the current rotating BLE endpoint at click time.
            if (IsNativePc(phone)) {
                var current = pcDiscovery?.Resolve(phone);
                if (current is null) { RefreshNearbyList(); ShowToast("该设备已离开，请等待重新发现。"); return; }
                phone = current;
            }
            await ConnectNearbyAsync(phone);
        }
    }

    private async Task DisconnectNearbyAsync()
    {
        if (core is null || pairing || closing || storageChanging) return;
        if (sending || transferRows.Any(t => t.Active)) {
            ShowToast("请先完成或取消当前传输，再断开连接。"); return;
        }
        pairing = true; UpdateSelection();
        try {
            // Let an in-flight status request release its session before disposing it.
            while (networkPolling) await Task.Delay(40);
            if (sending || transferRows.Any(t => t.Active) || core.Call(new { op = "snapshot" }).GetProperty("transfers").EnumerateArray()
                .Any(t => Text(t, "state") is not ("completed" or "cancelled" or "rejected" or "failed"))) {
                ShowToast("请先完成或取消当前传输，再断开连接。"); return;
            }
            await RestoreNetworkAsync();
            if (nearbySession?.Authenticated == true)
            {
                // Explicitly release the phone's logical session: Windows may retain
                // the physical Bluetooth link after our GATT objects are disposed.
                try { await SecureAsync(new { op = "disconnect" }); }
                catch (InvalidOperationException ex) when (ex.Message == "不支持的加密控制操作") { /* Older phones rely on link disposal. */ }
                catch (Exception) when (nearbySession?.Authenticated != true) { /* A lost control link still needs local cleanup. */ }
            }
            var session = nearbySession;
            nearbySession = null; connectedPhone = null; selectedIsNearby = false; selectedPeer = null;
            session?.Dispose();
            if (session is { PairId.Length: > 0 })
                await Task.Run(() => core.Call(new { op = "pair_close", pairId = session.PairId }));
            BusinessActivity();
            StatusMessage.Text = "已断开连接，可选择其他附近设备。";
        }
        catch (Exception ex) { ShowError("断开连接未完成：" + ex.Message); }
        finally { pairing = false; RefreshNearbyList(); UpdateSelection(); }
    }

    private async Task ConnectNearbyAsync(NearbyPhone phone, bool invitation = false)
    {
        if (core is null || !TryBeginNearbyConnection()) return;
        BusinessActivity();
        if (nearbySession?.Authenticated == true || HostedPcConnected) return;
        using var timing = StageTiming.Measure(stateDir, "ble_handshake");
        UpdateSelection();
        try
        {
            await RestoreNetworkAsync();
            if (nearbySession is { PairId.Length: > 0 } old) try { await Task.Run(() => core.Call(new { op = "pair_close", pairId = old.PairId })); } catch { }
            nearbySession?.Dispose(); nearbySession = null;
            selectedPeer = null; selectedIsNearby = false;
            StatusMessage.Text = "正在连接附近设备…";
            if (pcDiscovery is not null) await pcDiscovery.WaitForMetadataIdleAsync(TimeSpan.FromSeconds(30));
            if(IsNativePc(phone)) phone=pcDiscovery?.Resolve(phone) ?? throw new InvalidOperationException("该电脑的当前广播已失效，请等待重新发现后连接。");
            if (invitation) await Task.Delay(1200); // Allow a single-ACL phone's invitation link to close first.
            var session = await NearbySession.ConnectAsync(phone.Address, phone.AddressType, phone.ControlService);
            nearbySession = session;
            var hint = await session.RequestAsync(new { op = "hello" });
            if (Number(hint, "controlMaxWrite") >= 20) session.ControlWriteLimit = (int)Math.Clamp(Number(hint, "controlMaxWrite"), 20, 512);
            phoneInfo = hint.GetProperty("info");
            if (hint.TryGetProperty("report", out var report)) phoneReport = report;
            var begun = await Task.Run(() => core.Call(new { op = "pair_begin" }));
            session.PairId = Text(begun, "pairId");
            var remote = await session.RequestAsync(new { op = "pair_begin", hello = begun.GetProperty("hello") });
            var finished = await Task.Run(() => core.Call(new { op = "pair_finish", pairId = session.PairId, hello = remote.GetProperty("hello") }));
            var boundPeer = finished.GetProperty("peer");
            session.Fingerprint = Text(boundPeer, "fingerprint"); session.Name = Text(boundPeer, "name");
            if (IsNativePc(phone) && !session.Fingerprint.StartsWith(phone.FingerprintPrefix,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("电脑广播与加密连接身份不一致");
            bool needsUserCode = !IsNativePc(phone) && remote.TryGetProperty("requiresPairingConfirmation", out var required) && required.ValueKind == JsonValueKind.True;
            bool userApproved = !needsUserCode || AppDialog.Show(this, "核对连接校验码", "对方设备：" + session.Name + "\n\n校验码：" + Text(finished, "sas") + "\n\n请核对两台电脑显示相同校验码；不一致请取消。", "相同，允许连接", "取消");
            if (!userApproved) throw new InvalidOperationException("已取消校验码确认，未建立连接。");
            await Task.Run(() => core.Call(new { op = "pair_confirm", pairId = session.PairId, confirmed = userApproved }));
            StatusMessage.Text = "正在建立加密连接…";
            var deadline = DateTime.UtcNow.AddSeconds(90);
            bool confirmed = false;
            while (DateTime.UtcNow < deadline && !closing)
            {
                var confirmation = await session.RequestAsync(new { op = "pair_confirm", confirmed = true });
                if (confirmation.TryGetProperty("confirmed", out var answer) && answer.ValueKind == JsonValueKind.True) { confirmed = true; break; }
                await Task.Delay(700);
            }
            if (!confirmed) throw new TimeoutException("手机连接未完成，请重新连接。");
            session.Authenticated = true;
            var authenticatedStatus = await SecureAsync(new { op = "status", report = networkReport.Payload });
            if (!authenticatedStatus.TryGetProperty("info", out phoneInfo) || !string.Equals(Text(phoneInfo, "fingerprint"), session.Fingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("手机数据服务身份与当前会话绑定的身份不一致。");
            if (authenticatedStatus.TryGetProperty("report", out report)) phoneReport = report;
            session.AutomaticHotspot = ConnectionPolicy.SupportsAutomaticHotspot(authenticatedStatus);
            // Endpoints are hints only. Probe never replaces the session-bound TLS pin.
            selectedPeer = null;
            selectedIsNearby = true;
            connectedPhone = phone with { DeviceType = Text(phoneInfo, "deviceType") };
            nearbyIdentities[phone.Address] = (session.Fingerprint, session.Name, connectedPhone.DeviceType);
            if(IsNativePc(phone))RememberPcDisplayName(session.Fingerprint,session.Name);
            RefreshNearbyList();
            
            StatusMessage.Text = selectedPeer is null
                ? session.AutomaticHotspot ? "BLE 加密连接已就绪，发送时自动建立 Wi-Fi 直连。" : "BLE 已连接，但对方尚不能建立临时 Wi-Fi；局域网模块未启用。"
                : "已建立加密连接，可发送文件。";
            BluetoothStatus.Text = "已连接附近设备：" + session.Name;
        }
        catch (Exception ex)
        {
            if (nearbySession?.PairId.Length > 0) try { await Task.Run(() => core.Call(new { op = "pair_close", pairId = nearbySession.PairId })); } catch { }
            nearbySession?.Dispose(); nearbySession = null; selectedIsNearby = false;
            var detail = string.IsNullOrWhiteSpace(ex.Message) ? $"{ex.GetType().Name}（0x{ex.HResult:X8}）" : ex.Message;
            try { File.AppendAllText(Path.Combine(stateDir, "nearby-errors.log"), $"{DateTimeOffset.Now:O} · {phone.Address:X12} · {phone.AddressType}\n{ex}\n"); } catch { }
            var controlTrace=ex.Data.Keys.Cast<object>().Where(k=>k is string text && (text.StartsWith("control",StringComparison.Ordinal)||text is "gattTrace" or "gattStep")).ToDictionary(k=>k.ToString()!,k=>ex.Data[k]);
            try { if(controlTrace.Count>0)File.AppendAllText(Path.Combine(stateDir,"nearby-errors.log"),JsonSerializer.Serialize(controlTrace)+"\n"); } catch { }
            ShowError("附近连接失败：" + detail + (IsNativePc(phone) ? "\n请在对方电脑断开本次连接，再重新选择设备连接。" : "\n请在手机重试附近分享，再刷新电脑设备列表。"));
        }
        finally { pairing = false; UpdateSelection(); }
    }

    private async Task<JsonElement> SecureAsync(object payload)
    {
        await controlGate.WaitAsync();
        try
        {
            var session = nearbySession ?? throw new InvalidOperationException("请先连接附近手机。");
            if (!session.Authenticated || core is null) throw new InvalidOperationException("附近设备尚未建立加密连接。");
            var typedPayload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(payload))!;
            typedPayload["deviceType"] = JsonSerializer.SerializeToElement("computer");
            var encrypted = await Task.Run(() => core.Call(new { op = "pair_encrypt", pairId = session.PairId, payload = typedPayload }));
            var response = await session.RequestAsync(new { op = "secure", envelope = encrypted.GetProperty("envelope") }, 20);
            var decrypted = await Task.Run(() => core.Call(new { op = "pair_decrypt", pairId = session.PairId, envelope = response.GetProperty("envelope") }));
            var content = decrypted.GetProperty("payload");
            if (content.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False) throw new InvalidOperationException(Text(content, "error"));
            return content;
        }
        finally { controlGate.Release(); }
    }

    private async Task<Peer?> FindPhonePeerAsync(JsonElement endpointInfo, NearbySession session)
    {
        if (core is null || endpointInfo.ValueKind != JsonValueKind.Object || !endpointInfo.TryGetProperty("addresses", out var endpoints) || endpoints.ValueKind != JsonValueKind.Array) return null;
        int port = (int)Number(endpointInfo, "port"); if (port is < 1 or > 65535) port = 53317;
        var hints = endpoints.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!);
        var discovered = knownPeers.Values.Where(p => string.Equals(p.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase)).Select(p => p.Host);
        var verified = selectedPeer is not null && string.Equals(selectedPeer.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase) ? new[] { selectedPeer.Host } : [];
        var hosts = ConnectionPolicy.OrderEndpoints(verified.Concat(discovered).Concat(hints), verified.Concat(discovered));
        foreach (string host in hosts)
        {
            try
            {
                var probe = await Task.Run(() => core.Call(new { op = "probe", host, port }));
                var p = ReadPeer(probe.GetProperty("peer"));
                if (!string.Equals(p.Fingerprint, session.Fingerprint, StringComparison.OrdinalIgnoreCase)) continue;
                return p with { Manual = true, Name = session.Name, Fingerprint = session.Fingerprint };
            }
            catch { }
        }
        return null;
    }

    private async Task<(double? Rate, string Confidence)> MeasureAsync(Peer peer, string code, string direction)
    {
        try
        {
            NetworkDecision.Text = direction == "download" ? "正在测量手机到电脑的加密链路…" : "正在测量电脑到接收端的加密链路…";
            var began = DateTime.UtcNow;
            var result = await Task.Run(() => core!.Call(new { op = "measure", peer = PeerPayload(peer), verificationCode = code, direction }));
            double rate = Number(result, "bytesPerSecond"), measuredBytes = Number(result, "bytes"), sampleMs = Number(result, "durationMs");
            string confidence = Text(result, "confidence");
            var rates = new List<double> { rate };
            // A fast path can exhaust 16 MiB before 250 ms. Repeat only complete
            // byte-limited samples, bounded to three samples / 48 MiB. A tiny
            // sample never becomes a claim of sustained file throughput.
            for (int repeat = 0; repeat < 2 && confidence == "low" && measuredBytes >= 16 * 1048576 && (DateTime.UtcNow - began).TotalSeconds < 1.5; repeat++)
            {
                var next = await Task.Run(() => core!.Call(new { op = "measure", peer = PeerPayload(peer), verificationCode = code, direction }));
                rates.Add(Number(next, "bytesPerSecond")); measuredBytes += Number(next, "bytes"); sampleMs += Number(next, "durationMs");
            }
            if (rates.Count == 3 && rates.Min() > 0 && rates.Max() <= rates.Min() * 1.2 && sampleMs >= 250) confidence = "medium";
            rate = rates.Min();
            NetworkDecision.Text = $"本次链路实测 {rate / 1_000_000:F1} MB/s · {measuredBytes / 1048576:F1} MiB / {rates.Count} 次 · {(confidence == "low" ? "短样本，保留低置信度" : "有限样本，中等置信度")} · 不含文件落盘";
            return (rate > 0 ? rate : null, confidence);
        }
        catch (Exception ex) { NetworkDecision.Text = "链路测速未完成：" + ex.Message + "；保留未知，不使用协商速率冒充吞吐。"; return (null, "unknown"); }
    }

    private readonly NetworkRestoration networkRestoration = new();
    private async Task<Peer?> ReusableNearbyPeerAsync(NearbySession session)
    {
        if(!temporaryNetwork||wifi is null||core is null)return null;
        return await NearbyLinkReuse.FindAsync(selectedPeer,session.Fingerprint,peer=>{
            var addresses=core.Call(new{op="snapshot"}).GetProperty("info").GetProperty("addresses").EnumerateArray().Select(a=>a.GetString()).ToArray();
            if(pcUsingLocalHotspot) {
                if(pcAccessPoint is null||pcCredentials is not JsonElement credentials)return false;
                var own=PcHotspotPolicy.Validate(credentials,Text(core.Call(new{op="snapshot"}).GetProperty("info"),"fingerprint"));
                return addresses.Contains(own.Owner.ToString());
            }
            if(reusedPersonalHotspot)return NativeWifi.ExistingHotspotCandidates().Any(link=>link.Owner.ToString()==peer.Host&&addresses.Contains(link.Local));
            string local=wifi.DirectAddress(IPAddress.Parse(peer.Host));
            return addresses.Contains(local);
        },async peer=>{
            var result=await Task.Run(()=>core.Call(new{op="probe",host=peer.Host,port=peer.Port}));
            return Text(result.GetProperty("peer"),"fingerprint");
        });
    }
    private async Task<Peer> ChoosePathAsync(Peer? current, string code, long bytes, string direction, bool networkChangeApproved = false, SelectedFile[]? outgoingFiles = null)
    {
        if (selectedIsNearby)
        {
            var session = nearbySession?.Authenticated == true ? nearbySession : null;
            if (session is null || modules.Direct is null) throw new InvalidOperationException("请先通过 BLE 连接附近设备。");
            await networkRestoration.WaitAsync();
            if(closing||pcCloseInProgress)throw new OperationCanceledException("接收请求已结束。");
            // A completed previous task must not tear down a new route while it is opening.
            if(!transferRows.Any(t=>t.Active)&&!PcHasTasks())temporaryTransferId="";
            var reusable=await ReusableNearbyPeerAsync(session);
            if(reusable is not null)return reusable;
            if (transferRows.Any(t => t.Active)||PcHasTasks()) throw new InvalidOperationException("请先完成当前传输后再建立新的直连。");
            if(temporaryNetwork||wifi?.HasRecovery==true||pcUsingLocalHotspot)await RestoreNetworkAsync();
            selectedPeer=null;
            var preferredPc = await TryPcHotspotRoleAsync(session,outgoingFiles);
            if (preferredPc is not null) return preferredPc;
            var existing = await TryPersonalHotspotAsync(session);
            if (existing is not null) return existing;
            var report = NativeWifi.Collect();
            if (!networkChangeApproved && !diagnostic && !NativeWifi.WiredInternetInterface().HasValue && report.Interfaces.Length > 0 && report.Interfaces.All(i => i.Connected))
                if (!AppDialog.Show(this, "需要切换无线网络", TemporaryWifiNotice.Message, "继续连接", "取消"))
                    throw new OperationCanceledException("已保留当前网络和待发送文件。");
            return await StartPhoneHotspotAsync(session);
        }
        if (!modules.LanEnabled || current is null) throw new InvalidOperationException("局域网模块未加载，或未选择局域网设备。");
        NetworkDecision.Text = "局域网传输模块 · 使用所选网络设备";
        return current;
    }

    private bool reusedPersonalHotspot;
    private async Task<Peer?> TryPersonalHotspotAsync(NearbySession session)
    {
        var response = await ConnectionPolicy.QueryPersonalHotspotAsync(() => SecureAsync(new { op = "personal_hotspot" }));
        if (response is null) return null; // Only an explicitly unsupported operation is a legacy peer.
        var state = response.Value;
        if (!state.TryGetProperty("personalHotspotActive", out var active) || active.ValueKind != JsonValueKind.True) return null;
        int candidateIndex = 0;
        foreach (var candidate in NativeWifi.ExistingHotspotCandidates())
        {
            candidateIndex++;
            string stage = "phone-bind";
            try {
                var remote = await SecureAsync(new { op = "personal_hotspot", address = candidate.Owner.ToString() });
                if (!remote.TryGetProperty("info", out var info)) continue;
                if (!Text(info,"fingerprint").Equals(session.Fingerprint,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("热点身份与蓝牙会话不一致。");
                stage = "computer-bind";
                await Task.Run(() => core!.Call(new { op = "direct_bind", address = candidate.Local }));
                stage = "pinned-file-service";
                var result = await Task.Run(() => core!.Call(new { op = "probe", host = candidate.Owner.ToString(), port = (int)Number(info,"port") }));
                var peer = result.GetProperty("peer");
                if (!Text(peer,"fingerprint").Equals(session.Fingerprint,StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("热点文件服务证书不一致。");
                var selected = new Peer(Text(peer,"id"),session.Name,candidate.Owner.ToString(),(int)Number(info,"port"),session.Fingerprint,true);
                reusedPersonalHotspot = true; temporaryNetwork = true; selectedPeer = selected;
                modules.Direct!.Evidence = new { transport = "personal-hotspot", existingNetworkReused = true, wlanJoinPerformed = false };
                NetworkDecision.Text = "已核验手机个人热点 · 使用当前连接，不切换网络";
                return selected;
            } catch (Exception error) {
                // Local diagnosis only: no hotspot password, address or peer identity is recorded.
                try { File.AppendAllText(Path.Combine(stateDir, "personal-hotspot-errors-private.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, candidateIndex, stage, error = error.Message, type = error.GetType().Name }) + Environment.NewLine); } catch { }
                await Task.Run(() => core!.Call(new { op = "direct_bind", address = (string?)null }));
            }
        }
        throw new InvalidOperationException("手机个人热点已开启，请先让电脑连接本手机热点并重试；若无可用设备，请手动关闭热点后使用普通设备互传连接。");
    }
    private async Task<Peer> StartPhoneHotspotAsync(NearbySession session)
    {
        if (wifi is null || modules.Direct is null) throw new InvalidOperationException("Wi-Fi 直连模块未启动。");
        temporaryNetwork = true;
        RestoreNetworkButton.Visibility = Visibility.Visible;
        var peer = await modules.Direct.OpenAsync(core!, wifi, session, SecureAsync, text => NetworkDecision.Text = text, stateDir);
        selectedPeer = peer;
        networkReport = await Task.Run(NativeWifi.Collect);
        NetworkSummary.Text = networkReport.Display + " · 本次临时直连";
        return peer;
    }

    private void RememberDirect(NearbySession session, string direction, (double? Rate, string Confidence) sample)
    {
        if (sample.Rate is null || sample.Confidence == "low" || networkReport.Connected is not { } link || link.Standard == "unknown" || link.Band == "unknown") return;
        string key = $"{session.Fingerprint}|{direction}|{link.Standard}|{link.Band}";
        directSamples[key] = (sample.Rate.Value, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    private double? FindDirectHistory(NearbySession session, string direction)
    {
        // AP configuration is unknown until created. Do not apply a previous
        // 5 GHz sample to a possibly 2.4 GHz hotspot or infer AP from phone STA.
        if (phoneReport.ValueKind != JsonValueKind.Object || !phoneReport.TryGetProperty("hotspot", out var ap)) return null;
        string standard = Text(ap, "standard"), band = Text(ap, "band");
        if (standard is "" or "unknown" || band is "" or "unknown") return null;
        return directSamples.TryGetValue($"{session.Fingerprint}|{direction}|{standard}|{band}", out var sample) && DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - sample.At < 600000 ? sample.Rate * 0.75 : null;
    }

    private readonly Dictionary<string, PhoneReceiveApproval> approvedPhoneReceives = [];
    private CancellationTokenSource? receiveOfferLifetime;

    private async Task WatchPhoneReceiveOfferAsync(NearbySession session, PhoneReceiveApproval approval, CancellationTokenSource lifetime)
    {
        try {
            while (!lifetime.IsCancellationRequested) {
                await Task.Delay(700,lifetime.Token);
                if (closing || !ReferenceEquals(nearbySession,session) || !session.Authenticated) { lifetime.Cancel(); return; }
                var status=await SecureAsync(new { op="status" });
                if (!status.TryGetProperty("pullOffer",out var current) || current.ValueKind != JsonValueKind.Object || !approval.MatchesOffer(current,session.Fingerprint)) { lifetime.Cancel(); return; }
            }
        } catch (OperationCanceledException) when(lifetime.IsCancellationRequested) {}
        catch { lifetime.Cancel(); }
    }

    private async Task<string?> ReceivePhoneOfferAsync(JsonElement offer)
    {
        if (core is null || nearbySession?.Authenticated != true || sending || pairing || receivePromptActive) return null;
        string token = Text(offer, "token");
        if (token.Length == 0 || pulledOffers.Contains(token)) return null;
        long total = ConnectionPolicy.OfferedBytes(offer);
        BusinessActivity();
        sending = true; receivePromptActive = true; UpdateSelection();
        string pulledId = "";
        try
        {
            selectedIsNearby = true;
            var session = nearbySession;
            var code = session.Fingerprint[..16];
            PhoneReceiveApproval? approval = null;
            Peer? preparedPeer = null;
            bool needsNetworkNotice = true;
            // Every authenticated pull receive, including computers, is approved before routing.
            {
                approval = PhoneReceiveApproval.Create(offer,session.Fingerprint);
                // Probing/binding the current network does not join or create a Wi-Fi network.
                await networkRestoration.WaitAsync();
                preparedPeer = await ReusableNearbyPeerAsync(session) ?? await TryPersonalHotspotAsync(session);
                needsNetworkNotice = preparedPeer is null || !reusedPersonalHotspot;
                using var lifetime = new CancellationTokenSource();
                receiveOfferLifetime = lifetime;
                var watcher = WatchPhoneReceiveOfferAsync(session,approval,lifetime);
                bool accepted;
                try {
                    accepted = AppDialog.Show(this,"接收文件",approval.Message(session.Name,downloadDir,needsNetworkNotice),"接收","拒绝",cancellation:lifetime.Token);
                    accepted = accepted && !lifetime.IsCancellationRequested && !closing && ReferenceEquals(nearbySession,session);
                } finally {
                    lifetime.Cancel(); await watcher;
                    if (ReferenceEquals(receiveOfferLifetime,lifetime)) receiveOfferLifetime=null;
                }
                if (!accepted) {
                    try { await SecureAsync(new { op="pull_reject", id=approval.Id }); } catch { }
                    pulledOffers.Add(token); await RestoreNetworkAsync(); return null;
                }
                var current = await SecureAsync(new { op="status" });
                if (closing || !ReferenceEquals(nearbySession,session) || !current.TryGetProperty("pullOffer",out var pending) || pending.ValueKind != JsonValueKind.Object || !approval.MatchesOffer(pending,session.Fingerprint)) throw new OperationCanceledException("接收请求已结束或内容已变化。");
            }
            Peer peer;
            if(!needsNetworkNotice) {
                // Approval without a switch warning cannot authorize creating a new temporary network.
                await networkRestoration.WaitAsync();
                peer=await ReusableNearbyPeerAsync(session)??throw new OperationCanceledException("接收请求已结束或内容已变化。");
                if(!reusedPersonalHotspot)throw new OperationCanceledException("接收请求已结束或内容已变化。");
            } else peer=await ChoosePathAsync(selectedPeer, code, total, "download", networkChangeApproved: approval is not null);
            if (closing || !ReferenceEquals(nearbySession,session)) throw new OperationCanceledException("接收请求已结束。");
            var result = await Task.Run(() => core.Call(new { op = "pull", peer = PeerPayload(peer), verificationCode = code, token }));
            pulledId = Text(result,"id");
            if (approval is not null) approvedPhoneReceives.Add(pulledId,approval);
            pulledOffers.Add(token);
            try { await SecureAsync(new { op = "pull_consumed", id = Text(offer, "id") }); } catch { /* The pinned TLS pull now owns the exact approved manifest. */ }
            if (temporaryNetwork) temporaryTransferId = pulledId;
            StatusMessage.Text = approval is null ? "文件请求已收到，请在弹出窗口中选择接收。" : "已确认接收，正在核对文件信息。";
            return pulledId;
        }
        catch (Exception ex)
        {
            if (pulledId.Length > 0) approvedPhoneReceives.Remove(pulledId);
            // A failed route must settle the phone's waiting task rather than
            // leave it stranded behind an offer Windows will never consume.
            try { await SecureAsync(new { op = "pull_reject", id = Text(offer, "id") }); pulledOffers.Add(token); } catch { }
            await RestoreNetworkAsync();
            try { File.AppendAllText(Path.Combine(stateDir, "nearby-receive-errors-private.jsonl"), JsonSerializer.Serialize(new { at = DateTimeOffset.Now, error = ex.ToString() }) + Environment.NewLine); } catch { }
            if (diagnostic && !pcNativeTest) throw;
            ShowError("接收附近设备文件失败：" + ex.Message);
            return null;
        }
        finally { sending = false; receivePromptActive = false; UpdateSelection(); }
    }

    private async void RestoreNetwork_Click(object sender, RoutedEventArgs e)
    {
        if (transferRows.Any(t => t.Active)) { ShowError("请先完成或取消正在进行的传输。"); return; }
        await RestoreNetworkAsync();
    }
    private Task RestoreNetworkAsync()=>networkRestoration.RunAsync(RestoreNetworkCoreAsync);
    private async Task RestoreNetworkCoreAsync()
    {
        if (wifi is null) return;
        if (!restoringForClose && (transferRows.Any(t => t.Active)||PcHasTasks()))
        {
            NetworkDecision.Text = "仍有传输正在使用当前连接，将在全部结束后恢复原网络。";
            return;
        }
        bool restored = true;
        bool localPcHotspot = pcUsingLocalHotspot;
        if(localPcHotspot) await StopLocalPcClientHotspotAsync();
        if (modules.Direct is not null && core is not null) await Task.Run(() => core.Call(new { op = "direct_bind", address = (string?)null }));
        try { if (wifi.HasRecovery) await Task.Run(() => wifi.RestoreAsync()); }
        catch (Exception ex) { restored = false; NetworkDecision.Text = "原网络恢复失败，记录已保留：" + ex.Message; }
        try { if (!localPcHotspot && temporaryNetwork && nearbySession?.Authenticated == true) await SecureAsync(new { op = "hotspot_stop" }); }
        catch { BluetoothStatus.Text = "手机热点清理未确认，请检查手机；电脑恢复记录仍独立保留。"; }
        temporaryNetwork = !restored && wifi.HasRecovery;
        if (restored) reusedPersonalHotspot = false;
        RestoreNetworkButton.Visibility = wifi.HasRecovery ? Visibility.Visible : Visibility.Collapsed;
        if (restored && !restoringForClose) NetworkDecision.Text = "已释放临时连接；原网络恢复流程已完成。";
    }
    private static object PeerPayload(Peer peer) => new { name = peer.Name, host = peer.Host, port = peer.Port, fingerprint = peer.Fingerprint };
}
