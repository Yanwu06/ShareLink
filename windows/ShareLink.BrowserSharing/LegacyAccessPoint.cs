using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using Windows.Devices.WiFiDirect;

namespace ShareLink.BrowserSharing;

// Desktop Legacy AP: ordinary WLAN clients can join without BLE or ShareLink.
internal sealed class LegacyAccessPoint : IShareAccessPoint
{
    private readonly WiFiDirectAdvertisementPublisher publisher = new();
    private readonly WiFiDirectConnectionListener listener = new();
    private readonly ConcurrentDictionary<string, WiFiDirectDevice> peers = new();
    private readonly ConcurrentDictionary<string, string> names = new();
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool disposed;
    private bool startRequested;
    private Task? disposal;
    public string Ssid { get; } = "SL" + RandomNumberGenerator.GetInt32(1000).ToString("D3");
    public string Password { get; } = "SL" + RandomNumberGenerator.GetInt32(1000000).ToString("D6");
    public string Status { get; private set; } = "尚未启动";
    public string? InterfaceId { get; private set; }
    public string? InterfaceName { get; private set; }
    public string Mode => "兼容热点 · 频段由系统选择";
    public int ConnectedCount { get { lock (peers) return peers.Values.Count(p => p.ConnectionStatus == WiFiDirectConnectionStatus.Connected); } }
    public string[] PeerAddresses => peers.Values.SelectMany(p => p.GetConnectionEndpointPairs())
        .Select(p => p.RemoteHostName.CanonicalName).Distinct().ToArray();
    public string DeviceLabel(string address) => names.TryGetValue(address, out string? name) ? name : PeerIdentity.Mac(address, InterfaceId) ?? address;

    public async Task StartAsync(CancellationToken token)
    {
        if (DirectAddresses().Any()) throw new InvalidOperationException("已有活动 Wi-Fi Direct 网络，请先结束原有分享。");
        publisher.Advertisement.IsAutonomousGroupOwnerEnabled = true;
        publisher.Advertisement.LegacySettings.IsEnabled = true;
        publisher.Advertisement.LegacySettings.Ssid = Ssid;
        publisher.Advertisement.LegacySettings.Passphrase.Password = Password;
        listener.ConnectionRequested += Accept;
        publisher.StatusChanged += Changed;
        startRequested = true;
        publisher.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(25), token);
    }
    private void Changed(WiFiDirectAdvertisementPublisher sender, WiFiDirectAdvertisementPublisherStatusChangedEventArgs args)
    {
        Status = $"{args.Status}: {args.Error}";
        if (args.Status == WiFiDirectAdvertisementPublisherStatus.Started) started.TrySetResult();
        if (args.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
            started.TrySetException(new InvalidOperationException("临时 Wi-Fi 启动失败：" + args.Error));
    }
    private async void Accept(WiFiDirectConnectionListener sender, WiFiDirectConnectionRequestedEventArgs args)
    {
        try
        {
            using var request = args.GetConnectionRequest();
            var device = await WiFiDirectDevice.FromIdAsync(request.DeviceInformation.Id);
            if (device is null) throw new InvalidOperationException("系统未返回 Wi-Fi 客户端。");
            bool keep;
            lock (peers)
            {
                keep = !disposed && peers.TryAdd(device.DeviceId, device);
            }
            if (!keep) device.Dispose();
            else {
                string name = request.DeviceInformation.Name;
                if (!string.IsNullOrWhiteSpace(name) && !name.Equals(Ssid, StringComparison.OrdinalIgnoreCase) && !name.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase))
                    foreach (var endpoint in device.GetConnectionEndpointPairs()) names[endpoint.RemoteHostName.CanonicalName] = name;
                device.ConnectionStatusChanged += Disconnected;
            }
        }
        catch (Exception ex) { Status = "客户端连接失败：" + ex.Message; }
    }
    private void Disconnected(WiFiDirectDevice device, object args) {
        if (device.ConnectionStatus != WiFiDirectConnectionStatus.Disconnected) return;
        WiFiDirectDevice? removed;
        lock (peers) peers.TryRemove(device.DeviceId,out removed);
        if (removed is not null) {
            removed.ConnectionStatusChanged -= Disconnected;
            _ = Task.Run(removed.Dispose);
        }
    }
    public async Task<IPAddress> WaitForAddressAsync(CancellationToken token)
    {
        for (int attempt = 0; attempt < 120; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (publisher.Status == WiFiDirectAdvertisementPublisherStatus.Aborted)
                throw new InvalidOperationException(Status);
            var address = DirectAddresses().FirstOrDefault();
            if (address != default)
            {
                InterfaceId = address.Adapter.Id; InterfaceName = address.Adapter.Name;
                return address.Address;
            }
            await Task.Delay(500, token);
        }
        throw new TimeoutException("临时 Wi-Fi 已广播，但系统尚未分配可用 IPv4 地址。");
    }
    internal static IEnumerable<(NetworkInterface Adapter, IPAddress Address)> DirectAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.Description.Contains("Wi-Fi Direct Virtual Adapter", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
                .Select(a => (n, a.Address)));
    public async ValueTask DisposeAsync()
    {
        lock (peers)
        {
            if (disposal is null) {
                disposed = true;
                var connected = peers.Values.ToArray();
                peers.Clear();
                // Repeated cleanup waits for the same native release, including
                // after a timeout; it must never claim a pending release finished.
                disposal = Task.Run(() => {
                    publisher.StatusChanged -= Changed; listener.ConnectionRequested -= Accept;
                    foreach (var peer in connected) { peer.ConnectionStatusChanged -= Disconnected; peer.Dispose(); }
                    if (startRequested) publisher.Stop();
                });
            }
        }
        // Native shutdown can wait for connection events. Never hold the peer lock
        // or the WPF dispatcher while releasing WinRT network resources.
        await disposal.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
