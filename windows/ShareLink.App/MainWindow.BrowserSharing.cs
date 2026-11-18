using System.IO;
using System.Windows;
using ShareLink.BrowserSharing;

namespace ShareLink;

public partial class MainWindow
{
    private bool browserSharing;
    private bool modeChanging, wasDiscoveringBeforeSingle;
    private BrowserShareView? browserView;

    private async void SelectShareMode(bool single)
    {
        if (modeChanging || (diagnostic && !pcNativeTest) || closing) return;
        if (single == browserSharing) { ShowHistory(false); return; }
        if (browserView?.SharingActive == true) {
            StatusMessage.Text = "请先点击‘结束分享’，再切换模式。"; return;
        }
        if (sending || hasActiveTransfers || pairing || PcPairing || PcLinkBusy || receivePromptActive) {
            StatusMessage.Text = "请先完成或取消传输，再切换模式。"; return;
        }
        var previousMode = browserSharing;
        var previousPage = browserSharing ? (FrameworkElement)BrowserHome : TransferPage;
        var previousImage = Motion.CapturePage(previousPage);
        modeChanging = true; UpdateShareModeAvailability();
        try {
            if (!single) await LeaveSingleModeAsync();
            else await EnterSingleModeAsync();
        } catch (Exception ex) { ShowError(ex.Message); }
        finally {
            modeChanging = false; UpdateShareModeAvailability();
            if (previousMode != browserSharing)
                Motion.SwitchPage(browserSharing ? BrowserHome : TransferPage, ModeTransitionOverlay, previousImage, browserSharing ? 1 : -1);
        }
    }
    private void UpdateShareModeAvailability()
    {
        BrowserShareButton.IsEnabled = !modeChanging && !storageChanging && !sending && !pairing && !hasActiveTransfers && !receivePromptActive
            && !PcPairing && browserView?.SharingActive != true && !PcLinkBusy;
    }
    private async Task EnterSingleModeAsync()
    {
        if (storageChanging || sending || pairing || PcPairing || hasActiveTransfers || receivePromptActive || core is null) {
            ShowError("请先结束当前传输或连接操作，再开启二维码分享。"); return;
        }
        wasDiscoveringBeforeSingle = discoveryEnabled;
        browserSharing = true; storageChanging = true; timer.Stop();
        BrowserShareButton.IsEnabled = false;
        try {
            var deadline = DateTime.UtcNow.AddSeconds(8);
            while (polling || networkPolling || pcRefreshing || pcDiscoveryChanging) {
                if (DateTime.UtcNow >= deadline) throw new IOException("连接状态仍在更新，请稍后重试。");
                await Task.Delay(40);
            }
            if (core.Call(new { op = "snapshot" }).GetProperty("transfers").EnumerateArray()
                .Any(t => Text(t, "state") is not ("completed" or "cancelled" or "rejected" or "failed")))
                throw new IOException("当前有活动传输，请结束后再开启二维码分享。");
            // Pause native listeners and invitations throughout the modal session,
            // preventing inbound LAN/BLE tasks from racing with hotspot ownership.
            await PausePcNativeAsync();
            DisableDiscovery();
            await RestoreNetworkAsync();
            if (wifi?.HasRecovery == true) throw new IOException("原网络尚未恢复，请先完成网络恢复。");
            if (nearbySession is { PairId.Length: > 0 } session) try { core.Call(new { op = "pair_close", pairId = session.PairId }); } catch { }
            nearbySession?.Dispose(); nearbySession = null; connectedPhone = null;
            selectedIsNearby = false; selectedPeer = null;
            var previousCore = core; core = null; await Task.Run(previousCore.Dispose);
            var sessionDirectory = Path.Combine(stateDir, "browser-share", DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            browserView = new BrowserShareView(sessionDirectory, files.Select(f => f.Path), confirmEnd: () =>
                AppDialog.Show(this, "结束分享？", "仍有文件正在传输。结束分享会中断传输，是否继续？", "结束分享", "继续分享", danger: true),
                confirmNetworkAccess: () => AppDialog.Show(this, "允许手机访问下载页？", "Windows 需要一次管理员确认，允许接入临时 Wi-Fi 的设备访问下载页。如果此前点过拒绝，将调整此程序由系统生成的 TCP 拒绝规则。", "允许访问", "取消"), receiveDirectory: downloadDir);
            browserView.OperationNotice += ShowToast;
            browserView.StatusChanged += text => { if (browserSharing) { StatusMessage.Text = text.Replace('\n', ' '); UpdateShareModeAvailability(); } };
            showingHistory = showingSettings = false;
            TransferPage.Visibility = HistoryPage.Visibility = SettingsPage.Visibility = Visibility.Collapsed;
            BrowserHome.Content = browserView; BrowserHome.Visibility = Visibility.Visible;
            BrandHeader.Margin = new Thickness(0, 0, 0, 16);
            SettingsButton.IsEnabled = HistoryButton.IsEnabled = true;
            BrowserShareButton.IsSingle = true;
            ServiceStatus.Text = "●  浏览器分享";
            StatusMessage.Text = "接收方无需安装应用，连接 Wi-Fi 后直接下载。";
            storageChanging = false;
        }
        catch {
            await RestoreDoubleModeAsync(); throw;
        }
    }
    private async Task<bool> LeaveSingleModeAsync(bool endForClose = false)
    {
        if (browserView?.SharingActive == true) {
            if(!endForClose) { StatusMessage.Text = "请先结束当前分享，再切换传输方式。"; return false; }
            if(!await browserView.EndAsync())return false;
        }
        storageChanging = true;
        try {
            if (browserView is not null) await browserView.DisposeAsync();
            browserView = null;
            await RestoreDoubleModeAsync(); return true;
        } catch { storageChanging = false; throw; }
    }
    private async Task RestoreDoubleModeAsync()
    {
        if (core is null) { await Task.Delay(150); core = await Task.Run(() => new NativeCore(CoreConfiguration())); }
        storageChanging = false; browserSharing = false;
        showingSettings = showingHistory = false; SettingsPage.Visibility = HistoryPage.Visibility = Visibility.Collapsed;
        ServiceStatus.Text = "●  设备互传";
        BrowserHome.Content = null; BrowserHome.Visibility = Visibility.Collapsed; TransferPage.Visibility = Visibility.Visible;
        BrandHeader.Margin = new Thickness(0, 0, 0, 16);
        SettingsButton.IsEnabled = HistoryButton.IsEnabled = true;
        BrowserShareButton.IsSingle = false;
        if (wasDiscoveringBeforeSingle) await EnableDiscoveryAsync();
        if (!closing) { timer.Start(); await RefreshState(); }
        StatusMessage.Text = "设备互传 · 双方打开应用后发现并连接。";
        UpdateSelection();
    }
}
