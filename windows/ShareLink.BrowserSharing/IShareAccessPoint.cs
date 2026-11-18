using System.Net;

namespace ShareLink.BrowserSharing;

internal interface IShareAccessPoint : IAsyncDisposable
{
    string Ssid { get; }
    string Password { get; }
    string Status { get; }
    string Mode { get; }
    string? InterfaceId { get; }
    string? InterfaceName { get; }
    int ConnectedCount { get; }
    string DeviceLabel(string address);
    Task StartAsync(CancellationToken token);
    Task<IPAddress> WaitForAddressAsync(CancellationToken token);
}
