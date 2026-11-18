using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shell;
using Microsoft.VisualBasic.FileIO;

namespace ShareLink;

public partial class MainWindow
{
    private readonly ObservableCollection<TransferView> activeRows = [];
    private readonly ObservableCollection<TransferView> historyRows = [];
    private readonly Dictionary<string, TransferView> historyFileRows = [];
    private readonly HashSet<string> hiddenHistoryKeys = [], deletingHistoryKeys = [];
    private bool historyClearing;
    private readonly ObservableCollection<LocalFileView> localFiles = [];
    private readonly DiscoveryLifetime discoveryLifetime = new();
    private bool discoveryEnabled { get => discoveryLifetime.Enabled; set => discoveryLifetime.Enabled = value; }
    private readonly HashSet<string> observedTerminal = [];
    private DesktopNotifications? desktopNotifications;
    private long localFilesUpdated;

    private void InitializeStage8()
    {
        Transfers.ItemsSource = historyRows;
        ActiveTransfers.ItemsSource = activeRows;
        LocalFiles.ItemsSource = localFiles;
        TaskbarItemInfo = new TaskbarItemInfo();
        if (!diagnostic)
        {
            try { desktopNotifications = new DesktopNotifications(this,
                () => { Show(); WindowState = WindowState.Normal; Activate(); },
                () => { Show(); WindowState = WindowState.Normal; Activate(); ShowSettings(true); },
                () => OpenFolder_Click(this, new RoutedEventArgs()), Close); }
            catch (Exception ex) { StatusMessage.Text = "系统通知暂不可用：" + ex.Message; }
        }
    }
    private void BusinessActivity() => discoveryLifetime.Activity(Environment.TickCount64);
    private async Task EnableDiscoveryAsync()
    {
        if (modules.Direct is null || discoveryEnabled || pcDiscoveryChanging || closing || pcCloseInProgress) return;
        pcDiscoveryChanging = true;
        try {
        if (!pcNativeTest) {
        nearbyDiscovery = new(); nearbyDiscovery.Start();
        nearbyInvitation = new(nearbyDiscovery);
        nearbyInvitation.Ready = _ => discoveryEnabled && !closing && !storageChanging && !pairing && !PcSessionOwned && nearbySession is null && !sending && !hasActiveTransfers && !modeChanging;
        nearbyInvitation.Accepted = phone => Dispatcher.BeginInvoke(new Action(() => { _ = ConnectNearbyAsync(phone, invitation: true); }));
        }
        discoveryEnabled = true; BusinessActivity();
        DiscoveryButton.Content = "已开启附近设备发现";
        UpdatePresentation();
        if (nearbyInvitation is not null) await nearbyInvitation.StartAsync(provider => InitializePcNativeAsync(provider));
        if (pcDiscovery is null && pcRadio is null && !closing && !pcCloseInProgress && discoveryEnabled && !browserSharing) await InitializePcNativeAsync();
        RefreshNearbyList();
        } finally { pcDiscoveryChanging = false; UpdatePresentation(); }
    }
    private void DisableDiscovery()
    {
        discoveryEnabled = false;
        nearbyDiscovery?.Dispose(); nearbyDiscovery = null;
        nearbyInvitation?.Dispose(); nearbyInvitation = null;
        DiscoveryButton.Content = "已关闭附近设备发现";
        RefreshNearbyList();
        // Low-level radio stop; callers that close discovery also release idle sessions.
    }
    private async Task CloseDiscoveryAsync()
    {
        if (pairing || PcPairing || sending || hasActiveTransfers || closing || storageChanging || pcDiscoveryChanging) return;
        await DisconnectNearbyAsync();
        if (nearbySession is null) await PausePcNativeAsync();
        if (nearbySession is null) { DisableDiscovery(); UpdatePresentation(); }
    }
    private async void Discovery_Click(object sender, RoutedEventArgs e)
    {
        if (storageChanging || sending || pairing || PcPairing || hasActiveTransfers || modeChanging) return;
        if (discoveryEnabled) {
            // A task may have arrived since the last presentation refresh.
            try {
                if (core is null || core.Call(new { op = "snapshot" }).GetProperty("transfers").EnumerateArray()
                    .Any(t => Text(t, "state") is not ("completed" or "cancelled" or "rejected" or "failed"))) return;
                await CloseDiscoveryAsync();
            } catch (Exception ex) { ShowError("无法确认传输状态：" + ex.Message); }
        } else await EnableDiscoveryAsync();
    }
    private void UpdateStage8(JsonElement snapshot)
    {
        foreach (var row in transferRows.Where(row => row.Active))
            if (!activeRows.Contains(row)) activeRows.Insert(0, row);
        foreach (var row in activeRows.Where(row => !row.Active || !transferRows.Contains(row)).ToArray()) activeRows.Remove(row);
        var historyKeys = new HashSet<string>();
        foreach (var item in snapshot.GetProperty("transfers").EnumerateArray())
        {
            if (Text(item, "state") is not ("completed" or "failed" or "cancelled" or "rejected")) continue;
            int index = 0;
            foreach (var file in item.GetProperty("files").EnumerateArray())
            {
                string key = Text(item, "id") + ":" + index++;
                if (hiddenHistoryKeys.Contains(key) && !deletingHistoryKeys.Contains(key)) continue;
                historyKeys.Add(key);
                if (!historyFileRows.TryGetValue(key, out var row))
                {
                    row = new TransferView(Text(item, "id"), index - 1);
                    historyFileRows.Add(key, row); historyRows.Add(row);
                }
                row.Update(item);
            }
        }
        foreach (var key in historyFileRows.Keys.Where(key => !historyKeys.Contains(key)).ToArray())
        { historyRows.Remove(historyFileRows[key]); historyFileRows.Remove(key); }
        foreach (var item in snapshot.GetProperty("transfers").EnumerateArray())
        {
            var state = Text(item, "state");
            if (state is not ("completed" or "failed" or "cancelled" or "rejected") || Number(item, "finishedAt") == 0 || !observedTerminal.Add(Text(item, "id"))) continue;
            var finished = Number(item, "finishedAt");
            if (finished < startedAt) continue;
            BusinessActivity();
            localFilesUpdated = 0;
            if (!IsVisible || WindowState == WindowState.Minimized)
                desktopNotifications?.Notify(state == "completed" ? "文件传输已完成" : "文件传输已结束", Text(item, "peerName") + " · " + FormatBytes(Number(item, "total")));
        }
        if (discoveryLifetime.Expired(Environment.TickCount64, sending || pairing || PcPairing || hasActiveTransfers || PcLinkBusy)) _ = CloseDiscoveryAsync();
        var tasks = snapshot.GetProperty("transfers").EnumerateArray().Where(item => Text(item, "state") is not ("completed" or "failed" or "cancelled" or "rejected")).ToArray();
        double total = tasks.Sum(item => Number(item, "total"));
        double bytes = tasks.Sum(item => Number(item, "bytes"));
        TaskbarItemInfo.ProgressState = tasks.Length == 0 ? TaskbarItemProgressState.None : total > 0 ? TaskbarItemProgressState.Normal : TaskbarItemProgressState.Indeterminate;
        TaskbarItemInfo.ProgressValue = total > 0 ? Math.Clamp(bytes / total, 0, 1) : 0;
        desktopNotifications?.Progress(tasks.Length > 0 ? $"ShareLink · {FormatBytes(bytes)} / {FormatBytes(total)} · {TaskbarItemInfo.ProgressValue:P0}" : "ShareLink · 点击打开");
        SortHistoryRows();
        EmptyTransfers.Visibility = historyRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (Environment.TickCount64 - localFilesUpdated > 2000) { ReloadLocalFiles(); localFilesUpdated = Environment.TickCount64; }
        UpdatePresentation();
    }
    private readonly long startedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private void Hide_Click(object sender, RoutedEventArgs e)
    {
        if (desktopNotifications is null) { WindowState = WindowState.Minimized; return; }
        Hide(); desktopNotifications.Notify("ShareLink 正在后台运行", "传输会继续，点击托盘图标返回；在窗口关闭应用会终止未完成任务。");
    }
    private async void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (historyClearing || historyRows.Count == 0) return;
        if (!AppDialog.Show(this, "全部清除传输记录？", "只清除已结束的传输记录，文件和正在进行的任务会保留。", "全部清除", danger: true)) return;
        await ClearHistoryRowsAsync(historyRows.ToArray());
    }
    private async void DeleteHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!historyClearing && sender is Button { Tag: TransferView row }) await ClearHistoryRowsAsync([row]);
    }
    private async Task ClearHistoryRowsAsync(TransferView[] rows)
    {
        if (historyClearing || rows.Length == 0) return;
        historyClearing = true; ClearAllHistoryButton.IsEnabled = false;
        string[] keys = rows.Select(row => row.Id + ":" + row.FileIndex).ToArray();
        try {
            var next = hiddenHistoryKeys.Concat(keys).Distinct(StringComparer.Ordinal).ToArray();
            // Per-file presentation metadata keeps sibling rows and the original batch journal intact.
            DesktopPreferences.Save(stateDir, "history-file-records.json", new HistoryRecordPreference(next));
            deletingHistoryKeys.UnionWith(keys); hiddenHistoryKeys.UnionWith(keys);
            var containers = rows.Select(row => Transfers.ItemContainerGenerator.ContainerFromItem(row)).OfType<FrameworkElement>().ToArray();
            await AnimateHistoryRemovalAsync(containers);
        } catch (Exception ex) { ShowError("清除记录未完成：" + ex.Message); }
        finally {
            deletingHistoryKeys.ExceptWith(keys);
            foreach (var row in rows) {
                string key = row.Id + ":" + row.FileIndex;
                if (hiddenHistoryKeys.Contains(key)) { historyRows.Remove(row); historyFileRows.Remove(key); }
            }
            EmptyTransfers.Visibility = historyRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            historyClearing = false; ClearAllHistoryButton.IsEnabled = true;
        }
    }
    private static async Task AnimateHistoryRemovalAsync(FrameworkElement[] elements)
    {
        if (!SystemParameters.ClientAreaAnimation || elements.Length == 0) return;
        var duration = TimeSpan.FromMilliseconds(220);
        foreach (var element in elements) {
            element.IsHitTestVisible = false; element.ClipToBounds = true;
            element.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(element.Opacity, 0, TimeSpan.FromMilliseconds(160)));
            element.BeginAnimation(HeightProperty, new System.Windows.Media.Animation.DoubleAnimation(element.ActualHeight, 0, duration) {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }
            });
        }
        await Task.Delay(duration + TimeSpan.FromMilliseconds(25));
    }
    private void ReloadLocalFiles()
    {
        try
        {
            var files = Directory.EnumerateFiles(downloadDir).Where(path => (File.GetAttributes(path) & (System.IO.FileAttributes.ReparsePoint | System.IO.FileAttributes.Hidden | System.IO.FileAttributes.System)) == 0)
                .OrderByDescending(File.GetLastWriteTimeUtc).ThenBy(Path.GetFileName)
                .Select(path => new LocalFileView(path, Path.GetFileName(path), FormatBytes(new FileInfo(path).Length))).ToArray();
            if (localFiles.SequenceEqual(files)) return;
            for (int i = localFiles.Count - 1; i >= 0; i--) if (!files.Contains(localFiles[i])) localFiles.RemoveAt(i);
            for (int i = 0; i < files.Length; i++)
            {
                int current = localFiles.IndexOf(files[i]);
                if (current < 0) localFiles.Insert(i, files[i]); else if (current != i) localFiles.Move(current, i);
            }
            lastStorageWarning = "";
            UpdatePresentation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            const string message = "保存位置暂时不可用，可在设置中重新选择文件夹。";
            StatusMessage.Text = message;
            if (lastStorageWarning != message) { lastStorageWarning = message; ShowToast(message); }
        }
    }
    private string CheckedLocalFile(object sender)
    {
        if ((sender as Button)?.Tag is not LocalFileView file) throw new InvalidOperationException("请选择本地文件。");
        var path = Path.GetFullPath(file.Path);
        if (!string.Equals(Path.GetDirectoryName(path), Path.GetFullPath(downloadDir), StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(path) || (File.GetAttributes(path) & System.IO.FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("文件已变化或不属于收件目录。");
        return path;
    }
    private void OpenLocalFile_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        OpenLocalFile(sender);
    }
    private void OpenLocalFile_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        OpenLocalFile(sender);
    }
    private void OpenLocalFile(object sender)
    {
        try { Process.Start(new ProcessStartInfo(CheckedLocalFile(sender)) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void LocateLocalFile_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", CheckedLocalFile(sender) }, UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void DeleteLocalFile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = CheckedLocalFile(sender);
            var recycle = new CheckBox { Content = "移动到系统回收站", IsChecked = true, Padding = new Thickness(0, 3, 0, 3) };
            if (!AppDialog.Show(this, "删除文件？", Path.GetFileName(path) + "\n\n删除后将从本地文件列表移除，请确认不再需要。", "删除文件", extra: recycle, danger: true)) return;
            CheckedLocalFile(sender);
            if (recycle.IsChecked == true) FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else File.Delete(path);
            ReloadLocalFiles();
            ShowToast(recycle.IsChecked == true ? "文件已移到回收站。" : "文件已删除。");
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

}
internal record LocalFileView(string Path, string Name, string Size)
{
    public string Detail => Size + " · 已保存";
}
