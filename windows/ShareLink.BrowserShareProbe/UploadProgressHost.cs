using System.IO;using System.Net;using System.Reflection;using System.Text.Json;using System.Windows;using System.Windows.Controls;using System.Windows.Threading;using ShareLink.BrowserSharing;
namespace ShareLink.BrowserShareProbe;
internal static class UploadProgressHost {
 public static int Run(string output) {
  var app=new Application { ShutdownMode=ShutdownMode.OnExplicitShutdown };BrowserFileServer? server=null;BrowserShareView? view=null;Window? window=null;var begin=DateTime.UtcNow;DispatcherTimer? timer=null;
  app.Startup+=async(_,_)=>{try{
   string source=Path.Combine(output,"selected.txt");File.WriteAllText(source,"dedicated selected fixture");string received=Path.Combine(output,"received");Directory.CreateDirectory(received);
   server=new BrowserFileServer([source],output,true);await server.StartAsync(IPAddress.Loopback,0,CancellationToken.None);
   view=new BrowserShareView(Path.Combine(output,"view"));typeof(BrowserShareView).GetField("server",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,server);
   window=new Window{Content=view,Width=850,Height=750,Left=-20000,Top=-20000,ShowInTaskbar=false,ShowActivated=false};window.Show();
   File.WriteAllText(Path.Combine(output,"ready.json"),JsonSerializer.Serialize(new{url=$"http://127.0.0.1:{server.Port}/"}));bool allowed=false;
   timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(80)};timer.Tick+=async(_,_)=>{try{
    if(!allowed&&File.Exists(Path.Combine(output,"allow"))){server.EnableUploads(received);allowed=true;}
    typeof(BrowserShareView).GetMethod("UpdateReceivers",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);
    var peers=server.Progress.Snapshot();var data=JsonSerializer.Serialize(new{at=DateTimeOffset.UtcNow,receivers=peers});File.AppendAllText(Path.Combine(output,"progress-samples.jsonl"),data+"\n");
    if(File.Exists(Path.Combine(output,"stop"))||DateTime.UtcNow-begin>TimeSpan.FromMinutes(4)){timer!.Stop();await server.DisposeAsync();window.Close();app.Shutdown(0);}
   }catch(Exception ex){File.WriteAllText(Path.Combine(output,"fatal-error.txt"),ex.ToString());timer!.Stop();await server.DisposeAsync();app.Shutdown(1);}};timer.Start();
  }catch(Exception ex){File.WriteAllText(Path.Combine(output,"fatal-error.txt"),ex.ToString());app.Shutdown(1);}};return app.Run();
 }
}
