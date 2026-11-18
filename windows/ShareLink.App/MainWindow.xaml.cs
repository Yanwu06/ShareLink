using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace ShareLink;

public partial class MainWindow : Window
{
    private NativeCore? core;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly ObservableCollection<SelectedFile> files = [];
    private readonly Dictionary<string, Peer> knownPeers = [];
    private readonly Dictionary<string, TransferView> transferViews = [];
    private readonly Dictionary<string, Peer> sentPeers = [];
    private readonly ObservableCollection<TransferView> transferRows = [];
    private Peer? selectedPeer;
    private bool polling;
    private volatile bool closing, sending, hasActiveTransfers;
    private string addresses = "";
    private readonly HashSet<string> promptedTransfers = [];
    private bool receivePromptActive;
    private ReceiveConfirmation? receiveConfirmation;
    private UserExitHistory? userExitHistory;
    private UserExitHistory ExitHistory => userExitHistory ??= new UserExitHistory(stateDir);
    private string downloadDir = "";
    private readonly string stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ShareLink", "v3");
    private readonly bool diagnostic;
    private readonly string? isolatedDesktopRoot;
    private readonly TransportModules modules;

    public MainWindow() : this(null) { }

    internal MainWindow(string? diagnosticRoot, bool directDiagnostic = false, bool pcNativeTest = false, bool desktopTest = false)
    {
        if (desktopTest && (diagnosticRoot is null || pcNativeTest)) throw new ArgumentException("普通入口测试需要独立目录且不能混用原生实验入口");
        isolatedDesktopRoot = desktopTest ? Path.GetFullPath(diagnosticRoot!) : null;
        diagnostic = diagnosticRoot is not null && !desktopTest; this.pcNativeTest = pcNativeTest;
        modules = TransportModules.Load(diagnostic && !directDiagnostic);
        if (diagnosticRoot is not null)
        {
            downloadDir = Path.Combine(diagnosticRoot, "received");
            stateDir = Path.Combine(diagnosticRoot, "identity");
        }
        InitializeComponent();
        if (pcNativeTest) Title="ShareLink · 电脑原生传输验收";
        PeerList.Visibility = modules.LanEnabled ? Visibility.Visible : Visibility.Collapsed;
        KeepNetwork.Visibility = Visibility.Collapsed;
        LanConnectButton.Visibility = modules.LanEnabled ? Visibility.Visible : Visibility.Collapsed;
        LanCopyButton.Visibility = modules.LanEnabled ? Visibility.Visible : Visibility.Collapsed;
        SelectedFiles.ItemsSource = files;
        BrowserShareButton.SelectionRequested += SelectShareMode;
        Transfers.ItemsSource = transferRows;
        timer.Tick += async (_, _) => await RefreshState();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await InitializeCoreAsync();

    internal async Task InitializeCoreAsync()
    {
        if (core is not null || closing) return;
        try
        {
            Directory.CreateDirectory(stateDir);
            string? storageNotice = null;
            if (diagnostic) Directory.CreateDirectory(downloadDir);
            else
            {
                storagePreference = StoragePaths.Read(stateDir);
                var choice = ResolveReceiveLocation(storagePreference);
                downloadDir = choice.Directory; storageNotice = choice.Notice;
            }
            var started = await Task.Run(() => new NativeCore(CoreConfiguration()));
            if (closing) { started.Dispose(); return; }
            core = started;
            uiPreference = diagnostic ? new() : DesktopPreferences.Read(stateDir, "ui-settings.json", new UiPreference());
            hiddenHistoryKeys.UnionWith(DesktopPreferences.Read(stateDir, "history-file-records.json", new HistoryRecordPreference([])).Hidden);
            InitializeStage8();
            if (!diagnostic || pcNativeTest) await InitializeNetworkAsync();
            else { NetworkSummary.Text = "诊断模式 · 不扫描蓝牙、不读取或修改无线连接"; BluetoothStatus.Text = "无线接口未启动"; }
            if (closing || pcCloseInProgress) return;
            ServiceStatus.Text = "●  接收已开启";
            LocalName.Text = Environment.MachineName;
            if (!diagnostic || pcNativeTest) timer.Start();
            await RefreshState();
            UpdateSelection();
            if (!diagnostic && uiPreference.SingleMode) await EnterSingleModeAsync();
            if (storageNotice is not null) { StatusMessage.Text = storageNotice; ShowToast(storageNotice); }
            if (!diagnostic && isolatedDesktopRoot is null) _ = ScheduleUpdateCheckAsync();
        }
        catch (Exception ex)
        {
            if (diagnostic) throw;
            ServiceStatus.Text = "●  启动失败";
            var detail = ex is DllNotFoundException ? "缺少传输核心 sharelink_core.dll。请使用完整发布目录，或运行项目中的构建脚本。" : ex.Message;
            ShowError(detail);
        }
    }

    private async Task RefreshState()
    {
        if (polling || closing || storageChanging || core is null) return;
        polling = true;
        try
        {
            var snapshot = await Task.Run(() => core.Call(new { op = "snapshot" }));
            if (closing) return;
            snapshot = ExitHistory.Apply(snapshot);
            // Request lifetime follows core state before any presentation work.
            receiveConfirmation?.Observe(snapshot);
            if (snapshot.TryGetProperty("info", out var info))
            {
                if (info.TryGetProperty("addresses", out var ips))
                    addresses = string.Join(" / ", ips.EnumerateArray().Select(p => p.GetString()));
                LocalAddress.Text = addresses.Length > 0 ? $"{addresses}  ·  {Number(info, "port"):0}" : modules.LanEnabled
                    ? "尚无局域网地址，请连接 Wi-Fi" : "附近直连 · 等待蓝牙连接";
            }
            if (snapshot.TryGetProperty("peers", out var peers))
            {
                var fresh = peers.EnumerateArray().Select(ReadPeer).ToList();
                // Keep manually probed devices until the app closes.
                foreach (var peer in fresh) knownPeers[peer.Key] = peer;
                foreach (var key in knownPeers.Keys.ToArray())
                    if (!knownPeers[key].Manual && fresh.All(p => p.Key != key)) knownPeers.Remove(key);
                // Render lists and their empty hint together. A LAN-empty snapshot
                // must not reveal the hint while BLE peers are already visible,
                // even when asynchronous control/name refresh is still pending.
                RefreshNearbyList();
            }
            if (snapshot.TryGetProperty("transfers", out var transfers))
            {
                foreach (var item in transfers.EnumerateArray())
                {
                    string id = Text(item, "id");
                    if (!transferViews.TryGetValue(id, out var row))
                    {
                        row = new TransferView(id);
                        transferViews[id] = row;
                        transferRows.Insert(0, row);
                    }
                    row.Update(item);                }
                var liveIds = transfers.EnumerateArray().Select(t => Text(t, "id")).ToHashSet();
                foreach (var staleId in transferViews.Keys.Where(id => !liveIds.Contains(id)).ToArray())
                {
                    transferRows.Remove(transferViews[staleId]);
                    transferViews.Remove(staleId);
                    sentPeers.Remove(staleId);
                }
                EmptyTransfers.Visibility = transferRows.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
                hasActiveTransfers = transferRows.Any(t => t.Active);
                UpdateStage8(snapshot);
            }
            if (!diagnostic || pcNativeTest)
            {
                _ = RefreshPcNativeAsync();
                if (!receivePromptActive && snapshot.TryGetProperty("transfers", out var pending))
                {
                    foreach (var item in pending.EnumerateArray())
                    {
                        var id = Text(item, "id");
                        if (Text(item, "direction") != "receive" || Text(item, "state") != "awaiting_accept" || promptedTransfers.Contains(id)) continue;
                        promptedTransfers.Add(id);
                        receivePromptActive = true;
                        _ = PromptReceiveAsync(item.Clone());
                        break;
                    }
                }
            }
        }
        catch (Exception ex) { StatusMessage.Text = "刷新状态失败：" + ex.Message; }
        finally { polling = false; timer.Interval = TimeSpan.FromMilliseconds(hasActiveTransfers ? 100 : 700); }
    }

    private async Task PromptReceiveAsync(JsonElement transfer)
    {
        // Yield out of snapshot polling before opening the modal receive window.
        await Dispatcher.Yield(DispatcherPriority.Background);
        using var request = new ReceiveConfirmation(Text(transfer, "id"));
        receiveConfirmation = request;
        try
        {
            if (closing || core is null) return;
            var activeCore = core;
            request.Observe(await Task.Run(() => activeCore.Call(new { op = "snapshot" })));
            if (request.Ended) return;
            var names = transfer.GetProperty("files").EnumerateArray().Select(f => Text(f, "name")).ToArray();
            var preview = string.Join("\n", names.Take(8));
            if (names.Length > 8) preview += $"\n……共 {names.Length} 个文件";
            var message = $"来自「{Text(transfer, "peerName")}」\n\n{preview}\n\n{FormatBytes(Number(transfer, "total"))} · 保存到 {downloadDir}";
            string op;
            if (approvedPhoneReceives.Remove(Text(transfer,"id"),out var approved)) {
                op = approved.MatchesTransfer(transfer) ? "accept" : "reject";
                if (op == "reject") StatusMessage.Text = "接收内容已变化，请重新发送。";
            } else op = AppDialog.Show(this, "接收文件", message, "接收", "拒绝", cancellation: request.Cancellation) ? "accept" : "reject";
            if (request.Ended || closing) return;
            request.Observe(await Task.Run(() => activeCore.Call(new { op = "snapshot" })));
            if (!request.Ended) await Task.Run(() => activeCore.Call(new { op, id = Text(transfer, "id") }));
        }
        catch (Exception ex) { StatusMessage.Text = "接收请求已结束或未能处理：" + ex.Message; }
        finally { if (ReferenceEquals(receiveConfirmation, request)) receiveConfirmation = null; receivePromptActive = false; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshDevicesAsync();
        if (modules.LanEnabled) await RunCommand(new { op = "discover" });
    }
    private void Peer_Click(object sender, RoutedEventArgs e)
    {
        if (!modules.LanEnabled) return;
        if (sending || pairing) return;
        if ((sender as Button)?.Tag is not Peer peer) return;
        selectedPeer = peer;
        selectedIsNearby = nearbySession?.Authenticated == true && string.Equals(nearbySession.Fingerprint, peer.Fingerprint, StringComparison.OrdinalIgnoreCase);
        
        UpdateSelection();
    }
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (!modules.LanEnabled) { ShowError("局域网传输模块未加载，请通过蓝牙选择附近设备。"); return; }
        if (core is null) { ShowError("请先启动传输服务。"); return; }
        var address = Ask("通过 IP 地址连接", "输入另一台设备 ShareLink 显示的 IP 地址。\n设备需连接同一个 Wi-Fi 或热点。", "例如 192.168.1.23 或 192.168.1.23:53317", "查找设备");
        if (address is null) return;
        try
        {
            string host = address.Trim();
            int port = 53317;
            if (host.Contains(':'))
            {
                var parts = host.Split(':');
                if (parts.Length != 2 || !int.TryParse(parts[1], out port) || port is < 1 or > 65535)
                    throw new InvalidOperationException("请输入 IPv4 地址，端口应为 1–65535。");
                host = parts[0];
            }
            if (!System.Net.IPAddress.TryParse(host, out var parsed) || parsed.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("请输入设备显示的 IPv4 地址。");
            StatusMessage.Text = "正在连接设备…";
            var peer = await LanTransportModule.ProbeAsync(core, host, port);
            knownPeers[peer.Key] = peer;
            selectedPeer = peer;
            selectedIsNearby = false;
            
            StatusMessage.Text = "已找到设备。选中文件后即可发送。";
            UpdateSelection();
            await RefreshState();
        }
        catch (Exception ex) { ShowError("连接失败：" + ex.Message + "\n请检查对端是否已启动、网络是否互通，以及防火墙是否允许 ShareLink。"); }
    }

    private void SelectFiles_Click(object sender, RoutedEventArgs e)
    {
        if (storageChanging) return;
        var dialog = new OpenFileDialog { Multiselect = true, Title = "选择要发送的文件" };
        if (dialog.ShowDialog(this) == true) AddFiles(dialog.FileNames);
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (storageChanging) return;
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            ShowHistory(false);
            AddFiles(paths);
        }
    }
    private void AddFiles(IEnumerable<string> paths)
    {
        int previousCount = files.Count;
        foreach (var path in paths)
        {
            if (Directory.Exists(path)) { StatusMessage.Text = "首版支持文件多选。文件夹请先压缩后发送。"; continue; }
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && files.All(f => !string.Equals(f.Path, file.FullName, StringComparison.OrdinalIgnoreCase)))
                    files.Add(new SelectedFile(file.FullName, file.Name, file.Length));
            }
            catch (Exception ex) { ShowError(ex.Message); }
        }
        UpdateSelection();
        if (files.Count > previousCount) ShowToast($"已添加 {files.Count - previousCount} 个文件，选择接收设备后即可发送。");
    }
    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is SelectedFile file) files.Remove(file);
        UpdateSelection();
    }
    private void UpdateSelection()
    {
        SelectionSummary.Text = files.Count == 0 ? "尚未选择文件" : $"{files.Count} 个文件 · {FormatBytes(files.Sum(f => (double)f.Size))}";
        AddFilesButton.IsEnabled = !storageChanging;
        SendButton.IsEnabled = !storageChanging && core is not null && files.Count > 0 && (modules.LanEnabled && selectedPeer is not null || selectedIsNearby) && !sending && !pairing;
        UpdatePresentation();
    }
    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        try { await SendSelectedFilesAsync(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async Task<string> SendSelectedFilesAsync()
    {
        if (core is null || (selectedPeer is null && !selectedIsNearby) || files.Count == 0 || sending || storageChanging) throw new InvalidOperationException("请先选择文件和接收设备。");
        if (HostedPcConnected) return await OfferHostedPcFilesAsync();
        var peer = selectedPeer;
        var fingerprint = selectedIsNearby && nearbySession?.Authenticated == true ? nearbySession.Fingerprint : peer!.Fingerprint;
        if (fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new InvalidOperationException("设备连接信息不完整，请刷新后重试。");
        var code = fingerprint[..16];
        sending = true;
        UpdateSelection();
        var selected = files.ToArray();
        var payload = selected.Select(f => new { path = f.Path, name = f.Name }).ToArray();
        try
        {
            if (selectedIsNearby && !temporaryNetwork) peer = null;
            peer = await ChoosePathAsync(peer, code, selected.Sum(f => f.Size), "upload", outgoingFiles: selected);
            // A receiver joining our hotspot pulls the exact preapproved offer.
            var id = pcPreparedSendId;
            if (id.Length == 0) {
                var result = await Task.Run(() => core.Call(new { op = "send", peer = new { name = peer.Name, host = peer.Host, port = peer.Port, fingerprint = peer.Fingerprint, deviceType = peer.DeviceType }, verificationCode = code, files = payload }));
                id = Text(result, "id");
            }
            if (id.Length == 0) throw new InvalidOperationException("未能创建发送任务，选中的文件已保留。");
            BusinessActivity();
            if (temporaryNetwork) temporaryTransferId = id;
            sentPeers[id] = peer;
            // Register the task before removing only the submitted selection.
            // Files added while the route was being checked remain selected.
            if (!transferViews.ContainsKey(id)) {
                var row = new TransferView(id);
                transferViews[id] = row;
                transferRows.Insert(0, row);
            }
            EmptyTransfers.Visibility = Visibility.Collapsed;
            foreach (var file in selected) files.Remove(file);
            StatusMessage.Text = "已加入传输记录，等待对方接收；文件传输时同步校验。";
            await RefreshState();
            return id;
        }
        catch {
            CancelPreparedPcSend();
            if (temporaryNetwork) await RestoreNetworkAsync();
            throw;
        }
        finally { pcPreparedSendId = ""; sending = false; UpdateSelection(); }
    }

    private async void Accept_Click(object sender, RoutedEventArgs e) => await TransferAction(sender, "accept");
    private async void Reject_Click(object sender, RoutedEventArgs e) => await TransferAction(sender, "reject");
    private async void Cancel_Click(object sender, RoutedEventArgs e) => await TransferAction(sender, "cancel");
    private async Task TransferAction(object sender, string op)
    {
        if ((sender as Button)?.Tag is string id) await RunCommand(new { op, id });
    }
    private async Task RunCommand(object request)
    {
        if (core is null) return;
        try { await Task.Run(() => core.Call(request)); await RefreshState(); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText($"ShareLink\n{Environment.MachineName}\nIP: {addresses}\n端口: 53317"); StatusMessage.Text = "连接信息已复制。"; }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { if (!Directory.Exists(downloadDir)) throw new IOException("保存位置已不可用，请在设置中重新选择已有文件夹。"); Process.Start(new ProcessStartInfo(downloadDir) { UseShellExecute = true }); }
        catch (Exception ex) { ShowError(ex.Message); }
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        if (browserSharing) {
            e.Cancel = true;
            if (modeChanging) return;
            modeChanging = true;
            try { if (await LeaveSingleModeAsync(endForClose: true)) Close(); }
            catch (Exception ex) { ShowError(ex.Message); }
            finally { modeChanging = false; }
            return;
        }
        if (sending || pairing || PcPairing || storageChanging || browserSharing) { e.Cancel = true; ShowToast("正在处理连接或分享操作，请完成当前操作后退出。"); return; }
        if ((!diagnostic || pcNativeTest) && !pcClosed) { e.Cancel = true; if (await ClosePcNativeAsync()) Close(); return; }
        if (!pcClosed && !closeAfterRestore && transferRows.Any(t => t.Active) && !AppDialog.Show(this, "退出 ShareLink？", "还有传输正在进行。退出会终止此次传输并清理未完成缓存，下次需要重新发送。", "退出应用", "继续传输", danger: true))
        { e.Cancel = true; return; }
        if (!diagnostic && !pcClosed && !closeAfterRestore)
        {
            e.Cancel = true;
            if (restoringForClose) return;
            restoringForClose = true;
            timer.Stop();
            await RestoreNetworkAsync();
            closeAfterRestore = true;
            Close();
            return;
        }
        closing = true; receiveConfirmation?.Cancel(); receiveOfferLifetime?.Cancel(); updateLifetime.Cancel();
        toastTimer.Stop(); timer.Stop();
        nearbyDiscovery?.Dispose();
        nearbyInvitation?.Dispose();
        nearbySession?.Dispose();
        core?.Dispose();
        desktopNotifications?.Dispose();
    }
    private void ShowError(string message)
    {
        StatusMessage.Text = message.Replace('\n', ' ');
        AppDialog.Show(this, "操作提示", message, "知道了", cancel: null);
    }
    internal JsonElement DiagnosticSnapshot() => core?.Call(new { op = "snapshot" }) ?? throw new InvalidOperationException("Core has not started.");
    internal void StopDiagnostic() { closing = true; toastTimer.Stop(); timer.Stop(); core?.Dispose(); }
    private string? Ask(string title, string description, string hint, string buttonText)
    {
        var input = new TextBox { MaxLength = 120, MinHeight = 42 };
        var stack = new StackPanel(); stack.Children.Add(input);
        stack.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, FontSize = 15, Foreground = Brushes.SlateGray, Margin = new Thickness(0, 10, 0, 0) });
        return AppDialog.Show(this, title, description, buttonText, extra: stack) && !string.IsNullOrWhiteSpace(input.Text) ? input.Text : null;
    }
    private static Peer ReadPeer(JsonElement p) => new(Text(p, "id"), Text(p, "name"), Text(p, "host"), (int)Number(p, "port"), Text(p, "fingerprint"), DeviceType: Text(p, "deviceType"));
    internal static string Text(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
    internal static double Number(JsonElement p, string key) => p.ValueKind == JsonValueKind.Object && p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n : 0;
    private static string GroupCode(string code) => string.Join(" ", Enumerable.Range(0, (code.Length + 3) / 4).Select(i => code.Substring(i * 4, Math.Min(4, code.Length - i * 4))));
    internal static string FormatBytes(double n) => n >= 1073741824 ? $"{n / 1073741824:F2} GB" : n >= 1048576 ? $"{n / 1048576:F1} MB" : n >= 1024 ? $"{n / 1024:F1} KB" : $"{n:0} B";
}

internal record SelectedFile(string Path, string Name, long Size)
{
    public string SizeLabel => MainWindow.FormatBytes(Size);
}
internal record Peer(string Id, string Name, string Host, int Port, string Fingerprint, bool Manual = false, string DeviceType = "unknown")
{
    public string Address => $"{Host}:{Port}";
    public string Key => $"{Host}:{Port}:{Fingerprint}";
}
internal sealed class TransferView(string id, int fileIndex = -1) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public int FileIndex { get; } = fileIndex;
    public string Title { get; private set; } = "";
    public string Subtitle { get; private set; } = "";
    public string StateLabel { get; private set; } = "";
    public string StateColor { get; private set; } = "#2463EB";
    public double SortTime { get; private set; }
    public string Detail { get; private set; } = "";
    public double Percent { get; private set; }
    public bool Active { get; private set; }
    public Visibility ProgressVisibility => Active ? Visibility.Visible : Visibility.Collapsed;
    public string PercentLabel => $"{Percent:0}%";
    public Visibility AcceptVisibility { get; private set; } = Visibility.Collapsed;
    public Visibility CancelVisibility { get; private set; } = Visibility.Collapsed;    public event PropertyChangedEventHandler? PropertyChanged;
    public void Update(JsonElement item)
    {
        var before = (Title, Subtitle, StateLabel, StateColor, SortTime, Detail, Percent, Active, ProgressVisibility, PercentLabel, AcceptVisibility, CancelVisibility);
        string state = MainWindow.Text(item, "state");
        bool receive = MainWindow.Text(item, "direction") == "receive";
        var names = item.TryGetProperty("files", out var list) ? list.EnumerateArray().Select(f => MainWindow.Text(f, "name")).ToArray() : [];
        Title = names.Length == 0 ? "准备文件" : names.Length == 1 ? names[0] : $"{names[0]} 等 {names.Length} 个文件";
        JsonElement? historyFile = FileIndex >= 0 && list.ValueKind == JsonValueKind.Array && FileIndex < list.GetArrayLength() ? list[FileIndex] : null;
        if (historyFile is { } entry) Title = MainWindow.Text(entry, "name");
        Subtitle = (receive ? "接收自 " : "发送给 ") + MainWindow.Text(item, "peerName");
        bool userTerminated = state == "cancelled" && MainWindow.Text(item, "error") == UserExitHistory.Label;
        StateLabel = userTerminated ? UserExitHistory.Label : state switch { "hashing" => "准备与校验", "awaiting_accept" => receive ? "等待你接收" : "等待对方接收", "transferring" => receive ? "正在接收" : "正在发送", "verifying" => "校验文件", "completed" => receive ? "已保存" : "已完成", "failed" => "传输失败", "cancelled" => "已取消", "rejected" => "已拒绝", _ => "正在连接" };
        bool savedFile = historyFile is { } saved && receive && MainWindow.Text(saved, "path").Length > 0;
        if (savedFile) StateLabel = "已保存";
        StateColor = state switch { "completed" => "#27836B", "failed" => "#C75757", "cancelled" or "rejected" => "#8290A5", _ => "#2463EB" };
        var total = historyFile is { } selectedFile ? MainWindow.Number(selectedFile, "size") : MainWindow.Number(item, "total");
        var bytes = MainWindow.Number(item, "bytes");
        var speed = MainWindow.Number(item, "speed");
        Percent = state == "completed" ? 100 : total > 0 ? Math.Clamp(bytes / total * 100, 0, 100) : 0;
        Active = state is not ("completed" or "failed" or "cancelled" or "rejected");        Detail = savedFile || userTerminated ? "" : FailureDetail(state, MainWindow.Text(item, "error"));
        if (savedFile) StateColor = "#27836B";
        string size = Active ? $"{MainWindow.FormatBytes(bytes)} / {MainWindow.FormatBytes(total)}" + (state == "transferring" ? $" · {MainWindow.FormatBytes(speed)}/s" : "") : MainWindow.FormatBytes(total);
        var finishedAt = MainWindow.Number(item, "finishedAt");
        SortTime = finishedAt > 0 ? finishedAt : MainWindow.Number(item, "createdAt");
        if (!Active && finishedAt > 0) size += " · " + DateTimeOffset.FromUnixTimeSeconds((long)finishedAt).LocalDateTime.ToString("yyyy年M月d日 tt h:mm", System.Globalization.CultureInfo.GetCultureInfo("zh-CN"));
        Detail = size + (Detail.Length > 0 ? " · " + Detail : "");
        AcceptVisibility = receive && state == "awaiting_accept" ? Visibility.Visible : Visibility.Collapsed;
        CancelVisibility = Active && AcceptVisibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        Changed(nameof(Title), before.Title, Title); Changed(nameof(Subtitle), before.Subtitle, Subtitle);
        Changed(nameof(StateLabel), before.StateLabel, StateLabel); Changed(nameof(StateColor), before.StateColor, StateColor);
        Changed(nameof(SortTime), before.SortTime, SortTime); Changed(nameof(Detail), before.Detail, Detail);
        Changed(nameof(Percent), before.Percent, Percent); Changed(nameof(Active), before.Active, Active);
        Changed(nameof(ProgressVisibility), before.ProgressVisibility, ProgressVisibility); Changed(nameof(PercentLabel), before.PercentLabel, PercentLabel);
        Changed(nameof(AcceptVisibility), before.AcceptVisibility, AcceptVisibility); Changed(nameof(CancelVisibility), before.CancelVisibility, CancelVisibility);
    }
    private static string FailureDetail(string state, string error)
    {
        // A closed transport does not prove that the peer chose to exit.
        // Keep the native state and raw history unchanged; only explain known interruptions.
        bool interrupted = error.Contains("(os error 10053)", StringComparison.Ordinal)
            || error.Contains("peer closed connection without sending TLS close_notify", StringComparison.OrdinalIgnoreCase);
        return state == "failed" && interrupted ? "连接已中断，文件未传完。请重新连接后发送。" : error;
    }

    private void Changed<T>(string property, T before, T after) {
        if (!EqualityComparer<T>.Default.Equals(before, after)) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

}
