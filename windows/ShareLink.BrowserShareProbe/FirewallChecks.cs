
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using ShareLink.BrowserSharing;

namespace ShareLink.BrowserShareProbe;

internal static class FirewallChecks
{
    public static void Run(string output)
    {
        var checks=new List<object>();
        void Check(string name,bool pass){checks.Add(new{name,passed=pass});if(!pass)throw new Exception(name);}
        JoinedHotspotChecks.Run(Check);
        var scope=new ShareFirewallScope(Environment.ProcessPath!,"192.168.137.1",80,"192.168.137.0/24","Local Wi-Fi Test");
        var allow=new ShareFirewallRule("existing",scope.Program,true,1,1,6,"80","*","*",BrowserShareFirewall.AllProfiles,"All",[],"");
        ShareFirewallState Eval(params ShareFirewallRule[] rules)=>BrowserShareFirewall.Evaluate(scope,4,false,rules);
        Check("fresh install requires network authorization",!Eval().Ready);
        Check("private-only exception does not cover public hotspot",!Eval(allow with{Profiles=2}).Ready);
        Check("all-profile TCP exception is recognized",Eval(allow).Ready);
        Check("disabled exception is ignored",!Eval(allow with{Enabled=false}).Ready);
        Check("other program path is ignored",!Eval(allow with{Program=scope.Program+".other"}).Ready);
        Check("other destination port is ignored",!Eval(allow with{Ports="443"}).Ready);
        Check("comma-separated destination ports are recognized",Eval(allow with{Ports="443,80"}).Ready);
        Check("fallback port ranges are recognized",BrowserShareFirewall.PortMatches("8000-9000",8080)&&!BrowserShareFirewall.PortMatches("8000-9000",80));
        Check("only correct local address is recognized",!Eval(allow with{Local="192.168.138.1"}).Ready);
        Check("actual local subnet is recognized",Eval(allow with{Local="192.168.137.0/24",Remote=scope.Subnet}).Ready);
        Check("Windows dotted subnet mask normalization is recognized",Eval(allow with{Remote="192.168.137.0/255.255.255.0"}).Ready);
        Check("smaller remote range is insufficient",!Eval(allow with{Remote="192.168.137.0/25"}).Ready);
        Check("different remote subnet is insufficient",!Eval(allow with{Remote="192.168.138.0/24"}).Ready);
        Check("interface alias comparison is case-insensitive",Eval(allow with{Interfaces=["LOCAL WI-FI TEST"]}).Ready);
        Check("other interface exception is insufficient",!Eval(allow with{Interfaces=["Other Wi-Fi"]}).Ready);
        Check("service-specific exception is insufficient",!Eval(allow with{Service="OtherService"}).Ready);
        Check("every active profile requires coverage",!BrowserShareFirewall.Evaluate(scope,6,false,[allow with{Profiles=2}]).Ready);
        Check("policy blocking cannot be overridden",!BrowserShareFirewall.Evaluate(scope,4,true,[allow]).Ready);
        var deny=allow with{Name="manual-denial",Action=0};
        Check("explicit denial overrides allow",!Eval(allow,deny).Ready&&Eval(deny).GeneratedBlocks.Length==0);
        string generatedId="TCP Query User{C42674B0-7A07-43D7-BF6A-E356B1CDAF47}"+scope.Program;
        var generated=deny with{Name="ShareLink",InternalId=generatedId};
        Check("only same-program generated TCP denials are candidates",Eval(generated,generated with{Program="another.exe"},generated with{Name="UDP Query User Test",Protocol=17}).GeneratedBlocks.SequenceEqual(new[]{generatedId}));
        dynamic realPolicy=BrowserShareFirewall.Policy();
        bool missingRecognized=false;string missingType="";
        try{_=realPolicy.Rules.Item("ShareLink-ReadOnly-Missing-"+Guid.NewGuid().ToString("N"));}catch(Exception ex){missingRecognized=BrowserShareFirewall.MissingRule(ex);missingType=ex.GetType().Name;}
        Check("actual missing Windows rule is recognized regardless of exception subclass: "+missingType,missingRecognized);
        Check("access denied is never treated as a missing rule",!BrowserShareFirewall.MissingRule(new COMException("denied",unchecked((int)0x80070005))));
        string raw="v2.10|Action=Block|Active=TRUE|Dir=In|Protocol=6|Profile=Private|App="+scope.Program+"|Name=ShareLink|Desc=ShareLink|";
        var displayed=deny with{Name="ShareLink",Ports="*",Profiles=2};
        var annotated=GeneratedFirewallBlocks.Bind([displayed],[new(generatedId,raw)]);
        Check("laptop-shaped friendly name resolves to real local internal ID",annotated[0].InternalId==generatedId);
        Check("friendly name prefix cannot impersonate a generated identity",!GeneratedFirewallBlocks.Bind([displayed with{Name="TCP Query User manual"}],[])[0].Generated);
        Check("manual duplicate with same display name remains protected",!GeneratedFirewallBlocks.Bind([displayed],[new(generatedId,raw),new("manual-duplicate",raw)])[0].Generated);
        Check("unmapped effective duplicate remains protected",!GeneratedFirewallBlocks.Bind([displayed,displayed with{}],[new(generatedId,raw)])[0].Generated);
        Check("subnet derives from actual prefix",BrowserShareFirewall.Subnet(IPAddress.Parse("172.23.9.1"),20)=="172.23.0.0/20");
        Check("malformed elevated invocation is rejected",BrowserShareFirewall.RunHelper(["--configure-browser-firewall"])==2);
        bool rejected=false;try{BrowserShareFirewall.CreateScope(scope.Program,IPAddress.Loopback,80);}catch(ArgumentException){rejected=true;}
        Check("loopback cannot authorize an adapter",rejected);
        using(var listener=new TcpListener(IPAddress.Loopback,0)){
            listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;
            Check("native listener lookup identifies real owning process",BrowserShareFirewall.OwnsListener(Environment.ProcessId,IPAddress.Loopback,port));
            Check("native listener lookup rejects another process",!BrowserShareFirewall.OwnsListener(-1,IPAddress.Loopback,port));
        }
        var read=BrowserShareFirewall.Inspect(scope);
        File.WriteAllText(Path.Combine(output,"firewall-read-only.json"),JsonSerializer.Serialize(read,new JsonSerializerOptions{WriteIndented=true}));
        Check("actual Windows firewall policy can be read without changes",read.Problem is null);
        dynamic uncommitted=Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwRule",true)!)!;
        uncommitted.Name="ShareLink in-memory validation";uncommitted.ApplicationName=scope.Program;uncommitted.Protocol=6;
        uncommitted.LocalPorts="80";uncommitted.LocalAddresses=scope.Address;uncommitted.RemoteAddresses=scope.Subnet;
        uncommitted.Direction=1;uncommitted.Action=1;uncommitted.Profiles=BrowserShareFirewall.AllProfiles;uncommitted.InterfaceTypes="All";uncommitted.Interfaces=new object[]{System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().First(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up).Name};uncommitted.Enabled=true;
        Check("Windows accepts the scoped rule properties in memory",((string)uncommitted.LocalPorts)=="80"&&((Array)uncommitted.Interfaces).Length==1);
        dynamic installed=InstalledNetworkAccess.CreateRule(scope.Program);
        Check("installer permission covers all network categories for this EXE only",(string)installed.ApplicationName==scope.Program&&((int)installed.Profiles&7)==7&&(int)installed.Protocol==6);
        Check("installer permission is restricted to local subnet without fixed IP or port",(string)installed.RemoteAddresses=="LocalSubnet"&&(string)installed.LocalAddresses=="*"&&(string)installed.LocalPorts=="*"&&!(bool)installed.EdgeTraversal);
        Check("installation rule identity is stable across path case",InstalledNetworkAccess.RuleName(scope.Program)==InstalledNetworkAccess.RuleName(scope.Program.ToUpperInvariant()));
        Check("separate installation paths get separate network rules",InstalledNetworkAccess.RuleName(scope.Program)!=InstalledNetworkAccess.RuleName(scope.Program+".other"));
        Check("installation helper rejects additional path or output arguments",InstalledNetworkAccess.RunHelper([InstalledNetworkAccess.InstallSwitch,"another.exe"])==2);
        Check("uninstall helper rejects additional path or output arguments",InstalledNetworkAccess.RunHelper([InstalledNetworkAccess.RemoveSwitch,"another.exe"])==2);
        Check("existing private-only allow does not become a denial",InstalledNetworkAccess.GeneratedBlocks([allow with{Profiles=2}]).Length==0);
        Check("installation may repair only generated same-path TCP denials",InstalledNetworkAccess.GeneratedBlocks([generated]).SequenceEqual(new[]{generatedId}));
        bool manualRejected=false;try{InstalledNetworkAccess.GeneratedBlocks([deny]);}catch(ShareFirewallException){manualRejected=true;}
        Check("installation refuses manual denials",manualRejected);
        Check("installed LocalSubnet rule satisfies sharing on private and public profiles",BrowserShareFirewall.Evaluate(scope,6,false,[allow with{Ports="*",Remote="LocalSubnet"}]).Ready);
        // Fake stores exercise mutations and rollback; no Rules.Add is called on Windows.
        var fake=new FakeFirewallPolicy();var replacement=new FakeFirewallRule{Name=scope.RuleName,ApplicationName=scope.Program};
        var old=new FakeFirewallRule{Name=scope.RuleName,ApplicationName=scope.Program};
        var block=new FakeFirewallRule{Name="ShareLink",InternalId=generatedId,ApplicationName=scope.Program,Action=0};
        fake.Rules.Add(old);fake.Rules.Add(block);
        BrowserShareFirewall.ApplyRule(fake,replacement,scope,[block.InternalId!]);
        Check("replacement precedes disabling the generated denial",ReferenceEquals(fake.Rules.Item(scope.RuleName),replacement)&&!block.Enabled);
        fake=new FakeFirewallPolicy();old=new(){Name=scope.RuleName,ApplicationName=scope.Program};
        block=new(){Name="ShareLink",InternalId=generatedId,ApplicationName=scope.Program,Action=0};fake.Rules.Add(old);fake.Rules.Add(block);
        bool rolled=false;try{BrowserShareFirewall.ApplyRule(fake,replacement,scope,[block.InternalId!,"missing"]);}catch(COMException){rolled=true;}
        Check("partial failure restores prior exception and generated denial",rolled&&block.Enabled&&ReferenceEquals(fake.Rules.Item(scope.RuleName),old));
        fake=new FakeFirewallPolicy();fake.Rules.Add(new FakeFirewallRule{Name=scope.RuleName,ApplicationName="another.exe"});
        bool conflict=false;try{BrowserShareFirewall.ApplyRule(fake,replacement,scope,[]);}catch(InvalidOperationException){conflict=true;}
        Check("name conflict cannot overwrite another program rule",conflict&&fake.Rules.Item(scope.RuleName).ApplicationName=="another.exe");
        fake=new FakeFirewallPolicy();old=new(){Name=scope.RuleName,ApplicationName=scope.Program};
        block=new(){Name="ShareLink",InternalId=generatedId,ApplicationName=scope.Program,Action=0};fake.Rules.Add(old);fake.Rules.Add(block);
        bool verifyRolled=false;
        try{BrowserShareFirewall.ApplyNamedRule(fake,replacement,scope.RuleName,scope.Program,[block.InternalId!],(Action)(()=>throw new IOException("verification failed")));}catch(IOException){verifyRolled=true;}
        Check("failed post-write verification restores old rule and generated denial",verifyRolled&&block.Enabled&&ReferenceEquals(fake.Rules.Item(scope.RuleName),old));
        fake=new FakeFirewallPolicy();
        string installedName=InstalledNetworkAccess.RuleName(scope.Program);
        fake.Rules.Add(new FakeFirewallRule{Name=installedName,ApplicationName=scope.Program,Description=InstalledNetworkAccess.RuleDescription});
        fake.Rules.Add(new FakeFirewallRule{Name="unrelated",ApplicationName=scope.Program});
        InstalledNetworkAccess.Remove(fake,scope.Program);
        bool absent=false;try{fake.Rules.Item(installedName);}catch(COMException){absent=true;}
        Check("uninstall removes only its marked path-specific rule",absent&&fake.Rules.Item("unrelated").Enabled);
        InstalledNetworkAccess.Remove(fake,scope.Program);
        Check("uninstall cleanup succeeds when its rule is already absent",true);
        fake.Rules.Add(new FakeFirewallRule{Name=installedName,ApplicationName=scope.Program,Description="manual rule"});
        bool removeRefused=false;try{InstalledNetworkAccess.Remove(fake,scope.Program);}catch(IOException){removeRefused=true;}
        Check("uninstall cannot remove an unowned rule even at the same path",removeRefused&&fake.Rules.Item(installedName).Description=="manual rule");
        fake=new FakeFirewallPolicy();
        var privateOnly=new FakeFirewallRule{Name="existing private",ApplicationName=scope.Program,Profiles=2};fake.Rules.Add(privateOnly);
        InstalledNetworkAccess.Configure(fake,scope.Program);
        Check("installation adds verified LocalSubnet permission beside a private-only rule",(string)fake.Rules.Item(installedName).RemoteAddresses=="LocalSubnet"&&privateOnly.Profiles==2);
        InstalledNetworkAccess.Configure(fake,scope.Program);
        Check("reinstall repairs the same rule without accumulating duplicates",fake.Rules.Count==2);
        InstalledNetworkAccess.Remove(fake,scope.Program);
        Check("uninstall keeps pre-existing system and user rules",fake.Rules.Count==1&&ReferenceEquals(fake.Rules.Item(privateOnly.Name),privateOnly));
        fake.LocalPolicyModifyState=1;
        bool policyRefused=false;try{InstalledNetworkAccess.Configure(fake,scope.Program);}catch(ShareFirewallException){policyRefused=true;}
        Check("installation leaves system policy untouched",policyRefused&&fake.Rules.Count==1);
        fake=new FakeFirewallPolicy();fake.Rules.Add(new FakeFirewallRule{Name="manual denial",ApplicationName=scope.Program,Action=0});
        manualRejected=false;try{InstalledNetworkAccess.Configure(fake,scope.Program);}catch(ShareFirewallException){manualRejected=true;}
        Check("end-to-end installation preserves a manual blocking rule",manualRejected&&fake.Rules.Count==1&&fake.Rules.Item("manual denial").Enabled);
        fake=new FakeFirewallPolicy();block=new(){Name="ShareLink",InternalId=generatedId,ApplicationName=scope.Program,Action=0};fake.Rules.Add(block);
        InstalledNetworkAccess.Configure(fake,scope.Program);
        Check("end-to-end installation repairs an eligible generated TCP denial",!block.Enabled&&fake.Rules.Count==2);
        File.WriteAllText(Path.Combine(output,"firewall-checks.json"),JsonSerializer.Serialize(new{passed=true,scope="Policy matching, in-memory COM properties, actual read-only policy/listener queries, fake-store mutation/rollback. No firewall writes, elevation or hotspot.",checks},new JsonSerializerOptions{WriteIndented=true}));
    }
}
public sealed class FakeFirewallPolicy
{
    public FakeFirewallRules Rules{get;}=new();
    public int LocalPolicyModifyState{get;set;}
    public bool[] FirewallEnabled{get;}=new bool[5];
    public bool[] BlockAllInboundTraffic{get;}=new bool[5];
    public KeyValuePair<string,string>[] LocalRuleIdentities=>Rules.OfType<FakeFirewallRule>().Where(r=>r.Action==0).Select(r=>new KeyValuePair<string,string>(r.InternalId??("Manual-"+r.Name),"v2.10|Action=Block|Active="+(r.Enabled?"TRUE":"FALSE")+"|Dir=In|Protocol="+r.Protocol+"|Profile="+(r.Profiles switch{2=>"Private",4=>"Public",1=>"Domain",_=>""})+"|App="+r.ApplicationName+"|Name="+r.Name+"|Desc=ShareLink|")).ToArray();
}
public sealed class FakeFirewallRules : IEnumerable<object>
{
    private readonly Dictionary<string,object> rules=new();
    public int Count=>rules.Count;
    public dynamic Item(string name)=>rules.TryGetValue(name,out var rule)?rule:throw new COMException("missing",unchecked((int)0x80070002));
    public void Add(dynamic rule)=>rules.Add(rule is FakeFirewallRule f?f.InternalId??f.Name:(string)rule.Name,(object)rule);
    public void Remove(string name){if(!rules.Remove(name))throw new COMException("missing");}
    public IEnumerator<object> GetEnumerator()=>rules.Values.GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();
}
public sealed class FakeFirewallRule
{
    public string Name{get;set;}="";
    public string? InternalId{get;set;}
    public string ApplicationName{get;set;}="";
    public string Description{get;set;}="";
    public bool Enabled{get;set;}=true;
    public int Protocol{get;set;}=6;
    public int Action{get;set;}=1;
    public int Direction{get;set;}=1;
    public string LocalPorts{get;set;}="*";
    public string LocalAddresses{get;set;}="*";
    public string RemoteAddresses{get;set;}="*";
    public int Profiles{get;set;}=BrowserShareFirewall.AllProfiles;
    public string InterfaceTypes{get;set;}="All";
    public object[] Interfaces{get;set;}=[];
    public string ServiceName{get;set;}="";
}
