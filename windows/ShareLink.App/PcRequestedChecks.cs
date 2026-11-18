using System.Text.Json;
namespace ShareLink;
internal static class PcRequestedChecks
{
    internal static async Task<string[]> RunAsync(string output)
    {
        var checks=new List<string>();void Check(string name,bool passed){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
        JsonElement J(object value)=>JsonSerializer.SerializeToElement(value);
        JsonElement Report(double? frequency,bool connected=true)=>J(new{current=new{connected,frequencyMhz=(object?)frequency??"unknown"}});
        Check("local 5GHz is preferred when remote is disconnected",PcHotspotPolicy.PreferLocal(Report(5180),Report(null,false)));
        Check("local 5GHz is preferred over remote 2.4GHz",PcHotspotPolicy.PreferLocal(Report(5180),Report(2412)));
        Check("remote 5GHz retains the default remote hotspot",!PcHotspotPolicy.PreferLocal(Report(2412),Report(5805)));
        Check("both 5GHz retain default hotspot owner",!PcHotspotPolicy.PreferLocal(Report(5180),Report(5805)));
        Check("neither connected retains default hotspot owner",!PcHotspotPolicy.PreferLocal(Report(null,false),Report(null,false)));
        Check("unknown band does not masquerade as 5GHz",!PcHotspotPolicy.HasFiveGhz(Report(null)));
        Check("disconnected 5GHz scan data is not a connected Wi-Fi",!PcHotspotPolicy.HasFiveGhz(Report(5180,false)));
        Check("6GHz does not count as a 5GHz connection",!PcHotspotPolicy.HasFiveGhz(Report(6105)));
        Check("a second connected 5GHz adapter is considered",PcHotspotPolicy.HasFiveGhz(J(new{interfaces=new[]{new{connected=true,frequencyMhz=2412},new{connected=true,frequencyMhz=5180}}})));
        string fingerprint=new('a',64);
        object Credentials(string hash)=>new{ssid="SL123",password="sl123456",securityType="wpa2-psk",addresses=new[]{"192.168.49.1"},port=53317,fingerprint=hash};
        Check("authenticated reverse hotspot parameters validate",PcHotspotPolicy.Validate(J(Credentials(fingerprint)),fingerprint).Owner.ToString()=="192.168.49.1");
        bool denied=false;try{PcHotspotPolicy.Validate(J(Credentials(new string('b',64))),fingerprint);}catch(InvalidOperationException){denied=true;}
        Check("reverse hotspot credentials cannot switch the paired identity",denied);
        object Config(string name)=>new{name,device_type="computer",state_dir=System.IO.Path.Combine(output,name,"identity"),download_dir=System.IO.Path.Combine(output,name,"received"),port=0,discovery_port=0,transport_modules=new[]{"nearby-direct"}};
        using var client=new NativeCore(Config("click-client"));using var receiver=new NativeCore(Config("click-receiver"));int dialogs=0;
        await using var host=new PcControlProtocol(receiver,(_,_)=>{dialogs++;return Task.FromResult(false);},clickToConnect:true);
        var hello=await host.RequestAsync(J(new{op="hello"}));Check("normal PC hello no longer requests a code dialog",!hello.GetProperty("requiresPairingConfirmation").GetBoolean());
        var begin=client.Call(new{op="pair_begin"});string pair=MainWindow.Text(begin,"pairId");
        var remote=await host.RequestAsync(J(new{op="pair_begin",hello=begin.GetProperty("hello")}));
        var bound=client.Call(new{op="pair_finish",pairId=pair,hello=remote.GetProperty("hello")});
        Check("click-to-connect still derives a full certificate-bound identity",MainWindow.Text(bound.GetProperty("peer"),"fingerprint")==host.LocalFingerprint);
        Check("click-to-connect never invokes the SAS callback",dialogs==0&&!remote.GetProperty("requiresPairingConfirmation").GetBoolean());
        Check("key exchange alone cannot execute secure commands",!(await host.RequestAsync(J(new{op="secure",envelope=new{}}))).GetProperty("ok").GetBoolean());
        client.Call(new{op="pair_confirm",pairId=pair,confirmed=true});await host.RequestAsync(J(new{op="pair_confirm",confirmed=true}));
        Check("clicked connection completes without a code prompt",host.Authenticated&&dialogs==0);
        var encrypted=client.Call(new{op="pair_encrypt",pairId=pair,payload=new{op="status"}});
        var reply=await host.RequestAsync(J(new{op="secure",envelope=encrypted.GetProperty("envelope")}));
        var payload=client.Call(new{op="pair_decrypt",pairId=pair,envelope=reply.GetProperty("envelope")}).GetProperty("payload");
        Check("no-code connection still exchanges encrypted status",payload.GetProperty("pairingCheckpoint").GetBoolean());
        client.Call(new{op="pair_close",pairId=pair});
        return checks.ToArray();
    }
}
