using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ShareLink;

public partial class MainWindow
{
    internal async Task<object> DiagnosticUpdatesAsync(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException("Update UI: " + name); }
        IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T item) yield return item; foreach (var nested in Descendants<T>(child)) yield return nested; }
        }
        var stamp = new UpdateCheckStamp(DateTimeOffset.UtcNow);
        DesktopPreferences.Save(stateDir, "update-check.json", stamp);
        Check("Automatic check timestamp persists in isolated app data", DesktopPreferences.Read<UpdateCheckStamp?>(stateDir, "update-check.json", null)?.LastAutomaticAttempt == stamp.LastAutomaticAttempt);
        Show(); ShowSettings(true); UpdateLayout();
        var browserCalls = new List<string>(); bool sawDownloadReminder = false;
        openUpdateBrowser = uri => { Check("Browser handoff occurs after visible manual-install reminder", sawDownloadReminder || uri == ReleaseUpdateClient.ReleasePage); browserCalls.Add(uri.AbsoluteUri); };
        var localRelease=System.Version.Parse(ReleaseUpdateClient.LocalVersion.Split('-')[0]);
        string nextTag=$"v{localRelease.Major}.{localRelease.Minor}.{localRelease.Build+1}";
        string fixture = JsonSerializer.Serialize(new { tag_name=nextTag,body="更新说明测试：保留原目录与文件。",assets=new[]{new { name=ReleaseUpdateClient.InstallerName,browser_download_url=ReleaseUpdateClient.Repository+"/releases/download/"+nextTag+"/"+ReleaseUpdateClient.InstallerName}} });
        async Task Scenario(string name, HttpStatusCode status, string body, string choice, string expectedText, bool duplicate = false)
        {
            int requests = 0, dialogs = 0;
            using var http = new HttpClient(new UpdateDiagnosticHandler(async (_, cancellation) => { requests++; await Task.Delay(80, cancellation); return new HttpResponseMessage(status) { Content = new StringContent(body,Encoding.UTF8,"application/json") }; }));
            updateClient = new ReleaseUpdateClient(http);
            var poll = new DispatcherTimer { Interval=TimeSpan.FromMilliseconds(25) };
            Exception? error = null;
            poll.Tick += (_,_) =>
            {
                foreach (Window dialog in OwnedWindows.Cast<Window>().ToArray())
                {
                    string actualChoice = name=="browser-error" && dialog.Title=="操作提示" ? "知道了" : choice;
                    var action = Descendants<Button>(dialog).FirstOrDefault(b=>b.Content?.ToString()==actualChoice);
                    if (action is null || !action.IsEnabled) continue;
                    try
                    {
                        dialogs++;
                        string text=string.Join("\n",Descendants<TextBlock>(dialog).Select(t=>t.Text));
                        Check(name+": visible message",text.Contains(name=="browser-error" && dialog.Title=="操作提示" ? "无法打开浏览器" : expectedText));
                        if(actualChoice=="浏览器下载") { sawDownloadReminder=text.Contains(UpdateDownloadFlow.Notice); Check("New-version dialog includes release notes",text.Contains("更新说明测试")); }
                        if (name=="available") { dialog.UpdateLayout(); var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(dialog.ActualWidth),(int)Math.Ceiling(dialog.ActualHeight),96,96,PixelFormats.Pbgra32);bitmap.Render(dialog);var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));using var file=System.IO.File.Create(System.IO.Path.Combine(output,"update-dialog.png"));encoder.Save(file); }
                        action.IsEnabled=false; action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                    catch(Exception ex) {error=ex;dialog.Close();}
                }
            };
            poll.Start();
            try
            {
                var request=CheckForUpdatesAsync(true);
                Check(name+": nonblocking busy state",checkingUpdates&&!CheckUpdatesButton.IsEnabled&&CheckUpdatesButton.Content?.ToString()=="正在检查更新…");
                if(duplicate) await CheckForUpdatesAsync(true);
                await request;
                if(error is not null)throw error;
                Check(name+": only one request",requests==1);
                Check(name+": expected dialogs presented",dialogs==(name=="browser-error" ? 2 : 1));
                Check(name+": control restored",!checkingUpdates&&CheckUpdatesButton.IsEnabled&&CheckUpdatesButton.Content?.ToString()=="检查更新");
            }
            finally {poll.Stop();}
        }
        await Scenario("current",HttpStatusCode.OK,JsonSerializer.Serialize(new {tag_name="v"+ReleaseUpdateClient.LocalVersion}),"知道了","当前已是最新版本",duplicate:true);
        await Scenario("not-published",HttpStatusCode.NotFound,"{}","知道了","暂未发布");
        await Scenario("available",HttpStatusCode.OK,fixture,"浏览器下载",UpdateDownloadFlow.Notice);
        Check("New version opens exact installer asset once",browserCalls.Count==1&&browserCalls[0].EndsWith("/"+nextTag+"/ShareLink-Setup-x64.exe"));
        int before=browserCalls.Count;
        await Scenario("cancel",HttpStatusCode.OK,fixture,"稍后再说",UpdateDownloadFlow.Notice);
        Check("Postponing update never launches browser",browserCalls.Count==before);
        await Scenario("missing-asset",HttpStatusCode.OK,JsonSerializer.Serialize(new {tag_name=nextTag}),"查看发行版","暂未提供匹配");
        Check("Missing installer opens repository release page",browserCalls.Last()==ReleaseUpdateClient.ReleasePage.AbsoluteUri);
        await Scenario("rate-limited",(HttpStatusCode)429,"{}","知道了","检查过于频繁");
        await Scenario("network-failed",HttpStatusCode.ServiceUnavailable,"{}","知道了","网络异常");
        openUpdateBrowser=_=>throw new InvalidOperationException("Simulated unavailable system browser");
        await Scenario("browser-error",HttpStatusCode.OK,fixture,"浏览器下载",UpdateDownloadFlow.Notice);
        using(var http=new HttpClient(new UpdateDiagnosticHandler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)))))
        {updateClient=new ReleaseUpdateClient(http);await CheckForUpdatesAsync(false);Check("Automatic no-release check is silent",OwnedWindows.Count==0);}
        browserSharing=true;Check("Automatic update prompts defer during active sharing",!CanShowAutomaticUpdate);browserSharing=false;
        Check("Update flow keeps application alive and native core unchanged",!closing&&core is not null);
        RenderDiagnostic(System.IO.Path.Combine(output,"updates-settings.png"));
        return new {passed=true,scope="Actual WPF settings and modal interactions with injected HTTP and intercepted system-browser calls; no live download, app shutdown, radio or user data changes",checks};
    }
    private sealed class UpdateDiagnosticHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> handle):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>handle(request,cancellationToken);}
}
