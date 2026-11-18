using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace ShareLink.BrowserSharing;

internal static class PeerIdentity
{
    public static string Display(string label) {
        string normalized = label.Replace('-', ':').ToUpperInvariant();
        return System.Text.RegularExpressions.Regex.IsMatch(normalized, @"^([0-9A-F]{2}:){5}[0-9A-F]{2}$")
            ? "已连接设备的MAC地址：" + normalized : label;
    }
    internal static string AuthorizedDisplay(string? address, Func<string, string?> lookup)
    {
        if (address is null) return "当前无可下载设备连接";
        string? mac = lookup(address)?.Trim().Replace('-', ':').ToUpperInvariant();
        if (mac is null || !System.Text.RegularExpressions.Regex.IsMatch(mac, @"^([0-9A-F]{2}:){5}[0-9A-F]{2}$") ||
            mac is "00:00:00:00:00:00" or "02:00:00:00:00:00" or "FF:FF:FF:FF:FF:FF")
            return "可下载设备已连接，暂无法读取 MAC 地址";
        return Display(mac);
    }
    public static string? Mac(string address, string? interfaceId)
    {
        try {
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return null;
            var adapter = NetworkInterface.GetAllNetworkInterfaces().SingleOrDefault(n => n.Id.Equals(interfaceId, StringComparison.OrdinalIgnoreCase));
            if (adapter is null) return null;
            var row = new Neighbor { Family = 2, Address = BitConverter.ToUInt32(ip.GetAddressBytes()), Index = (uint)adapter.GetIPProperties().GetIPv4Properties()!.Index, Physical = new byte[32] };
            if (GetIpNetEntry2(ref row) != 0 || row.Length != 6) return null;
            string mac = string.Join(":", row.Physical.Take(6).Select(b => b.ToString("X2")));
            return mac == "00:00:00:00:00:00" ? null : mac;
        } catch { return null; }
    }
    [StructLayout(LayoutKind.Explicit, Size = 88)]
    private struct Neighbor {
        [FieldOffset(0)] public ushort Family;
        [FieldOffset(4)] public uint Address;
        [FieldOffset(28)] public uint Index;
        [FieldOffset(32)] public ulong Luid;
        [FieldOffset(40), MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Physical;
        [FieldOffset(72)] public uint Length;
    }
    [DllImport("iphlpapi.dll")] private static extern uint GetIpNetEntry2(ref Neighbor row);
}
