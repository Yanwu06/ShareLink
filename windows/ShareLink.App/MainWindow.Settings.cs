using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ShareLink;

public partial class MainWindow
{
    private StoragePreference storagePreference = new();
    private UiPreference uiPreference = new();
    private bool loadingModePreference;
    private bool showingSettings, storageChanging;
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3.2) };
    private string lastStorageWarning = "";
    private string? diagnosticPartialDirectory;

    private object CoreConfiguration() => new { name = Environment.MachineName, device_type = "computer", download_dir = downloadDir, state_dir = stateDir,
        // Nearby peers exchange the actual endpoint; let Windows select an available port.
        port = diagnostic || !modules.LanEnabled ? 0 : 53317, discovery_port = !diagnostic && modules.LanEnabled ? 53318 : 0,
        transport_modules = modules.Ids, streaming_hash = true, discard_partials = true,
        partial_dir = diagnostic ? diagnosticPartialDirectory : Path.Combine(stateDir, "receive-incomplete") };

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings(true);
    private void SettingsBack_Click(object sender, RoutedEventArgs e) => ShowHistory(false);
    private void ShowSettings(bool visible)
    {
        if (!visible) { ShowHistory(false); return; }
        showingSettings = true; showingHistory = false;
        BrowserHome.Visibility = Visibility.Collapsed;
        TransferPage.Visibility = HistoryPage.Visibility = Visibility.Collapsed; SettingsPage.Visibility = Visibility.Visible;
        SettingsScroll.ScrollToHome();
        StoragePathBox.Text = downloadDir;
        loadingModePreference = true;
        try { DefaultSingleMode.IsChecked = uiPreference.SingleMode; DefaultDualMode.IsChecked = !uiPreference.SingleMode; }
        finally { loadingModePreference = false; }
        SettingsVersion.Text = "版本 v" + (typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "未知");
        UpdateSettingsState();
        UpdateNavigation();
        Motion.Reveal(SettingsPage);
        if (IsVisible) SettingsButton.Focus();
    }
    private void UpdateSettingsState()
    {
        bool idle = !storageChanging && !sending && !pairing && !hasActiveTransfers && !receivePromptActive && browserView?.HasActiveDownloads != true && browserView?.Starting != true;
        BrowseStorageButton.IsEnabled = ResetStorageButton.IsEnabled = idle;
        SettingsButton.IsEnabled = !storageChanging;
        StorageSettingsHint.Text = storageChanging ? "正在应用保存位置，请稍候…" : !idle ? "当前有任务或连接操作，结束后即可修改保存位置。" : "";
        StorageSettingsHint.Visibility = StorageSettingsHint.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StorageCaption.Text = (storagePreference.Directory is null ? "接收后直接保存到文档中的 ShareLink 接收文件夹" : "接收后直接保存到你选择的文件夹") + " · 双击打开文件";
        StorageCaption.ToolTip = downloadDir;
    }
    private async void BrowseStorage_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "选择文件保存位置", Multiselect = false, InitialDirectory = Directory.Exists(StoragePathBox.Text) ? StoragePathBox.Text : "" };
        if (picker.ShowDialog(this) != true) return;
        if (!StoragePaths.Usable(picker.FolderName)) { ShowError("所选文件夹无法写入，请选择另一个已有文件夹。"); return; }
        await ChangeStorageAsync(Path.GetFullPath(picker.FolderName), new(Path.GetFullPath(picker.FolderName)));
    }
    private async void ResetStorage_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var choice = diagnostic ? new StorageChoice(downloadDir, false) : ResolveReceiveLocation(new());
            await ChangeStorageAsync(choice.Directory, new());
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async Task ChangeStorageAsync(string directory, StoragePreference preference)
    {
        try
        {
            await ApplyStorageAsync(directory, preference);
            StoragePathBox.Text = downloadDir;
            ShowToast("保存位置已更新，新收件将直接保存到所选文件夹。");
            UpdateSettingsState();
        }
        catch (Exception ex) { StoragePathBox.Text = downloadDir; ShowError(ex.Message); }
        finally { UpdateSettingsState(); }
    }

    private void DefaultShareMode_Changed(object sender, RoutedEventArgs e)
    {
        if (loadingModePreference || DefaultSingleMode is null || DefaultDualMode is null) return;
        try {
            var next = new UiPreference(DefaultSingleMode.IsChecked == true);
            DesktopPreferences.Save(stateDir, "ui-settings.json", next);
            uiPreference = next; ShowToast("默认分享方式已更新，下次启动时生效。");
        } catch (Exception ex) {
            loadingModePreference = true;
            try { DefaultSingleMode.IsChecked = uiPreference.SingleMode; DefaultDualMode.IsChecked = !uiPreference.SingleMode; }
            finally { loadingModePreference = false; }
            ShowError("默认分享方式未保存：" + ex.Message);
        }
    }

    private const string DesktopDownloadUrl = ReleaseUpdateClient.Repository + "/releases/download/latest/" + ReleaseUpdateClient.InstallerName;

    private void DownloadLink_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(DesktopDownloadUrl); ShowToast("已复制到剪贴板"); }
        catch { ShowToast("复制失败，请重试"); }
    }

    private const string FeedbackUrl = "https://f.wps.cn/g/FiHhs3vL/";

    private void Feedback_Click(object sender, RoutedEventArgs e) => OpenSettingsWebLink(FeedbackUrl);
    private void SupportDeveloper_Click(object sender, RoutedEventArgs e) => OpenSettingsWebLink("https://ifdian.net/a/yanwu06/plan");

    private void OpenSettingsWebLink(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { ShowToast("暂时无法打开网页，请稍后重试。"); }
    }

    private StorageChoice ResolveReceiveLocation(StoragePreference preference)
    {
        if (isolatedDesktopRoot is null) return StoragePaths.Resolve(preference);
        string receive = Path.Combine(isolatedDesktopRoot, "received"); Directory.CreateDirectory(receive);
        return StoragePaths.Choose(preference, receive, [], StoragePaths.Usable);
    }
    private async Task ApplyStorageAsync(string directory, StoragePreference preference)
    {
        if (storageChanging || sending || pairing || PcPairing || PcLinkBusy || hasActiveTransfers || receivePromptActive)
            throw new InvalidOperationException("请先完成当前传输或连接操作，再修改保存位置。");
        if (!StoragePaths.Usable(directory)) throw new IOException("保存位置不存在或无法写入，请重新选择文件夹。");
        if (browserSharing) {
            if (browserView?.HasActiveDownloads == true || browserView?.Starting == true) throw new IOException("请先完成当前下载或准备操作。");
            downloadDir = Path.GetFullPath(directory); StoragePaths.Save(stateDir, preference); storagePreference = preference;
            localFiles.Clear(); localFilesUpdated = 0; ReloadLocalFiles(); return;
        }
        string previousDirectory = downloadDir;
        bool wasDiscovering = discoveryEnabled, stoppedDiscovery = false, replaced = false;
        storageChanging = true; timer.Stop(); UpdateSelection(); UpdateSettingsState();
        try
        {
            var waitUntil = DateTime.UtcNow.AddSeconds(8);
            while (polling || networkPolling || pcRefreshing || pcDiscoveryChanging)
            {
                if (DateTime.UtcNow >= waitUntil) throw new InvalidOperationException("连接状态仍在更新，请稍后重试。");
                await Task.Delay(40);
            }
            if (core is not null && core.Call(new { op = "snapshot" }).GetProperty("transfers").EnumerateArray()
                .Any(t => Text(t, "state") is not ("completed" or "cancelled" or "rejected" or "failed")))
                throw new InvalidOperationException("当前有活动传输，保存位置尚未更改。");
            if (!diagnostic)
            {
                if (wasDiscovering) { await PausePcNativeAsync(); DisableDiscovery(); stoppedDiscovery = true; }
                await RestoreNetworkAsync();
                if (wifi?.HasRecovery == true) throw new InvalidOperationException("请先完成原网络恢复，再修改保存位置。");
                if (core is not null && nearbySession is { PairId.Length: > 0 } session)
                    try { core.Call(new { op = "pair_close", pairId = session.PairId }); } catch { }
                nearbySession?.Dispose(); nearbySession = null; connectedPhone = null; selectedIsNearby = false; selectedPeer = null;
            }
            var previousCore = core; core = null;
            if (previousCore is not null) await Task.Run(previousCore.Dispose);
            downloadDir = Path.GetFullPath(directory);
            await Task.Delay(150); // Let the old listener release its port.
            core = await Task.Run(() => new NativeCore(CoreConfiguration()));
            replaced = true;
            StoragePaths.Save(stateDir, preference); storagePreference = preference;
            localFiles.Clear(); localFilesUpdated = 0; ReloadLocalFiles();
            StatusMessage.Text = "保存位置已更新；已有文件保留原处。";
            if (!diagnostic) StatusMessage.Text += "如需发送文件，请重新连接设备。";
            lastStorageWarning = "";
        }
        catch
        {
            if (!replaced && core is null && Directory.Exists(previousDirectory))
            {
                downloadDir = previousDirectory; await Task.Delay(150);
                core = await Task.Run(() => new NativeCore(CoreConfiguration()));
            }
            throw;
        }
        finally
        {
            storageChanging = false;
            if (stoppedDiscovery && core is not null) await EnableDiscoveryAsync();
            if (!diagnostic && core is not null) timer.Start();
            if (core is not null) await RefreshState();
            UpdateSelection(); UpdateSettingsState();
        }
    }

    private void ShowToast(string message)
    {
        if (diagnostic || !IsVisible) return;
        toastTimer.Stop();
        ToastMessage.Text = message; ToastHost.Visibility = Visibility.Visible;
        Motion.Reveal(ToastHost);
        toastTimer.Tick -= ToastTimeout; toastTimer.Tick += ToastTimeout; toastTimer.Start();
    }
    private void ToastTimeout(object? sender, EventArgs e)
    {
        toastTimer.Stop();
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => ToastHost.Visibility = Visibility.Collapsed;
        ToastHost.BeginAnimation(OpacityProperty, fade);
    }
}
