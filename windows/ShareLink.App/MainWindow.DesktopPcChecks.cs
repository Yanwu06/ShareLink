using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;

namespace ShareLink;

public partial class MainWindow
{
    internal static async Task<string[]> CheckDesktopPcAsync(string output)
    {
        var checks = new List<string>();
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        var window = new MainWindow(Path.Combine(output, "desktop-fixture"), desktopTest: true);
        using var core = new NativeCore(window.CoreConfiguration()); window.core = core; window.pcRefreshing = true; window.pcWifiReport = () => Task.FromResult(window.networkReport);
        using var client = new NativeCore(new { name = "Generated PC client", device_type = "computer", state_dir = Path.Combine(output,"desktop-client","identity"), download_dir = Path.Combine(output,"desktop-client","received"), port = 0, discovery_port = 0, transport_modules = new[] { "nearby-direct" } });
        using var pc = new PcCheckDiscovery(); using var mobile = new NearbyBluetooth();
        window.pcDiscovery = pc; window.nearbyDiscovery = mobile; window.discoveryEnabled = true;
        string pcHash = "0102030405060708" + new string('a',48), mobileHash = "1112131415161718" + new string('b',48);
        Guid service = PcServiceIdentity.Create(pcHash);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        pc.Record(1,"Same generated name",-40,now,BluetoothAddressType.Public,service);
        mobile.RecordAdvertisement(1,"",-50,now,BluetoothAddressType.Public,false,fingerprintPrefix:mobileHash[..16],advertisedName:"Same generated name");
        window.RefreshNearbyList();
        var rows = (NearbyPhone[])window.NearbyList.ItemsSource;
        Check("ordinary desktop shows PC and phone together, retaining identical names and addresses", rows.Length == 2 && rows.Count(IsNativePc) == 1);
        Check("phone connects through its legacy control service while PC uses its identity service", rows.Single(IsNativePc).ControlService == service && rows.Single(p => !IsNativePc(p)).ControlService is null);
        Check("combined device list hides the empty search icon", window.NoPeers.Visibility == System.Windows.Visibility.Collapsed && window.NoPeersPanel.Visibility == System.Windows.Visibility.Collapsed);
        window.nearbyIdentities[1] = (mobileHash,"Confirmed phone","phone"); window.RefreshNearbyList();
        Check("a stale address identity cannot rename a different PC identity", ((NearbyPhone[])window.NearbyList.ItemsSource).Single(IsNativePc).Name == "Same generated name");
        Check("isolated normal desktop uses only its test identity and receive directory", window.stateDir.StartsWith(Path.Combine(output,"desktop-fixture")) && window.ResolveReceiveLocation(new()).Directory == Path.Combine(output,"desktop-fixture","received"));
        Check("phone connection reserves admission before any radio request", window.TryBeginNearbyConnection());
        Check("PC pairing cannot race an outgoing phone connection", !window.ReservePcPeer());
        window.pairing = false;
        Check("PC pairing reserves shared admission", window.ReservePcPeer());
        Check("phone connection cannot race a pending PC pairing", !window.TryBeginNearbyConnection()); window.ReleasePcPeer();
        window.storageChanging = true; Check("storage change refuses incoming PC pairing", !window.ReservePcPeer()); window.storageChanging = false;
        window.modeChanging = true; Check("mode transition refuses incoming PC pairing", !window.ReservePcPeer()); window.modeChanging = false;
        window.discoveryEnabled = false; Check("disabled discovery refuses incoming PC pairing", !window.ReservePcPeer()); window.discoveryEnabled = true;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only this non-radio fixture supplies simulated consent. Live entry uses ConfirmNativePc's visible SAS dialog.
        await using var protocol = new PcControlProtocol(core, (_,_) => decision.Task, window.PcCommandAsync, () => window.CanAcceptPc, window.ReservePcPeer, window.ReleasePcPeer);
        window.pcProtocol = protocol;
        var begun = client.Call(new { op="pair_begin" }); string pair = Text(begun,"pairId");
        try {
            var remote = await protocol.RequestAsync(J(new { op="pair_begin", hello=begun.GetProperty("hello") }));
            client.Call(new { op="pair_finish", pairId=pair, hello=remote.GetProperty("hello") });
            Check("pending PC consent owns admission without authenticating", protocol.HasSession && !protocol.Authenticated && !window.TryBeginNearbyConnection());
            var duplicate = await protocol.RequestAsync(J(new { op="pair_begin", hello=begun.GetProperty("hello") }));
            Check("a duplicate pairing request cannot cancel the first pending consent", !duplicate.GetProperty("ok").GetBoolean() && protocol.HasSession);
            Check("unanswered PC consent cannot execute a plaintext hotspot command", !(await protocol.RequestAsync(J(new { op="hotspot_start" }))).GetProperty("ok").GetBoolean() && window.pcAccessPoint is null);
            decision.SetResult(true); bool fixtureClientConsent = true;
            client.Call(new { op="pair_confirm", pairId=pair, confirmed=fixtureClientConsent });
            await protocol.RequestAsync(J(new { op="pair_confirm", confirmed=fixtureClientConsent }));
            window.RefreshNearbyList();
            Check("normal desktop recognizes the authenticated inbound PC without an experiment flag", window.HostedPcConnected && !window.pcNativeTest && ((NearbyPhone[])window.NearbyList.ItemsSource) is [{Connected:true}]);
            window.UpdateSendTargetPresentation();
            Check("real authenticated inbound PC uses a computer icon in the send target",window.TargetDeviceIcon.Visibility==System.Windows.Visibility.Visible&&(string)window.TargetDeviceIcon.Tag=="computer");
            string source = Path.Combine(output,"desktop-generated-only.bin"); await File.WriteAllBytesAsync(source,new byte[256]);
            window.files.Add(new SelectedFile(source,Path.GetFileName(source),256));
            string task = await window.OfferHostedPcFilesAsync();
            Check("normal inbound PC sends through prepare-pull without opening a hotspot in the fixture", window.pcOffer is not null && window.pcAccessPoint is null && window.PcHasTasks());
            async Task<JsonElement> Secure(object command) {
                var encrypted = client.Call(new { op="pair_encrypt", pairId=pair, payload=command });
                var reply = await protocol.RequestAsync(J(new { op="secure", envelope=encrypted.GetProperty("envelope") }));
                return client.Call(new { op="pair_decrypt", pairId=pair, envelope=reply.GetProperty("envelope") }).GetProperty("payload");
            }
            var offeredStatus=await Secure(new { op="status" });
            Check("outgoing host offer retains reverse hotspot routing for legacy receivers",offeredStatus.GetProperty("capabilities").GetProperty("hotspotRoleSelection").GetBoolean()&&offeredStatus.GetProperty("pullOffer").GetProperty("id").GetString()==task);
            Check("active file offer blocks encrypted disconnect", !(await Secure(new { op="disconnect" })).GetProperty("ok").GetBoolean() && protocol.Authenticated);
            core.Call(new { op="cancel", id=task });
            var deadline = DateTime.UtcNow.AddSeconds(5); while(window.PcHasTasks() && DateTime.UtcNow < deadline) await Task.Delay(20);
            Check("generated waiting offer cancels without transferring a file", !window.PcHasTasks());
            await window.RefreshState();
            var idleStatus=await Secure(new { op="status" });
            Check("cancelled PC offers are removed before a later session can reuse the view", idleStatus.GetProperty("pullOffer").ValueKind==JsonValueKind.Null && window.pcOffer is null);
            Check("explicit encrypted disconnect replies before closing the pair", (await Secure(new { op="disconnect" })).GetProperty("ok").GetBoolean() && !protocol.Authenticated && !protocol.HasSession && protocol.ReleaseRequested);
            Check("disconnect releases admission for a phone connection", window.TryBeginNearbyConnection()); window.pairing=false;
            window.pcOffer=null;
            Check("closed PC pair no longer occupies the connected device view", !window.HostedPcConnected);
        } finally {
            client.Call(new { op="pair_close", pairId=pair });
            window.core=null; window.pcProtocol=null; window.pcDiscovery=null; window.nearbyDiscovery=null; window.timer.Stop();
        }
        return checks.ToArray();
    }
}
