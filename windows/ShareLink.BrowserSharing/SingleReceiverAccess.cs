using System.Net;

namespace ShareLink.BrowserSharing;

// One receiver owns this server session. Keep ownership until sharing ends so
// a disconnected browser's progress cannot become another device's progress.
internal sealed class SingleReceiverAccess
{
    private readonly object gate = new();
    private IPAddress? admitted;
    public string? Address { get { lock (gate) return admitted?.ToString(); } }
    public bool TryAdmit(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (gate) {
            admitted ??= address;
            return admitted.Equals(address);
        }
    }
}
