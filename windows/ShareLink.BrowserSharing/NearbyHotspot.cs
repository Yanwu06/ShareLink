using System.Net;
namespace ShareLink.BrowserSharing;
// Owns only our legacy AP; client access requires the exact joined session profile.
// Never changes ICS, system mobile hotspots or an arbitrary LAN interface.
public sealed class NearbyHotspot : IAsyncDisposable
{
    private readonly LegacyAccessPoint ap = new();
    public string Ssid => ap.Ssid;
    public string Password => ap.Password;
    public Task StartAsync(CancellationToken token) => ap.StartAsync(token);
    public Task<IPAddress> WaitForAddressAsync(CancellationToken token) => ap.WaitForAddressAsync(token);
    public Task EnsureAccessAsync(IPAddress address,int port,string output,Func<bool> confirm,CancellationToken token) => BrowserShareFirewall.EnsureAsync(address,port,output,confirm,token);
    public static Task EnsureClientAccessAsync(IPAddress address,int port,JoinedHotspotNetwork joined,string output,Func<bool> confirm,CancellationToken token) => BrowserShareFirewall.EnsureAsync(address,port,output,confirm,token,joined);
    public ValueTask DisposeAsync() => ap.DisposeAsync();
}
