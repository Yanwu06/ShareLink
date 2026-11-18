namespace ShareLink;
internal static class NearbyLinkReuseChecks
{
    internal static async Task<string[]> RunAsync()
    {
        var checks=new List<string>();void Check(string name,bool passed){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
        string fingerprint=new('a',64);var peer=new Peer("generated","Generated PC","127.0.0.1",53317,fingerprint,true);
        int currentCalls=0,probeCalls=0;
        bool Current(Peer _){currentCalls++;return true;}
        Task<string> Probe(Peer _){probeCalls++;return Task.FromResult(fingerprint);}
        Check("no cached endpoint never starts a probe",await NearbyLinkReuse.FindAsync(null,fingerprint,Current,Probe) is null&&currentCalls==0&&probeCalls==0);
        Check("cached endpoint from another paired identity is never used",await NearbyLinkReuse.FindAsync(peer,new string('b',64),Current,Probe) is null&&currentCalls==0&&probeCalls==0);
        Check("a stale temporary profile never probes through the original network",await NearbyLinkReuse.FindAsync(peer,fingerprint,_=>false,Probe) is null&&probeCalls==0);
        Check("a missing recovery record makes the cached endpoint unusable",await NearbyLinkReuse.FindAsync(peer,fingerprint,_=>throw new System.IO.FileNotFoundException(),Probe) is null&&probeCalls==0);
        Check("a verified current link and paired certificate retain the cached endpoint",ReferenceEquals(await NearbyLinkReuse.FindAsync(peer,fingerprint,Current,Probe),peer)&&probeCalls==1);
        Check("a replaced file-service certificate cannot reuse the cached endpoint",await NearbyLinkReuse.FindAsync(peer,fingerprint,Current,_=>Task.FromResult(new string('c',64))) is null);
        Check("an unreachable cached service requires a fresh route",await NearbyLinkReuse.FindAsync(peer,fingerprint,Current,_=>Task.FromException<string>(new InvalidOperationException("generated offline"))) is null);
        Check("fingerprint comparison preserves case-insensitive certificate identity",ReferenceEquals(await NearbyLinkReuse.FindAsync(peer,fingerprint.ToUpperInvariant(),Current,Probe),peer));
        var cleanup=new NetworkRestoration();var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);int starts=0;
        var first=cleanup.RunAsync(()=>{starts++;return release.Task;});var second=cleanup.RunAsync(()=>{starts++;return Task.CompletedTask;});
        Check("overlapping restoration requests share one cleanup",ReferenceEquals(first,second)&&starts==1);
        var waiting=cleanup.WaitAsync();Check("new route waits while the old network is restoring",!waiting.IsCompleted&&ReferenceEquals(waiting,first));
        release.SetResult();await Task.WhenAll(first,second,waiting);
        Check("completed restoration releases the route gate",cleanup.Pending is null&&cleanup.WaitAsync().IsCompletedSuccessfully);
        await cleanup.RunAsync(()=>{starts++;return Task.CompletedTask;});Check("a later link can start its own cleanup",starts==2&&cleanup.Pending is null);
        var failing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failed=cleanup.RunAsync(()=>failing.Task);var failedWait=cleanup.WaitAsync();failing.SetException(new InvalidOperationException("generated restore failure"));
        bool requestFailed=false,routeFailed=false;try{await failed;}catch(InvalidOperationException){requestFailed=true;}try{await failedWait;}catch(InvalidOperationException){routeFailed=true;}
        Check("restore failure reaches both cleanup and the waiting route",requestFailed&&routeFailed&&cleanup.Pending is null);
        await cleanup.RunAsync(()=>Task.CompletedTask);Check("a failed cleanup can be retried without a stuck gate",cleanup.Pending is null);
        bool syncFailed=false;try{await cleanup.RunAsync(()=>throw new InvalidOperationException("generated immediate failure"));}catch(InvalidOperationException){syncFailed=true;}
        Check("synchronous cleanup failure also releases the gate",syncFailed&&cleanup.Pending is null);
        return checks.ToArray();
    }
}
