using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using ShareLink.BrowserSharing;

namespace ShareLink.BrowserShareProbe;

internal static class SingleReceiverChecks
{
    private static HttpClient Client(string source, int port) {
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (context, token) => {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try {
                    socket.Bind(new IPEndPoint(IPAddress.Parse(source), 0));
                    await socket.ConnectAsync(context.DnsEndPoint, token);
                    return new NetworkStream(socket, ownsSocket: true);
                } catch { socket.Dispose(); throw; }
            } };
        return new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
    }
    public static async Task RunAsync(string output) {
        Directory.CreateDirectory(output); var checks = new List<object>();
        void Check(string name, bool passed) { if (!passed) throw new Exception(name); lock(checks) checks.Add(new { name, passed }); }
        string path = Path.Combine(output, "single-fixture.bin"); byte[] bytes = Enumerable.Range(0,65536).Select(i=>(byte)(i%251)).ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        await using (var server = new BrowserFileServer([path], output, trackLocalRequests:true)) {
            await server.StartAsync(IPAddress.Loopback, 0, CancellationToken.None);
            using var first = Client("127.0.0.1",server.Port); using var other = Client("127.0.0.2",server.Port);first.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);other.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);
            Check("invalid route does not reserve the receiver", (await other.GetAsync("/favicon.ico")).StatusCode == HttpStatusCode.NotFound && server.AllowedReceiver is null);
            using var wrongHost = new HttpRequestMessage(HttpMethod.Get,"/");wrongHost.Headers.Host="invalid.example";
            Check("invalid host cannot reserve the receiver", (int)(await other.SendAsync(wrongHost)).StatusCode==421 && server.AllowedReceiver is null);
            Check("first valid page admits one receiver", (await first.GetAsync("/")).StatusCode == HttpStatusCode.OK && server.AllowedReceiver=="127.0.0.1");
            string route="/files/"+server.Files[0].Id;
            foreach(var method in new[]{HttpMethod.Get,HttpMethod.Head})foreach(var url in new[]{"/",route}) {
                using var response=await other.SendAsync(new HttpRequestMessage(method,url));
                Check("second source IP rejected for "+method+" "+url, response.StatusCode==HttpStatusCode.Forbidden);
            }
            Check("trailing slash cannot bypass file admission", (await other.GetAsync(route+"/")).StatusCode==HttpStatusCode.Forbidden);
            Check("encoded route cannot bypass file admission", (await other.GetAsync("/%66iles/"+server.Files[0].Id)).StatusCode==HttpStatusCode.Forbidden);
            using var spoof=new HttpRequestMessage(HttpMethod.Get,route);spoof.Headers.TryAddWithoutValidation("X-Forwarded-For","127.0.0.1");spoof.Headers.TryAddWithoutValidation("Range","bytes=100-199");
            Check("forwarded address and range cannot bypass admission", (await other.SendAsync(spoof)).StatusCode==HttpStatusCode.Forbidden);
            Check("denied requests do not create receiver progress", server.Progress.Snapshot().Length==1 && server.Progress.Snapshot()[0].Address=="127.0.0.1" && server.DownloadRequests==0);
            var ranges=await Task.WhenAll(new[]{"bytes=100-199","bytes=300-499"}.Select(async range=>{
                using var request=new HttpRequestMessage(HttpMethod.Get,route);request.Headers.TryAddWithoutValidation("Range",range);using var response=await first.SendAsync(request);
                Check("authorized range still returns 206",response.StatusCode==HttpStatusCode.PartialContent);return await response.Content.ReadAsByteArrayAsync();
            }));
            Check("parallel ranges from admitted device remain valid",ranges[0].SequenceEqual(bytes[100..200])&&ranges[1].SequenceEqual(bytes[300..500]));
            Check("ownership remains locked after requests finish",server.AllowedReceiver=="127.0.0.1" && (await other.GetAsync("/")).StatusCode==HttpStatusCode.Forbidden);
        }
        await using(var next=new BrowserFileServer([path],output,trackLocalRequests:true)) {
            await next.StartAsync(IPAddress.Loopback,0,CancellationToken.None);using var first=Client("127.0.0.1",next.Port);using var second=Client("127.0.0.2",next.Port);first.DefaultRequestHeaders.Add("X-ShareLink-Session",next.SessionId);second.DefaultRequestHeaders.Add("X-ShareLink-Session",next.SessionId);
            var race=await Task.WhenAll(first.GetAsync("/"),second.GetAsync("/"));
            Check("two concurrent first visits cannot both win",race.Count(r=>r.StatusCode==HttpStatusCode.OK)==1&&race.Count(r=>r.StatusCode==HttpStatusCode.Forbidden)==1);
            foreach(var response in race)response.Dispose();
        }
        await using(var restarted=new BrowserFileServer([path],output,trackLocalRequests:true)) {
            await restarted.StartAsync(IPAddress.Loopback,0,CancellationToken.None);using var second=Client("127.0.0.2",restarted.Port);second.DefaultRequestHeaders.Add("X-ShareLink-Session",restarted.SessionId);
            Check("new sharing session can admit the previously denied device via direct file",(await second.GetAsync("/files/"+restarted.Files[0].Id)).StatusCode==HttpStatusCode.OK&&restarted.AllowedReceiver=="127.0.0.2");
        }
        var gate=new SingleReceiverAccess();
        Check("IPv4-mapped IPv6 is the same admitted receiver",gate.TryAdmit(IPAddress.Parse("::ffff:192.168.49.2"))&&gate.TryAdmit(IPAddress.Parse("192.168.49.2"))&&!gate.TryAdmit(IPAddress.Parse("192.168.49.3")));
        var queried=new List<string>();string? Lookup(string ip){queried.Add(ip);return ip=="192.168.49.2"?"10-20-30-40-50-60":"AA:BB:CC:DD:EE:11";}
        Check("MAC display resolves only the admitted IP",PeerIdentity.AuthorizedDisplay(gate.Address,Lookup)=="已连接设备的MAC地址：10:20:30:40:50:60"&&queried.SequenceEqual(new[]{"192.168.49.2"}));
        Check("unknown MAC never substitutes another hotspot client",PeerIdentity.AuthorizedDisplay(gate.Address,_=>null)=="可下载设备已连接，暂无法读取 MAC 地址");
        Check("no admitted peer never queries a hotspot client",PeerIdentity.AuthorizedDisplay(null,_=>throw new Exception("must not query"))=="当前无可下载设备连接");
        await File.WriteAllTextAsync(Path.Combine(output,"single-receiver-checks.json"),JsonSerializer.Serialize(new{passed=true,scope="real Kestrel requests from two distinct loopback source IPs, concurrent admission and exact-IP MAC resolution; no two physical Wi-Fi clients",checks},new JsonSerializerOptions{WriteIndented=true}));
    }
}
