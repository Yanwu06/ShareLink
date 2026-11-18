namespace ShareLink;
internal static class HotspotVisibility
{
    // Microsoft documents scan completion or a four-second timeout before querying.
    // At most two scans of the negotiated SSID; association happens only after success.
    internal static async Task WaitAsync(Func<Task> scan, Func<Task<bool>> visible,
        Func<TimeSpan,CancellationToken,Task> wait, CancellationToken token, Action? onScanBusy = null)
    {
        System.ComponentModel.Win32Exception? busy = null;
        for (int attempt = 0; attempt < 2; attempt++) {
            token.ThrowIfCancellationRequested(); busy = null;
            try { await scan(); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 170) { busy = ex; onScanBusy?.Invoke(); }
            token.ThrowIfCancellationRequested();
            await wait(TimeSpan.FromSeconds(4), token); token.ThrowIfCancellationRequested();
            if (await visible()) return;
        }
        if (busy is not null) throw busy;
        throw new TimeoutException("未扫描到本次临时热点，请保持双方 ShareLink 打开后重试；已保留原网络恢复记录。");
    }
    internal static async Task<string[]> ChecksAsync()
    {
        var checks = new List<string>();
        void Check(string name,bool value) { if(!value)throw new InvalidOperationException(name);checks.Add(name); }
        var order = new List<string>(); int scans=0,queries=0,waits=0;
        Task Scan() { scans++;order.Add("scan");return Task.CompletedTask; }
        Task Wait(TimeSpan delay,CancellationToken token) { token.ThrowIfCancellationRequested(); Check("bounded scan timeout is four seconds " + waits,delay==TimeSpan.FromSeconds(4)); waits++;order.Add("wait");return Task.CompletedTask; }
        Task<bool> Visible() {queries++;order.Add("query");return Task.FromResult(scans==2);}
        await WaitAsync(Scan,Visible,Wait,CancellationToken.None);
        Check("fresh hotspot waits for scan result before association",order.SequenceEqual(new[]{"scan","wait","query","scan","wait","query"}));
        Check("late hotspot becomes available on bounded second scan",scans==2&&queries==2);
        scans=queries=0;
        bool absent=false;try{await WaitAsync(Scan,()=>{queries++;return Task.FromResult(false);},(_,_)=>Task.CompletedTask,CancellationToken.None);}catch(TimeoutException){absent=true;}
        Check("absent hotspot cannot proceed to association or loop forever",absent&&scans==2&&queries==2);
        scans=0;using var cancelled=new CancellationTokenSource();cancelled.Cancel();bool cancelledBeforeScan=false;
        try{await WaitAsync(Scan,Visible,Wait,cancelled.Token);}catch(OperationCanceledException){cancelledBeforeScan=true;}
        Check("cancelled setup performs no scan",cancelledBeforeScan&&scans==0);
        bool denied=false;queries=0;
        try{await WaitAsync(()=>throw new System.ComponentModel.Win32Exception(5),Visible,Wait,CancellationToken.None);}catch(System.ComponentModel.Win32Exception e){denied=e.NativeErrorCode==5;}
        Check("permission failure is surfaced without bypass or association",denied&&queries==0);
        scans=queries=waits=0;int busyEvents=0;
        await WaitAsync(()=>{scans++;if(scans==1)throw new System.ComponentModel.Win32Exception(170);return Task.CompletedTask;},()=>{queries++;return Task.FromResult(scans==2);},(_,_)=>{waits++;return Task.CompletedTask;},CancellationToken.None,()=>busyEvents++);
        Check("busy first scan waits and retries only the negotiated hotspot within the original budget",scans==2&&queries==2&&waits==2&&busyEvents==1);
        scans=queries=waits=0;
        await WaitAsync(()=>{scans++;throw new System.ComponentModel.Win32Exception(170);},()=>{queries++;return Task.FromResult(true);},(_,_)=>{waits++;return Task.CompletedTask;},CancellationToken.None);
        Check("busy scan can use the specifically queried negotiated hotspot after waiting",scans==1&&queries==1&&waits==1);
        scans=queries=waits=0;bool stillBusy=false;
        try{await WaitAsync(()=>{scans++;throw new System.ComponentModel.Win32Exception(170);},()=>{queries++;return Task.FromResult(false);},(_,_)=>{waits++;return Task.CompletedTask;},CancellationToken.None);}catch(System.ComponentModel.Win32Exception e){stillBusy=e.NativeErrorCode==170;}
        Check("persistent busy preserves its error and never exceeds two waits or scans",stillBusy&&scans==2&&queries==2&&waits==2);
        scans=queries=waits=0;using var busyCancellation=new CancellationTokenSource();bool busyCancelled=false;
        try{await WaitAsync(()=>{scans++;busyCancellation.Cancel();throw new System.ComponentModel.Win32Exception(170);},()=>{queries++;return Task.FromResult(true);},(_,_)=>{waits++;return Task.CompletedTask;},busyCancellation.Token);}catch(OperationCanceledException){busyCancelled=true;}
        Check("cancellation during busy scan performs no wait query or association",busyCancelled&&scans==1&&waits==0&&queries==0);
        scans=queries=0;bool queryDenied=false;
        try{await WaitAsync(()=>{scans++;throw new System.ComponentModel.Win32Exception(170);},()=>{queries++;throw new System.ComponentModel.Win32Exception(5);},(_,_)=>Task.CompletedTask,CancellationToken.None);}catch(System.ComponentModel.Win32Exception e){queryDenied=e.NativeErrorCode==5;}
        Check("busy scan does not suppress permission failure in the result query",queryDenied&&scans==1&&queries==1);
        return checks.ToArray();
    }
}
