using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace ShareLink;

internal static class ConnectionPolicy
{
    // A failed encrypted request is not evidence that the peer lacks hotspot reuse.
    internal static async Task<JsonElement?> QueryPersonalHotspotAsync(Func<Task<JsonElement>> query)
    {
        try { return await query(); }
        catch (InvalidOperationException error) when (error.Message == "不支持的加密控制操作") { return null; }
    }

    internal static long OfferedBytes(JsonElement offer)
    {
        static long Size(JsonElement element, string field) => element.ValueKind == JsonValueKind.Object &&
            element.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetInt64(out long bytes) && bytes >= 0 ? bytes : 0;
        long total = Size(offer, "totalBytes");
        if (total == 0) total = Size(offer, "total");
        if (total > 0) return total;
        if (offer.ValueKind != JsonValueKind.Object || !offer.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array) return 0;
        foreach (var file in files.EnumerateArray())
        {
            long bytes = Size(file, "size");
            if (bytes > long.MaxValue - total) return long.MaxValue;
            total += bytes;
        }
        return total;
    }

    internal static Peer? UniquePresentationPeer(NearbyPhone phone, IEnumerable<Peer> peers)
    {
        if (phone.FingerprintPrefix.Length != 16 || !phone.FingerprintPrefix.All(Uri.IsHexDigit)) return null;
        var matches = peers.Where(p => p.Fingerprint.Length == 64 && p.Fingerprint.StartsWith(phone.FingerprintPrefix, StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p.Fingerprint, StringComparer.OrdinalIgnoreCase).ToArray();
        return matches.Length == 1 ? matches[0].First() : null;
    }
    internal static bool SupportsAutomaticHotspot(JsonElement status)
    {
        if (status.ValueKind != JsonValueKind.Object) return false;
        if (status.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object && capabilities.TryGetProperty("automaticHotspot", out var flag))
            return flag.ValueKind == JsonValueKind.True;
        // Older Android releases have no capabilities object. Harmony reports
        // unsupported; an absent/unknown state must never authorize a switch.
        return MainWindow.Text(status, "hotspotState") is "idle" or "ready" or "starting" or "failed" or "declined" or "awaiting_consent";
    }

    internal static string[] OrderEndpoints(IEnumerable<string> hints, IEnumerable<string> preferred)
    {
        var first = preferred.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var networks = new List<(byte[] Address, byte[] Mask)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
                foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(i => i.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(i.Address)))
                    networks.Add((ip.Address.GetAddressBytes(), ip.IPv4Mask.GetAddressBytes()));
        }
        catch (NetworkInformationException) { }
        return hints.Where(h => IPAddress.TryParse(h, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip) && !ip.Equals(IPAddress.Any))
            .Distinct().OrderBy(h => first.Contains(h) ? 0 : SameSubnet(IPAddress.Parse(h).GetAddressBytes(), networks) ? 1 : 2).Take(4).ToArray();
    }

    private static bool SameSubnet(byte[] address, IEnumerable<(byte[] Address, byte[] Mask)> networks) =>
        networks.Any(n => Enumerable.Range(0, 4).All(i => (address[i] & n.Mask[i]) == (n.Address[i] & n.Mask[i])));
}
