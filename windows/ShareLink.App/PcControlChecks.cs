using System.IO;
using System.Text.Json;
namespace ShareLink;
internal static class PcControlChecks
{
    public static async Task<object> RunAsync(string output)
    {
        var checks = new List<string>();
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        object Config(string name) => new { name = "Generated PC " + name, device_type = "computer", state_dir = Path.Combine(output, name, "identity"), download_dir = Path.Combine(output, name, "received"), port = 0, discovery_port = 0, transport_modules = new[] { "nearby-direct" } };
        var visibleGatt=GattAdvertisingPolicy.Parameters(true);var hiddenGatt=GattAdvertisingPolicy.Parameters(false);
        Check("visible GATT publication permits discovery and new connections",visibleGatt.IsDiscoverable&&visibleGatt.IsConnectable);
        Check("hidden GATT publication suppresses discovery and new connections",!hiddenGatt.IsDiscoverable&&!hiddenGatt.IsConnectable);
        Check("separate GATT visibility parameters do not mutate a published visible instance",visibleGatt.IsDiscoverable&&visibleGatt.IsConnectable&&!ReferenceEquals(visibleGatt,hiddenGatt));
        var supportedPersonal = await ConnectionPolicy.QueryPersonalHotspotAsync(() => Task.FromResult(J(new { ok=true, personalHotspotActive=true })));
        Check("explicit personal hotspot support remains available", supportedPersonal?.GetProperty("personalHotspotActive").GetBoolean() == true);
        var legacyPersonal = await ConnectionPolicy.QueryPersonalHotspotAsync(() => Task.FromException<JsonElement>(new InvalidOperationException("不支持的加密控制操作")));
        Check("only explicit unsupported operation enables legacy hotspot routing", legacyPersonal is null);
        foreach (var failure in new Exception[] { new TimeoutException("control timeout"), new InvalidOperationException("控制消息验证失败"), new InvalidOperationException("权限不足") }) {
            bool preserved = false;
            try { await ConnectionPolicy.QueryPersonalHotspotAsync(() => Task.FromException<JsonElement>(failure)); }
            catch (Exception error) { preserved = ReferenceEquals(error, failure); }
            Check("hotspot query does not turn a real failure into legacy fallback: " + failure.Message, preserved);
        }
        int nativeCalls=0;bool nativeFail=false;IntPtr seenInstance=IntPtr.Zero,seenParameters=IntPtr.Zero;
        GattAdvertisingPolicy.UpdateParametersDelegate updateProbe=(instance,parameters)=>{nativeCalls++;seenInstance=instance;seenParameters=parameters;return nativeFail?unchecked((int)0x80004005):0;};
        var table=System.Runtime.InteropServices.Marshal.AllocHGlobal(7*IntPtr.Size);var instancePointer=System.Runtime.InteropServices.Marshal.AllocHGlobal(IntPtr.Size);
        try {
            for(int slot=0;slot<7;slot++)System.Runtime.InteropServices.Marshal.WriteIntPtr(table,slot*IntPtr.Size,IntPtr.Zero);
            System.Runtime.InteropServices.Marshal.WriteIntPtr(table,6*IntPtr.Size,System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(updateProbe));
            System.Runtime.InteropServices.Marshal.WriteIntPtr(instancePointer,table);
            GattAdvertisingPolicy.InvokeUpdate(instancePointer,new IntPtr(123));
            Check("GATT update calls IInspectable extension slot with borrowed arguments",nativeCalls==1&&seenInstance==instancePointer&&seenParameters==new IntPtr(123));
            nativeFail=true;bool refused=false;try{GattAdvertisingPolicy.InvokeUpdate(instancePointer,new IntPtr(456));}catch(System.Runtime.InteropServices.COMException){refused=true;}
            Check("GATT native update failure propagates rather than reporting success",refused&&nativeCalls==2);
            Check("GATT borrowed vtable remains owned by the caller",System.Runtime.InteropServices.Marshal.ReadIntPtr(instancePointer)==table);
        } finally {System.Runtime.InteropServices.Marshal.FreeHGlobal(instancePointer);System.Runtime.InteropServices.Marshal.FreeHGlobal(table);GC.KeepAlive(updateProbe);}
        string hashA="0102030405060708"+new string('a',48),hashB="1112131415161718"+new string('b',48),hashC="2122232425262728"+new string('c',48);
        Guid serviceA=PcServiceIdentity.Create(hashA),serviceB=PcServiceIdentity.Create(hashB),serviceC=PcServiceIdentity.Create(hashC);
        Check("bounded identity travels inside one UUID without extra AD payload",PcServiceIdentity.Hint(serviceA)==hashA[..16].ToUpperInvariant());
        Check("unrelated and old anonymous checkpoint UUIDs are ignored",PcServiceIdentity.Hint(Guid.NewGuid())==""&&PcServiceIdentity.Hint(Guid.Parse("7c3d0020-6b4a-4f9d-9a31-534c4e4b0001"))=="");
        Check("truncated service data is ignored",!PcCheckDiscovery.IsPcServiceData(serviceA.ToByteArray()[..15]));
        using(var scanner=new PcCheckDiscovery()){
            scanner.SetLocalFingerprint(hashB);
            scanner.Record(1,"same name",-50,1000,Windows.Devices.Bluetooth.BluetoothAddressType.Public,serviceA);
            scanner.Record(2,"same name",-40,1001,Windows.Devices.Bluetooth.BluetoothAddressType.Random,serviceA);
            Check("public/random address changes merge one device by identity",scanner.SnapshotAt(1002).Length==1&&scanner.SnapshotAt(1002)[0].Address==2);
            scanner.Record(3,"local PC",-20,1002,Windows.Devices.Bluetooth.BluetoothAddressType.Random,serviceB);
            Check("local device is excluded by its own identity",scanner.SnapshotAt(1003).Length==1);
            scanner.Record(4,"same name",-30,1003,Windows.Devices.Bluetooth.BluetoothAddressType.Public,serviceC);
            Check("distinct computers with the same name remain distinct",scanner.SnapshotAt(1004).Length==2);
            Check("expired candidate identities are removed",scanner.SnapshotAt(8000).Length==0);
        }
        Check("system name including Chinese survives public metadata encoding",PcDeviceName.Decode(PcDeviceName.Encode("测试电脑 · Workstation"))=="测试电脑 · Workstation");
        Check("invalid display metadata is rejected",PcDeviceName.Decode([0xff]) is null && PcDeviceName.Decode([10]) is null && PcDeviceName.Decode(new byte[129]) is null);
        var pendingName=new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads=0;
        using(var names=new PcCheckDiscovery(_=>{reads++;return pendingName.Task;})) {
            long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            names.Record(1,"Old check name",-50,now,Windows.Devices.Bluetooth.BluetoothAddressType.Public,serviceA);
            var clicked=names.Snapshot().Single();var reading=names.RefreshNamesAsync();
            await names.RefreshNamesAsync();
            Check("name metadata lookup is bounded to one pending request",reads==1);
            names.Record(2,"",-40,now,Windows.Devices.Bluetooth.BluetoothAddressType.Random,serviceA);
            pendingName.SetResult("Actual system name");await reading;
            Check("resolved system name replaces stale broadcast name without stale endpoint",names.Resolve(clicked) is {Name:"Actual system name",Address:2});
            names.Record(3,"Old check name",-40,now,Windows.Devices.Bluetooth.BluetoothAddressType.Random,serviceA);
            Check("later advertisements cannot overwrite resolved system name",names.Resolve(clicked) is {Name:"Actual system name",Address:3});
            await names.RefreshNamesAsync();Check("resolved names are cached instead of connecting on every refresh",reads==1);
        }
        checks.AddRange(await PcDisplayNames.ChecksAsync(output));
        checks.AddRange(await HotspotVisibility.ChecksAsync());
        checks.AddRange(await MainWindow.CheckPcUiAsync(output));
        checks.AddRange(await MainWindow.CheckReceiveConfirmationAsync(output));
        checks.AddRange(await MainWindow.CheckUserExitHistoryAsync(output));
        checks.AddRange(await MainWindow.CheckDiscoveryRefreshAsync(output));
        checks.AddRange(MainWindow.CheckSendTarget(output));
        checks.AddRange(await MainWindow.CheckMarqueeAsync(output));
        checks.AddRange(await MainWindow.CheckDesktopPcAsync(output));
        checks.AddRange(await MainWindow.CheckPcReceiveConsentAsync(output));
        checks.AddRange(await NearbyLinkReuseChecks.RunAsync());
        checks.AddRange(await PcSharedDiscoveryChecks.RunAsync());
        checks.AddRange(await PcRequestedChecks.RunAsync(output));
        using var client = new NativeCore(Config("client")); using var receiver = new NativeCore(Config("receiver"));
        var userDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string displayedName = "", displayedCode = "";
        await using (var host = new PcControlProtocol(receiver, (name, code) => { displayedName = name; displayedCode = code; return userDecision.Task; })) {
            Check("name metadata uses the configured core name",host.LocalName=="Generated PC receiver");
            var unpaired = await host.RequestAsync(J(new { op = "secure", envelope = new { } }));
            Check("secure control is rejected before pairing", !unpaired.GetProperty("ok").GetBoolean());
            var plain = await host.RequestAsync(J(new { op = "hotspot_start" })); Check("plaintext hotspot commands are rejected", !plain.GetProperty("ok").GetBoolean());
            var hello = await host.RequestAsync(J(new { op = "hello" })); Check("new receiver requires explicit user confirmation", hello.GetProperty("requiresPairingConfirmation").GetBoolean());
            var begun = client.Call(new { op = "pair_begin" }); string pair = MainWindow.Text(begun, "pairId");
            var remote = await host.RequestAsync(J(new { op = "pair_begin", hello = begun.GetProperty("hello") }));
            var finish = client.Call(new { op = "pair_finish", pairId = pair, hello = remote.GetProperty("hello") });
            Check("independent cores derive the same displayed SAS", displayedCode.Length > 0 && displayedCode == MainWindow.Text(finish, "sas"));
            Check("confirmation displays the authenticated peer name", displayedName == "Generated PC client");
            var pending = await host.RequestAsync(J(new { op = "pair_confirm", confirmed = true }));
            Check("unanswered dialog cannot activate receiver keys", !pending.GetProperty("confirmed").GetBoolean() && !host.Authenticated);
            bool prevented = false; try { client.Call(new { op = "pair_encrypt", pairId = pair, payload = new { op = "status" } }); } catch { prevented = true; }
            Check("client core also forbids encryption before confirmation", prevented);
            // Deliberate simulated user action inside the non-radio test only.
            // Production confirmation is supplied exclusively by the visible dialog.
            userDecision.SetResult(true); bool simulatedClientConsent = true;
            client.Call(new { op = "pair_confirm", pairId = pair, confirmed = simulatedClientConsent });
            var accepted = await host.RequestAsync(J(new { op = "pair_confirm", confirmed = simulatedClientConsent })); Check("explicit simulated confirmation activates receiver", accepted.GetProperty("confirmed").GetBoolean() && host.Authenticated);
            var encrypted = client.Call(new { op = "pair_encrypt", pairId = pair, payload = new { op = "status" } });
            var request = J(new { op = "secure", envelope = encrypted.GetProperty("envelope") }); var response = await host.RequestAsync(request);
            var decoded = client.Call(new { op = "pair_decrypt", pairId = pair, envelope = response.GetProperty("envelope") }).GetProperty("payload");
            Check("real independent cores exchange authenticated encrypted status", decoded.GetProperty("pairingCheckpoint").GetBoolean());
            Check("file identity is pinned to the paired receiver certificate", MainWindow.Text(decoded.GetProperty("info"), "fingerprint") == hostIdentity(receiver));
            Check("checkpoint does not claim hotspot or file capability", !decoded.GetProperty("capabilities").GetProperty("automaticHotspot").GetBoolean());
            var replay = await host.RequestAsync(request); Check("replayed control ciphertext is rejected", !replay.GetProperty("ok").GetBoolean());
            Check("plain status cannot read authenticated metadata", !(await host.RequestAsync(J(new { op = "status" }))).GetProperty("ok").GetBoolean());
            client.Call(new { op = "pair_close", pairId = pair });
        }
        await using (var host = new PcControlProtocol(receiver, (_, _) => Task.FromResult(false))) {
            var begun = client.Call(new { op = "pair_begin" }); string pair = MainWindow.Text(begun, "pairId");
            var remote = await host.RequestAsync(J(new { op = "pair_begin", hello = begun.GetProperty("hello") }));
            client.Call(new { op = "pair_finish", pairId = pair, hello = remote.GetProperty("hello") });
            var rejected = await host.RequestAsync(J(new { op = "pair_confirm", confirmed = true }));
            Check("cancelled user confirmation closes receiver keys", !rejected.GetProperty("ok").GetBoolean() && !host.Authenticated);
            Check("cancelled receiver cannot accept secure control", !(await host.RequestAsync(J(new { op = "secure", envelope = new { } }))).GetProperty("ok").GetBoolean());
            client.Call(new { op = "pair_close", pairId = pair });
        }
        int invoked=0;
        await using(var busy=new PcControlProtocol(receiver,(_,_)=>{invoked++;return Task.FromResult(false);},_=>{invoked++;return Task.FromResult(J(new{ok=true}));},()=>false)){
            Check("busy receiver rejects hello without showing consent dialog",!(await busy.RequestAsync(J(new{op="hello"}))).GetProperty("ok").GetBoolean());
            var begin=client.Call(new{op="pair_begin"});
            Check("pair-begin cannot bypass the busy admission guard",!(await busy.RequestAsync(J(new{op="pair_begin",hello=begin.GetProperty("hello")}))).GetProperty("ok").GetBoolean());
            Check("unconfirmed secure command cannot invoke hotspot handler",!(await busy.RequestAsync(J(new{op="secure",envelope=new{}}))).GetProperty("ok").GetBoolean()&&invoked==0);
            client.Call(new{op="pair_close",pairId=MainWindow.Text(begin,"pairId")});
        }
        return new { passed = true, checks, radioAccessed = false, networkChanged = false, filesTransferred = false, scope = "Two independent real native cores; explicit consent simulated for protocol tests only" };
        static string hostIdentity(NativeCore core) => MainWindow.Text(core.Call(new { op = "snapshot" }).GetProperty("info"), "fingerprint");
    }
}
