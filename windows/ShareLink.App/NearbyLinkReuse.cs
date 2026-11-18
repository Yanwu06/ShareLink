namespace ShareLink;

// A cached endpoint is usable only on the current owned link and with the paired certificate.
internal static class NearbyLinkReuse
{
    internal static async Task<Peer?> FindAsync(Peer? candidate,string fingerprint,Func<Peer,bool> currentLink,Func<Peer,Task<string>> probe)
    {
        if(candidate is null||!candidate.Fingerprint.Equals(fingerprint,StringComparison.OrdinalIgnoreCase))return null;
        try {
            if(!currentLink(candidate))return null;
            return (await probe(candidate)).Equals(fingerprint,StringComparison.OrdinalIgnoreCase)?candidate:null;
        } catch(Exception) {return null;}
    }
}

// Restoration is shared by all callers; a new route waits for the same cleanup to finish.
internal sealed class NetworkRestoration
{
    internal Task? Pending {get;private set;}
    internal Task RunAsync(Func<Task> cleanup)
    {
        if(Pending is not null)return Pending;
        var completion=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending=completion.Task;_ = CompleteAsync(cleanup,completion);return completion.Task;
    }
    internal Task WaitAsync()=>Pending??Task.CompletedTask;
    private async Task CompleteAsync(Func<Task> cleanup,TaskCompletionSource completion)
    {
        try{await cleanup();completion.TrySetResult();}
        catch(Exception error){completion.TrySetException(error);}
        finally{Pending=null;}
    }
}
