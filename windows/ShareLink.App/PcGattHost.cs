using System.Buffers.Binary;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
namespace ShareLink;

// One explicitly owned GATT session; unrelated centrals cannot read queued
// replies, take over an established channel or exceed bounded control frames.
internal sealed class PcGattHost : IAsyncDisposable
{
    public Guid AdvertisedService => PcServiceIdentity.Create(protocol.LocalFingerprint);
    private readonly PcControlProtocol protocol;
    private readonly object gate = new();
    private readonly SemaphoreSlim requests = new(1, 1);
    private readonly ControlFrames incoming = new();
    private readonly Queue<byte[]> responses = new();
    private string owner = "";
    private int offset, queued;
    private bool disposed;
    private int readEvents,writeEvents,readReplies,writeReplies;
    private string lastRead="none",lastWrite="none",lastCommand="none";
    internal object Diagnostics { get { lock(gate)return new { disposed,hasOwner=owner.Length>0,queued,responseFrames=responses.Count,responseOffset=offset,readEvents,writeEvents,readReplies,writeReplies,lastRead,lastWrite,lastCommand }; } }
    private GattServiceProvider? provider;
    private bool ownsAdvertisement;
    private bool discoverable=true;
    private AdvertisementVisibility? advertising;
    public bool SharedAdvertisement => provider is not null && !ownsAdvertisement;
    private GattLocalCharacteristic? writer, reader, poll;
    public string AdvertisementState { get; private set; } = "not-started";
    public string Status { get; private set; } = "正在准备电脑蓝牙接收…";
    public event Action<string>? Changed;
    public PcGattHost(PcControlProtocol protocol) { this.protocol = protocol; }
    private void Publish(string status) { Status = status; Changed?.Invoke(status); }
    public async Task<bool> StartAsync(GattServiceProvider? sharedProvider = null)
    {
        var adapter = await BluetoothAdapter.GetDefaultAsync();
        if (adapter is null || !adapter.IsPeripheralRoleSupported) { Publish("此电脑不支持蓝牙接收角色；仍可由此电脑选择附近设备连接。"); return false; }
        ownsAdvertisement = sharedProvider is null;
        if (sharedProvider is not null) provider = sharedProvider;
        else {
            var made = await GattServiceProvider.CreateAsync(AdvertisedService);
            if (made.Error != BluetoothError.Success) throw new InvalidOperationException("创建电脑接收服务失败：" + made.Error);
            provider = made.ServiceProvider;
        }
        async Task<GattLocalCharacteristic> Create(Guid id, GattCharacteristicProperties properties) {
            var c = await provider.Service.CreateCharacteristicAsync(id, new GattLocalCharacteristicParameters { CharacteristicProperties = properties, ReadProtectionLevel = GattProtectionLevel.Plain, WriteProtectionLevel = GattProtectionLevel.Plain });
            if (c.Error != BluetoothError.Success) throw new InvalidOperationException("创建电脑控制接口失败：" + c.Error);
            return c.Characteristic;
        }
        using (var nameData = new DataWriter()) {
            nameData.WriteBytes(PcDeviceName.Encode(protocol.LocalName));
            var named = await provider.Service.CreateCharacteristicAsync(PcDeviceName.Characteristic,
                new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read,
                    ReadProtectionLevel = GattProtectionLevel.Plain, StaticValue = nameData.DetachBuffer() });
            if (named.Error != BluetoothError.Success) throw new InvalidOperationException("创建设备名称接口失败：" + named.Error);
        }
        using (var fingerprintData = new DataWriter()) {
            fingerprintData.WriteBytes(System.Text.Encoding.ASCII.GetBytes(protocol.LocalFingerprint));
            var fingerprint = await provider.Service.CreateCharacteristicAsync(PcDeviceName.FingerprintCharacteristic,
                new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read, ReadProtectionLevel = GattProtectionLevel.Plain, StaticValue = fingerprintData.DetachBuffer() });
            if (fingerprint.Error != BluetoothError.Success) throw new InvalidOperationException("创建设备展示身份接口失败：" + fingerprint.Error);
        }
        writer = await Create(NearbyBluetooth.RequestUuid, GattCharacteristicProperties.Write);
        reader = await Create(NearbyBluetooth.ResponseUuid, GattCharacteristicProperties.Read);
        poll = await Create(NearbyBluetooth.PollResponseUuid, GattCharacteristicProperties.Read);
        writer.WriteRequested += Write; reader.ReadRequested += Read; poll.ReadRequested += Read;
        provider.AdvertisementStatusChanged += Advertising;
        if(ownsAdvertisement){advertising=new(visible=>{GattAdvertisingPolicy.Apply(provider,visible);});if(!disposed)SetDiscoverable(discoverable);}
        return true;
    }
    private void Advertising(GattServiceProvider p, GattServiceProviderAdvertisementStatusChangedEventArgs e)
    {
        AdvertisementState = e.Status.ToString();
        Publish(e.Error == BluetoothError.Success && e.Status is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData
            ? "电脑蓝牙接收已开启，等待附近电脑连接。" : "电脑广播状态：" + e.Status + " / " + e.Error);
    }
    private async void Read(GattLocalCharacteristic c, GattReadRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        Interlocked.Increment(ref readEvents);lastRead="request";
        try {
            var r = await args.GetRequestAsync(); if (r is null) {lastRead="null";return;}
            lock (gate) {
                if (disposed || r.Offset != 0 || owner.Length > 0 && owner != args.Session.DeviceId.Id) { lastRead="denied";r.RespondWithProtocolError(0x08); return; }
                int count = responses.Count == 0 ? 0 : Math.Min(Math.Clamp((int)args.Session.MaxPduSize - 3, 20, 512), responses.Peek().Length - offset);
                byte[] chunk = count == 0 ? [0] : responses.Peek().AsSpan(offset, count).ToArray();
                using var data = new DataWriter(); data.WriteBytes(chunk); r.RespondWithValue(data.DetachBuffer());readReplies++;lastRead=$"ok:{chunk.Length}";
                if (count > 0) { offset += count; if (offset == responses.Peek().Length) { responses.Dequeue(); offset = 0; } }
                ReleaseOwnerIfDrained();
            }
        } catch (Exception ex) { lastRead=$"exception:{ex.GetType().Name}:0x{ex.HResult:X8}";Publish("读取电脑控制响应失败：" + ex.Message); }
        finally { deferral.Complete(); }
    }
    private async void Write(GattLocalCharacteristic c, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        Interlocked.Increment(ref writeEvents);lastWrite="request";
        try {
            var r = await args.GetRequestAsync(); if (r is null) {lastWrite="null";return;}
            lock (gate) {
                ReleaseOwnerIfDrained();
                if (disposed || r.Offset != 0 || r.Value.Length > 512 || queued >= 8 || owner.Length > 0 && owner != args.Session.DeviceId.Id) { lastWrite="denied";r.RespondWithProtocolError(0x08); return; }
                using var data = DataReader.FromBuffer(r.Value); byte[] bytes = new byte[data.UnconsumedBufferLength]; data.ReadBytes(bytes);
                var messages = incoming.Feed(bytes);
                if (queued + messages.Length > 8 || messages.Any(m => !m.TryGetProperty("requestId", out var id) || !id.TryGetInt32(out int number) || number < 0)) { incoming.Clear(); r.RespondWithProtocolError(0x0d); return; }
                owner = args.Session.DeviceId.Id;
                foreach (var message in messages) { queued++; _ = RespondAsync(message); }
                r.Respond();writeReplies++;lastWrite=$"ok:{r.Value.Length}";
            }
        } catch (Exception ex) { lastWrite=$"exception:{ex.GetType().Name}:0x{ex.HResult:X8}";Publish("电脑控制写入失败：" + ex.Message); }
        finally { deferral.Complete(); }
    }
    private void ReleaseOwnerIfDrained()
    {
        if (responses.Count == 0 && queued == 0 && protocol.ReleaseRequested) { owner = ""; incoming.Clear(); protocol.AcknowledgeRelease(); }
    }
    private async Task RespondAsync(JsonElement message)
    {
        await Task.Yield(); await requests.WaitAsync();
        try {
            lastCommand=MainWindow.Text(message,"op");
            var response = await protocol.RequestAsync(message);
            var fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(response.GetRawText())!; fields["requestId"] = message.GetProperty("requestId");
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(fields); if (body.Length > 65536) throw new InvalidOperationException("控制响应过长");
            byte[] frame = new byte[body.Length + 4]; BinaryPrimitives.WriteInt32BigEndian(frame, body.Length); body.CopyTo(frame, 4);
            lock (gate) { if (!disposed) { if (responses.Sum(f => f.Length) + frame.Length > 131080) throw new InvalidOperationException("控制响应队列已满"); responses.Enqueue(frame); } }
        } catch (Exception ex) { Publish("电脑加密检查失败：" + ex.Message); }
        finally { lock (gate) queued--; requests.Release(); }
    }
    public void SetDiscoverable(bool visible)
    {
        discoverable=visible;if(!disposed){if(ownsAdvertisement)advertising?.Set(visible);if(!visible)Status="对外发现已暂停";}
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; }
        try { if (ownsAdvertisement) provider?.StopAdvertising(); } catch { }
        if (writer is not null) writer.WriteRequested -= Write;
        if (reader is not null) reader.ReadRequested -= Read;
        if (poll is not null) poll.ReadRequested -= Read;
        if (provider is not null) provider.AdvertisementStatusChanged -= Advertising;
        await requests.WaitAsync(); try { await protocol.DisposeAsync(); } finally { requests.Release(); }
    }
}
