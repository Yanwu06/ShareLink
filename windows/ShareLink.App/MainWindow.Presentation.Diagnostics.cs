using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace ShareLink;

public partial class MainWindow
{
    internal static async Task<object> DiagnosticMotionChecksAsync()
    {
        var content = new Border { Height = 120, Width = 240 };
        var panel = new ScrollViewer { Content = content, Visibility = Visibility.Collapsed, MaxHeight = 160, ClipToBounds = true };
        var host = new Window { Content = new StackPanel { Children = { panel } }, Width = 300, Height = 220,
            Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        var checks = new List<object>();
        void Check(string name, bool passed) {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Transfer-card motion: " + name);
        }
        try {
            host.Show(); host.UpdateLayout();
            Motion.SetExpanded(panel, true, true);
            if (SystemParameters.ClientAreaAnimation) {
                await Task.Delay(85); host.UpdateLayout();
                Check("enter animates through an intermediate height", panel.ActualHeight > 0 && panel.ActualHeight < 120);
            }
            await Task.Delay(300); host.UpdateLayout();
            Check("enter restores automatic layout and full card height", double.IsNaN(panel.Height) && panel.ActualHeight == 120 && panel.IsHitTestVisible);
            Motion.SetExpanded(panel, false, true);
            if (SystemParameters.ClientAreaAnimation) {
                await Task.Delay(70); host.UpdateLayout();
                Check("exit remains present while smoothly shrinking", panel.Visibility == Visibility.Visible && panel.ActualHeight > 0 && panel.ActualHeight < 120 && !panel.IsHitTestVisible);
            }
            await Task.Delay(280); host.UpdateLayout();
            Check("exit removes layout space after the animation", panel.Visibility == Visibility.Collapsed && double.IsNaN(panel.Height));
            Motion.SetExpanded(panel, true, true); await Task.Delay(60);
            Motion.SetExpanded(panel, false, true); await Task.Delay(40);
            Motion.SetExpanded(panel, true, true); await Task.Delay(340); host.UpdateLayout();
            Check("rapid task changes cannot let a stale completion hide a new card", panel.Visibility == Visibility.Visible && panel.ActualHeight == 120 && panel.Opacity == 1);
            Motion.SetExpanded(panel, false, false);
            Check("nonanimated layout settles immediately", panel.Visibility == Visibility.Collapsed);
            var page = new Border { Background = System.Windows.Media.Brushes.White, Width = 240, Height = 120 };
            var overlay = new Image { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
            host.Content = new Grid { Children = { page, overlay } }; host.UpdateLayout();
            var image = Motion.CapturePage(page);
            Motion.SwitchPage(page, overlay, image, 1);
            if (SystemParameters.ClientAreaAnimation) {
                await Task.Delay(85);
                Check("home transition crossfades and slides an inert prior-page image", page.Opacity > 0 && page.Opacity < 1 && overlay.Opacity > 0 && overlay.Opacity < 1
                    && ((System.Windows.Media.TranslateTransform)page.RenderTransform).X > 0 && !overlay.IsHitTestVisible);
            }
            await Task.Delay(370);
            Check("home transition releases the prior page and restores full opacity", overlay.Source is null && overlay.Visibility == Visibility.Collapsed && page.Opacity == 1);
            image = Motion.CapturePage(page); Motion.SwitchPage(page, overlay, image, -1);
            if (SystemParameters.ClientAreaAnimation) {
                await Task.Delay(85);
                Check("returning home slides from the opposite direction", ((System.Windows.Media.TranslateTransform)page.RenderTransform).X < 0);
            }
            await Task.Delay(370);
            Check("return transition releases its snapshot too", overlay.Source is null && page.Opacity == 1);
            return new { passed = true, systemAnimations = SystemParameters.ClientAreaAnimation, checks };
        }
        finally { host.Close(); }
    }

    internal async Task<object> DiagnosticPresentationChecksAsync(string output)
    {
        if (!diagnostic || hasActiveTransfers) throw new InvalidOperationException("UI checks require an isolated, idle diagnostic window.");
        var selectedBefore = files.ToArray();
        var localBefore = localFiles.ToArray();
        var activeBefore = activeRows.ToArray();
        var historyBefore = historyRows.ToArray();
        var peerBefore = selectedPeer;
        var nearbyBefore = NearbyList.ItemsSource;
        var noPeersBefore = NoPeers.Text;
        var peerVisibility = PeerList.Visibility;
        var connectVisibility = LanConnectButton.Visibility;
        var copyVisibility = LanCopyButton.Visibility;
        var snapshotBefore = DiagnosticSnapshot().GetProperty("transfers").GetRawText();
        var checks = new List<object>();
        void Check(string name, bool passed)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException("Desktop presentation: " + name);
        }
        Rect Bounds(FrameworkElement element) => element.TransformToAncestor((FrameworkElement)Content)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        void Capture(string name, int width = 1180, int height = 800)
        {
            RenderDiagnostic(Path.Combine(output, name + ".png"), width, height);
            File.WriteAllText(Path.Combine(output, name + "-layout.json"), JsonSerializer.Serialize(new {
                width, height, sendBar = Bounds(SendBar), discovery = Bounds(DiscoveryCard), fileCard = Bounds(FileCard),
                localViewport = Bounds(LocalFileScroll), selectedViewport = Bounds(SelectionScroll),
                localScrollableHeight = LocalFileScroll.ScrollableHeight, selectionScrollableHeight = SelectionScroll.ScrollableHeight,
                activeViewport = Bounds(ActiveScroll), historyVisible = showingHistory,
                header = Bounds(BrandHeader), technicalStatusVisible = TechnicalStatusPanel.Visibility == Visibility.Visible, mainTop = Bounds(TransferPage).Top
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        try
        {
            files.Clear(); localFiles.Clear(); activeRows.Clear(); historyRows.Clear();
            selectedPeer = null; NearbyList.ItemsSource = Array.Empty<NearbyPhone>();
            PeerList.Visibility = LanConnectButton.Visibility = LanCopyButton.Visibility = Visibility.Collapsed;
            NoPeers.Visibility = Visibility.Visible;
            NoPeers.Text = "打开设备发现，连接身边的设备";
            StatusMessage.Text = "文件不经过云端；接收前需要你的确认。";
            UpdateSelection(); ShowHistory(false);
            Capture("ui-empty");
            var emptyBar = Bounds(SendBar);
            Check("empty selection disables sending", !SendButton.IsEnabled);
            Check("empty library has an honest empty state", EmptyLocalFiles.Visibility == Visibility.Visible);
            Check("file badge has a square canvas", EmptyFileBadge.ActualWidth == 80 && EmptyFileBadge.ActualHeight == 80);
            var historyGlyph = (System.Windows.Media.Geometry)FindResource("HistoryGlyph");
            Check("history icon keeps the arrow joint solid and the clock face unfilled",
                historyGlyph.FillContains(new Point(3, 8)) && !historyGlyph.FillContains(new Point(8, 13)));
            var iconArtboard = new Rect(0, 0, 24, 24);
            Check("the converted Lucide outlines stay within their artboard",
                new[] { "ArrowRightGlyph", "DualModeGlyph", "CloseGlyph" }.All(key =>
                    iconArtboard.Contains(((System.Windows.Media.Geometry)FindResource(key)).Bounds)));
            Check("technical status and connection details are absent without taking header space", TechnicalStatusPanel.Visibility == Visibility.Collapsed && TechnicalStatusPanel.DesiredSize.Height == 0 && Bounds(TransferPage).Top <= Bounds(BrandHeader).Top + 80);
            ShowSettings(true); Capture("ui-settings");
            Check("footer reads the current Windows computer name", LocalName.Text == Environment.MachineName);
            Check("settings shows current receive location", SettingsPage.Visibility == Visibility.Visible && StoragePathBox.Text == downloadDir);
            Check("settings applies folder changes directly and includes startup preference and version", BrowseStorageButton.Content.ToString() == "更改文件夹" && DefaultDualMode.IsChecked == true && SettingsVersion.Text == "版本 v" + System.Diagnostics.FileVersionInfo.GetVersionInfo(Environment.ProcessPath!).ProductVersion?.Split('+')[0]);
            ShowHistory(false);
            for (int i = 0; i < 36; i++)
            {
                string name = $"{i + 1:00} 项目资料与设计说明_长文件名用于窗口缩放检查_2026年10月.pdf";
                if (i == 0) { name = "ShareLink-thumbnail-64KB.bin"; File.WriteAllBytes(Path.Combine(output, name), new byte[65536]); }
                localFiles.Add(new LocalFileView(Path.Combine(output, name), name, "24.6 MB"));
                files.Add(new SelectedFile(Path.Combine(output, "pending-" + i), name, 1048576L * (i + 1)));
            }
            selectedPeer = new Peer("ui-only", "这是一台名称很长的手机_用于发送按钮边界验收", "127.0.0.1", 1, new string('0', 64));
            NearbyList.ItemsSource = new[] { new NearbyPhone(1, "我的 Mate 60 Pro+", -50, 0) { Connected = true } };
            NoPeers.Visibility = Visibility.Collapsed;
            UpdateSelection(); DiscoveryButton.Tag = "True";
            Capture("ui-files");
            await Task.Delay(600);
            Capture("ui-thumbnails");
            IEnumerable<T> Children<T>(DependencyObject node) where T : DependencyObject {
                for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(node); i++) {
                    var child = System.Windows.Media.VisualTreeHelper.GetChild(node, i);
                    if (child is T match) yield return match;
                    foreach (var descendant in Children<T>(child)) yield return descendant;
                }
            }
            var thumbnail = Children<Image>(LocalFiles).First(image => ShareLink.BrowserSharing.ShellThumbnail.GetFilePath(image) is not null);
            var frame = (Border)thumbnail.Parent;
            var cell = (Grid)frame.Parent;
            var text = cell.Children.OfType<StackPanel>().Single();
            File.WriteAllText(Path.Combine(output, "thumbnail-bounds.json"), JsonSerializer.Serialize(new { frameWidth = frame.ActualWidth, columnWidth = cell.ColumnDefinitions[0].ActualWidth, frame = Bounds(frame), image = Bounds(thumbnail), text = Bounds(text), source = thumbnail.Source?.GetType().Name }, new JsonSerializerOptions { WriteIndented = true }));
            Check("file thumbnails keep transparent backgrounds", frame.Background is null || frame.Background is System.Windows.Media.SolidColorBrush brush && brush.Color.A == 0);
            Check("thumbnail frame fits its reserved column without right clipping", frame.ActualWidth <= cell.ColumnDefinitions[0].ActualWidth && Bounds(thumbnail).Right <= Bounds(frame).Right);
            Check("filename stays clear of the full thumbnail frame", Bounds(text).Left - Bounds(frame).Right >= 8);
            var populatedBar = Bounds(SendBar);
            Check("file count does not move the send bar beyond layout rounding", Math.Abs(populatedBar.Top - emptyBar.Top) <= 1 && populatedBar.Left == emptyBar.Left && populatedBar.Width == emptyBar.Width);
            Check("received files have independent scrolling", LocalFileScroll.ScrollableHeight > 0);
            Check("selected files have independent scrolling", SelectionScroll.ScrollableHeight > 0);
            LocalFileScroll.ScrollToEnd(); SelectionScroll.ScrollToEnd();
            Capture("ui-files-last");
            Check("last receipt is reachable", LocalFileScroll.VerticalOffset >= LocalFileScroll.ScrollableHeight - 1);
            Check("last selected file is reachable", SelectionScroll.VerticalOffset >= SelectionScroll.ScrollableHeight - 1);
            var deviceTop = Bounds(DeviceScroll).Top;
            var active = new TransferView("ui-only-active");
            active.Update(JsonSerializer.SerializeToElement(new { state = "transferring", direction = "send", peerName = "我的 Mate 60 Pro+",
                files = new[] { new { name = "ShareLink 项目素材与设计文件.zip" } }, total = 1791376099, bytes = 736000000, speed = 28600000 }));
            activeRows.Add(active); UpdatePresentation(); DiscoveryButton.Tag = "True";
            LocalFileScroll.ScrollToHome(); SelectionScroll.ScrollToHome();
            Capture("ui-active");
            Check("device position stays fixed when a transfer appears", Math.Abs(Bounds(DeviceScroll).Top - deviceTop) <= 1);
            activeRows.Clear(); UpdatePresentation(); Capture("ui-active-dismissed");
            Check("device position stays fixed when a transfer disappears", Math.Abs(Bounds(DeviceScroll).Top - deviceTop) <= 1 && ActiveScroll.Visibility == Visibility.Collapsed);
            activeRows.Add(active); UpdatePresentation(); Capture("ui-active-restored");
            Check("active card stays inside the file panel", Bounds(ActiveScroll).Bottom <= Bounds(FileCard).Bottom);
            Capture("ui-small", 940, 640);
            Check("minimum client size keeps both bottom actions visible", Bounds(SendBar).Bottom <= 640 && Bounds(SendBar).Right <= 940);
            Check("minimum client size retains a complete first file row", LocalFileScroll.ViewportHeight >= 96);
            ConnectionDetails.IsExpanded = true;
            Capture("ui-small-details", 940, 640);
            Check("expanded details do not push send controls offscreen", Bounds(SendBar).Bottom <= 640);
            Check("active transfer stays inside the file panel while details expand", Bounds(ActiveScroll).Bottom <= Bounds(FileCard).Bottom);
            ConnectionDetails.IsExpanded = false;
            for (int i = 0; i < 18; i++)
            {
                var history = new TransferView("ui-history-" + i);
                history.Update(JsonSerializer.SerializeToElement(new { state = i % 3 == 0 ? "cancelled" : i % 3 == 1 ? "completed" : "failed",
                    direction = i % 2 == 0 ? "receive" : "send", peerName = "我的 Mate 60 Pro+", total = 98554422,
                    files = new[] { new { name = "项目资料与设计说明_2026年10月.pdf" } }, finishedAt = 1791936000 + i * 120 }));
                historyRows.Add(history);
            }
            EmptyTransfers.Visibility = Visibility.Collapsed;
            SortHistoryRows();
            Check("history displays newest records first", historyRows[0].SortTime == 1791936000 + 17 * 120);
            ShowHistory(true); Capture("ui-history");
            Check("history has its own scroll region", HistoryScroll.ScrollableHeight > 0 && TransferPage.Visibility == Visibility.Collapsed);
            ShowHistory(false); Capture("ui-return");
            Check("history navigation preserves pending selection", files.Count == 36 && SelectionPanel.Visibility == Visibility.Visible);
            hasActiveTransfers = true; UpdatePresentation();
            Check("active transfers disable mode changes and the discovery switch", !BrowserShareButton.IsEnabled && !DiscoveryButton.IsEnabled);
            hasActiveTransfers = false; UpdatePresentation();
            Check("terminal transfers restore mode changes and discovery", BrowserShareButton.IsEnabled && DiscoveryButton.IsEnabled);
            Check("UI fixtures never create transfer tasks", snapshotBefore == DiagnosticSnapshot().GetProperty("transfers").GetRawText());
            return new { passed = true, scope = "synthetic presentation fixtures rendered by the actual WPF templates; no radio, user files or task history mutations", checks };
        }
        finally
        {
            files.Clear(); foreach (var file in selectedBefore) files.Add(file);
            localFiles.Clear(); foreach (var file in localBefore) localFiles.Add(file);
            activeRows.Clear(); foreach (var row in activeBefore) activeRows.Add(row);
            historyRows.Clear(); foreach (var row in historyBefore) historyRows.Add(row);
            selectedPeer = peerBefore; NearbyList.ItemsSource = nearbyBefore; NoPeers.Text = noPeersBefore;
            NoPeers.Visibility = Visibility.Visible;
            PeerList.Visibility = peerVisibility; LanConnectButton.Visibility = connectVisibility; LanCopyButton.Visibility = copyVisibility;
            ConnectionDetails.IsExpanded = false; ShowHistory(false); UpdateSelection();
        }
    }
}
