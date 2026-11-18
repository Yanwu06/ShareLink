using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace ShareLink;

public partial class MainWindow
{
    private bool showingHistory;
    private string lastConnectionWarning = "";

    private void UpdatePresentation()
    {
        UpdateSettingsState();
        UpdateNavigation();
        UpdateShareModeAvailability();
        if (pcNativeTest) { SettingsButton.IsEnabled=false; StorageCaption.Text="接收后保存到本次测试的接收文件夹 · 双击打开文件"; }
        DiscoveryButton.IsEnabled = !storageChanging && !sending && !pairing && !hasActiveTransfers && !modeChanging && !PcPairing && !pcDiscoveryChanging;
        RefreshDevicesButton.IsEnabled = CanRefreshDiscovery;
        DiscoveryButton.Tag = discoveryEnabled ? "True" : "False";
        NoPeersPanel.Visibility = NoPeers.Visibility;
        if (modules.Direct is not null)
            NoPeers.Text = discoveryEnabled ? "正在搜索附近设备\n请在对方设备打开 ShareLink" : "打开设备发现，连接身边的设备";
        EmptyLocalFiles.Visibility = localFiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionPanel.Visibility = files.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Motion.SetExpanded(ActiveScroll, activeRows.Count > 0, !diagnostic && IsVisible);
        UpdateFilePanelLimits(FileCard.ActualHeight);
        DeviceScroll.MaxHeight = double.PositiveInfinity;
        ConnectionDetails.MaxHeight = 150;
        DiscoveryFooter.Visibility = Visibility.Visible;
        ConnectionHint.Visibility = Visibility.Visible;
        UpdateSendTargetPresentation();
        UpdateDiscoverability();
        string warning = NetworkDecision.Text.Contains("失败") || NetworkDecision.Text.Contains("未完成") ? NetworkDecision.Text : "";
        if (!diagnostic && warning.Length > 0 && warning != lastConnectionWarning)
        {
            lastConnectionWarning = warning;
            // Keep technical recovery details internal; actionable failures use the normal operation dialog.
            StatusMessage.Text = warning;
        }
    }

    private void History_Click(object sender, RoutedEventArgs e) => ShowHistory(true);
    private void HistoryBack_Click(object sender, RoutedEventArgs e) => ShowHistory(false);

    private void SortHistoryRows()
    {
        var ordered = historyRows.OrderByDescending(row => row.SortTime).ThenBy(row => row.Id, StringComparer.Ordinal).ThenBy(row => row.FileIndex).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            int current = historyRows.IndexOf(ordered[i]);
            if (current != i) historyRows.Move(current, i);
        }
    }

    private void ShowHistory(bool visible)
    {
        showingSettings = false; SettingsPage.Visibility = Visibility.Collapsed;
        showingHistory = visible;
        TransferPage.Visibility = visible || browserSharing ? Visibility.Collapsed : Visibility.Visible;
        BrowserHome.Visibility = !visible && browserSharing ? Visibility.Visible : Visibility.Collapsed;
        HistoryPage.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        UpdateNavigation();
        FrameworkElement page = visible ? HistoryPage : browserSharing ? BrowserHome : TransferPage;
        if (IsVisible)
        {
            Motion.Reveal(page);
            if (visible) HistoryButton.Focus(); else TransferNavButton.Focus();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if(key==Key.Escape&&DiscoveryInfoPopup.IsOpen){DiscoveryInfoPopup.IsOpen=false;e.Handled=true;return;}
        if ((showingHistory || showingSettings) && (key == Key.Escape || key == Key.Left && Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)))
        {
            ShowHistory(false);
            e.Handled = true;
        }
    }

    private void ConnectionDetails_Expanded(object sender, RoutedEventArgs e)
    {
        if (ConnectionDetails.Content is FrameworkElement content) Motion.Reveal(content);
    }

    private void FileCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateFilePanelLimits(e.NewSize.Height);
    }

    private void UpdateFilePanelLimits(double height)
    {
        var panel = (System.Windows.Controls.Grid)FileCard.Child;
        double headerHeight = ((FrameworkElement)panel.Children[0]).DesiredSize.Height;
        if (headerHeight <= 0) headerHeight = 80;
        bool active = activeRows.Count > 0, selected = files.Count > 0;
        double budget = Math.Max(0, height - FileCard.Padding.Top - FileCard.Padding.Bottom - 2
            - headerHeight - 120 - (active ? 14 : 0) - (selected ? 58 : 0));
        ActiveScroll.MaxHeight = Math.Clamp(budget * (selected ? .6 : 1), 80, 230);
        SelectionScroll.MaxHeight = Math.Clamp(budget * (active ? .4 : 1), 48, 142);
    }

    private void DiscoveryCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Leave space for device selection and fixed controls even at the
        // minimum window height; simultaneous tasks remain scrollable.
        DeviceScroll.MaxHeight = Math.Max(80, e.NewSize.Height - 160);
    }
}
