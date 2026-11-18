using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace ShareLink.BrowserSharing;

// Supplied only after an authenticated peer's credentials were joined using our
// fresh per-session WLAN profile. Never contains a password or profile XML.
public sealed record JoinedHotspotNetwork(Guid InterfaceId, string Profile, string Ssid, IPAddress Owner)
{
    internal const string ProfilePrefix = "ShareLink-session-";
    internal void Validate()
    {
        if (InterfaceId == Guid.Empty || !Profile.StartsWith(ProfilePrefix, StringComparison.Ordinal) ||
            !Guid.TryParseExact(Profile[ProfilePrefix.Length..], "N", out _) || Encoding.UTF8.GetByteCount(Ssid) is <1 or >32 ||
            Owner.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(Owner) || Owner.Equals(IPAddress.Any) || Owner.GetAddressBytes()[0] is 169 or >=224)
            throw new InvalidOperationException("缺少本次临时热点的接入记录，已保留原网络。");
    }
    internal void VerifyConnection(JoinedWifiConnection connection)
    {
        Validate();
        if (!connection.Connected || connection.Profile != Profile || !connection.Ssid.AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(Ssid)))
            throw new InvalidOperationException("网卡已离开本次临时 Wi-Fi，请重新连接设备后重试。");
    }
    internal static JoinedWifiConnection ReadConnection(Guid id)
    {
        uint result = WlanOpenHandle(2, IntPtr.Zero, out _, out var client);
        if (result != 0) throw new Win32Exception((int)result, "无法核验本次临时 Wi-Fi。");
        try {
            result = WlanQueryInterface(client, ref id, 7, IntPtr.Zero, out var size, out var data, out _);
            if (result != 0) throw new Win32Exception((int)result, "无法核验本次临时 Wi-Fi，请检查无线信息访问权限。");
            try {
                // WLAN_CONNECTION_ATTRIBUTES: state, mode, WCHAR profile[256],
                // WLAN_ASSOCIATION_ATTRIBUTES beginning with DOT11_SSID.
                if (size < 604) throw new InvalidOperationException("系统返回的 Wi-Fi 信息不完整。");
                int length = Marshal.ReadInt32(data, 520);
                if (length is <1 or >32) throw new InvalidOperationException("当前 Wi-Fi 名称无效。");
                byte[] ssid = new byte[length]; Marshal.Copy(IntPtr.Add(data, 524), ssid, 0, length);
                return new(Marshal.ReadInt32(data) == 1, Marshal.PtrToStringUni(IntPtr.Add(data, 8), 256)!.TrimEnd('\0'), ssid);
            } finally { WlanFreeMemory(data); }
        } finally { WlanCloseHandle(client, IntPtr.Zero); }
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int type);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
}
internal sealed record JoinedWifiConnection(bool Connected, string Profile, byte[] Ssid);
internal sealed record ShareFirewallAddress(Guid Id, string Name, bool Up, bool Wireless, bool Direct, IPAddress Address, int Prefix);
