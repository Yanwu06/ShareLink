using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ShareLink;

internal record NearbyPhone(ulong Address, string Name, short Rssi, long SeenAtMs, BluetoothAddressType AddressType = BluetoothAddressType.Unspecified, string InstanceId = "", string FingerprintPrefix = "")
{
    public string DeviceType { get; init; } = "unknown";
    public Guid? ControlService { get; init; }
    public string Display => DeviceType == "computer"
        ? Name.StartsWith("Windows · ", StringComparison.Ordinal) ? Name : "Windows · " + (Name.Length > 0 ? Name : "电脑")
        : PresentationName(Name);
    public bool Connected { get; init; }
    public string Detail => Connected ? "已连接 点击断开" : "点击连接";
    internal static string PresentationName(string name)
    {
        if (name.StartsWith("鸿蒙 ", StringComparison.Ordinal)) return "HarmonyOS · " + name[3..].Trim();
        if (name.StartsWith("HarmonyOS ", StringComparison.Ordinal)) return "HarmonyOS · " + name[10..].TrimStart(' ', '·');
        return name.Length > 0 ? name : "附近的 ShareLink 设备";
    }
}

internal sealed class NearbyBluetooth : IDisposable
{
    internal static readonly Guid ServiceUuid = Guid.Parse("7c3d0001-6b4a-4f9d-9a31-534c4e4b0001");
    internal static readonly Guid RequestUuid = Guid.Parse("7c3d0002-6b4a-4f9d-9a31-534c4e4b0001");
    internal static readonly Guid ResponseUuid = Guid.Parse("7c3d0003-6b4a-4f9d-9a31-534c4e4b0001");
    internal static readonly Guid PollResponseUuid = Guid.Parse("7c3d0004-6b4a-4f9d-9a31-534c4e4b0001");
    private BluetoothLEAdvertisementWatcher? watcher;
    private readonly object gate = new();
    private readonly Dictionary<ulong, NearbyPhone> phones = [];
    private const long VisibleForMs = 5000;
    private const long InvitationForMs = 15000;
    public string Status { get; private set; } = "蓝牙发现尚未开启";
    public NearbyPhone[] Snapshot() => SnapshotAt(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    internal NearbyPhone[] SnapshotAt(long now)
    {
        lock (gate)
        {
            Prune(now);
            return phones.Values.Where(p => now - p.SeenAtMs < VisibleForMs &&
                (p.InstanceId.Length > 0 || p.FingerprintPrefix.Length > 0)).OrderByDescending(p => p.Rssi).ToArray();
        }
    }
    public NearbyPhone? FindInvitation(string instance)
    {
        lock (gate) return MatchInvitation(phones.Values, instance, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }
    internal static NearbyPhone? MatchInvitation(IEnumerable<NearbyPhone> values, string instance, long now) =>
        string.IsNullOrEmpty(instance) ? null : values.Where(p =>
            (string.Equals(p.InstanceId, instance, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.FingerprintPrefix, instance, StringComparison.OrdinalIgnoreCase)) &&
            now - p.SeenAtMs < InvitationForMs).OrderByDescending(p => p.SeenAtMs).FirstOrDefault();
    private void Prune(long now)
    {
        foreach (var key in phones.Where(p => now - p.Value.SeenAtMs >= InvitationForMs).Select(p => p.Key).ToArray()) phones.Remove(key);
    }
    public void Refresh()
    {
        bool wasRunning=watcher is not null;StopWatcher();
        lock(gate)phones.Clear();
        if(wasRunning)Start();
    }
    private void StopWatcher()
    {
        var old=watcher;watcher=null;
        if(old is not null){old.Received-=Received;try{old.Stop();}catch{}}
    }
    internal static object DiscoveryChecks()
    {
        using var discovery = new NearbyBluetooth();
        var checks = new List<string>();
        void Check(string name, bool condition) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        Check("HarmonyOS platform is consistent across legacy discovery, new discovery and authenticated names",
            new NearbyPhone(0, "鸿蒙 测试手机", 0, 0).Display == "HarmonyOS · 测试手机" &&
            new NearbyPhone(0, "HarmonyOS 测试手机", 0, 0).Display == "HarmonyOS · 测试手机" &&
            new NearbyPhone(0, "HarmonyOS · 测试手机", 0, 0).Display == "HarmonyOS · 测试手机");
        Check("connection actions describe click to connect and click to disconnect",
            new NearbyPhone(0, "", 0, 0).Detail == "点击连接" &&
            (new NearbyPhone(0, "", 0, 0) { Connected = true }).Detail == "已连接 点击断开");
        const string hint = "0102030405060708";
        discovery.RecordAdvertisement(1, "", -60, 1000, BluetoothAddressType.Random, true);
        Check("primary packet waits for identifying scan response", discovery.SnapshotAt(1000).Length == 0);
        discovery.RecordAdvertisement(1, "", -60, 1100, BluetoothAddressType.Random, false, fingerprintPrefix: hint, advertisedName: "鸿蒙 A");
        var first = discovery.SnapshotAt(1100).Single();
        Check("Harmony fingerprint invitation matches current address", MatchInvitation([first], hint, 1200)?.Address == 1);
        discovery.RecordAdvertisement(1, "unrelated", -40, 5500, BluetoothAddressType.Random, false);
        Check("unrelated advertisements do not renew ShareLink presence", discovery.SnapshotAt(6100).Length == 0);
        Check("brief scan pause still allows invitation", MatchInvitation([first], hint, 6100)?.Address == 1);
        Check("expired invitation cannot reuse stale address", MatchInvitation([first], hint, 16100) is null);
        for (ulong address = 2; address <= 5; address++)
            discovery.RecordAdvertisement(address, "", -60, 17000 + (long)address, BluetoothAddressType.Random, false, fingerprintPrefix: hint, advertisedName: "鸿蒙 A");
        Check("radio address rotation replaces old presentation rows", discovery.SnapshotAt(17005) is [{ Address: 5 }]);
        discovery.RecordAdvertisement(6, "", -60, 17006, BluetoothAddressType.Random, false, fingerprintPrefix: "1112131415161718", advertisedName: "鸿蒙 A");
        Check("same-name distinct identities remain separate", discovery.SnapshotAt(17006).Length == 2);
        discovery.Refresh();
        Check("manual refresh removes all stale discovery rows immediately", discovery.SnapshotAt(17006).Length == 0);
        discovery.RecordAdvertisement(7, "Android", -60, 18000, BluetoothAddressType.Random, true, instanceId: hint);
        var legacy = discovery.SnapshotAt(18000).Single();
        Check("legacy invitation remains supported", MatchInvitation([legacy], hint, 18100)?.Address == 7);
        Check("manual refresh resumes live discovery without reconnecting", legacy.Address == 7);
        Check("closed broadcasts leave the visible list within five seconds", discovery.SnapshotAt(23000).Length == 0);
        discovery.Refresh();
        Check("explicit type hint decodes phone, tablet, computer and unknown independently of names",
            ReadDeviceTypeHint([0x53,0x54,1]) == "phone" && ReadDeviceTypeHint([0x53,0x54,2]) == "tablet" &&
            ReadDeviceTypeHint([0x53,0x54,3]) == "computer" && ReadDeviceTypeHint([0x53,0x54,0]) == "unknown" &&
            ReadDeviceTypeHint([0x53,0x54,255]) == "unknown" && ReadDeviceTypeHint([0x53,0x48,1]) == "" &&
            ReadDeviceTypeHint([0x53,0x54]) == "");
        discovery.RecordAdvertisement(8, "", -60, 24000, BluetoothAddressType.Random, true, deviceType:"phone");
        discovery.RecordAdvertisement(8, "", -60, 24001, BluetoothAddressType.Random, false, fingerprintPrefix:hint, advertisedName:"HarmonyOS device");
        Check("scan response retains explicit type from the primary packet", discovery.SnapshotAt(24001).Single().DeviceType == "phone");
        discovery.RecordAdvertisement(9, "", -60, 24002, BluetoothAddressType.Random, false, fingerprintPrefix:hint, advertisedName:"HarmonyOS device");
        Check("identity rotation retains the explicit shape before its next primary packet", discovery.SnapshotAt(24002).Single().DeviceType == "phone");
        discovery.RecordAdvertisement(9, "", -60, 24003, BluetoothAddressType.Random, true, deviceType:"tablet");
        Check("explicit updated type replaces the earlier discovery shape", discovery.SnapshotAt(24003).Single().DeviceType == "tablet");
        discovery.RecordAdvertisement(10, "", -60, 24004, BluetoothAddressType.Random, false, fingerprintPrefix:"2122232425262728", advertisedName:"HarmonyOS phone");
        Check("legacy names never imply a phone shape", discovery.SnapshotAt(24004).Single(p => p.Address == 10).DeviceType == "unknown");
        return new { passed = true, checks = checks.Count, cases = checks, radioAccessed = false, visibleExpiryMs = VisibleForMs };
    }
    public void Start()
    {
        if (watcher is not null) return;
        try
        {
            watcher = new() { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += Received;
            watcher.Stopped += (sender, args) => { if(sender==watcher)Status = args.Error == BluetoothError.Success ? "蓝牙发现已停止" : "蓝牙不可用 · 请开启蓝牙或检查权限"; };
            watcher.Start();
            Status = "蓝牙附近发现已开启";
        }
        catch { Status = "蓝牙不可用 · 仍可通过局域网连接"; Dispose(); }
    }
    internal static string ReadDeviceTypeHint(ReadOnlySpan<byte> bytes) =>
        bytes.Length == 3 && bytes[0] == 0x53 && bytes[1] == 0x54
            ? bytes[2] switch { 1 => "phone", 2 => "tablet", 3 => "computer", _ => "unknown" } : "";
    private void Received(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs args)
    {
        if (sender != watcher) return;
        string instanceId = "", fingerprintPrefix = "", advertisedName = "", deviceType = "";
        foreach (var entry in args.Advertisement.ManufacturerData.Where(m => m.CompanyId == 0xffff))
        {
            using var data = DataReader.FromBuffer(entry.Data);
            if (data.UnconsumedBufferLength > 64) continue;
            var bytes = new byte[data.UnconsumedBufferLength]; data.ReadBytes(bytes);
            string typeHint = ReadDeviceTypeHint(bytes);
            if (typeHint.Length > 0) deviceType = typeHint;
            if (bytes.Length == 10 && bytes[0] == 0x53 && bytes[1] == 0x4c) instanceId = Convert.ToHexString(bytes[2..]);
            if (bytes.Length is >= 11 and <= 27 && bytes[0] == 0x53 && bytes[1] == 0x4c && bytes[2] == 0x48)
            {
                fingerprintPrefix = Convert.ToHexString(bytes[3..11]);
                try { advertisedName = new UTF8Encoding(false, true).GetString(bytes[11..]); }
                catch (DecoderFallbackException) { advertisedName = ""; }
            }
        }
        RecordAdvertisement(args.BluetoothAddress, args.Advertisement.LocalName, args.RawSignalStrengthInDBm,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), args.BluetoothAddressType,
            args.Advertisement.ServiceUuids.Contains(ServiceUuid), instanceId, fingerprintPrefix, advertisedName, deviceType);
    }
    internal void RecordAdvertisement(ulong address, string name, short rssi, long now, BluetoothAddressType addressType,
        bool shareLinkService, string instanceId = "", string fingerprintPrefix = "", string advertisedName = "", string deviceType = "")
    {
        lock (gate)
        {
            // Other advertisements from a known radio address must not keep a
            // stopped ShareLink broadcast alive.
            if (!shareLinkService && instanceId.Length == 0 && fingerprintPrefix.Length == 0) return;
            Prune(now);
            bool known = phones.TryGetValue(address, out var previous);
            if (advertisedName.Length > 0) name = advertisedName;
            if (name.Length == 0 && known) name = previous!.Name;
            if (instanceId.Length == 0 && known) instanceId = previous!.InstanceId;
            if (fingerprintPrefix.Length == 0 && known) fingerprintPrefix = previous!.FingerprintPrefix;
            var sameIdentity = phones.Values.FirstOrDefault(p =>
                fingerprintPrefix.Length > 0 && p.FingerprintPrefix == fingerprintPrefix ||
                instanceId.Length > 0 && p.InstanceId == instanceId);
            if (deviceType.Length == 0) deviceType = (known ? previous : sameIdentity)?.DeviceType ?? "unknown";
            // The stable broadcast hint associates presentation rows across
            // random addresses; the full encrypted certificate still authenticates connections.
            foreach (var key in phones.Where(p => p.Key != address &&
                (fingerprintPrefix.Length > 0 && p.Value.FingerprintPrefix == fingerprintPrefix ||
                 instanceId.Length > 0 && p.Value.InstanceId == instanceId)).Select(p => p.Key).ToArray()) phones.Remove(key);
            if (!phones.ContainsKey(address) && phones.Count >= 64) return;
            var type = addressType == BluetoothAddressType.Unspecified && known ? previous!.AddressType : addressType;
            phones[address] = new(address, name, rssi, now, type, instanceId, fingerprintPrefix) { DeviceType = deviceType };
        }
    }
    public void Dispose()
    {
        StopWatcher();
        lock(gate)phones.Clear();
    }
}

// GATT is a framed control channel only. Hotspot secrets are exchanged only as
// core-produced encrypted envelopes after the shells activate session keys.
internal sealed class NearbySession : IDisposable
{
    private BluetoothLEDevice? device;
    private GattDeviceService? service;
    private GattSession? connection;
    private GattCharacteristic? writer, reader;
    private readonly SemaphoreSlim requestGate = new(1, 1);
    private readonly object incomingGate = new();
    private readonly ControlFrames incoming = new();
    private TaskCompletionSource<JsonElement>? pending;
    private int nextRequest, waitingRequest;
    private volatile bool disposed;
    private int disposalStarted;
    private bool pollingResponses;
    private bool pollingV2;
    public int ControlWriteLimit { get; set; } = 20;
    public string PairId { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Authenticated { get; set; }
    public bool AutomaticHotspot { get; set; }
    public string ControlMode => pollingV2 ? "read-poll-v2" : pollingResponses ? "read-poll" : "notify";

    public static Task<NearbySession> ConnectAsync(ulong address, BluetoothAddressType addressType, Guid? controlService = null) => BluetoothWorker.RunAsync(() => ConnectCoreAsync(address, addressType, controlService ?? NearbyBluetooth.ServiceUuid));

    private static async Task<NearbySession> ConnectCoreAsync(ulong address, BluetoothAddressType addressType, Guid controlService)
    {
        var result = new NearbySession();
        var trace = new Dictionary<string, object>();
        string step = "取得对方设备蓝牙设备";
        try
        {
            result.device = await BluetoothWorker.AwaitAsync(BluetoothLEDevice.FromBluetoothAddressAsync(address, addressType), TimeSpan.FromSeconds(20))
                ?? throw new InvalidOperationException("对方设备蓝牙不可用或连接权限未授予。");
            trace["deviceAddress"] = $"{result.device.BluetoothAddress:X12}";
            step = "建立对方设备蓝牙会话";
            result.connection = await BluetoothWorker.AwaitAsync(GattSession.FromDeviceIdAsync(result.device.BluetoothDeviceId), TimeSpan.FromSeconds(15));
            if (result.connection is null) throw new InvalidOperationException("无法创建对方设备蓝牙连接会话。");
            result.connection.MaintainConnection = true;
            result.device.ConnectionStatusChanged += result.ConnectionChanged;
            step = "查询对方设备控制服务";
            var services = await BluetoothWorker.AwaitAsync(result.device.GetGattServicesForUuidAsync(controlService, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(15));
            if (services.Status != GattCommunicationStatus.Success || services.Services.Count == 0) throw new InvalidOperationException($"查找对方设备蓝牙服务失败：{services.Status}，协议错误 {services.ProtocolError?.ToString() ?? "无"}。请在对方设备重试附近分享。");
            trace["serviceCount"] = services.Services.Count;
            trace["serviceHandles"] = services.Services.Select(s => s.AttributeHandle).ToArray();
            result.service = services.Services[0];
            foreach (var extra in services.Services.Skip(1)) extra.Dispose();
            step = "打开对方设备控制服务";
            var open = await BluetoothWorker.AwaitAsync(result.service.OpenAsync(GattSharingMode.SharedReadAndWrite), TimeSpan.FromSeconds(10));
            trace["serviceOpenStatus"] = open.ToString();
            if (open is not GattOpenStatus.Success and not GattOpenStatus.AlreadyOpened)
                throw new InvalidOperationException($"对方设备控制服务未能打开：{open}。");
            step = "查询对方设备控制接口";
            var requests = await BluetoothWorker.AwaitAsync(result.service.GetCharacteristicsForUuidAsync(NearbyBluetooth.RequestUuid, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(10));
            var responses = await BluetoothWorker.AwaitAsync(result.service.GetCharacteristicsForUuidAsync(NearbyBluetooth.ResponseUuid, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(10));
            var poll = await BluetoothWorker.AwaitAsync(result.service.GetCharacteristicsForUuidAsync(NearbyBluetooth.PollResponseUuid, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(10));
            if (requests.Status != GattCommunicationStatus.Success || responses.Status != GattCommunicationStatus.Success || requests.Characteristics.Count == 0 || responses.Characteristics.Count == 0)
                throw new InvalidOperationException("对方设备控制服务尚未准备好。");
            result.writer = requests.Characteristics[0]; result.reader = responses.Characteristics[0];
            if (poll.Status == GattCommunicationStatus.Success && poll.Characteristics.Count > 0) {
                result.reader = poll.Characteristics[0]; result.pollingV2 = true;
            }
            trace["readerHandle"] = result.reader.AttributeHandle;
            trace["writerHandle"] = result.writer.AttributeHandle;
            trace["readerProperties"] = result.reader.CharacteristicProperties.ToString();
            trace["connectionStatus"] = result.device.ConnectionStatus.ToString();
            trace["sessionStatus"] = result.connection.SessionStatus.ToString();
            // New phones expose READ specifically for the control channel. Do not
            // attempt CCCD first: some drivers hang there instead of returning ATT 3.
            if (result.reader.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
            {
                result.pollingResponses = true;
                step = "读取对方设备控制响应";
                var probe = await BluetoothWorker.AwaitAsync(result.reader.ReadValueAsync(BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(10));
                if (probe.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"对方设备兼容控制连接失败：{probe.Status}。");
            }
            else
            {
                result.reader.ValueChanged += result.ValueChanged;
                step = "订阅旧版对方设备通知";
                var subscription = await BluetoothWorker.AwaitAsync(result.reader.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify), TimeSpan.FromSeconds(10));
                if (subscription != GattCommunicationStatus.Success) throw new InvalidOperationException($"对方设备通知连接失败：{subscription}，请更新 Android 0.4.0 及以上版本。");
            }
            return result;
        }
        catch (TimeoutException ex) {
            result.Dispose();
            var failure = new TimeoutException($"连接超时：{step}。请保持对方设备 ShareLink 打开后重试。", ex);
            failure.Data["controlMode"] = result.ControlMode;
            failure.Data["gattTrace"] = trace;
            throw failure;
        }
        catch (Exception ex) {
            ex.Data["controlMode"] = result.ControlMode;
            ex.Data["gattTrace"] = trace;
            ex.Data["gattStep"] = step;
            result.Dispose(); throw;
        }
    }

    public Task<JsonElement> RequestAsync(object request, int timeoutSeconds = 15) => BluetoothWorker.RunAsync(() => RequestCoreAsync(request, timeoutSeconds));

    private async Task<JsonElement> RequestCoreAsync(object request, int timeoutSeconds)
    {
        await requestGate.WaitAsync();
        string requestStage="prepare"; int writeChunk=0,writeOffset=0,frameBytes=0,pdu=0;
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            int id = Interlocked.Increment(ref nextRequest);
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(request))!;
            fields["requestId"] = JsonSerializer.SerializeToElement(id);
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(fields);
            if (data.Length > 65536) throw new InvalidOperationException("控制消息超过长度限制。");
            var frame = new byte[data.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(frame, data.Length); data.CopyTo(frame, 4);
            var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (incomingGate) { waitingRequest = id; pending = response; }
            var deadline = DateTime.UtcNow.AddSeconds(pollingResponses ? Math.Max(timeoutSeconds, 60) : Math.Max(timeoutSeconds, 30));
            pdu=connection?.MaxPduSize ?? 23;
            int chunk = Math.Clamp(Math.Min(ControlWriteLimit, pdu - 3), 20, 512);
            frameBytes=frame.Length;
            for (int offset = 0; offset < frame.Length; offset += chunk)
            {
                requestStage="write";writeOffset=offset;writeChunk=Math.Min(chunk,frame.Length-offset);
                using var buffer = new DataWriter(); buffer.WriteBytes(frame.AsSpan(offset, writeChunk).ToArray());
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("对方设备控制消息发送超时。");
                var status = await BluetoothWorker.AwaitAsync(writer!.WriteValueAsync(buffer.DetachBuffer(), GattWriteOption.WriteWithResponse), remaining);
                if (status != GattCommunicationStatus.Success) throw new InvalidOperationException("蓝牙控制连接已中断。");
            }
            if (pollingResponses)
            {
                while (!response.Task.IsCompleted)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) throw new TimeoutException("对方设备兼容控制响应超时。");
                    requestStage="read";
                    var read = await BluetoothWorker.AwaitAsync(reader!.ReadValueAsync(BluetoothCacheMode.Uncached), remaining);
                    if (read.Status != GattCommunicationStatus.Success) throw new InvalidOperationException($"对方设备兼容控制读取失败：{read.Status}。");
                    using var buffer = DataReader.FromBuffer(read.Value);
                    var bytes = new byte[buffer.UnconsumedBufferLength]; buffer.ReadBytes(bytes);
                    if (!ControlFrames.IsIdleRead(bytes, pollingV2)) AcceptFragment(bytes); else await Task.Delay(40);
                }
            }
            var result = await response.Task.WaitAsync(deadline > DateTime.UtcNow ? deadline - DateTime.UtcNow : TimeSpan.FromMilliseconds(1));
            if (result.TryGetProperty("ok", out var ok) && !ok.GetBoolean()) throw new InvalidOperationException(MainWindow.Text(result, "error") is { Length: > 0 } error ? error : "对方设备返回失败响应，但未提供原因。");
            return result;
        }
        catch (Exception ex) { ex.Data["controlStage"]=requestStage;ex.Data["controlPdu"]=pdu;ex.Data["controlChunk"]=writeChunk;ex.Data["controlOffset"]=writeOffset;ex.Data["controlFrameBytes"]=frameBytes; Dispose(); throw; }
        finally { lock (incomingGate) { pending = null; } requestGate.Release(); }
    }

    private void ValueChanged(GattCharacteristic sender, GattValueChangedEventArgs args)
    {
        try
        {
            using var buffer = DataReader.FromBuffer(args.CharacteristicValue);
            var bytes = new byte[buffer.UnconsumedBufferLength]; buffer.ReadBytes(bytes);
            AcceptFragment(bytes);
        }
        catch (Exception ex) { lock (incomingGate) { incoming.Clear(); pending?.TrySetException(ex); } }
    }
    private void AcceptFragment(byte[] bytes)
    {
        lock (incomingGate)
            foreach (var message in incoming.Feed(bytes))
                if (MainWindow.Number(message, "requestId") == waitingRequest) pending?.TrySetResult(message);
    }
    private void ConnectionChanged(BluetoothLEDevice sender, object args)
    {
        if (sender.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
            lock (incomingGate) { Authenticated = false; incoming.Clear(); pending?.TrySetException(new InvalidOperationException("对方设备蓝牙连接已断开。")); }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposalStarted, 1) != 0) return; disposed = true;
        lock (incomingGate) { incoming.Clear(); pending?.TrySetException(new ObjectDisposedException(nameof(NearbySession))); }
        Authenticated = false;
        // WinRT teardown may wait for an outstanding driver request. Never do it
        // on the WPF thread, including the failure path after a timed-out operation.
        _ = BluetoothWorker.Run(() => {
            try { if (reader is not null && !pollingResponses) reader.ValueChanged -= ValueChanged; } catch { }
            try { if (device is not null) device.ConnectionStatusChanged -= ConnectionChanged; } catch { }
            try { if (connection is not null) { connection.MaintainConnection = false; connection.Dispose(); } } catch { }
            try { service?.Dispose(); } catch { }
            try { device?.Dispose(); } catch { }
        });
    }
}

internal sealed class ControlFrames
{
    private readonly List<byte> buffer = [];
    public void Clear() => buffer.Clear();
    public static bool IsIdleRead(byte[] bytes, bool markedIdle) => bytes.Length == 0 || (markedIdle && bytes.Length == 1 && bytes[0] == 0);
    public JsonElement[] Feed(byte[] fragment)
    {
        var messages = new List<JsonElement>();
        // Consume incrementally so two valid adjacent frames are allowed while
        // an attacker cannot force an allocation beyond one bounded message.
        foreach (byte b in fragment)
        {
            buffer.Add(b);
            if (buffer.Count < 4) continue;
            int length = BinaryPrimitives.ReadInt32BigEndian(buffer.Take(4).ToArray());
            if (length is < 1 or > 65536) { Clear(); throw new InvalidOperationException("对方设备控制消息长度无效。"); }
            if (buffer.Count < length + 4) continue;
            using var document = JsonDocument.Parse(buffer.Skip(4).ToArray());
            if (document.RootElement.ValueKind != JsonValueKind.Object) { Clear(); throw new InvalidOperationException("对方设备控制消息必须为对象。"); }
            messages.Add(document.RootElement.Clone()); Clear();
        }
        return messages.ToArray();
    }
    public static object DiagnosticChecks()
    {
        static byte[] Frame(int id)
        {
            byte[] body = Encoding.UTF8.GetBytes($"{{\"requestId\":{id},\"name\":\"对方设备\"}}");
            var frame = new byte[body.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(frame, body.Length); body.CopyTo(frame, 4); return frame;
        }
        var first = Frame(7); var parser = new ControlFrames();
        if (parser.Feed(first[..1]).Length != 0 || parser.Feed(first[1..3]).Length != 0 || parser.Feed(first[3..8]).Length != 0)
            throw new InvalidOperationException("分片消息被提前提交。");
        var complete = parser.Feed(first[8..]);
        if (complete.Length != 1 || MainWindow.Number(complete[0], "requestId") != 7) throw new InvalidOperationException("分片消息未正确重组。");
        var adjacent = parser.Feed(Frame(8).Concat(Frame(9)).ToArray());
        if (adjacent.Length != 2 || MainWindow.Number(adjacent[1], "requestId") != 9) throw new InvalidOperationException("连续消息重组失败。");
        foreach (int invalid in new[] { 0, 65537, -1 })
        {
            byte[] header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, invalid);
            bool refused = false; try { parser.Feed(header); } catch (InvalidOperationException) { refused = true; }
            if (!refused) throw new InvalidOperationException("无效消息长度未拒绝。");
        }
        parser.Feed(Frame(10)[..10]); parser.Clear();
        if (parser.Feed(Frame(11)).Length != 1) throw new InvalidOperationException("断开后的消息缓存未重置。");
        if (parser.Feed([]).Length != 0) throw new InvalidOperationException("空读取被当成完整响应。");
        byte[] largeBody = JsonSerializer.SerializeToUtf8Bytes(new { requestId = 42, name = string.Concat(Enumerable.Repeat("对方设备📁", 1200)) });
        byte[] large = new byte[largeBody.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(large, largeBody.Length); largeBody.CopyTo(large, 4);
        var polled = new List<JsonElement>();
        for (int offset = 0; offset < large.Length; offset += 20)
        {
            polled.AddRange(parser.Feed(large.AsSpan(offset, Math.Min(20, large.Length - offset)).ToArray()));
            if (parser.Feed([]).Length != 0) throw new InvalidOperationException("读取分片间的空值破坏了帧。");
        }
        if (polled.Count != 1 || MainWindow.Text(polled[0], "name") != string.Concat(Enumerable.Repeat("对方设备📁", 1200))) throw new InvalidOperationException("兼容读取的长 Unicode 响应未正确重组。");
        var cached = new NearbyPhone(1, "对方设备", -60, 1000, BluetoothAddressType.Random, "0102030405060708");
        if (NearbyBluetooth.MatchInvitation([cached], cached.InstanceId, 6000) != cached) throw new InvalidOperationException("对方设备邀请连接时错误丢弃刚停止广播的对方设备。");
        if (NearbyBluetooth.MatchInvitation([cached], cached.InstanceId, 16000) is not null) throw new InvalidOperationException("过期的对方设备邀请匹配仍被使用。");
        if (!IsIdleRead([0], true) || IsIdleRead([0], false) || IsIdleRead([0, 0, 0, 12], true) || IsIdleRead([(byte)'}'], true)) throw new InvalidOperationException("读取空闲标记与数据分片混淆。");
        var withIdle = Frame(53); parser.Feed(withIdle[..2]);
        if (!IsIdleRead([0], true)) parser.Feed([0]);
        var afterIdle = parser.Feed(withIdle[2..]);
        if (afterIdle.Length != 1 || MainWindow.Number(afterIdle[0], "requestId") != 53) throw new InvalidOperationException("空闲标记破坏了部分帧。");
        return new { passed = true, checks = 12, radioAccessed = false, cases = new[] { "fragmented-header-and-body", "adjacent-frames", "zero-length", "oversize", "negative-length", "disconnect-reset", "empty-poll", "unicode-long-poll-response", "invitation-after-advertising-pause", "expired-invitation-cache", "idle-marker-is-not-data", "idle-marker-preserves-partial-frame" } };
    }
}
