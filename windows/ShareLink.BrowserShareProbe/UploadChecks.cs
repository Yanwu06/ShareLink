using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using ShareLink.BrowserSharing;
namespace ShareLink.BrowserShareProbe;
internal static class UploadChecks {
 internal static async Task RunAsync(string output) {
  var checks=new List<object>();void Check(string name,bool passed){if(!passed)throw new Exception(name);checks.Add(new{name,passed});}
  string source=Path.Combine(output,"source.txt"),received=Path.Combine(output,"received");Directory.CreateDirectory(received);File.WriteAllText(source,"selected");
  await using var server=new BrowserFileServer([source],output,true);await server.StartAsync(IPAddress.Loopback,0,CancellationToken.None);
  using var client=new HttpClient(new HttpClientHandler{UseProxy=false}){BaseAddress=new Uri($"http://127.0.0.1:{server.Port}/")};
  client.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);
  async Task<string?> Token(){using var doc=JsonDocument.Parse(await client.GetStringAsync("manifest.json"));return doc.RootElement.GetProperty("uploadToken").GetString();}
  async Task<HttpResponseMessage> Post(string name,string token,byte[] bytes){using var request=new HttpRequestMessage(HttpMethod.Post,"upload"){Content=new ByteArrayContent(bytes)};request.Headers.Add("X-ShareLink-Upload",token);request.Headers.Add("X-ShareLink-Filename",Uri.EscapeDataString(name));return await client.SendAsync(request);}
  Check("uploads hidden until host consent",await Token() is null);
  using(var denied=await Post("a.bin","bad",[1,2,3]))Check("unauthorized upload rejected",denied.StatusCode==HttpStatusCode.Forbidden);
  server.EnableUploads(received);string token=(await Token())!;byte[] body=RandomNumberGenerator.GetBytes(65536);
  using(var bad=await Post("../escape",token,body))Check("path traversal rejected",bad.StatusCode==HttpStatusCode.BadRequest);
  using(var first=await Post("测试.bin",token,body))Check("real binary upload saved",first.IsSuccessStatusCode);
  Check("independent receipt hash equals source",SHA256.HashData(File.ReadAllBytes(Path.Combine(received,"测试.bin"))).SequenceEqual(SHA256.HashData(body)));
  using(var second=await Post("测试.bin",token,[9,8,7]))Check("same name creates another file",second.IsSuccessStatusCode);
  Check("original file preserved",File.ReadAllBytes(Path.Combine(received,"测试.bin")).SequenceEqual(body)&&Directory.GetFiles(received).Length==2);
  using(var empty=await Post("empty.txt",token,[]))Check("empty upload saved",empty.IsSuccessStatusCode&&new FileInfo(Path.Combine(received,"empty.txt")).Length==0);
  using (var socket = new System.Net.Sockets.TcpClient()) {
   await socket.ConnectAsync(IPAddress.Loopback, server.Port); using var stream = socket.GetStream();
   byte[] header=System.Text.Encoding.ASCII.GetBytes($"POST /upload HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-ShareLink-Session: {server.SessionId}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: paced.bin\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
   await stream.WriteAsync(header); await stream.WriteAsync(body.AsMemory(0,16384));
   DownloadFileStatus? progress=null;for(int i=0;i<100;i++){progress=server.Progress.Snapshot().SelectMany(r=>r.Files).FirstOrDefault(f=>f.Name=="paced.bin");if(progress?.Sent>0)break;await Task.Delay(10);}
   Check("live upload exposes partial receiver progress",progress is {Direction:"upload",Active:1,Complete:false,Interrupted:false}&&progress.Sent>0&&progress.Sent<body.Length);
   Check("upload contributes to active operation guard",server.ActiveDownloads==1);
   await stream.WriteAsync(body.AsMemory(16384)); using var response=new StreamReader(stream);string raw=await response.ReadToEndAsync();Check("paced upload saves successfully",raw.StartsWith("HTTP/1.1 200"));
   var finished=server.Progress.Snapshot().SelectMany(r=>r.Files).Single(f=>f.Name=="paced.bin");Check("receiver completes only after file commit",finished.Complete&&finished.Active==0&&finished.Sent==body.Length);
  }
  using(var socket=new System.Net.Sockets.TcpClient()) {
   await socket.ConnectAsync(IPAddress.Loopback,server.Port);using var stream=socket.GetStream();await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"POST /upload HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-ShareLink-Session: {server.SessionId}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: interrupted.bin\r\nContent-Length: 65536\r\nConnection: close\r\n\r\n"));await stream.WriteAsync(body.AsMemory(0,8192));for(int i=0;i<100;i++){if(server.Progress.Snapshot().SelectMany(r=>r.Files).Any(f=>f.Name=="interrupted.bin"&&f.Sent==8192))break;await Task.Delay(10);}socket.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send);using var reader=new StreamReader(stream);await reader.ReadToEndAsync();
  }
  for(int i=0;i<100&&server.ActiveDownloads>0;i++)await Task.Delay(10);
  var interrupted=server.Progress.Snapshot().SelectMany(r=>r.Files).Single(f=>f.Name=="interrupted.bin");File.WriteAllText(Path.Combine(output,"interrupted-progress.json"),JsonSerializer.Serialize(interrupted));Check("interrupted upload remains visible and never completes",interrupted.Interrupted&&!interrupted.Complete&&interrupted.Active==0&&interrupted.Sent==8192&&!File.Exists(Path.Combine(received,"interrupted.bin")));
  var zero=server.Progress.Snapshot().SelectMany(r=>r.Files).Single(f=>f.Name=="empty.txt");Check("empty upload completes with zero bytes",zero.Direction=="upload"&&zero.Complete&&zero.Sent==0&&zero.Size==0);
  Check("no incomplete files remain",!Directory.GetFiles(received,"*.part").Any());
  await server.DisposeAsync();bool closed=false;try{await client.GetAsync("manifest.json");}catch(HttpRequestException){closed=true;}Check("ended session no longer reachable",closed);
  File.WriteAllText(Path.Combine(output,"upload-checks.json"),JsonSerializer.Serialize(new{passed=true,checks},new JsonSerializerOptions{WriteIndented=true}));
 }
}
