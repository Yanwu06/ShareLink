using System.IO;
using System.IO.Pipelines;
using Microsoft.AspNetCore.Http.Features;

namespace ShareLink.BrowserSharing;

internal sealed record DownloadFileStatus(string Id, string Name, long Size, long Sent, int Active, bool Interrupted, bool Complete, double Speed, string Direction = "download");
internal sealed record BrowserReceiverStatus(string Address, bool PageVisited, DownloadFileStatus[] Files);

internal sealed class DownloadProgress
{
    private sealed class FileProgress(string name, long size)
    {
        public string Name = name;
        public bool Upload;
        public readonly long Size = size;
        public readonly List<(long From, long To)> Ranges = [];
        public int Active;
        public long Attempt;
        public long Covered => Ranges.Sum(range => range.To - range.From);
        public bool Interrupted;
        public bool SuccessfulResponse;
        public long SampledBytes, LastWrite;
        public readonly Queue<(long At, long Bytes)> Samples = new();
        public void Written(int length) {
            long now = Environment.TickCount64;
            if (Samples.Count == 0) Samples.Enqueue((now, SampledBytes));
            SampledBytes += length; LastWrite = now;
            if (now - Samples.Last().At >= 100) Samples.Enqueue((now, SampledBytes));
            while (Samples.Count > 1 && now - Samples.ElementAt(1).At >= 1500) Samples.Dequeue();
        }
        public double Speed {
            get {
                long now = Environment.TickCount64;
                if (Active == 0 || Covered >= Size || Samples.Count == 0 || now - LastWrite > 2000) return 0;
                return (SampledBytes - Samples.Peek().Bytes) * 1000.0 / Math.Max(50, now - Samples.Peek().At);
            }
        }
    }
    private sealed class Receiver
    {
        public bool PageVisited;
        public readonly Dictionary<string, FileProgress> Files = [];
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Receiver> receivers = [];
    private Receiver Get(string address) { if (!receivers.TryGetValue(address, out var receiver)) receivers[address] = receiver = new(); return receiver; }
    public void Visit(string address) { lock (gate) Get(address).PageVisited = true; }
    public long Begin(string address, SharedFile file, long rangeStart = 0)
    {
        lock (gate) {
            var receiver = Get(address);
            if (!receiver.Files.TryGetValue(file.Id, out var progress)) receiver.Files[file.Id] = progress = new(file.Name, file.Size);
            // A full GET or a range beginning at zero is a new download attempt.
            // Old sockets remain counted by BrowserFileServer.ActiveDownloads, but
            // their late writes/finalizers must not alter the new attempt's UI.
            if (rangeStart <= 0) {
                progress.Attempt++; progress.Ranges.Clear(); progress.Active = 0;
                progress.SuccessfulResponse = false; progress.Interrupted = false;
                progress.SampledBytes = progress.LastWrite = 0; progress.Samples.Clear();
            }
            progress.Active++; progress.Interrupted = false;
            return progress.Attempt;
        }
    }
    public string BeginUpload(string address, string name, long size)
    {
        lock (gate) {
            string id = "upload-" + Guid.NewGuid().ToString("N");
            Get(address).Files[id] = new(name, size) { Upload = true, Active = 1, Attempt = 1 };
            return id;
        }
    }
    public void Bytes(string address, string id, long from, int length, long? attempt = null)
    {
        lock (gate) {
            var file = Get(address).Files[id];
            if (attempt.HasValue && file.Attempt != attempt.Value) return;
            long before = file.Covered;
            long begin = Math.Clamp(from, 0, file.Size), end = Math.Clamp(from + length, 0, file.Size);
            if (begin >= end) return;
            var merged = new List<(long From, long To)>(); bool inserted = false;
            foreach (var range in file.Ranges) {
                if (range.To < begin) merged.Add(range);
                else if (end < range.From) { if (!inserted) { merged.Add((begin,end)); inserted = true; } merged.Add(range); }
                else { begin = Math.Min(begin,range.From); end = Math.Max(end,range.To); }
            }
            if (!inserted) merged.Add((begin,end));
            file.Ranges.Clear(); file.Ranges.AddRange(merged);
            int added = (int)(file.Covered - before);
            if (added > 0) file.Written(added);
        }
    }
    public void Finish(string address, string id, bool success, long? attempt = null, string? savedName = null)
    {
        lock (gate) {
            var file = Get(address).Files[id];
            if (attempt.HasValue && file.Attempt != attempt.Value) return;
            file.Active = Math.Max(0, file.Active - 1); file.Interrupted |= !success; file.SuccessfulResponse |= success;
            if (success && savedName is not null) file.Name = savedName;
        }
    }
    public BrowserReceiverStatus[] Snapshot()
    {
        lock (gate) return receivers.Select(pair => new BrowserReceiverStatus(pair.Key, pair.Value.PageVisited,
            pair.Value.Files.Select(entry => new DownloadFileStatus(entry.Key, entry.Value.Name, entry.Value.Size, entry.Value.Covered,entry.Value.Active,entry.Value.Interrupted,entry.Value.Active == 0 && entry.Value.SuccessfulResponse && entry.Value.Covered == entry.Value.Size,entry.Value.Speed,entry.Value.Upload ? "upload" : "download")).ToArray())).ToArray();
    }
}

// Results.File uses SendFileAsync, which bypasses a normal counting Stream.
// Count completed writes at their actual file offsets to merge parallel ranges.
internal sealed class DownloadBody(IHttpResponseBodyFeature original, Action<long,int> report, bool empty) : IHttpResponseBodyFeature
{
    public bool Sent { get; private set; } = empty;
    public Stream Stream => original.Stream;
    public PipeWriter Writer => original.Writer;
    public void DisableBuffering() => original.DisableBuffering();
    public Task StartAsync(CancellationToken token = default) => original.StartAsync(token);
    public Task CompleteAsync() => original.CompleteAsync();
    public async Task SendFileAsync(string path, long offset, long? count, CancellationToken token = default)
    {
        await using var file = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        file.Position = offset; long remaining = count ?? file.Length-offset;
        byte[] block = new byte[65536];
        while (remaining > 0) {
            int read = await file.ReadAsync(block.AsMemory(0,(int)Math.Min(block.Length,remaining)),token);
            if (read == 0) throw new EndOfStreamException("Source ended during download.");
            await original.Stream.WriteAsync(block.AsMemory(0,read),token);
            report(offset,read); offset += read; remaining -= read;
        }
        Sent = true;
    }
}
