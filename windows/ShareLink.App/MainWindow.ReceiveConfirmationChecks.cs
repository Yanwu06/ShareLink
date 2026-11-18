using System.IO;
using System.Text.Json;
namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckReceiveConfirmationAsync(string output)
    {
        var checks=new List<string>();
        void Check(string name,bool passed){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
        JsonElement Snapshot(params object[] entries)=>JsonSerializer.SerializeToElement(new{transfers=entries});
        object Task(string id,string state,string direction="receive")=>new{id,state,direction};
        using(var request=new ReceiveConfirmation("one")){
            request.Observe(Snapshot(Task("one","awaiting_accept"),Task("other","cancelled")));
            Check("the exact pending receive keeps its confirmation alive",!request.Ended);
            request.Observe(Snapshot(Task("one","cancelled"),Task("other","awaiting_accept")));
            Check("sender cancellation ends only the matching receive prompt",request.Cancellation.IsCancellationRequested);
            request.Observe(Snapshot(Task("one","awaiting_accept")));
            Check("a stale pending snapshot cannot revive an ended confirmation",request.Ended);
        }
        foreach(string state in new[]{"cancelled","failed","completed","rejected","transferring","verifying"}){
            using var request=new ReceiveConfirmation("one");request.Observe(Snapshot(Task("one",state)));
            Check("confirmation closes when receive state becomes "+state,request.Ended);
        }
        using(var request=new ReceiveConfirmation("one")){
            request.Observe(Snapshot(Task("different","awaiting_accept")));
            Check("a different pending request cannot retain an obsolete prompt",request.Ended);
        }
        using(var request=new ReceiveConfirmation("one")){
            request.Observe(Snapshot(Task("one","awaiting_accept","send")));
            Check("a sending task cannot authorize receiving",request.Ended);
        }
        using(var request=new ReceiveConfirmation("one")){
            request.Observe(Snapshot());Check("removed task closes its prompt",request.Ended);
        }
        using(var first=new ReceiveConfirmation("one"))using(var second=new ReceiveConfirmation("two")){
            var snapshot=Snapshot(Task("one","cancelled"),Task("two","awaiting_accept"));first.Observe(snapshot);second.Observe(snapshot);
            Check("one ended request does not close an independent pending request",first.Ended&&!second.Ended);
        }
        var owner=new MainWindow(Path.Combine(output,"receive-confirmation-fixture"),desktopTest:true);
        using(var cancelled=new CancellationTokenSource()){
            cancelled.Cancel();Check("a pre-cancelled dialog never opens or requires an initialized owner",!AppDialog.Show(owner,"Generated test","not shown",cancellation:cancelled.Token));
        }
        using(var native=new NativeCore(owner.CoreConfiguration()))
        using(var request=new ReceiveConfirmation("already-gone")){
            owner.core=native;owner.pcRefreshing=true;owner.receiveConfirmation=request;
            try { await owner.RefreshState();Check("real normal snapshot refresh invalidates a disappeared receive confirmation",request.Ended); }
            finally { owner.receiveConfirmation=null;owner.core=null; }
        }
        checks.AddRange(CheckPhoneReceiveApproval());
        return checks.ToArray();
    }
}
