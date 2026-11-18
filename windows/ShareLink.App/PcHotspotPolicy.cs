using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
namespace ShareLink;
internal static class PcHotspotPolicy
{
    internal static bool HasFiveGhz(JsonElement report)
    {
        static bool Five(JsonElement link) => link.ValueKind==JsonValueKind.Object && link.TryGetProperty("connected",out var connected)&&connected.ValueKind==JsonValueKind.True && link.TryGetProperty("frequencyMhz",out var frequency)&&frequency.ValueKind==JsonValueKind.Number&&frequency.TryGetDouble(out double mhz)&&mhz>=4900&&mhz<5925;
        if(report.ValueKind!=JsonValueKind.Object)return false;
        if(report.TryGetProperty("interfaces",out var interfaces)&&interfaces.ValueKind==JsonValueKind.Array&&interfaces.EnumerateArray().Any(Five))return true;
        return report.TryGetProperty("current",out var current)&&Five(current);
    }
    // The existing default is the BLE server. Only an exclusively local 5GHz connection changes that choice.
    internal static bool PreferLocal(JsonElement local,JsonElement remote)=>HasFiveGhz(local)&&!HasFiveGhz(remote);
    internal static (string Ssid,string Password,IPAddress Owner,int Port) Validate(JsonElement credentials,string pairedFingerprint)
    {
        if(!MainWindow.Text(credentials,"fingerprint").Equals(pairedFingerprint,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("热点身份与加密连接不一致。");
        string ssid=MainWindow.Text(credentials,"ssid"),password=MainWindow.Text(credentials,"password");
        if(Encoding.UTF8.GetByteCount(ssid) is <1 or >32||password.Length is <8 or >63||MainWindow.Text(credentials,"securityType")!="wpa2-psk")throw new InvalidOperationException("热点参数不完整。");
        var addresses=credentials.GetProperty("addresses");
        if(addresses.ValueKind!=JsonValueKind.Array||addresses.GetArrayLength()!=1||!IPAddress.TryParse(addresses[0].GetString(),out var owner)||owner.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(owner)||owner.Equals(IPAddress.Any))throw new InvalidOperationException("热点未提供唯一的直连地址。");
        int port=(int)MainWindow.Number(credentials,"port");if(port is <1 or >65535)throw new InvalidOperationException("热点文件服务端口无效。");
        return(ssid,password,owner,port);
    }
}
