using System.Text.Json;
namespace ShareLink;
public partial class MainWindow
{
    internal static string[] CheckPhoneReceiveApproval()
    {
        var checks=new List<string>(); void Check(string n,bool v){if(!v)throw new InvalidOperationException(n);checks.Add(n);}
        JsonElement Offer(string id="offer-one",string? token=null,object[]? files=null)=>JsonSerializer.SerializeToElement(new{id,token=token??new string('a',64),files=files??[new{name="生成文件.bin",size=33554432L},new{name="empty.txt",size=0L}]});
        JsonElement Transfer(object[]? files=null,long total=33554432L,string direction="receive",string state="awaiting_accept")=>JsonSerializer.SerializeToElement(new{direction,state,total,files=files??[new{name="生成文件.bin",size=33554432L},new{name="empty.txt",size=0L}]});
        string fingerprint=new string('b',64);var approval=PhoneReceiveApproval.Create(Offer(),fingerprint);
        Check("BLE approval retains exact file sizes including empty files",approval.Total==33554432&&approval.MatchesTransfer(Transfer()));
        string original=approval.Message("Generated phone","generated-save",false),withWarning=approval.Message("Generated phone","generated-save",true);
        Check("verified existing personal hotspot keeps the original receive message",original=="来自「Generated phone」\n\n生成文件.bin\nempty.txt\n\n32.0 MB · 保存到 generated-save"&&!original.Contains(PhoneReceiveApproval.NetworkWarning));
        Check("temporary WiFi warning appears once in the same receive message",withWarning==original+"\n\n"+"无线网络需要切换至临时WiFi以进行文件传输，此期间可能会造成网络中断，文件传输完毕后即恢复原有无线网络，是否继续？");
        Check("exact offer can remain pending while receive dialog is open",approval.MatchesOffer(Offer(),fingerprint.ToUpperInvariant()));
        Check("different offer id cannot retain the confirmation",!approval.MatchesOffer(Offer(id:"two"),fingerprint));
        Check("different pull token cannot inherit consent",!approval.MatchesOffer(Offer(token:new string('c',64)),fingerprint));
        Check("different authenticated identity cannot inherit consent",!approval.MatchesOffer(Offer(),new string('d',64)));
        foreach(var changed in new object[][] { [new{name="changed.bin",size=33554432L},new{name="empty.txt",size=0L}], [new{name="生成文件.bin",size=33554431L},new{name="empty.txt",size=0L}], [new{name="empty.txt",size=0L},new{name="生成文件.bin",size=33554432L}], [new{name="生成文件.bin",size=33554432L}] }) {
            Check("changed offer manifest closes existing consent "+checks.Count,!approval.MatchesOffer(Offer(files:changed),fingerprint));
            Check("changed TLS manifest cannot use prior consent "+checks.Count,!approval.MatchesTransfer(Transfer(files:changed)));
        }
        Check("mismatched transfer total cannot use prior consent",!approval.MatchesTransfer(Transfer(total:42)));
        foreach(var state in new[]{"connecting","transferring","cancelled","completed","rejected"})Check("consent cannot revive transfer in "+state,!approval.MatchesTransfer(Transfer(state:state)));
        Check("receive consent never authorizes sending",!approval.MatchesTransfer(Transfer(direction:"send")));
        foreach(var malformed in new[]{Offer(files:[]),Offer(token:"bad"),Offer(files:[new{name="bad.bin",size=-1L}]),Offer(files:[new{name="",size=0L}])}) {bool refused=false;try{PhoneReceiveApproval.Create(malformed,fingerprint);}catch(InvalidOperationException){refused=true;}Check("malformed receive offer is refused "+checks.Count,refused);}
        bool overflow=false;try{PhoneReceiveApproval.Create(Offer(files:[new{name="a",size=long.MaxValue},new{name="b",size=1L}]),fingerprint);}catch(OverflowException){overflow=true;}Check("cumulative size overflow cannot authorize files",overflow);
        return checks.ToArray();
    }
}
