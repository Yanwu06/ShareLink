using System.IO;
using System.Text.Json;
namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckUserExitHistoryAsync(string output)
    {
        var checks = new List<string>();
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        JsonElement Snapshot(params object[] tasks) => JsonSerializer.SerializeToElement(new { transfers=tasks, sentinel="untouched" });
        object Row(string id, string state, string error="") => new { id, state, direction="receive", error, bytes=17, total=32, files=new[]{new{name="generated.bin",size=32}} };
        JsonElement Find(JsonElement snapshot,string id) => snapshot.GetProperty("transfers").EnumerateArray().Single(t=>Text(t,"id")==id);
        string directory=Path.Combine(output,"user-exit-history");
        var history=new UserExitHistory(directory);
        var ordinary=Snapshot(Row("ordinary","cancelled"));
        Check("ordinary cancellation creates no exit ledger",history.RememberActive(ordinary).Length==0&&!Directory.Exists(directory));
        Check("ordinary cancellation has no user-exit overlay",Text(Find(history.Apply(ordinary),"ordinary"),"error")=="");
        var before=Snapshot(Row("send","hashing"),Row("receive","transferring"),Row("pending","awaiting_accept"),Row("done","completed"),Row("failed","failed"),Row("ordinary","cancelled"),Row("rejected","rejected"));
        Check("exit remembers only nonterminal tasks",history.RememberActive(before).Order().SequenceEqual(new[]{"pending","receive","send"}));
        string ledger=Path.Combine(directory,"user-terminated-tasks.json");
        Check("exit intent is durable before native cancellation",File.Exists(ledger)&&JsonSerializer.Deserialize<string[]>(File.ReadAllText(ledger))!.Length==3);
        var after=Snapshot(Row("send","cancelled"),Row("receive","cancelled"),Row("ordinary","cancelled"));
        var applied=history.Apply(after);
        Check("only matching cancelled tasks show user termination",Text(Find(applied,"send"),"error")==UserExitHistory.Label&&Text(Find(applied,"receive"),"error")==UserExitHistory.Label&&Text(Find(applied,"ordinary"),"error")=="");
        Check("overlay preserves native snapshot and unrelated fields",Text(Find(after,"send"),"error")==""&&Text(applied,"sentinel")=="untouched"&&Number(Find(applied,"send"),"bytes")==17);
        Check("a fresh process reloads exit intent",Text(Find(new UserExitHistory(directory).Apply(after),"send"),"error")==UserExitHistory.Label);
        foreach(string state in new[]{"completed","failed","rejected","transferring"})
            Check("exit intent never relabels a task that became "+state,Text(Find(history.Apply(Snapshot(Row("send",state,"original"))),"send"),"error")=="original");
        history.RememberActive(Snapshot(Row("later","verifying")));
        Check("a later exit preserves earlier task reasons",Text(Find(new UserExitHistory(directory).Apply(after),"receive"),"error")==UserExitHistory.Label);
        string failure=Path.Combine(output,"user-exit-write-failure");Directory.CreateDirectory(Path.Combine(failure,"user-terminated-tasks.json.tmp"));
        var unwritable=new UserExitHistory(failure);bool refused=false;
        try{unwritable.RememberActive(Snapshot(Row("unsaved","transferring")));}catch(IOException){refused=true;}catch(UnauthorizedAccessException){refused=true;}
        Check("failed durable write aborts without marking memory",refused&&!File.Exists(Path.Combine(failure,"user-terminated-tasks.json"))&&Text(Find(unwritable.Apply(Snapshot(Row("unsaved","cancelled"))),"unsaved"),"error")=="");
        var view=new TransferView("receive");view.Update(Find(applied,"receive"));
        Check("history displays the exact user termination label once",view.StateLabel==UserExitHistory.Label&&!view.Detail.Contains(UserExitHistory.Label)&&!view.Active);
        view.Update(Find(applied,"ordinary"));Check("ordinary cancellation remains displayed as cancelled",view.StateLabel=="已取消");
        foreach(string transportError in new[]{
            "你的主机中的软件中止了一个已建立的连接。 (os error 10053)",
            "Connection ended before the complete file arrived: peer closed connection without sending TLS close_notify: https://docs.rs/rustls/latest/rustls/manual/_03_howto/index.html#unexpected-eof"}) {
            var failed=Row("interrupted","failed",transportError);var original=JsonSerializer.SerializeToElement(failed);
            view.Update(original);
            Check("observed interruption is readable without claiming a user exit: "+(transportError.Contains("10053")?"winsock":"tls"),
                view.StateLabel=="传输失败"&&!view.Active&&view.Detail.Contains("连接已中断，文件未传完。请重新连接后发送。")
                &&!view.Detail.Contains(transportError)&&!view.Detail.Contains(UserExitHistory.Label)&&!view.Detail.Contains("https://"));
            Check("presentation preserves native failure and diagnostic cause: "+(transportError.Contains("10053")?"winsock":"tls"),
                Text(original,"state")=="failed"&&Text(original,"error")==transportError);
        }
        foreach(string preciseError in new[]{"SHA-256 mismatch","磁盘可用空间不足","Unexpected storage failure (os error 1005)"}) {
            view.Update(JsonSerializer.SerializeToElement(Row("precise","failed",preciseError)));
            Check("unrelated failure retains its specific cause: "+preciseError,view.StateLabel=="传输失败"&&view.Detail.Contains(preciseError));
        }
        var interruptedBatch=JsonSerializer.SerializeToElement(new{id="broken-batch",state="failed",direction="receive",error="Connection ended before the complete file arrived: peer closed connection without sending TLS close_notify",total=64,bytes=41,files=new[]{new{name="saved.bin",size=32,path=Path.Combine(output,"saved.bin")},new{name="unfinished.bin",size=32,path=""}}});
        var kept=new TransferView("broken-batch",0);var pending=new TransferView("broken-batch",1);kept.Update(interruptedBatch);pending.Update(interruptedBatch);
        Check("transport failure keeps saved prefix while explaining only the incomplete file",kept.StateLabel=="已保存"&&!kept.Detail.Contains("连接已中断")&&pending.StateLabel=="传输失败"&&pending.Detail.Contains("连接已中断"));
        var batch=JsonSerializer.SerializeToElement(new{id="receive",state="cancelled",direction="receive",error=UserExitHistory.Label,total=64,bytes=41,files=new[]{new{name="saved.bin",size=32,path=Path.Combine(output,"saved.bin")},new{name="unfinished.bin",size=32,path=""}}});
        var saved=new TransferView("receive",0);var unfinished=new TransferView("receive",1);saved.Update(batch);unfinished.Update(batch);
        Check("saved batch prefix stays saved while incomplete row shows user termination",saved.StateLabel=="已保存"&&unfinished.StateLabel==UserExitHistory.Label);
        var owner=new MainWindow(Path.Combine(output,"exit-history-native"),desktopTest:true);
        using(var native=new NativeCore(owner.CoreConfiguration())){
            string source=Path.Combine(output,"exit-history-generated.bin");await File.WriteAllBytesAsync(source,new byte[32]);
            var offer=native.Call(new{op="prepare_pull",peerName="Generated PC",peerDeviceType="computer",files=new[]{new{path=source,name=Path.GetFileName(source)}}});string id=Text(offer,"id");
            owner.core=native;owner.pcRefreshing=true;
            try{
                owner.ExitHistory.RememberActive(native.Call(new{op="snapshot"}));native.Call(new{op="cancel",id});
                await owner.RefreshState();
                Check("normal UI refresh renders a real native cancelled task with durable exit reason",owner.transferViews[id].StateLabel==UserExitHistory.Label);
                Check("user-exit presentation does not rewrite native history or source",Text(Find(native.Call(new{op="snapshot"}),id),"state")=="cancelled"&&Text(Find(native.Call(new{op="snapshot"}),id),"error")!=UserExitHistory.Label&&new FileInfo(source).Length==32);
            }finally{owner.core=null;}
        }
        return checks.ToArray();
    }
}
