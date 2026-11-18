using System.Net;
using System.Text;
using ShareLink.BrowserSharing;
namespace ShareLink.BrowserShareProbe;
internal static class JoinedHotspotChecks
{
    internal static void Run(Action<string,bool> check)
    {
        Guid id=Guid.NewGuid(); string profile="ShareLink-session-"+Guid.NewGuid().ToString("N");
        var joined=new JoinedHotspotNetwork(id,profile,"SL测试123",IPAddress.Parse("192.168.137.1"));
        var local=IPAddress.Parse("192.168.137.22");
        var adapter=new ShareFirewallAddress(id,"Wi-Fi",true,true,false,local,24);
        var connection=new JoinedWifiConnection(true,profile,Encoding.UTF8.GetBytes(joined.Ssid));
        string program=Environment.ProcessPath!;
        ShareFirewallScope Scope(ShareFirewallAddress[] adapters,JoinedHotspotNetwork? proof,JoinedWifiConnection? current=null,IPAddress? address=null,int port=41000)
            =>BrowserShareFirewall.CreateScope(program,address??local,port,adapters,proof,_=>current??connection);
        bool Reject(Action action){try{action();return false;}catch(InvalidOperationException){return true;}catch(ArgumentException){return true;}}
        check("joined temporary hotspot accepts physical Wi-Fi with exact session evidence",Scope([adapter],joined) is {InterfaceName:"Wi-Fi",Subnet:"192.168.137.0/24",Port:41000});
        check("browser sharing continues to reject ordinary physical Wi-Fi",Reject(()=>Scope([adapter],null)));
        check("owner hotspot still accepts only its active direct adapter",Scope([adapter with{Direct=true}],null).Address==local.ToString());
        check("joined evidence cannot authorize Ethernet",Reject(()=>Scope([adapter with{Wireless=false}],joined)));
        check("joined evidence cannot authorize another Wi-Fi adapter",Reject(()=>Scope([adapter with{Id=Guid.NewGuid()}],joined)));
        check("inactive joined Wi-Fi cannot authorize access",Reject(()=>Scope([adapter with{Up=false}],joined)));
        check("address must belong to the verified adapter",Reject(()=>Scope([adapter],joined,address:IPAddress.Parse("192.168.137.23"))));
        check("a broad prefix cannot authorize client access",Reject(()=>Scope([adapter with{Prefix=8}],joined)));
        check("disconnected Wi-Fi fails closed",Reject(()=>Scope([adapter],joined,connection with{Connected=false})));
        check("manually chosen original Wi-Fi is preserved and rejected",Reject(()=>Scope([adapter],joined,connection with{Profile="Original user network"})));
        check("a previous session profile cannot authorize current session",Reject(()=>Scope([adapter],joined,connection with{Profile="ShareLink-session-"+Guid.NewGuid().ToString("N")})));
        check("SSID must match authenticated credentials byte for byte",Reject(()=>Scope([adapter],joined,connection with{Ssid=Encoding.UTF8.GetBytes("Other Wi-Fi")})));
        check("missing session prefix cannot authorize an ordinary WLAN profile",Reject(()=>Scope([adapter],joined with{Profile="User Wi-Fi"})));
        check("non-GUID suffix cannot impersonate a generated session profile",Reject(()=>Scope([adapter],joined with{Profile="ShareLink-session-"+new string('x',32)})));
        check("missing interface identity is rejected",Reject(()=>Scope([adapter],joined with{InterfaceId=Guid.Empty})));
        check("SSID length is bounded in UTF8 bytes",Reject(()=>Scope([adapter],joined with{Ssid=new string('界',11)})));
        check("owner must be in the actual client subnet",Reject(()=>Scope([adapter],joined with{Owner=IPAddress.Parse("10.1.2.1")})));
        check("client cannot claim the owner's address",Reject(()=>Scope([adapter],joined with{Owner=local})));
        check("owner loopback and unspecified endpoints are rejected",Reject(()=>Scope([adapter],joined with{Owner=IPAddress.Loopback}))&&Reject(()=>Scope([adapter],joined with{Owner=IPAddress.Any})));
        check("multicast and link-local client endpoints are rejected",Reject(()=>Scope([adapter with{Address=IPAddress.Parse("224.1.2.3")}],joined,address:IPAddress.Parse("224.1.2.3")))&&Reject(()=>Scope([adapter with{Address=IPAddress.Parse("169.254.1.2")}],joined,address:IPAddress.Parse("169.254.1.2"))));
        check("IPv6 and invalid port cannot authorize this IPv4 service",Reject(()=>Scope([adapter],joined,address:IPAddress.IPv6Loopback))&&Reject(()=>Scope([adapter],joined,port:0)));
        check("elevated entry rejects incomplete joined evidence",BrowserShareFirewall.RunHelper(["--configure-browser-firewall","1","1","192.168.137.22","41000",id.ToString()])==2);
        bool queryFailure=false;
        try{BrowserShareFirewall.CreateScope(program,local,41000,[adapter],joined,_=>throw new System.ComponentModel.Win32Exception(5));}catch(System.ComponentModel.Win32Exception){queryFailure=true;}
        check("wireless privacy denial cannot fall back to ordinary LAN",queryFailure);
    }
}
