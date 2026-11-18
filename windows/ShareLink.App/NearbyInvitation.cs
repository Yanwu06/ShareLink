using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace ShareLink;

// Invitations only ask the PC to open the existing encrypted phone control channel.
// They carry no file acceptance, certificate, token or hotspot password.
internal sealed class NearbyInvitation(NearbyBluetooth discovery) : IDisposable
{
    internal static readonly Guid ServiceUuid = Guid.Parse("7c3d0010-6b4a-4f9d-9a31-534c4e4b0001");
    private static readonly Guid InviteUuid = Guid.Parse("7c3d0011-6b4a-4f9d-9a31-534c4e4b0001");
    private static readonly Guid NameUuid = Guid.Parse("7c3d0012-6b4a-4f9d-9a31-534c4e4b0001");
    private GattServiceProvider? provider;
    private GattLocalCharacteristic? invitation;
    private bool disposed;
    private bool discoverable=true;
    private AdvertisementVisibility? advertising;
    private readonly SemaphoreSlim gate = new(1, 1);
    private long lastInvite;
    public string AdvertisementState { get; private set; } = "not-started";
    public string Status { get; private set; } = "电脑蓝牙可发现服务尚未开启";
    public Func<NearbyPhone, bool>? Ready { get; set; }
    public Action<NearbyPhone>? Accepted { get; set; }

    public async Task StartAsync(Func<GattServiceProvider, Task>? configureSharedService = null)
    {
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (adapter is null || !adapter.IsPeripheralRoleSupported) { Status = "此电脑蓝牙不支持被手机发现，请由电脑选择手机连接。"; return; }
            var created = await GattServiceProvider.CreateAsync(ServiceUuid).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (created.Error != BluetoothError.Success) throw new InvalidOperationException($"创建电脑广播服务：{created.Error}");
            if (disposed) { created.ServiceProvider.StopAdvertising(); return; }
            provider = created.ServiceProvider;
            advertising=new(visible=>{GattAdvertisingPolicy.Apply(provider,visible);});
            var write = await provider.Service.CreateCharacteristicAsync(InviteUuid, new GattLocalCharacteristicParameters {
                CharacteristicProperties = GattCharacteristicProperties.Write, WriteProtectionLevel = GattProtectionLevel.Plain
            });
            if (write.Error != BluetoothError.Success) throw new InvalidOperationException($"创建连接请求：{write.Error}");
            invitation = write.Characteristic; invitation.WriteRequested += WriteRequested;
            using var name = new DataWriter(); name.WriteBytes(Encoding.UTF8.GetBytes(Environment.MachineName));
            var read = await provider.Service.CreateCharacteristicAsync(NameUuid, new GattLocalCharacteristicParameters {
                CharacteristicProperties = GattCharacteristicProperties.Read, ReadProtectionLevel = GattProtectionLevel.Plain, StaticValue = name.DetachBuffer()
            });
            if (read.Error != BluetoothError.Success) throw new InvalidOperationException($"创建电脑名称：{read.Error}");
            if (configureSharedService is not null) await configureSharedService(provider);
            provider.AdvertisementStatusChanged += (_, args) => {
                AdvertisementState = args.Status.ToString();
                Status = args.Error == BluetoothError.Success && args.Status is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData
                    ? "手机可通过蓝牙发现此电脑" : $"手机邀请广播状态：{args.Status} / {args.Error}；仍可由电脑选择手机连接。";
            };
            if (!disposed) SetDiscoverable(discoverable);
        }
        catch (Exception ex) { Status = $"电脑广播未开启：{(string.IsNullOrWhiteSpace(ex.Message) ? $"0x{ex.HResult:X8}" : ex.Message)}；仍可由电脑选择手机连接。"; }
    }

    private async void WriteRequested(GattLocalCharacteristic sender, GattWriteRequestedEventArgs args)
    {
        var deferral = args.GetDeferral();
        GattWriteRequest? request = null;
        bool acquired = false;
        NearbyPhone? accepted = null;
        try
        {
            request = await args.GetRequestAsync();
            if (request is null) return;
            if (disposed || request.Offset != 0 || request.Value.Length != 8 || !await gate.WaitAsync(0)) { request.RespondWithProtocolError(0x0e); return; }
            acquired = true;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (now - lastInvite < 3000) { request.RespondWithProtocolError(0x0e); return; }
            using var data = DataReader.FromBuffer(request.Value);
            var bytes = new byte[8]; data.ReadBytes(bytes);
            string instance = Convert.ToHexString(bytes);
            NearbyPhone? phone = null;
            // Active scanning can deliver the phone's scan response shortly after its invitation.
            for (int attempt = 0; attempt < 12 && !disposed; attempt++)
            {
                phone = discovery.FindInvitation(instance);
                if (phone is not null) break;
                await Task.Delay(250);
            }
            if (disposed || phone is null) { Status = "手机连接请求未接入：未取得当前手机广播，请在手机重试附近分享。"; request.RespondWithProtocolError(0x0e); return; }
            if (Ready is null || !Ready(phone)) { Status = "正在处理另一项连接或传输，手机连接请求可稍后重试。"; request.RespondWithProtocolError(0x0e); return; }
            lastInvite = now; Status = "手机连接请求已接入"; request.Respond();
            accepted = phone;
        }
        catch { try { request?.RespondWithProtocolError(0x0e); } catch { } }
        finally { if (acquired) gate.Release(); deferral.Complete(); }
        // The phone must receive its write acknowledgement and release its
        // invitation connection before the PC attempts to connect back.
        if (accepted is not null && !disposed) Accepted?.Invoke(accepted);
    }

    public void SetDiscoverable(bool visible)
    {
        discoverable=visible;if(!disposed){advertising?.Set(visible);if(!visible)Status="对外发现已暂停";}
    }
    public void Dispose()
    {
        disposed = true;
        if (invitation is not null) invitation.WriteRequested -= WriteRequested;
        try { provider?.StopAdvertising(); } catch { }
        Ready = null; Accepted = null;
    }
}
