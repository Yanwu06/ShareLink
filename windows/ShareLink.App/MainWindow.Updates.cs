using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;

namespace ShareLink;

public partial class MainWindow
{
    private ReleaseUpdateClient updateClient = new();
    private readonly CancellationTokenSource updateLifetime = new();
    private bool checkingUpdates, updateScheduled;
    private Action<Uri> openUpdateBrowser = uri => Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    private sealed record UpdateCheckStamp(DateTimeOffset LastAutomaticAttempt);

    private static bool IsInternalRelease => typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .OfType<System.Reflection.AssemblyMetadataAttribute>().Any(a=>a.Key=="ShareLink.ReleaseChannel"&&a.Value=="Internal");
    private async void CheckUpdates_Click(object sender, RoutedEventArgs e)
    {
        if(IsInternalRelease){ShowToast("当前为内部测试版，暂不检查公开更新。");return;}
        await CheckForUpdatesAsync(true);
    }
    private bool CanShowAutomaticUpdate => IsVisible && IsActive && WindowState != WindowState.Minimized && OwnedWindows.Count == 0
        && !sending && !pairing && !storageChanging && !receivePromptActive && !hasActiveTransfers && !browserSharing && !modeChanging;
    private async Task ScheduleUpdateCheckAsync()
    {
        if (updateScheduled || diagnostic || IsInternalRelease) return; updateScheduled = true;
        try
        {
            await Task.Delay(3000, updateLifetime.Token);
            try { var stamp = DesktopPreferences.Read<UpdateCheckStamp?>(stateDir, "update-check.json", null);
                if (stamp is not null && stamp.LastAutomaticAttempt <= DateTimeOffset.UtcNow && DateTimeOffset.UtcNow - stamp.LastAutomaticAttempt < TimeSpan.FromHours(24)) return;
            } catch { /* An invalid optional timestamp must not prevent starting the app. */ }
            while (!CanShowAutomaticUpdate) { if (closing) return; await Task.Delay(5000, updateLifetime.Token); }
            if (checkingUpdates || closing) return;
            try { DesktopPreferences.Save(stateDir, "update-check.json", new UpdateCheckStamp(DateTimeOffset.UtcNow)); } catch { }
            await CheckForUpdatesAsync(false);
        }
        catch (OperationCanceledException) { }
        catch { /* Automatic checks never interrupt startup on failure. */ }
    }
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (checkingUpdates || closing) return;
        checkingUpdates = true;
        if (manual && !diagnostic) { try { DesktopPreferences.Save(stateDir, "update-check.json", new UpdateCheckStamp(DateTimeOffset.UtcNow)); } catch { } }
        CheckUpdatesButton.IsEnabled = false; CheckUpdatesButton.Content = "正在检查更新…";
        try
        {
            var result = await updateClient.CheckAsync(ReleaseUpdateClient.LocalVersion, updateLifetime.Token);
            if (closing) return;
            if (!manual)
            {
                if (result.Status != UpdateStatus.Available) return;
                while (!CanShowAutomaticUpdate) { if (closing) return; await Task.Delay(5000, updateLifetime.Token); }
            }
            await PresentUpdateAsync(result);
        }
        catch (OperationCanceledException) { if (manual && !closing) ShowError("检查更新超时，请检查网络后重试。"); }
        catch (HttpRequestException) { if (manual && !closing) ShowError("网络异常，检查更新失败，请稍后重试。"); }
        catch (Exception ex) when (ex is JsonException or FormatException or OverflowException or InvalidOperationException)
        { if (manual && !closing) ShowError("暂时无法读取版本信息，请稍后重试。"); }
        catch { if (manual && !closing) ShowError("检查更新失败，请稍后重试。"); }
        finally { checkingUpdates = false; CheckUpdatesButton.Content = "检查更新"; CheckUpdatesButton.IsEnabled = true; }
    }
    private async Task PresentUpdateAsync(ReleaseUpdate result)
    {
        switch (result.Status)
        {
            case UpdateStatus.Current: AppDialog.Show(this, "检查更新", "当前已是最新版本。", "知道了", cancel: null); return;
            case UpdateStatus.NotPublished: AppDialog.Show(this, "检查更新", "暂未发布可供检查的版本，请稍后再试。", "知道了", cancel: null); return;
            case UpdateStatus.RateLimited: AppDialog.Show(this, "检查更新", "检查过于频繁，请稍后再试。", "知道了", cancel: null); return;
            case UpdateStatus.MissingInstaller:
                if (AppDialog.Show(this, "发现新版本 " + result.Version, "此版本暂未提供匹配的 Windows 安装包，可前往发行版页面查看。", "查看发行版", "稍后再说"))
                    TryOpenUpdateBrowser(ReleaseUpdateClient.ReleasePage);
                return;
        }
        string notes = string.IsNullOrWhiteSpace(result.Notes) ? "本版本未填写更新说明。" : result.Notes;
        if (!AppDialog.Show(this, "发现新版本 " + result.Version, notes + "\n\n" + UpdateDownloadFlow.Notice, "浏览器下载", "稍后再说")) return;
        try
        {
            await UpdateDownloadFlow.OpenAsync(result.Download!, ShowToast, async () => { await Dispatcher.Yield(DispatcherPriority.Render); }, openUpdateBrowser);
        }
        catch { ShowError("无法打开浏览器，请稍后重试。"); }
    }
    private void TryOpenUpdateBrowser(Uri address)
    {
        try { openUpdateBrowser(address); } catch { ShowError("无法打开浏览器，请稍后重试。"); }
    }
}
