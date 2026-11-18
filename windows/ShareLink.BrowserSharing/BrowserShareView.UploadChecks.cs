using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
namespace ShareLink.BrowserSharing;
public sealed partial class BrowserShareView
{
    public static async Task<object> DiagnosticUploadSwitchAsync(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();void Check(string name,bool ok){if(!ok)throw new InvalidOperationException(name);checks.Add(name);}
        string source=Path.Combine(output,"generated-source.bin"),received=Path.Combine(output,"generated-received");Directory.CreateDirectory(received);byte[] data=Enumerable.Range(0,512).Select(i=>(byte)i).ToArray();await File.WriteAllBytesAsync(source,data);
        await using var listener=new BrowserFileServer([source],output);await listener.StartAsync(IPAddress.Loopback,0,CancellationToken.None);
        using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{listener.Port}/"),Timeout=TimeSpan.FromSeconds(10)};http.DefaultRequestHeaders.Add("X-ShareLink-Session",listener.SessionId);
        var view=new BrowserShareView(Path.Combine(output,"ui-state"),[source],receiveDirectory:received);view.server=listener;view.url=http.BaseAddress.AbsoluteUri;view.busy=true;view.UpdateActionState();
        async Task<JsonElement> Listing(){using var r=await http.GetAsync("manifest.json");r.EnsureSuccessStatusCode();return JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();}
        async Task<HttpResponseMessage> Upload(string name,string token,HttpContent body){using var r=new HttpRequestMessage(HttpMethod.Post,"upload"){Content=body};r.Headers.Add("X-ShareLink-Upload",token);r.Headers.Add("X-ShareLink-Filename",name);return await http.SendAsync(r);}
        void Toggle()=>view.allowUpload.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        try{
            var initial=await Listing();Check("fresh sharing session exposes no upload token and switch is off",initial.GetProperty("uploadToken").ValueKind==JsonValueKind.Null&&(string)view.allowUpload.Tag=="False");
            Toggle();var on=await Listing();string token=on.GetProperty("uploadToken").GetString()!;
            Check("actual allow-upload switch enables this session and advances webpage revision",listener.UploadsEnabled&&(string)view.allowUpload.Tag=="True"&&on.GetProperty("revision").GetInt64()>initial.GetProperty("revision").GetInt64());
            using(var accepted=await Upload("first.bin",token,new ByteArrayContent(data)))Check("enabled current-session upload saves the actual original bytes",accepted.StatusCode==HttpStatusCode.OK&&(await File.ReadAllBytesAsync(Path.Combine(received,"first.bin"))).SequenceEqual(data));
            Toggle();var off=await Listing();Check("switch off removes the upload token and updates webpage revision",!listener.UploadsEnabled&&off.GetProperty("uploadToken").ValueKind==JsonValueKind.Null&&off.GetProperty("revision").GetInt64()>on.GetProperty("revision").GetInt64());
            using(var denied=await Upload("denied.bin",token,new ByteArrayContent(data)))Check("a previously issued token cannot start new upload while switch is off",denied.StatusCode==HttpStatusCode.Forbidden&&!File.Exists(Path.Combine(received,"denied.bin")));
            using(var downloaded=await http.GetAsync("files/"+listener.Files[0].Id))Check("turning upload off leaves downloads intact",downloaded.IsSuccessStatusCode&&(await downloaded.Content.ReadAsByteArrayAsync()).SequenceEqual(data));
            Toggle();byte[] large=Enumerable.Range(0,65536).Select(i=>(byte)(i%251)).ToArray();var content=new PausedGeneratedUpload(large);var pending=Upload("in-flight.bin",token,content);
            try{await content.Paused.Task.WaitAsync(TimeSpan.FromSeconds(5));var until=DateTime.UtcNow.AddSeconds(5);while(listener.ActiveDownloads==0&&DateTime.UtcNow<until)await Task.Delay(10);Check("generated request is actually active before upload permission is revoked",listener.ActiveDownloads>0);Toggle();content.Release.TrySetResult(true);using var done=await pending;Check("already admitted upload finishes safely after the switch turns off",done.StatusCode==HttpStatusCode.OK&&(await File.ReadAllBytesAsync(Path.Combine(received,"in-flight.bin"))).SequenceEqual(large));}finally{content.Release.TrySetResult(true);}
            Check("completed uploads leave no partial cache files",Directory.GetFiles(received,".sharelink-upload-*.part").Length==0);
            return new{passed=true,checks,radioAccessed=false,networkChanged=false,filesTransferred=true,scope="Actual generated HTTP uploads/download over loopback and real WPF upload-switch handler; no device, radio, firewall or routes"};
        }finally{view.timer.Stop();view.server=null;view.busy=false;}
    }
    private sealed class PausedGeneratedUpload(byte[] data):HttpContent
    {
        internal TaskCompletionSource<bool> Paused{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Release{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length){length=data.Length;return true;}
        protected override async Task SerializeToStreamAsync(Stream stream,TransportContext? context){await stream.WriteAsync(data.AsMemory(0,4096));await stream.FlushAsync();Paused.TrySetResult(true);await Release.Task;await stream.WriteAsync(data.AsMemory(4096));}
    }
}
