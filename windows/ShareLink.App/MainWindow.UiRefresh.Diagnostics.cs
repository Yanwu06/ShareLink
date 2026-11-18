using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ShareLink;

public partial class MainWindow
{
    internal async Task<object> DiagnosticUiRefreshAsync(string output)
    {
        if (!diagnostic) throw new InvalidOperationException("Requires isolated state.");
        var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        _ = NearbyBluetooth.DiscoveryChecks();
        Check("discovery labels and broadcast identity checks pass", true);
        discoveryEnabled = true;
        nearbySession = new NearbySession();
        connectedPhone = new NearbyPhone(1, "HarmonyOS · 测试手机", -60, 0);
        hasActiveTransfers = true;
        await CloseDiscoveryAsync();
        Check("closing discovery cannot interrupt an active transfer", discoveryEnabled && nearbySession is not null);
        hasActiveTransfers = false;
        await CloseDiscoveryAsync();
        Check("closing idle discovery disposes the session and clears receiver state", !discoveryEnabled && nearbySession is null && connectedPhone is null && !selectedIsNearby);
        string rawBefore = DiagnosticSnapshot().GetProperty("transfers").GetRawText();
        string kept = Path.Combine(downloadDir, "record-delete-keeps-file.txt"); File.WriteAllText(kept, "Keep this generated file.");
        var snapshot = JsonSerializer.SerializeToElement(new { transfers = new[] {
            new { id = "ui-batch", state = "completed", direction = "send", peerName = "测试接收设备", total = 30, bytes = 30, finishedAt = 1,
                files = new[] { new { name = "第一个文件.txt", size = 10 }, new { name = "第二个文件.txt", size = 20 } } }
        } });
        UpdateStage8(snapshot); ShowHistory(true); RenderDiagnostic(Path.Combine(output, "history-before.png"));
        Check("batch displays one history row per file", historyRows.Count == 2);
        var removed = historyRows.Single(r => r.FileIndex == 0);
        var container = (FrameworkElement)Transfers.ItemContainerGenerator.ContainerFromItem(removed);
        double height = container.ActualHeight;
        var deletion = ClearHistoryRowsAsync([removed]);
        if (SystemParameters.ClientAreaAnimation) {
            await Task.Delay(70); RenderDiagnostic(Path.Combine(output, "history-delete-middle.png"));
            Check("deletion visibly fades and collapses through an intermediate frame", container.Opacity > 0 && container.Opacity < 1 && container.ActualHeight > 0 && container.ActualHeight < height);
        }
        await deletion;
        UpdateStage8(snapshot); RenderDiagnostic(Path.Combine(output, "history-after.png"));
        Check("deleting one file keeps its batch sibling across refresh", historyRows.Count == 1 && historyRows[0].FileIndex == 1);
        Check("history removal leaves original journal and received files intact", rawBefore == DiagnosticSnapshot().GetProperty("transfers").GetRawText() && File.ReadAllText(kept) == "Keep this generated file.");
        hiddenHistoryKeys.Clear(); hiddenHistoryKeys.UnionWith(DesktopPreferences.Read(stateDir, "history-file-records.json", new HistoryRecordPreference([])).Hidden);
        historyRows.Clear(); historyFileRows.Clear(); UpdateStage8(snapshot);
        Check("per-file removal survives reloading persisted metadata", historyRows.Count == 1 && historyRows[0].FileIndex == 1);
        ShowSettings(true); DefaultSingleMode.IsChecked = true;
        Check("single default is persisted without changing current mode", DesktopPreferences.Read(stateDir, "ui-settings.json", new UiPreference()).SingleMode && !browserSharing);
        DefaultDualMode.IsChecked = true;
        Check("dual default is persisted", !DesktopPreferences.Read(stateDir, "ui-settings.json", new UiPreference()).SingleMode);
        RenderDiagnostic(Path.Combine(output, "settings-expanded.png"));
        RenderDiagnostic(Path.Combine(output, "settings-minimum.png"), 940, 640);
        Check("larger settings remain within a scrolling viewport", SettingsPage.ActualHeight <= 640 && ((FrameworkElement)SettingsVersion.Parent).ActualHeight > 0);
        hasActiveTransfers = true; UpdateSettingsState();
        Check("active transfer blocks immediate folder changes", !BrowseStorageButton.IsEnabled && !ResetStorageButton.IsEnabled);
        hasActiveTransfers = false; UpdateSettingsState();
        Check("idle state restores folder changes", BrowseStorageButton.IsEnabled && ResetStorageButton.IsEnabled);
        string previousDirectory = downloadDir; var previousPreference = storagePreference;
        string changedDirectory = Path.Combine(output, "changed-receive-folder"); Directory.CreateDirectory(changedDirectory);
        await ChangeStorageAsync(changedDirectory, new(changedDirectory));
        Check("folder choice applies and persists without a save button", downloadDir == changedDirectory && StoragePathBox.Text == changedDirectory && StoragePaths.Read(stateDir).Directory == changedDirectory);
        Check("immediate folder change preserves existing files and history", File.Exists(kept) && rawBefore == DiagnosticSnapshot().GetProperty("transfers").GetRawText());
        await ChangeStorageAsync(previousDirectory, previousPreference); UpdateStage8(snapshot);
        ShowHistory(true);
        await ClearHistoryRowsAsync(historyRows.ToArray()); UpdateStage8(snapshot);
        Check("clear all removes all visible ended records without deleting files", historyRows.Count == 0 && EmptyTransfers.Visibility == Visibility.Visible && File.Exists(kept));
        return new { passed = true, scope = "actual WPF templates, isolated generated records and preferences; no radio or user data", checks };
    }
}
