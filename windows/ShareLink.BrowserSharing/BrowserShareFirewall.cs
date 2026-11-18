using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShareLink.BrowserSharing;

internal sealed record ShareFirewallScope(string Program, string Address, int Port, string Subnet, string InterfaceName)
{
    internal string RuleName => "ShareLink-LocalDownload-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Program.ToUpperInvariant()+"|"+Address+"|"+Port)))[..24];
}
internal sealed record ShareFirewallRule(string Name, string Program, bool Enabled, int Direction, int Action, int Protocol, string Ports, string Local, string Remote, int Profiles, string InterfaceTypes, string[] Interfaces, string Service, string? InternalId=null)
{
    internal string Key => InternalId ?? Name;
    internal bool Generated => InternalId is not null && GeneratedFirewallBlocks.ValidId(InternalId,Program);
}
internal sealed record ShareFirewallState(bool Ready, bool PolicyBlocked, int Profiles, ShareFirewallRule[] Rules, string[] GeneratedBlocks, string? Problem = null);
internal sealed class ShareFirewallException(string message):IOException(message);

public static class BrowserShareFirewall
{
    internal const string HelperSwitch = "--configure-browser-firewall";
    internal const int AllProfiles = 0x7fffffff;
    internal static ShareFirewallScope CreateScope(string program, IPAddress address, int port, JoinedHotspotNetwork? joined = null)
    {
        var addresses=NetworkInterface.GetAllNetworkInterfaces().Where(n=>n.OperationalStatus==OperationalStatus.Up).SelectMany(n=>n.GetIPProperties().UnicastAddresses
            .Where(a=>a.Address.AddressFamily==AddressFamily.InterNetwork).Select(a=>new ShareFirewallAddress(
                Guid.TryParse(n.Id,out var id)?id:Guid.Empty,n.Name,n.OperationalStatus==OperationalStatus.Up,
                n.NetworkInterfaceType==NetworkInterfaceType.Wireless80211,
                n.Description.Contains("Wi-Fi Direct Virtual Adapter",StringComparison.OrdinalIgnoreCase),a.Address,a.PrefixLength)));
        return CreateScope(program,address,port,addresses,joined,JoinedHotspotNetwork.ReadConnection);
    }
    internal static ShareFirewallScope CreateScope(string program, IPAddress address, int port,
        IEnumerable<ShareFirewallAddress> addresses, JoinedHotspotNetwork? joined, Func<Guid,JoinedWifiConnection> readConnection)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork||IPAddress.IsLoopback(address)||address.Equals(IPAddress.Any)||address.GetAddressBytes()[0] is 169 or >=224||port is <1 or >65535)
            throw new ArgumentException("Invalid share endpoint.");
        joined?.Validate();
        var match=addresses.SingleOrDefault(a=>a.Up&&a.Address.Equals(address)&&
            (joined is null?a.Direct:a.Wireless&&a.Id==joined.InterfaceId));
        if(match is null||match.Prefix is <16 or >30)throw new InvalidOperationException("分享地址不属于当前临时 Wi-Fi，请结束分享后重试。");
        if(joined is not null) {
            if(address.Equals(joined.Owner)||Subnet(address,match.Prefix)!=Subnet(joined.Owner,match.Prefix))
                throw new InvalidOperationException("文件地址与本次临时热点不匹配。");
            joined.VerifyConnection(readConnection(match.Id));
        }
        return new(Path.GetFullPath(program),address.ToString(),port,Subnet(address,match.Prefix),match.Name);
    }
    internal static string Subnet(IPAddress address,int prefix)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork||prefix is <0 or >32)throw new ArgumentException("Invalid IPv4 prefix.");
        var bytes=address.GetAddressBytes();for(int i=0;i<4;i++)bytes[i]&=(byte)(0xff<<Math.Clamp(8-(prefix-i*8),0,8));
        return new IPAddress(bytes)+"/"+prefix.ToString(CultureInfo.InvariantCulture);
    }
    internal static bool PortMatches(string ports,int port) => ports=="*"||ports.Split(',').Any(item=>int.TryParse(item,out int exact)?exact==port:item.Split('-') is [var a,var b]&&int.TryParse(a,out int lo)&&int.TryParse(b,out int hi)&&port>=lo&&port<=hi);
    private static bool LocalMatches(string scope,string address)=>scope=="*"||scope.Split(',').Any(p=>CoversRange(p.Trim(),address+"/32"));
    private static bool RemoteCovers(string remote,string subnet)=>remote=="*"||remote.Equals("LocalSubnet",StringComparison.OrdinalIgnoreCase)||remote.Split(',').Any(p=>CoversRange(p.Trim(),subnet));
    private static bool CoversRange(string outer,string inner)
    {
        static bool Parse(string text,out uint low,out uint high){low=high=0;var parts=text.Split('/');if(parts.Length>2||!IPAddress.TryParse(parts[0],out var ip)||ip.AddressFamily!=AddressFamily.InterNetwork)return false;
            uint value=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(ip.GetAddressBytes()),mask=uint.MaxValue;
            if(parts.Length==2){if(int.TryParse(parts[1],out int prefix)){if(prefix is <0 or >32)return false;mask=prefix==0?0:uint.MaxValue<<(32-prefix);}
                else if(IPAddress.TryParse(parts[1],out var parsed)&&parsed.AddressFamily==AddressFamily.InterNetwork){mask=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(parsed.GetAddressBytes());uint inverse=~mask;if((inverse&(inverse+1))!=0)return false;}else return false;}
            low=value&mask;high=low|~mask;return true;}
        return Parse(outer,out var a,out var b)&&Parse(inner,out var c,out var d)&&a<=c&&b>=d;
    }
    internal static ShareFirewallState Evaluate(ShareFirewallScope scope,int profiles,bool policyBlocked,IEnumerable<ShareFirewallRule> source)
    {
        var rules=source.Where(r=>r.Enabled&&r.Direction==1&&r.Program.Equals(scope.Program,StringComparison.OrdinalIgnoreCase)&&r.Service.Length==0&&(r.Profiles&profiles)!=0&&(r.Protocol is 6 or 256)&&PortMatches(r.Ports,scope.Port)&&LocalMatches(r.Local,scope.Address)).ToArray();
        var blocked=rules.Where(r=>r.Action==0).ToArray();
        var generated=blocked.Where(r=>r.Generated).Select(r=>r.Key).ToArray();
        bool ready=!policyBlocked&&blocked.Length==0;
        foreach(int profile in new[]{1,2,4})if((profiles&profile)!=0)
            ready &= rules.Any(r=>r.Action==1&&(r.Profiles&profile)!=0&&RemoteCovers(r.Remote,scope.Subnet)
                &&r.InterfaceTypes.Equals("All",StringComparison.OrdinalIgnoreCase)&&(r.Interfaces.Length==0||r.Interfaces.Contains(scope.InterfaceName,StringComparer.OrdinalIgnoreCase)));
        return new(ready,policyBlocked,profiles,rules,generated);
    }
    internal static dynamic Policy()=>Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2",true)!)!;
    internal static ShareFirewallState Inspect(ShareFirewallScope scope)
    {
        try{
            dynamic policy=Policy();int current=policy.CurrentProfileTypes;if(current==0)current=4;
            int enabled=0;bool blocked=(int)policy.LocalPolicyModifyState!=0;
            foreach(int profile in new[]{1,2,4})if((current&profile)!=0&&(bool)policy.FirewallEnabled[profile]){enabled|=profile;blocked|=(bool)policy.BlockAllInboundTraffic[profile];}
            var rules=ReadRules(policy,scope.Program);
            return Evaluate(scope,enabled,blocked,rules);
        }catch(Exception ex){return new(false,false,0,[],[],ex.GetType().Name);}
    }
    internal static ShareFirewallRule[] ReadRules(dynamic policy,string application)
    {
        var rules=new List<ShareFirewallRule>();
        foreach(dynamic r in policy.Rules){string program=r.ApplicationName??"";if(!program.Equals(application,StringComparison.OrdinalIgnoreCase))continue;
            string[] interfaces=[];try{if(r.Interfaces is Array values)interfaces=values.Cast<object>().Select(v=>v.ToString()??"").ToArray();}catch{}
            int protocol=r.Protocol;rules.Add(new((string)r.Name,program,(bool)r.Enabled,(int)r.Direction,(int)r.Action,protocol,protocol is 6 or 17?(string)r.LocalPorts:"*",(string)r.LocalAddresses,(string)r.RemoteAddresses,(int)r.Profiles,(string)r.InterfaceTypes,interfaces,(string?)r.ServiceName??""));}
        KeyValuePair<string,string>[] ids=Marshal.IsComObject(policy)?GeneratedFirewallBlocks.ReadLocal():(KeyValuePair<string,string>[])policy.LocalRuleIdentities;
        return GeneratedFirewallBlocks.Bind(rules,ids);
    }
    internal static async Task EnsureAsync(IPAddress address,int port,string output,Func<bool> confirm,CancellationToken cancellation,JoinedHotspotNetwork? joined=null)
    {
        var scope=CreateScope(Environment.ProcessPath??throw new IOException("无法读取程序位置。"),address,port,joined);
        var before=await Task.Run(()=>Inspect(scope),cancellation);
        await File.WriteAllTextAsync(Path.Combine(output,"firewall-before.json"),JsonSerializer.Serialize(new{scope,state=before}),cancellation);
        if(before.Ready)return;
        if(before.PolicyBlocked)throw new ShareFirewallException("Windows 的网络策略禁止接收连接，请联系电脑管理员后重试。");
        if(before.Rules.Any(r=>r.Action==0&&!before.GeneratedBlocks.Contains(r.Key)))throw new ShareFirewallException("Windows 中已有针对 ShareLink 的阻止规则，请在防火墙中允许此程序后重试。");
        if(!confirm())throw new ShareFirewallException("未允许接收设备访问下载页。请重新开启分享并允许 Windows 网络访问。");
        cancellation.ThrowIfCancellationRequested();
        try{
            using var current=Process.GetCurrentProcess();
            var start=new ProcessStartInfo(scope.Program){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden};
            foreach(string arg in new[]{HelperSwitch,current.Id.ToString(CultureInfo.InvariantCulture),current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),scope.Address,scope.Port.ToString(CultureInfo.InvariantCulture)})start.ArgumentList.Add(arg);
            if(joined is not null)foreach(string arg in new[]{joined.InterfaceId.ToString("D"),joined.Profile,joined.Ssid,joined.Owner.ToString()})start.ArgumentList.Add(arg);
            using var helper=Process.Start(start)??throw new IOException("无法启动网络访问授权。");
            await helper.WaitForExitAsync(cancellation);
            await File.WriteAllTextAsync(Path.Combine(output,"firewall-helper.json"),JsonSerializer.Serialize(new{helperExitCode=helper.ExitCode,helperExitHex=$"0x{helper.ExitCode:X8}",parentElevated=IsAdministrator()}),cancellation);
            if(helper.ExitCode!=0)throw new ShareFirewallException(helper.ExitCode==4?"Windows 中已有其他阻止规则，下载页尚不能供手机访问，请在防火墙中允许 ShareLink 后重试。":$"下载页网络授权未完成（代码 {helper.ExitCode}），请重新运行安装包配置网络权限。");
            for(int attempt=0;attempt<12;attempt++){var after=await Task.Run(()=>Inspect(scope),cancellation);if(after.Ready){await File.WriteAllTextAsync(Path.Combine(output,"firewall-after.json"),JsonSerializer.Serialize(new{scope,state=after}),cancellation);return;}await Task.Delay(250,cancellation);}
            throw new ShareFirewallException("Windows 尚未应用网络访问授权，请稍后重试开启分享。");
        }catch(Win32Exception ex)when(ex.NativeErrorCode==1223){throw new ShareFirewallException("Windows 网络访问授权已取消，请重新开启分享并允许访问。");}
    }
    // No arbitrary output files, commands, executable paths or address changes are accepted by the elevated entry.
    public static int RunHelper(string[] arguments)
    {
        try{
            if((arguments.Length is not (5 or 9))||arguments[0]!=HelperSwitch||!int.TryParse(arguments[1],out int pid)||!long.TryParse(arguments[2],out long started)||!IPAddress.TryParse(arguments[3],out var ip)||!int.TryParse(arguments[4],out int port))return 2;
            using var identity=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))return 1;
            using var parent=Process.GetProcessById(pid);string self=Environment.ProcessPath!;
            if(parent.StartTime.ToUniversalTime().Ticks!=started||!string.Equals(parent.MainModule?.FileName,self,StringComparison.OrdinalIgnoreCase)||!OwnsListener(pid,ip,port))return 2;
            JoinedHotspotNetwork? joined=null;
            if(arguments.Length==9) {
                if(!Guid.TryParse(arguments[5],out var interfaceId)||!IPAddress.TryParse(arguments[8],out var owner))return 2;
                joined=new(interfaceId,arguments[6],arguments[7],owner);joined.Validate();
            }
            var scope=CreateScope(self,ip,port,joined);var state=Inspect(scope);
            if(state.Ready)return 0;if(state.PolicyBlocked||state.Problem is not null)return 3;
            if(state.Rules.Any(r=>r.Action==0&&!state.GeneratedBlocks.Contains(r.Key)))return 4;
            dynamic policy=Policy();dynamic rule=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwRule",true)!)!;
            rule.Name=scope.RuleName;rule.Description="ShareLink local Wi-Fi download page; scoped to this application, hotspot IPv4, subnet and TCP port.";
            rule.ApplicationName=scope.Program;rule.Protocol=6;rule.LocalPorts=scope.Port.ToString(CultureInfo.InvariantCulture);rule.LocalAddresses=scope.Address;rule.RemoteAddresses=scope.Subnet;
            rule.Direction=1;rule.Action=1;rule.Profiles=AllProfiles;rule.InterfaceTypes="All";rule.Interfaces=new object[]{scope.InterfaceName};rule.EdgeTraversal=false;rule.Enabled=true;
            // Create the replacement exception before changing only system-generated, same-path TCP denials.
            if(parent.HasExited||!OwnsListener(pid,ip,port))return 2;
            _=CreateScope(self,ip,port,joined); // Recheck the actual adapter/profile after elevation.
            ApplyRule(policy,rule,scope,state.GeneratedBlocks);
            return 0;
        }catch(Exception ex){return ex.HResult;}
    }

    internal static bool IsAdministrator()
    {
        using var identity=WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    internal static bool MissingRule(Exception ex) => ex.HResult==unchecked((int)0x80070002);

    // Roll back our own changes if Windows rejects any part of the operation.
    internal static void ApplyRule(dynamic policy,dynamic rule,ShareFirewallScope scope,string[] generatedBlocks)
        => ApplyNamedRule(policy,rule,scope.RuleName,scope.Program,generatedBlocks);

    internal static void ApplyNamedRule(dynamic policy,dynamic rule,string name,string program,string[] generatedBlocks,Action? verify=null)
    {
        dynamic? previous=null;
        try{previous=policy.Rules.Item(name);}catch(Exception ex)when(MissingRule(ex)){}
        if(previous is not null&&!string.Equals((string)previous.ApplicationName,program,StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Rule name is already used by another application.");
        var changed=new List<Action>();bool removed=false,added=false;
        try{
            if(previous is not null){policy.Rules.Remove(name);removed=true;}
            policy.Rules.Add(rule);added=true;
            foreach(string blockName in generatedBlocks){
                if(Marshal.IsComObject(policy)){
                    changed.Add(()=>GeneratedFirewallBlocks.SetEnabled(blockName,program,true));
                    GeneratedFirewallBlocks.SetEnabled(blockName,program,false);
                }else{
                    dynamic original=policy.Rules.Item(blockName);
                    if(string.Equals((string)original.ApplicationName,program,StringComparison.OrdinalIgnoreCase)&&(int)original.Protocol==6&&(int)original.Action==0&&(bool)original.Enabled){
                        changed.Add(()=>original.Enabled=true);original.Enabled=false;
                    }
                }
            }
            verify?.Invoke();
        }catch{
            foreach(var restore in changed)try{restore();}catch{}
            if(added)try{policy.Rules.Remove(name);}catch{}
            if(removed)try{policy.Rules.Add(previous);}catch{}
            throw;
        }
    }
    [DllImport("iphlpapi.dll",SetLastError=true)]private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool order,int family,int kind,uint reserved);
    internal static bool OwnsListener(int pid,IPAddress address,int port)
    {
        if(address.AddressFamily!=AddressFamily.InterNetwork)return false;
        int size=0;uint first=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);if(first!=122||size<4)return false;
        IntPtr table=Marshal.AllocHGlobal(size);try{
            if(GetExtendedTcpTable(table,ref size,false,2,3,0)!=0)return false;int count=Marshal.ReadInt32(table);
            for(int i=0;i<count;i++){IntPtr row=IntPtr.Add(table,4+i*24);if(Marshal.ReadInt32(row+20)!=pid)continue;
                var bytes=new byte[4];Marshal.Copy(row+4,bytes,0,4);int localPort=(ushort)IPAddress.NetworkToHostOrder(Marshal.ReadInt16(row+8));
                if(localPort==port&&new IPAddress(bytes).Equals(address))return true;}
            return false;
        }finally{Marshal.FreeHGlobal(table);}
    }
}
