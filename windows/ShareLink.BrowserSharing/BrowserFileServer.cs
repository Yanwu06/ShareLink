using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ShareLink.BrowserSharing;

internal sealed class SharedFile
{
    public string Id { get; } = RandomNumberGenerator.GetHexString(24).ToLowerInvariant();
    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public long Size { get; }
    public DateTime Modified { get; }
    public string ETag { get; } = "\"" + RandomNumberGenerator.GetHexString(32).ToLowerInvariant() + "\"";
    public SharedFile(string path)
    {
        Path = System.IO.Path.GetFullPath(path);
        var info = new FileInfo(Path);
        if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("请选择存在的普通文件。");
        Size = info.Length; Modified = info.LastWriteTimeUtc;
    }
    public bool Unchanged()
    {
        var info = new FileInfo(Path);
        return info.Exists && info.Length == Size && info.LastWriteTimeUtc == Modified &&
            (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
    }
}

internal sealed class BrowserFileServer : IAsyncDisposable
{
    private WebApplication? app;
    private readonly string output;
    private readonly object logGate = new();
    private readonly bool trackLocalRequests;
    private HashSet<IPAddress> localAddresses = [];
    internal DownloadProgress Progress { get; } = new();
    internal SingleReceiverAccess Access { get; } = new();
    internal string? AllowedReceiver => Access.Address;
    private readonly object filesGate = new();
    private SharedFile[] files;
    private long listingRevision = 1;
    private bool stopped;
    private string? uploadDirectory;
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private readonly string uploadToken = RandomNumberGenerator.GetHexString(48);
    private readonly SemaphoreSlim uploadGate = new(1, 1);
    private int uploadedFiles;
    internal void EnableUploads(string directory) {
        if (!System.IO.Directory.Exists(directory)) throw new IOException("接收文件夹不可用。");
        lock (filesGate) { if (stopped) throw new IOException("分享已结束。"); uploadDirectory = System.IO.Path.GetFullPath(directory); listingRevision++; }
    }
    internal bool UploadsEnabled { get { lock(filesGate)return uploadDirectory is not null; } }
    internal void DisableUploads() { lock(filesGate){if(uploadDirectory is not null){uploadDirectory=null;listingRevision++;}} }
    public SharedFile[] Files { get { lock (filesGate) return files.ToArray(); } }
    internal string SessionId => sessionId;
    public int Port { get; private set; }
    public int DownloadRequests;
    public int ActiveDownloads;
    public int CompletedDownloads;
    public BrowserFileServer(IEnumerable<string> paths, string outputDirectory, bool trackLocalRequests = false)
    {
        files = paths.Select(p => new SharedFile(p)).ToArray();
        if (files.Length == 0 || files.Length > 64) throw new ArgumentException("每次请选择 1 至 64 个文件。");
        output = outputDirectory;
        this.trackLocalRequests = trackLocalRequests;
    }
    public SharedFile[] AddFiles(IEnumerable<string> paths)
    {
        lock (filesGate) {
            if (stopped) throw new IOException("分享已结束，请重新开启分享。");
            var pending = paths.Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !files.Any(f => f.Path.Equals(p, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (files.Length + pending.Length > 64) throw new IOException("每次最多分享 64 个文件。");
            // Prepare every entry before committing: a failed batch must not
            // expose half the selection or replace existing IDs and validators.
            var additions = pending.Select(p => new SharedFile(p)).ToArray();
            if (additions.Length > 0) { files = [.. files, .. additions]; listingRevision++; }
            return files.ToArray();
        }
    }
    private object Manifest()
    {
        lock (filesGate) return new { sessionId, uploadToken = uploadDirectory is null ? null : uploadToken, revision = listingRevision, files = files.Select(f => new { id = f.Id, name = f.Name, size = f.Size }).ToArray() };
    }
    public async Task StartAsync(IPAddress address, int port, CancellationToken token)
    {
        localAddresses = NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address).ToHashSet();
        localAddresses.Add(address);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => {
            k.Listen(address, port);
            k.Limits.MaxRequestBodySize = null;
            k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        app = builder.Build();
        app.Use(async (context, next) => {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18));
            context.Items["listingNonce"] = nonce;
            context.Response.Headers["Content-Security-Policy"] = $"default-src 'none'; script-src 'nonce-{nonce}'; connect-src 'self'; style-src 'unsafe-inline'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            string requestedHost = context.Request.Host.Host.TrimEnd('.');
            if (!(IPAddress.TryParse(requestedHost, out var host) && host.Equals(address))) {
                context.Response.StatusCode = 421; return;
            }
            try { await next(context); }
            finally {
                if (context.Items.ContainsKey("download")) {
                    Interlocked.Decrement(ref ActiveDownloads);
                    if (!context.RequestAborted.IsCancellationRequested && context.Response.StatusCode is 200 or 206)
                        Interlocked.Increment(ref CompletedDownloads);
                    if (context.Items.TryGetValue("progressPeer", out var peer) && context.Items["progressBody"] is DownloadBody body)
                        Progress.Finish((string)peer!, (string)context.Items["progressFile"]!, body.Sent && !context.RequestAborted.IsCancellationRequested && context.Response.StatusCode is 200 or 206, (long)context.Items["progressAttempt"]!);
                }
                var record = new { at = DateTimeOffset.Now, method = context.Request.Method, path = context.Request.Path.Value,
                    remote = context.Connection.RemoteIpAddress?.ToString(), status = context.Response.StatusCode,
                    range = context.Request.Headers.Range.ToString(), userAgent = context.Request.Headers.UserAgent.ToString(),
                    origin = context.Request.Headers.Origin.ToString(), host = context.Request.Host.ToString() };
                lock (logGate) File.AppendAllText(System.IO.Path.Combine(output, "http-requests.jsonl"), System.Text.Json.JsonSerializer.Serialize(record) + "\n");
            }
        });
        app.MapMethods("/", ["GET", "HEAD"], (HttpContext c) => { if (AdmissionFailure(c) is { } rejected) return rejected; if (Receiver(c) is string peer) Progress.Visit(peer); return Results.Content(Page((string)c.Items["listingNonce"]!), "text/html; charset=utf-8"); });
        app.MapMethods("/manifest.json", ["GET", "HEAD"], (HttpContext c) => !MatchesSession(c) ? Results.StatusCode(410) : AdmissionFailure(c) ?? Results.Json(Manifest()));
        app.MapMethods("/files/{id}", ["GET", "HEAD"], (HttpContext c, string id) => {
            var file = Files.SingleOrDefault(f => f.Id == id);
            if (file is null) return Results.NotFound();
            if (!file.Unchanged()) return Results.StatusCode(410);
            if (AdmissionFailure(c) is { } rejected) return rejected;
            if (HttpMethods.IsGet(c.Request.Method)) {
                Interlocked.Increment(ref DownloadRequests); Interlocked.Increment(ref ActiveDownloads);
                c.Items["download"] = true;
                if (Receiver(c) is string peer) {
                    long rangeStart = 0;
                    if (RangeHeaderValue.TryParse(c.Request.Headers.Range.ToString(), out var ranges) && ranges.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase) && ranges.Ranges.Count == 1) {
                        var range = ranges.Ranges.Single();
                        rangeStart = range.From ?? (range.To is > 0 ? Math.Max(0, file.Size - range.To.Value) : 0);
                    }
                    long attempt = Progress.Begin(peer, file, rangeStart);
                    var body = new DownloadBody(c.Features.Get<IHttpResponseBodyFeature>()!, (offset,read) => Progress.Bytes(peer,file.Id,offset,read,attempt),file.Size == 0);
                    c.Features.Set<IHttpResponseBodyFeature>(body);
                    c.Items["progressPeer"] = peer; c.Items["progressFile"] = file.Id; c.Items["progressBody"] = body; c.Items["progressAttempt"] = attempt;
                }
            }
            // Kestrel streams directly to browser download management, with independent
            // file handles, HEAD, Content-Disposition, ETag and single-range responses.
            return Results.File(file.Path, "application/octet-stream", file.Name,
                lastModified: new DateTimeOffset(file.Modified), entityTag: new EntityTagHeaderValue(file.ETag), enableRangeProcessing: true);
        });
        app.MapPost("/upload", async (HttpContext c) => {
            if (!MatchesSession(c)) return Results.StatusCode(410);
            if (AdmissionFailure(c) is { } rejected) return rejected;
            string destinationDirectory;
            lock(filesGate){
                if(uploadDirectory is null || c.Request.Headers["X-ShareLink-Upload"] != uploadToken)return Results.StatusCode(403);
                // This request was explicitly admitted; disabling uploads affects subsequent requests.
                destinationDirectory=uploadDirectory;
            }
            if (c.Request.Headers.Origin.Count > 0 && c.Request.Headers.Origin != $"http://{c.Request.Host}") return Results.StatusCode(403);
            if (c.Request.ContentLength is not long size || size < 0) return Results.StatusCode(413);
            if (!await uploadGate.WaitAsync(0, c.RequestAborted)) return Results.StatusCode(409);
            Interlocked.Increment(ref ActiveDownloads);
            string? pending = null;
            string? uploadPeer = Receiver(c), progressId = null, savedName = null;
            bool saved = false;
            try {
                if (uploadedFiles >= 64) return Results.StatusCode(413);
                string name = Uri.UnescapeDataString(c.Request.Headers["X-ShareLink-Filename"].ToString());
                if (string.IsNullOrWhiteSpace(name) || name.Length > 180 || name.Contains('/') || name.Contains('\\') || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')) return Results.BadRequest();
                if (uploadPeer is not null) progressId = Progress.BeginUpload(uploadPeer, name, size);
                pending = System.IO.Path.Combine(destinationDirectory, ".sharelink-upload-" + Guid.NewGuid().ToString("N") + ".part");
                await using (var target = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true)) {
                    var block = new byte[65536]; long received = 0;
                    while (received < size) { int n = await c.Request.Body.ReadAsync(block.AsMemory(0, (int)Math.Min(block.Length, size - received)), c.RequestAborted); if (n == 0) throw new IOException("上传中断。"); await target.WriteAsync(block.AsMemory(0, n), c.RequestAborted); if (progressId is not null) Progress.Bytes(uploadPeer!, progressId, received, n); received += n; }
                    await target.FlushAsync(c.RequestAborted);
                }
                lock (filesGate) {
                    if (stopped || c.RequestAborted.IsCancellationRequested) return Results.StatusCode(410);
                    string destination = System.IO.Path.Combine(destinationDirectory, name);
                    if (File.Exists(destination)) destination = System.IO.Path.Combine(destinationDirectory, System.IO.Path.GetFileNameWithoutExtension(name) + "-" + Guid.NewGuid().ToString("N")[..8] + System.IO.Path.GetExtension(name));
                    File.Move(pending, destination, false); pending = null; uploadedFiles++;
                    saved = true; savedName = System.IO.Path.GetFileName(destination);
                    return Results.Json(new { name = System.IO.Path.GetFileName(destination), size });
                }
            } catch (IOException) { return Results.StatusCode(507); }
              catch (UnauthorizedAccessException) { return Results.StatusCode(507); }
            finally { if (pending is not null) { try { File.Delete(pending); } catch { } } if (progressId is not null) Progress.Finish(uploadPeer!, progressId, saved, savedName: savedName); Interlocked.Decrement(ref ActiveDownloads); uploadGate.Release(); }
        });
        await app.StartAsync(token);
        Port = app.Urls.Select(u => new Uri(u)).First(u => u.Host == address.ToString()).Port;
    }
    private bool MatchesSession(HttpContext context) {
        bool query = context.Request.Query.TryGetValue("session",out var q), header = context.Request.Headers.TryGetValue("X-ShareLink-Session",out var h);
        return (query || header) && (!query || q.Count == 1 && q[0] == sessionId) && (!header || h.Count == 1 && h[0] == sessionId);
    }
    private IResult? AdmissionFailure(HttpContext context)
    {
        string? peer = Receiver(context);
        if (peer is null || Access.TryAdmit(IPAddress.Parse(peer))) return null;
        return Results.Text("为保证传输体验，仅可使用一台设备连接。当前分享已有接收设备，请发送方结束分享后重试。",
            "text/plain; charset=utf-8", statusCode: StatusCodes.Status403Forbidden);
    }
    private string? Receiver(HttpContext context) => NormalizeAddress(context.Connection.RemoteIpAddress) is { } peer &&
        (trackLocalRequests || (!IPAddress.IsLoopback(peer) && !localAddresses.Contains(peer) && !peer.Equals(context.Connection.LocalIpAddress))) ? peer.ToString() : null;
    private static IPAddress? NormalizeAddress(IPAddress? address) => address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address;
    private string Page(string nonce)
    {
        static string Escape(string s) => WebUtility.HtmlEncode(s);
        string body = "<p>仅提供本次选定的文件，请点击下载并在浏览器中确认保存。</p><ul id=\"shared-files\">" +
            string.Concat(Files.Select(f => $"<li data-file-id=\"{f.Id}\"><div>{Escape(f.Name)}</div><small>{ShareText.Size(f.Size)}</small><a href=\"/files/{f.Id}\" download>下载</a></li>")) + "</ul>";
        return "<!doctype html><html lang=\"zh-CN\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>ShareLink 文件下载</title>" +
            "<style>" + BrowserPageAssets.Style + "</style>" +
            "<main data-sharelink-session=\"" + sessionId + "\"><h1>ShareLink</h1>" + body + "<p><small>结束分享后，此页面将无法继续下载。</small></p>" + BrowserPageAssets.Footer + "</main>" + $"<script nonce=\"{nonce}\">" + BrowserListing.Script + BrowserPageAssets.CopyScript + BrowserPageAssets.UploadScript + "</script></html>";
    }
    public async ValueTask DisposeAsync()
    {
        lock (filesGate) stopped = true;
        if (app is not null) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)); await app.StopAsync(timeout.Token); await app.DisposeAsync(); app = null; }
    }
}
