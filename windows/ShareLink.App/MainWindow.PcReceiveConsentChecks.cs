using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckPcReceiveConsentAsync(string output)
    {
        var checks=new List<string>();
        void Check(string name,bool passed){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
        JsonElement J(object value)=>JsonSerializer.SerializeToElement(value);
        var window=new MainWindow(Path.Combine(output,"pc-receive-consent"),desktopTest:true){Left=-30000,Top=-30000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false};
        using var receiver=new NativeCore(window.CoreConfiguration());window.core=receiver;window.pcRefreshing=true;
        window.pcWifiReport=()=>Task.FromResult(window.networkReport);
        using var sender=new NativeCore(new{name="Generated PC sender",device_type="computer",state_dir=Path.Combine(output,"pc-consent-sender","identity"),download_dir=Path.Combine(output,"pc-consent-sender","received"),port=0,discovery_port=0,transport_modules=new[]{"nearby-direct"}});
        await using var protocol=new PcControlProtocol(receiver,secureCommand:window.PcCommandAsync,clickToConnect:true);window.pcProtocol=protocol;
        new WindowInteropHelper(window).EnsureHandle(); // No Loaded event: no radio or network initialization.
        var begin=sender.Call(new{op="pair_begin"});string pair=Text(begin,"pairId");
        try {
            var remote=await protocol.RequestAsync(J(new{op="pair_begin",hello=begin.GetProperty("hello")}));
            sender.Call(new{op="pair_finish",pairId=pair,hello=remote.GetProperty("hello")});
            sender.Call(new{op="pair_confirm",pairId=pair,confirmed=true});await protocol.RequestAsync(J(new{op="pair_confirm",confirmed=true}));
            async Task<JsonElement> Secure(object command){
                var encrypted=sender.Call(new{op="pair_encrypt",pairId=pair,payload=command});
                var reply=await protocol.RequestAsync(J(new{op="secure",envelope=encrypted.GetProperty("envelope")}));
                if(!reply.TryGetProperty("envelope",out var envelope))return reply;
                return sender.Call(new{op="pair_decrypt",pairId=pair,envelope}).GetProperty("payload");
            }
            var old=await Secure(new{op="status"});var current=await Secure(new{op="status",mergedReceiveApproval=true});
            Check("legacy sender cannot request an unapproved receiver role switch",!old.GetProperty("capabilities").GetProperty("hotspotRoleSelection").GetBoolean());
            Check("new peer negotiates manifest-aware receive role selection",current.GetProperty("capabilities").GetProperty("hotspotRoleSelection").GetBoolean()&&current.GetProperty("capabilities").GetProperty("mergedReceiveApproval").GetBoolean());
            string fingerprint=protocol.Fingerprint;
            var credentials=new{ssid="SLGenerated",password="generated-test",securityType="wpa2-psk",addresses=new[]{"192.168.49.1"},port=53317,fingerprint};
            object Offer(string token,string name="Generated PC.bin")=>new{id="generated-offer",token,files=new[]{new{name,size=32L}}};
            var malformed=await Secure(new{op="hotspot_join_start",credentials,offer=Offer("bad")});
            Check("malformed PC manifest is rejected before queuing a join",!malformed.GetProperty("ok").GetBoolean()&&window.pcJoining is null&&window.wifi is null);
            var withoutManifest=await Secure(new{op="hotspot_join_start",credentials});
            Check("receiver refuses a role switch without a bound receive manifest",!withoutManifest.GetProperty("ok").GetBoolean()&&window.pcJoining is null);
            foreach(string action in new[]{"reject","cancel","changed","accept"}) {
                var started=await Secure(new{op="hotspot_join_start",credentials,offer=Offer(new string('a',64))});
                Check(action+": encrypted join returns pending before showing consent",started.GetProperty("ok").GetBoolean()&&window.pcJoining is {IsCompleted:false}&&window.pcJoinedEndpoint is null&&window.wifi is null);
                Exception? callbackError=null;bool inspected=false;Task? stop=null;
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(100)};
                timer.Tick+=async(_,_)=>{
                    var dialog=window.OwnedWindows.Cast<Window>().SingleOrDefault(w=>w.Title=="接收文件");if(dialog is null)return;timer.Stop();
                    try {
                        string body=string.Join("\n",WalkPcConsent(dialog).OfType<TextBlock>().Select(t=>t.Text));
                        Check(action+": one receive dialog contains PC name, file and one network notice",body.Contains("Generated PC sender")&&body.Contains("Generated PC.bin")&&body.Split(TemporaryWifiNotice.Message).Length==2&&window.OwnedWindows.Cast<Window>().Count(w=>w.IsVisible)==1);
                        Check(action+": network setup waits for receive consent",window.receivePromptActive&&window.wifi is null&&window.pcJoinedEndpoint is null&&window.pcJoinError.Length==0);
                        inspected=true;
                        if(action=="cancel"){stop=Secure(new{op="hotspot_join_stop"});await stop;return;}
                        if(action=="changed") {
                            var mutation=await Secure(new{op="hotspot_join_start",credentials,offer=Offer(new string('a',64),"changed.bin")});
                            Check("changed manifest cannot replace an open PC confirmation",!mutation.GetProperty("ok").GetBoolean()&&window.pcJoinOffer?.GetProperty("files")[0].GetProperty("name").GetString()=="Generated PC.bin");
                        }
                        WalkPcConsent(dialog).OfType<Button>().Single(b=>b.Content?.ToString()==(action=="accept"?"接收":"拒绝")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    } catch(Exception error){callbackError=error;dialog.DialogResult=false;}
                };
                timer.Start();
                try {await window.pcJoining!;if(stop is not null)await stop;}
                finally{timer.Stop();}
                if(callbackError is not null)throw callbackError;
                Check(action+": actual receive dialog was exercised",inspected);
                Check(action+": no endpoint or native receive task exists without a Wi-Fi join",window.pcJoinedEndpoint is null&&window.pcJoinPullId.Length==0&&!window.PcHasTasks());
                if(action=="accept")Check("accepted PC consent reaches network initialization only afterwards",window.pcJoinError=="Wi-Fi直连尚未初始化。");
                else Check(action+": rejection or sender cancellation does not reach network initialization",window.pcJoinError!="Wi-Fi直连尚未初始化。");
                if(stop is null)await Secure(new{op="hotspot_join_stop"});
                Check(action+": cleanup clears pending manifest and receive prompt",window.pcJoining is null&&window.pcJoinOffer is null&&!window.receivePromptActive);
            }
        } finally {
            sender.Call(new{op="pair_close",pairId=pair});window.pcProtocol=null;window.core=null;window.timer.Stop();
        }
        return checks.ToArray();
    }
    private static IEnumerable<DependencyObject> WalkPcConsent(DependencyObject parent){yield return parent;for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)foreach(var child in WalkPcConsent(VisualTreeHelper.GetChild(parent,i)))yield return child;}
}
