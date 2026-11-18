using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using ShareLink.BrowserSharing;

namespace ShareLink.BrowserShareProbe;

internal static class AppendChecks
{
    public static async Task RunAsync(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(new { name, passed }); }
        string original = Path.Combine(output,"original-32MB.bin");
        await using (var file = File.Create(original)) file.SetLength(32 * 1024 * 1024);
        string added = Path.Combine(output,"新增 & 中文.txt"); await File.WriteAllTextAsync(added,"ShareLink append fixture");
        string next = Path.Combine(output,"second.txt"); await File.WriteAllTextAsync(next,"second file");
        await using var server = new BrowserFileServer([original],output,trackLocalRequests:true);
        await server.StartAsync(IPAddress.Loopback,0,CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/") };
        string id = server.Files[0].Id; string etag = server.Files[0].ETag; int port = server.Port;
        client.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);
        using var page = await client.GetAsync("/"); string html = await page.Content.ReadAsStringAsync();
        string policy = page.Headers.GetValues("Content-Security-Policy").Single();
        string nonce = policy.Split("'nonce-")[1].Split('\'')[0];
        Check("listing script is permitted only by its response nonce and polls same-origin",html.Contains($"<script nonce=\"{nonce}\">") && html.Contains("id=\"shared-files\"") && policy.Contains("connect-src 'self'") && !policy.Contains("script-src 'unsafe-inline'"));
        using var before = JsonDocument.Parse(await client.GetStringAsync("/manifest.json"));
        long revision = before.RootElement.GetProperty("revision").GetInt64();
        Check("manifest initially lists one selected file",before.RootElement.GetProperty("files").GetArrayLength()==1);
        using var download = await client.GetAsync("/files/"+id,HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await download.Content.ReadAsStreamAsync();
        byte[] first = new byte[1024]; int count = await stream.ReadAsync(first);
        Check("download is still active before appending",count>0 && server.ActiveDownloads>0);
        bool failed = false; try { server.AddFiles([added,Path.Combine(output,"missing.txt")]); } catch (IOException) { failed=true; }
        Check("failed batch never exposes a partial addition",failed && server.Files.Length==1);
        server.AddFiles([added,next]);
        Check("append keeps listener and existing file metadata",server.Port==port && server.Files[0].Id==id && server.Files[0].ETag==etag && server.ActiveDownloads>0);
        using var after = JsonDocument.Parse(await client.GetStringAsync("/manifest.json"));
        Check("manifest revision advances atomically with both new files",after.RootElement.GetProperty("revision").GetInt64()==revision+1 && after.RootElement.GetProperty("files").GetArrayLength()==3);
        Check("manifest preserves exact Unicode and ampersand names",after.RootElement.GetProperty("files")[1].GetProperty("name").GetString()==Path.GetFileName(added));
        server.AddFiles([added,original]);
        using var duplicate = JsonDocument.Parse(await client.GetStringAsync("/manifest.json"));
        Check("duplicate paths do not add IDs or increase revision",server.Files.Length==3 && duplicate.RootElement.GetProperty("revision").GetInt64()==revision+1);
        long received=count; byte[] buffer=new byte[65536]; int read;
        while((read=await stream.ReadAsync(buffer))>0) { received+=read; CheckZeros(buffer,read); }
        Check("original streaming download survives append byte for byte",received==32L*1024*1024 && first.Take(count).All(b=>b==0));
        Check("new file is immediately downloadable",await client.GetStringAsync("/files/"+server.Files[1].Id)=="ShareLink append fixture");
        using var rangeRequest=new HttpRequestMessage(HttpMethod.Get,"/files/"+id); rangeRequest.Headers.TryAddWithoutValidation("Range","bytes=100-199");
        using var range=await client.SendAsync(rangeRequest);
        Check("old download URL and ETag still support resume",range.StatusCode==HttpStatusCode.PartialContent && range.Headers.ETag?.Tag==etag && (await range.Content.ReadAsByteArrayAsync()).Length==100);
        using var denied = new HttpRequestMessage(HttpMethod.Get,$"http://127.0.0.1:{port}/manifest.json");
        using var other = SingleReceiverChecksForAppend.Client("127.0.0.2");other.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);
        using var denial = await other.SendAsync(denied);
        Check("second IP cannot obtain a live manifest",denial.StatusCode==HttpStatusCode.Forbidden);
        using var headDenial=await other.SendAsync(new HttpRequestMessage(HttpMethod.Head,$"http://127.0.0.1:{port}/manifest.json"));
        Check("second IP cannot bypass manifest admission with HEAD",headDenial.StatusCode==HttpStatusCode.Forbidden);
        bool limitFailed=false; try { server.AddFiles(Enumerable.Range(0,64).Select(i=>Path.Combine(output,"not-created-"+i))); } catch(IOException) {limitFailed=true;}
        Check("limit failure leaves existing selection unchanged",limitFailed && server.Files.Length==3);
        await server.DisposeAsync(); bool ended=false; try {server.AddFiles([added]);}catch(IOException){ended=true;}
        Check("ended session refuses new additions",ended);
        await File.WriteAllTextAsync(Path.Combine(output,"append-checks.json"),JsonSerializer.Serialize(new {passed=true,checks},new JsonSerializerOptions{WriteIndented=true}));
    }
    private static void CheckZeros(byte[] buffer,int count) { if(buffer.Take(count).Any(b=>b!=0)) throw new InvalidOperationException("stream bytes changed"); }
}
internal static class SingleReceiverChecksForAppend
{
    internal static HttpClient Client(string source)
    {
        var handler=new SocketsHttpHandler {UseProxy=false};
        handler.ConnectCallback=async(context,token)=>{
            var socket=new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,System.Net.Sockets.SocketType.Stream,System.Net.Sockets.ProtocolType.Tcp);
            try {socket.Bind(new IPEndPoint(IPAddress.Parse(source),0));await socket.ConnectAsync(context.DnsEndPoint,token);return new System.Net.Sockets.NetworkStream(socket,ownsSocket:true);}catch{socket.Dispose();throw;}
        };
        return new HttpClient(handler);
    }
}
