using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using ShareLink;

var checks = new List<object>();
void Check(string name, bool value) { checks.Add(new { name, passed = value }); if (!value) throw new Exception(name); }
async Task Throws<T>(string name, Func<Task> action) where T : Exception { try { await action(); } catch (T) { Check(name, true); return; } throw new Exception("Expected failure: " + name); }
string Url(string version = "v0.1.1") => ReleaseUpdateClient.Repository + "/releases/download/" + version + "/" + ReleaseUpdateClient.InstallerName;
string Json(string tag = "v0.1.1", object[]? assets = null, bool pre = false, string? notes = "修复问题") => JsonSerializer.Serialize(new { tag_name = tag, body = notes, prerelease = pre, assets = assets ?? [new { name = ReleaseUpdateClient.InstallerName, browser_download_url = Url(tag) }] });
Task<ReleaseUpdate> Fetch(string json, string local = "0.1.0", HttpStatusCode status = HttpStatusCode.OK) => new ReleaseUpdateClient(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") })))).CheckAsync(local);
foreach (var (a,b,expected) in new[] {("1.0.9","1.0.10",-1),("v0.1.0","0.1.0",0),("V2.0.0","1.99.99",1),("0.1.0-preview.2","0.1.0-preview.10",-1),("0.1.0-preview","0.1.0",-1),("0.1.0+one","0.1.0+two",0),("0.1.0-alpha","0.1.0-alpha.1",-1),("0.1.0-alpha.1","0.1.0-alpha.beta",-1)})
    Check($"Version order {a} vs {b}", Math.Sign(ReleaseVersion.Parse(a).CompareTo(ReleaseVersion.Parse(b))) == expected);
foreach (string bad in new[] {"", "abc", "0.01.0", "0.1.0-a.01", "0.1.0-", "0.1.0/../../", "0.1.0\r\nHeader: x"})
    await Throws<FormatException>("Reject malformed version " + JsonSerializer.Serialize(bad), () => { ReleaseVersion.Parse(bad); return Task.CompletedTask; });
var one=await Fetch(Json("v1.0.0"),"0.1.0");
Check("Public 0.1.0 recognizes stable 1.0.0 and the exact fixed installer asset",one.Status==UpdateStatus.Available&&one.Version=="v1.0.0"&&one.Download?.AbsoluteUri==Url("v1.0.0"));
Check("Stable 1.0.0 reports itself current",(await Fetch(Json("v1.0.0"),"1.0.0")).Status==UpdateStatus.Current);
var found = await Fetch(Json());
Check("New release preserves tag, body and exact installer URL", found.Status == UpdateStatus.Available && found.Version == "v0.1.1" && found.Notes == "修复问题" && found.Download!.AbsoluteUri == Url());
Check("Equal version reports current", (await Fetch(Json("v0.1.0"))).Status == UpdateStatus.Current);
Check("Newer local version never downgrades", (await Fetch(Json("v0.0.9"))).Status == UpdateStatus.Current);
Check("Stable channel does not silently install prerelease", (await Fetch(Json("v0.2.0-preview",pre:true))).Status == UpdateStatus.Current);
Check("Unmarked prerelease tags stay off the stable channel", (await Fetch(Json("v0.2.0-beta"))).Status == UpdateStatus.Current);
Check("Preview build can advance to newer preview", (await Fetch(Json("v0.2.0-preview.2",pre:true),"0.2.0-preview.1")).Status == UpdateStatus.Available);
Check("404 means no published release", (await Fetch("{}",status:HttpStatusCode.NotFound)).Status == UpdateStatus.NotPublished);
Check("429 is a separate retry-later result", (await Fetch("{}",status:(HttpStatusCode)429)).Status == UpdateStatus.RateLimited);
Check("Source archives cannot be mistaken for installers", (await Fetch(Json(assets:[new {name="Source code.zip",browser_download_url=Url()}]))).Status == UpdateStatus.MissingInstaller);
Check("Missing asset fields are handled without inventing a link", (await Fetch("{\"tag_name\":\"v0.1.1\",\"assets\":[{}]}")).Status == UpdateStatus.MissingInstaller);
Check("No assets and no html_url uses explicit missing-installer state", (await Fetch("{\"tag_name\":\"v0.1.1\"}")).Status == UpdateStatus.MissingInstaller);
foreach (string badUrl in new[] {"http://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/v0.1.1/ShareLink-Setup-x64.exe", "file:///C:/Windows/cmd.exe", "https://evil.example/ShareLink-Setup-x64.exe", "https://gitee.com/other/repo/releases/download/v0.1.1/ShareLink-Setup-x64.exe", "https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/v0.1.1/source.zip"})
    Check("Reject unintended installer URL " + badUrl, (await Fetch(Json(assets:[new {name=ReleaseUpdateClient.InstallerName,browser_download_url=badUrl}]))).Status == UpdateStatus.MissingInstaller);
var asset = new {name=ReleaseUpdateClient.InstallerName,browser_download_url=Url()};
Check("Ambiguous duplicate installer names do not select arbitrarily", (await Fetch(Json(assets:[asset,asset]))).Status == UpdateStatus.MissingInstaller);
await Throws<HttpRequestException>("HTTP errors remain failures", async () => {await Fetch("{}",status:HttpStatusCode.Forbidden);});
await Throws<JsonException>("Invalid JSON remains a failure", async () => {await Fetch("<html>offline</html>");});
await Throws<JsonException>("Invalid JSON shape remains a failure", async () => {await Fetch("[]");});
await Throws<JsonException>("Oversized release data is bounded", async () => {await Fetch(new string('x',300000));});
var events = new List<string>();
await UpdateDownloadFlow.OpenAsync(new Uri(Url()), message => {Check("Download notice includes manual-install instruction",message.Contains("下载完成后请手动运行"));events.Add("notice");}, () => {events.Add("visible");return Task.CompletedTask;}, uri => {Check("Only dynamic URL is sent to system browser",uri.AbsoluteUri == Url());events.Add("browser");});
Check("Notice is rendered before launching the browser",events.SequenceEqual(new[]{"notice","visible","browser"}));
await Throws<InvalidOperationException>("Invalid links never launch a browser", () => UpdateDownloadFlow.OpenAsync(new Uri("file:///C:/bad.exe"), _ => throw new Exception(), () => Task.CompletedTask, _ => throw new Exception()));
var inspected = false;
using(var http = new HttpClient(new FakeHandler((request,_) => {inspected = request.RequestUri == ReleaseUpdateClient.Api && request.Headers.Accept.Any(a=>a.MediaType=="application/json") && request.Headers.UserAgent.ToString().Contains("ShareLink/0.1.0") && !request.Headers.Contains("Authorization");return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));})))
{ await new ReleaseUpdateClient(http).CheckAsync("0.1.0"); Check("Request is anonymous JSON to the configured repository",inspected); }
using(var http = new HttpClient(new FakeHandler(async (_,ct) => {await Task.Delay(5000,ct);return new HttpResponseMessage(HttpStatusCode.OK);})))
    await Throws<OperationCanceledException>("Timeout cancels an unfinished request",async()=>{await new ReleaseUpdateClient(http,TimeSpan.FromMilliseconds(35)).CheckAsync("0.1.0");});
using(var source=new CancellationTokenSource())
using(var http=new HttpClient(new FakeHandler(async(_,ct)=>{await Task.Delay(5000,ct);return new HttpResponseMessage(HttpStatusCode.OK);})))
{source.Cancel(); await Throws<OperationCanceledException>("Shutdown cancellation stops a request",async()=>{await new ReleaseUpdateClient(http).CheckAsync("0.1.0",source.Token);});}
if(args.Contains("--live"))
{
    var real=await new ReleaseUpdateClient().CheckAsync("0.1.0");
    Check("Live repository returns a recognized update state",Enum.IsDefined(real.Status));
    Console.WriteLine("Live repository: "+real.Status);
}
string result=JsonSerializer.Serialize(new {passed=true,scope="Real update service with controlled HTTP responses; browser calls intercepted, no installer execution or client shutdown",checks},new JsonSerializerOptions{WriteIndented=true});
string? output=args.FirstOrDefault(a=>a.EndsWith(".json",StringComparison.OrdinalIgnoreCase)); if(output is not null)File.WriteAllText(output,result);
Console.WriteLine($"PASS: {checks.Count} update checks.");
sealed class FakeHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handle):HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>handle(request,cancellationToken); }
