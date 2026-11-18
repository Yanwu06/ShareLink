using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using QRCoder;

namespace ShareLink.BrowserSharing;

public sealed partial class BrowserShareView : UserControl, IAsyncDisposable
{
    private readonly string output;
    private readonly List<string> paths = [];
    private readonly TextBlock status = new() { Text = "选择要分享的文件，接收方无需安装 ShareLink。", TextWrapping = TextWrapping.Wrap, FontSize = 15 };
    private readonly StackPanel fileList = new();
    private readonly TextBlock emptyMessage = new() { Text = "请添加文件并开启分享", FontSize = 23, VerticalAlignment = VerticalAlignment.Center };
    private readonly Func<bool>? confirmEnd;
    private readonly Func<bool>? confirmNetworkAccess;
    private readonly ScrollViewer filesScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 96, Margin = new Thickness(0, 0, 0, 8) };
    private readonly StackPanel receiverRows = new();
    private readonly Dictionary<string, ReceiverRow> progressRows = new();
    private long snapshotSavedAt, peerLabelsUpdatedAt;
    private readonly Dictionary<string, string> peerLabels = new();
    private readonly ShareStepCard wifiStep = new(1), webStep = new(2);
    private readonly Grid steps = new();
    private Image qr => wifiStep.Qr;
    private Image websiteQr => webStep.Qr;
    private TextBlock address => webStep.Primary;
    private TextBlock receivers => wifiStep.Connection;
    private bool CanAddFiles => !stopping && (!busy || server is not null && url is not null && !Starting);
    private readonly Button add = new() { Content = "添加文件", Padding = new Thickness(16, 9, 16, 9) };
    private readonly Button start = new() { Content = "开启分享", Padding = new Thickness(22, 9, 22, 9), Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button stop = new() { Content = "结束分享", Padding = new Thickness(18, 10, 18, 10), Margin = new Thickness(12, 0, 0, 0) };
    private readonly Button allowUpload = new() { Content = "允许上传", Name = "BrowserAllowUpload" };
    private readonly TextBlock selectionSummary = new() { Foreground = new SolidColorBrush(Color.FromRgb(113,128,150)), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14,0,14,0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock sharingTitle = new() { Text = "待分享文件", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,12) };
    private readonly string? receiveDirectory;
    private IShareAccessPoint? ap;
    private CancellationTokenSource? cancellation;
    private BrowserFileServer? server;
    private Task? startup;
    private IPAddress? localAddress;
    private bool busy, stopping;
    private string? url;
    public bool CleanupSucceeded { get; private set; } = true;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public event Action<string>? StatusChanged;
    public event Action<string>? OperationNotice;
    public string DebugStatus => status.Text;
    private string StatusText { get => status.Text; set { status.Text = value; StatusChanged?.Invoke(value); } }
    public BrowserShareView(string output, IEnumerable<string>? initialFiles = null, bool autoStart = false, Func<bool>? confirmEnd = null, Func<bool>? confirmNetworkAccess = null, string? receiveDirectory = null)
    {
        this.receiveDirectory = receiveDirectory;
        this.confirmEnd = confirmEnd; this.confirmNetworkAccess = confirmNetworkAccess;
        this.output = Path.GetFullPath(output); Directory.CreateDirectory(this.output);
        Background = new SolidColorBrush(Color.FromRgb(245, 247, 251)); Foreground = new SolidColorBrush(Color.FromRgb(15, 38, 60));
        FontFamily = new FontFamily("Microsoft YaHei UI,Segoe UI");
        Background = Brushes.Transparent;
        var panel=new Grid();panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=GridLength.Auto});panel.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star),MinHeight=80});panel.RowDefinitions.Add(new(){Height=GridLength.Auto});
        var toolbar=new Grid{Margin=new Thickness(0,0,0,12)};toolbar.ColumnDefinitions.Add(new());toolbar.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        toolbar.Children.Add(new TextBlock{Text="浏览器分享",FontSize=23,FontWeight=FontWeights.Bold,VerticalAlignment=VerticalAlignment.Center});
        var uploadControl=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};uploadControl.Children.Add(new TextBlock{Text="允许上传",FontSize=15,Margin=new Thickness(0,0,10,0),VerticalAlignment=VerticalAlignment.Center});
        if(TryFindResource("Toggle") is Style toggle)allowUpload.Style=toggle;
        System.Windows.Automation.AutomationProperties.SetName(allowUpload,"允许上传");uploadControl.Children.Add(allowUpload);Grid.SetColumn(uploadControl,1);toolbar.Children.Add(uploadControl);panel.Children.Add(toolbar);
        steps.ColumnDefinitions.Add(new());steps.ColumnDefinitions.Add(new(){Width=new GridLength(12)});steps.ColumnDefinitions.Add(new());steps.Margin=new Thickness(0,0,0,12);steps.MinHeight=270;steps.Children.Add(wifiStep);Grid.SetColumn(webStep,2);steps.Children.Add(webStep);Grid.SetRow(steps,1);panel.Children.Add(steps);
        var sharing=new Grid{Margin=new Thickness(18)};sharing.RowDefinitions.Add(new(){Height=GridLength.Auto});sharing.RowDefinitions.Add(new(){Height=GridLength.Auto});sharing.RowDefinitions.Add(new());sharing.Children.Add(sharingTitle);
        filesScroll.Content=fileList;filesScroll.MaxHeight=142;filesScroll.Margin=new Thickness(0,0,0,10);Grid.SetRow(filesScroll,1);sharing.Children.Add(filesScroll);
        var receiverScroll=new ScrollViewer{Content=receiverRows,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};Grid.SetRow(receiverScroll,2);sharing.Children.Add(receiverScroll);
        emptyMessage.HorizontalAlignment=HorizontalAlignment.Center;emptyMessage.FontSize=17;emptyMessage.FontWeight=FontWeights.Normal;emptyMessage.Foreground=new SolidColorBrush(Color.FromRgb(113,128,150));Grid.SetRow(emptyMessage,2);sharing.Children.Add(emptyMessage);
        var sharingCard=new Border{Child=sharing,Background=Brushes.White,CornerRadius=new CornerRadius(8),BorderBrush=new SolidColorBrush(Color.FromRgb(226,230,237)),BorderThickness=new Thickness(1)};Grid.SetRow(sharingCard,2);panel.Children.Add(sharingCard);
        var actions=new Grid();actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});actions.ColumnDefinitions.Add(new());actions.ColumnDefinitions.Add(new(){Width=GridLength.Auto});
        add.Name="BrowserAddFilesButton";add.Margin=new Thickness(0);add.Padding=new Thickness(14,8,14,8);add.Background=Brushes.White;System.Windows.Automation.AutomationProperties.SetName(add,"添加文件");
        var addRow=new StackPanel{Orientation=Orientation.Horizontal};addRow.Children.Add(new System.Windows.Shapes.Path{Data=TryFindResource("AddGlyph") as Geometry,Width=16,Height=16,Stretch=Stretch.Uniform,Fill=TryFindResource("Blue") as Brush,Margin=new Thickness(0,0,9,0)});addRow.Children.Add(new TextBlock{Text="添加文件",FontSize=16,VerticalAlignment=VerticalAlignment.Center});add.Content=addRow;actions.Children.Add(add);
        Grid.SetColumn(selectionSummary,1);actions.Children.Add(selectionSummary);
        if(TryFindResource("Primary") is Style primary){start.Style=primary;stop.Style=primary;}start.Margin=stop.Margin=new Thickness(0);start.MinWidth=stop.MinWidth=140;stop.Background=new SolidColorBrush(Color.FromRgb(192,79,79));Grid.SetColumn(start,2);Grid.SetColumn(stop,2);actions.Children.Add(start);actions.Children.Add(stop);
        var actionBar=new Border{Child=actions,Margin=new Thickness(0,12,0,0),Padding=new Thickness(10,8,10,8),MinHeight=62,Background=Brushes.White,CornerRadius=new CornerRadius(8),BorderBrush=new SolidColorBrush(Color.FromRgb(226,230,237)),BorderThickness=new Thickness(1)};Grid.SetRow(actionBar,3);panel.Children.Add(actionBar);Content=panel;
        add.Click += (_, _) => { var picker = new OpenFileDialog { Multiselect = true, Title = "选择浏览器分享的文件" }; if (picker.ShowDialog(Window.GetWindow(this)) == true) AddFiles(picker.FileNames); };
        start.Click += (_, _) => BeginStart();
        allowUpload.Click += (_, _) => {
            if(server is null||receiveDirectory is null||stopping||Starting)return;
            try{
                if(server.UploadsEnabled){server.DisableUploads();OperationNotice?.Invoke("已关闭新的上传接收，正在接收的文件会继续完成。");}
                else{server.EnableUploads(receiveDirectory);OperationNotice?.Invoke("本次分享已允许上传，结束分享后失效。");}
            }catch(Exception ex){OperationNotice?.Invoke(ex.Message);}
            RenderFiles();
        };
        stop.Click += async (_, _) => await EndAsync();
        AllowDrop = true; Drop += (_, e) => { if (CanAddFiles && e.Data.GetData(DataFormats.FileDrop) is string[] dropped) AddFiles(dropped); };
        Loaded += (_, _) => { StatusText = "热点未开启"; timer.Start(); if (autoStart && paths.Count > 0) BeginStart(); };
        timer.Tick += async (_, _) => {
            if (File.Exists(Path.Combine(this.output, "stop"))) { await EndAsync(); return; }
            if (server is not null && url is not null && !stopping) {
                UpdateReceivers();
                bool pendingBytes = server.Progress.Snapshot().Any(receiver => receiver.Files.Any(file => file.Active > 0 && file.Sent < file.Size));
                bool receiving = server.Progress.Snapshot().Any(receiver => receiver.Files.Any(file => file.Direction == "upload" && file.Active > 0));
                StatusText = (receiving ? "分享中 · 正在接收文件" : server.ActiveDownloads > 0 ? pendingBytes ? "分享中 · 正在传送文件" : "分享中 · 正在结束下载请求" : "分享中 · 等待接收方下载") + "\n请在接收端浏览器中查看保存结果。";
            }
            UpdateActionState();
        };
        allowUpload.IsEnabled = receiveDirectory is not null;
        AddFiles(initialFiles ?? []);
        SaveState("prepared");
    }

    private void AddFiles(IEnumerable<string> selected)
    {
        if (!CanAddFiles) return;
        try {
            var additions = selected.Select(p => new SharedFile(p).Path).Where(p => !paths.Contains(p, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (paths.Count + additions.Length > 64) throw new IOException("每次最多分享 64 个文件。");
            if (busy) server!.AddFiles(additions);
            paths.AddRange(additions); RenderFiles();
            if (busy) { SaveSelectedFiles(); SaveState("ready"); }
        } catch (Exception ex) { StatusText = ex.Message; OperationNotice?.Invoke(ex.Message == "每次最多分享 64 个文件。" ? ex.Message : "文件未能添加，请重新选择可访问的文件。"); }
    }
    private void SaveSelectedFiles() => File.WriteAllText(Path.Combine(output, "selected-files.json"), JsonSerializer.Serialize(server!.Files.Select(f => new { name = f.Name, path = f.Path, bytes = f.Size }), new JsonSerializerOptions { WriteIndented = true }));
    private void RenderFiles()
    {
        fileList.Children.Clear();
        foreach (string path in paths.ToArray()) {
            var row = new DockPanel { Margin = new Thickness(0, 3, 0, 3) };
            var remove = new Button { Content = "移除", Padding = new Thickness(10, 4, 10, 4), IsEnabled = !busy };
            remove.Click += (_, _) => { if (!busy) { paths.Remove(path); RenderFiles(); } }; DockPanel.SetDock(remove, Dock.Right); row.Children.Add(remove);
            var thumbnail = new Image { Width = 36, Height = 36, Margin = new Thickness(0, 0, 10, 0), Source = TryFindResource("FileDrawing") as ImageSource };
            ShellThumbnail.SetFilePath(thumbnail, path); DockPanel.SetDock(thumbnail, Dock.Left); row.Children.Add(thumbnail);
            row.Children.Add(new MarqueeText { Text = Path.GetFileName(path), VerticalAlignment = VerticalAlignment.Center, ToolTip = path, Margin = new Thickness(0, 0, 10, 0) });
            fileList.Children.Add(row);
        }
        UpdateActionState();
        double total=0;try{total=paths.Sum(p=>File.Exists(p)?(double)new FileInfo(p).Length:0);}catch{}
        string size=total>=1073741824?$"{total/1073741824:F2} GB":total>=1048576?$"{total/1048576:F1} MB":$"{total:0} B";
        selectionSummary.Text=paths.Count==0?"尚未选择文件":$"已选 {paths.Count} 个文件 · {size}";

        if(!busy)emptyMessage.Text=paths.Count==0?"请添加文件并开启分享":"文件已就绪，点击开启分享";
        stop.IsEnabled = busy && !stopping || !CleanupSucceeded;
        filesScroll.Visibility = paths.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void UpdateActionState()
    {
        bool active=busy||stopping||server is not null||ap is not null||!CleanupSucceeded;
        start.IsEnabled=!busy&&!stopping&&CleanupSucceeded&&paths.Count>0;add.IsEnabled=CanAddFiles;
        start.Visibility=active?Visibility.Collapsed:Visibility.Visible;stop.Visibility=active?Visibility.Visible:Visibility.Collapsed;
        stop.IsEnabled=busy&&!stopping||!CleanupSucceeded;
        sharingTitle.Text=server is not null&&url is not null?"正在分享":"待分享文件";
        bool uploads=server?.UploadsEnabled==true;allowUpload.Tag=uploads?"True":"False";
        allowUpload.IsEnabled=receiveDirectory is not null&&server is not null&&url is not null&&!stopping;
        System.Windows.Automation.AutomationProperties.SetItemStatus(allowUpload,uploads?"已允许上传":"未允许上传");
    }
    private void BeginStart()
    {
        if (busy || stopping) return;
        if (paths.Count == 0) { StatusText = "请添加文件并开启分享"; OperationNotice?.Invoke("请先添加文件。"); return; }
        busy = true; RenderFiles();
        cancellation?.Dispose(); cancellation = new();
        url = null; localAddress = null; qr.Source = websiteQr.Source = null;
        emptyMessage.Text = "正在准备分享…"; wifiStep.SetActive(false, true); webStep.SetActive(false, true);
        address.Text = "";
        SaveState("starting");
        startup = StartAsync();
    }
    private async Task StartAsync()
    {
        var token = cancellation!.Token;
        CleanupSucceeded = false;
        try {
            // Validate files before modifying networking. Store metadata only; do not hash multi-GB sources on the UI thread.
            allowUpload.IsEnabled = receiveDirectory is not null;
            server = new BrowserFileServer(paths, output);
            SaveSelectedFiles();
            File.WriteAllText(Path.Combine(output, "network-before.json"), JsonSerializer.Serialize(NetworkSnapshot(), new JsonSerializerOptions { WriteIndented = true }));
            StatusText = "正在开启临时 Wi-Fi…";
            token.ThrowIfCancellationRequested();
            if (ap is null) { ap = new LegacyAccessPoint(); await ap.StartAsync(token); }
            token.ThrowIfCancellationRequested();
            wifiStep.SetWifi(ap.Ssid, ap.Password);
            string payload = $"WIFI:T:WPA;S:{ap.Ssid};P:{ap.Password};;";
            qr.Source = QrImage(payload, Path.Combine(output, "wifi-qr.png"));
            File.WriteAllText(Path.Combine(output, "wifi-qr-payload.txt"), payload); SaveState("wifi-started");
            localAddress = await ap.WaitForAddressAsync(token);
            try { await server.StartAsync(localAddress, 80, token); }
            catch (IOException) {
                await server.DisposeAsync(); server = new BrowserFileServer(paths, output);
                await server.StartAsync(localAddress, 0, token);
            }
            token.ThrowIfCancellationRequested();
            StatusText = "正在准备下载页访问权限…";
            await BrowserShareFirewall.EnsureAsync(localAddress, server.Port, output, () => confirmNetworkAccess?.Invoke() ?? MessageBox.Show("Windows 需要一次管理员确认，允许接入临时 Wi-Fi 的设备访问下载页。如果此前点过拒绝，将调整此程序由系统生成的 TCP 拒绝规则。是否继续？", "允许手机访问下载页", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes, token);
            token.ThrowIfCancellationRequested();
            ShowDownloadAddress(localAddress, server.Port);
            wifiStep.SetActive(true); webStep.SetActive(true);
            emptyMessage.Text = "等待设备连接并下载";
            add.IsEnabled = !stopping;
            RenderFiles();
            StatusText = "分享已开启，等待接收方扫码。"; SaveState("ready");
        }
        catch (Exception ex) {
            File.WriteAllText(Path.Combine(output, "start-error.txt"), ex.ToString());
            StatusText = ex is OperationCanceledException ? "正在结束分享…" : "分享未启动：" + ex.Message;
            if (ex is not OperationCanceledException) OperationNotice?.Invoke(ex is ShareFirewallException ? ex.Message : "分享未能开启，请检查 WLAN 后重试。");
            SaveState("failed");
            try { if (server is not null) { await server.DisposeAsync(); server = null; } if (ap is not null) { await ap.DisposeAsync(); ap = null; } CleanupSucceeded = true; }
            catch (Exception cleanup) { File.WriteAllText(Path.Combine(output, "cleanup-error.txt"), cleanup.ToString()); StatusText += "\n" + cleanup.Message; }
            if (!stopping && CleanupSucceeded) { busy = false; RenderFiles(); }
        }
    }
    private void ShowDownloadAddress(IPAddress hotspotAddress, int port)
    {
        // Use the actual Wi-Fi subnet address for both typing and scanning.
        url = new Uri($"http://{hotspotAddress}:{port}/").AbsoluteUri;
        address.FontSize = 15;
        address.Text = new Uri(url).Authority;
        websiteQr.Source = QrImage(url, Path.Combine(output, "website-qr.png"));
        File.WriteAllText(Path.Combine(output, "website-qr-payload.txt"), url);
    }
    public static object DiagnosticProgressChecks(string output) {
        Directory.CreateDirectory(output);
        string path = Path.Combine(output, "progress-only-fixture.bin");
        if (!File.Exists(path)) { using var file = File.Create(path); file.SetLength(8 * 1024 * 1024); }
        var view = new BrowserShareView(Path.Combine(output, "ui-session"));
        view.server = new BrowserFileServer([path], output);
        var fileInfo = view.server.Files[0]; const string peer = "192.168.49.2";
        var accessPoint = new DiagnosticAccessPoint(); view.ap = accessPoint;
        view.server.Access.TryAdmit(IPAddress.Parse(peer));
        view.server.Progress.Visit(peer);
        view.server.Progress.Begin("192.168.49.3", fileInfo);
        view.UpdateReceivers();
        bool authorizedVisitor = view.receivers.Text == "MAC 地址为：10:20:30:40:50:60 的设备已连接" && view.progressRows.Count == 0;
        view.server.Progress.Begin(peer, fileInfo); view.UpdateReceivers();
        var row = view.progressRows.Single().Value; var name = row.Name; int nameChanges = 0, detailChanges = 0;
        var nameDescriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
        EventHandler nameChanged = (_, _) => nameChanges++;
        EventHandler detailChanged = (_, _) => detailChanges++;
        nameDescriptor.AddValueChanged(name, nameChanged); nameDescriptor.AddValueChanged(row.Detail, detailChanged);
        for (int i = 0; i < 30; i++) { view.server.Progress.Bytes(peer, fileInfo.Id, i * 65536L, 65536); view.UpdateReceivers(); }
        bool stable = ReferenceEquals(row, view.progressRows.Single().Value) && ReferenceEquals(name, view.progressRows.Single().Value.Name) && nameChanges == 0 && detailChanges == 30;
        bool caption = PeerIdentity.Display("20-bd-1d-79-62-56") == "已连接设备的MAC地址：20:BD:1D:79:62:56" && PeerIdentity.Display("Test phone") == "Test phone";
        nameDescriptor.RemoveValueChanged(name, nameChanged); nameDescriptor.RemoveValueChanged(row.Detail, detailChanged); view.timer.Stop();
        bool authorizedOnly = authorizedVisitor && accessPoint.Queried.All(address => address == peer) && view.progressRows.Count == 1 && row.Peer.Text == "已连接设备的MAC地址：10:20:30:40:50:60";
        if (!stable || !caption || !authorizedOnly) throw new InvalidOperationException("Progress control reuse or authorized MAC display check failed");
        string uploadId = view.server.Progress.BeginUpload(peer, "upload-progress.bin", 65536);
        view.server.Progress.Bytes(peer, uploadId, 0, 16384); view.UpdateReceivers();
        var uploadRow = view.progressRows[peer + ":" + uploadId];
        bool uploadPartial = uploadRow.Bar.Value == 25 && uploadRow.Detail.Text.Contains("25%") && uploadRow.Detail.Text.Contains("接收中");
        view.server.Progress.Bytes(peer, uploadId, 16384, 49152); view.UpdateReceivers();
        bool uploadSaving = uploadRow.Bar.Value == 100 && uploadRow.Detail.Text.Contains("正在保存") && !uploadRow.Detail.Text.Contains("已接收");
        view.server.Progress.Finish(peer, uploadId, true); view.UpdateReceivers();
        bool uploadComplete = uploadRow.Bar.Value == 100 && uploadRow.Detail.Text.Contains("已接收");
        if (!uploadPartial || !uploadSaving || !uploadComplete) throw new InvalidOperationException("Upload receiver progress state failed");
        return new { passed = true, uploadPartial, uploadSaving, uploadComplete, stable, caption, authorizedOnly, nameChanges, detailChanges, refreshMilliseconds = view.timer.Interval.TotalMilliseconds, scope = "actual WPF controls with generated progress; no hotspot operations" };
    }
    public static object DiagnosticQrChecks(string output)
    {
        Directory.CreateDirectory(output);
        var view = new BrowserShareView(Path.Combine(output, "fixture-session"));
        var host = new Window { Content = view, Width = 876, Height = 490, Left = -20000, Top = -20000, ShowInTaskbar = false, ShowActivated = false, Opacity = 0 };
        var checks = new List<object>();
        void Check(string name, bool passed) { checks.Add(new { name, passed }); if (!passed) throw new InvalidOperationException(name); }
        try {
            host.Show(); host.UpdateLayout();
            Rect Bounds(FrameworkElement e) => e.TransformToAncestor(view).TransformBounds(new Rect(0,0,e.ActualWidth,e.ActualHeight));
            void Capture(string name) {
                host.UpdateLayout(); var bitmap = new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32); bitmap.Render(view);
                var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output,name)); png.Save(file);
            }
            Check("idle cards show placeholders before a live QR is generated",
                view.wifiStep.PlaceholderImage.IsVisible && view.webStep.PlaceholderImage.IsVisible
                && view.qr.Visibility == Visibility.Hidden && view.websiteQr.Visibility == Visibility.Hidden);
            Check("step explanations remain hidden until requested", !view.wifiStep.OfflineHint.IsVisible);
            view.wifiStep.OpenHelp(); host.UpdateLayout();
            Check("click-to-view connection explanation retains the offline reminder", view.wifiStep.OfflineHint.IsVisible && view.wifiStep.OfflineHint.Text.Contains("直至完成下载"));
            view.wifiStep.CloseHelp();
            Capture("desktop-steps-idle.png");
            var idleWifi = Bounds(view.wifiStep.PlaceholderImage); var idleWeb = Bounds(view.webStep.PlaceholderImage);
            view.wifiStep.SetWifi("SL123", "generated-fixture");
            view.qr.Source = QrImage("WIFI:T:WPA;S:SL123;P:generated-fixture;;",Path.Combine(output,"fixture-wifi-qr.png"));
            view.ShowDownloadAddress(IPAddress.Parse("192.168.137.1"),80); view.wifiStep.SetActive(true); view.webStep.SetActive(true); host.UpdateLayout();
            var first = Bounds(view.qr); var second = Bounds(view.websiteQr);
            Check("idle symbol and live QR have identical dimensions and positions", Math.Abs(idleWifi.Left-first.Left)<1 && Math.Abs(idleWifi.Top-first.Top)<1 && Math.Abs(idleWifi.Width-first.Width)<1 && Math.Abs(idleWifi.Height-first.Height)<1 && Math.Abs(idleWeb.Left-second.Left)<1 && Math.Abs(idleWeb.Top-second.Top)<1 && Math.Abs(idleWeb.Width-second.Width)<1 && Math.Abs(idleWeb.Height-second.Height)<1);
            Check("step one and step two occupy equal left and right cards", Bounds(view.wifiStep).Right < Bounds(view.webStep).Left && Math.Abs(view.wifiStep.ActualWidth-view.webStep.ActualWidth)<1);
            Check("both QR codes stay centered inside their own cards", Math.Abs(first.Left+first.Width/2-(Bounds(view.wifiStep).Left+view.wifiStep.ActualWidth/2))<1 && Math.Abs(second.Left+second.Width/2-(Bounds(view.webStep).Left+view.webStep.ActualWidth/2))<1);
            Check("Wi-Fi name and password stay fully below the QR area", Bounds(view.wifiStep.Primary).Top > first.Bottom && Bounds(view.wifiStep.Secondary).Top > first.Bottom);
            Check("web address stays fully below its QR area", Bounds(view.webStep.Primary).Top > second.Bottom);
            var qrArea = Bounds((FrameworkElement)view.qr.Parent);
            Check("QR is vertically centered in its dedicated area", Math.Abs(first.Top + first.Height/2 - (qrArea.Top + qrArea.Height/2)) < 1);
            Check("both step QR codes share the same horizontal band", Math.Abs(first.Top-second.Top) < 1);
            Check("QR and visible native address agree",view.address.Text == "192.168.137.1" && view.url == "http://192.168.137.1/" && File.ReadAllText(Path.Combine(output,"fixture-session","website-qr-payload.txt")) == view.url);
            Check("placeholder and live QR share the same centering coordinate space", ReferenceEquals(view.qr.Parent, view.wifiStep.PlaceholderImage.Parent) && ReferenceEquals(view.websiteQr.Parent, view.webStep.PlaceholderImage.Parent));
            var physicalPositions = new List<object>();
            foreach (var width in new[] { 875d, 876d, 877d, 1359d, 1360d, 1361d }) {
                host.Width = width; view.wifiStep.SetActive(false); view.webStep.SetActive(false); host.UpdateLayout();
                var idleOne = view.wifiStep.PlaceholderImage.PointToScreen(new Point());
                var idleTwo = view.webStep.PlaceholderImage.PointToScreen(new Point());
                view.wifiStep.SetActive(true); view.webStep.SetActive(true); host.UpdateLayout();
                var liveOne = view.qr.PointToScreen(new Point()); var liveTwo = view.websiteQr.PointToScreen(new Point());
                physicalPositions.Add(new { width, dpi = VisualTreeHelper.GetDpi(view).DpiScaleX, idleWifi = new { idleOne.X, idleOne.Y }, liveWifi = new { liveOne.X, liveOne.Y }, idleWeb = new { idleTwo.X, idleTwo.Y }, liveWeb = new { liveTwo.X, liveTwo.Y } });
                Check($"physical QR origins remain identical at window width {width}", Math.Abs(idleOne.X-liveOne.X)<0.001 && Math.Abs(idleOne.Y-liveOne.Y)<0.001 && Math.Abs(idleTwo.X-liveTwo.X)<0.001 && Math.Abs(idleTwo.Y-liveTwo.Y)<0.001);
            }
            File.WriteAllText(Path.Combine(output, "qr-physical-positions.json"), JsonSerializer.Serialize(physicalPositions, new JsonSerializerOptions { WriteIndented = true }));
            host.Width = 876; host.UpdateLayout();
            Capture("desktop-steps-active.png");
            view.ShowDownloadAddress(IPAddress.Parse("192.168.49.1"),8081);
            Check("fallback port is preserved in text and QR",view.address.Text == "192.168.49.1:8081" && view.url == "http://192.168.49.1:8081/");
            return new { passed = true, scope = "actual WPF equal step cards, Lucide placeholders and runtime address QR; no hotspot operations", checks };
        } finally { view.timer.Stop(); host.Close(); }
    }
    private static BitmapSource QrImage(string payload, string path)
    {
        using var data = QRCodeGenerator.GenerateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        using var png = new PngByteQRCode(data); byte[] bytes = png.GetGraphic(8);
        File.WriteAllBytes(path, bytes);
        var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.StreamSource = new MemoryStream(bytes); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }
    private void SaveState(string state) => File.WriteAllText(Path.Combine(output, "session.json"), JsonSerializer.Serialize(new {
        state, ssid = ap?.Ssid, password = ap?.Password, apStatus = ap?.Status, mode = ap?.Mode,
        interfaceId = ap?.InterfaceId, interfaceName = ap?.InterfaceName, address = localAddress?.ToString(),
        port = server?.Port, url,
        files = server?.Files.Select(f => new { id = f.Id, name = f.Name, size = f.Size }), at = DateTimeOffset.Now
    }, new JsonSerializerOptions { WriteIndented = true }));

    public static async Task<string[]> DiagnosticSharingLifecycleAsync(string output, Action<BrowserShareView, bool> verifyMode)
    {
        var checks = new List<string>();
        var view = new BrowserShareView(output);
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        try {
            verifyMode(view, false);
            Check("idle single mode permits switching", !view.SharingActive);
            view.startup = Task.CompletedTask;
            Check("completed failed startup does not lock idle mode", !view.SharingActive);
            view.busy = true; verifyMode(view, true);
            Check("startup locks mode before resources are ready", view.SharingActive);
            view.busy = false;
            var accessPoint = new DiagnosticAccessPoint { FailDisposal = true }; view.ap = accessPoint;
            verifyMode(view, true);
            Check("open idle hotspot locks mode without active transfers", view.SharingActive && !view.HasActiveDownloads);
            bool stopped = await view.EndAsync(false); verifyMode(view, true);
            Check("failed cleanup keeps mode locked for retry", !stopped && view.SharingActive);
            accessPoint.FailDisposal = false;
            stopped = await view.EndAsync(false); verifyMode(view, false);
            Check("explicit successful end unlocks mode", stopped && !view.SharingActive);
            return checks.ToArray();
        } finally { view.timer.Stop(); }
    }

    public bool HasActiveDownloads => server is { ActiveDownloads: > 0 };
    public bool Starting => startup is { IsCompleted: false };
    // An idle open share still owns its hotspot/server until explicitly ended.
    public bool SharingActive => busy || stopping || Starting || server is not null || ap is not null || !CleanupSucceeded;
    public async Task<bool> EndAsync(bool ask = true)
    {
        if (stopping) return false;
        // Closing during downloads requires an explicit choice; closing an idle session does not.
        if (server is { ActiveDownloads: > 0 } && (!ask || confirmEnd?.Invoke() != true)) return false;
        stopping = true; cancellation?.Cancel(); timer.Stop(); start.IsEnabled = add.IsEnabled = stop.IsEnabled = allowUpload.IsEnabled = false;
        void Trace(string stage) => File.AppendAllText(Path.Combine(output, "cleanup.log"), $"{DateTimeOffset.Now:O} {stage}\n");
        Trace("stop-requested");
        try {
            if (startup is not null) await startup;
            if (server is not null) { await server.DisposeAsync(); server = null; } Trace("http-stopped");
            if (ap is not null) { await ap.DisposeAsync(); ap = null; } Trace("ap-stopped");
            CleanupSucceeded = true; SaveState("stopped");
        } catch (Exception ex) {
            Trace("cleanup-error " + ex); SaveState("cleanup-failed");
            StatusText = "分享清理未完成：" + ex.Message + "\n请检查 Windows 移动热点后重试结束。";
            OperationNotice?.Invoke("分享尚未结束，请检查 Windows 移动热点后重试。");
            stopping = false; stop.IsEnabled = true; timer.Start(); return false;
        }
        File.WriteAllText(Path.Combine(output, "network-after.json"), JsonSerializer.Serialize(NetworkSnapshot(), new JsonSerializerOptions { WriteIndented = true }));
        cancellation?.Dispose(); cancellation = null; startup = null; busy = stopping = false;
        qr.Source = websiteQr.Source = null; address.Text = "";
        receiverRows.Children.Clear(); progressRows.Clear(); peerLabels.Clear();
        emptyMessage.Text = "请添加文件并开启分享"; emptyMessage.Visibility = Visibility.Visible;
        wifiStep.SetActive(false); webStep.SetActive(false);
        StatusText = "分享已结束，可重新选择文件开启分享。"; RenderFiles(); timer.Start(); return true;
    }
    private sealed class DiagnosticAccessPoint : IShareAccessPoint {
        public readonly List<string> Queried = [];
        public bool FailDisposal;
        public string Ssid => "generated-fixture";
        public string Password => "fixture-only";
        public string Status => "fixture";
        public string Mode => "fixture";
        public string? InterfaceId => null;
        public string? InterfaceName => null;
        public int ConnectedCount => 2;
        public string DeviceLabel(string address) { Queried.Add(address); return address == "192.168.49.2" ? "10:20:30:40:50:60" : "AA:BB:CC:DD:EE:11"; }
        public Task StartAsync(CancellationToken token) => throw new InvalidOperationException("Diagnostic adapter never starts a hotspot");
        public Task<IPAddress> WaitForAddressAsync(CancellationToken token) => throw new InvalidOperationException("Diagnostic adapter has no network");
        public ValueTask DisposeAsync() => FailDisposal ? ValueTask.FromException(new IOException("Generated cleanup failure")) : ValueTask.CompletedTask;
    }
    private sealed class ReceiverRow {
        public readonly StackPanel View = new() { Margin = new Thickness(0, 6, 0, 6) };
        public readonly TextBlock Peer = new() { FontSize = 15, Foreground = System.Windows.Media.Brushes.SlateGray, TextWrapping = TextWrapping.Wrap };
        public readonly MarqueeText Name = new() { FontSize = 17, Margin = new Thickness(0, 3, 0, 4) };
        public readonly ProgressBar Bar = new() { Minimum = 0, Maximum = 100, Height = 5 };
        public readonly TextBlock Detail = new() { FontSize = 15, Foreground = System.Windows.Media.Brushes.SlateGray, Margin = new Thickness(0, 3, 0, 0) };
        public ReceiverRow() { Peer.Visibility = Visibility.Collapsed; View.Children.Add(Peer); View.Children.Add(Name); View.Children.Add(Bar); View.Children.Add(Detail); }
    }
    private void UpdateReceivers()
    {
        string? allowed = server!.AllowedReceiver;
        var snapshot = server.Progress.Snapshot().Where(receiver => receiver.Address == allowed).ToArray();
        long now = Environment.TickCount64;
        if (now - peerLabelsUpdatedAt >= 1000 || allowed is not null && !peerLabels.ContainsKey(allowed)) {
            peerLabelsUpdatedAt = now; peerLabels.Clear();
            if (allowed is not null) peerLabels[allowed] = PeerIdentity.AuthorizedDisplay(allowed, address => PeerIdentity.Mac(address, ap?.InterfaceId) ?? ap?.DeviceLabel(address));
        }
        string connectionText = allowed is null ? "当前无设备连接" : peerLabels.GetValueOrDefault(allowed, "可下载设备已连接，暂无法读取 MAC 地址");
        const string macPrefix = "已连接设备的MAC地址：";
        if (connectionText.StartsWith(macPrefix, StringComparison.Ordinal)) connectionText = "MAC 地址为：" + connectionText[macPrefix.Length..] + " 的设备已连接";
        SetText(receivers, connectionText);
        var visible = new HashSet<string>();
        foreach (var receiver in snapshot) foreach (var file in receiver.Files) {
            string key = receiver.Address + ":" + file.Id; visible.Add(key);
            if (!progressRows.TryGetValue(key, out var row)) {
                progressRows[key] = row = new(); receiverRows.Children.Add(row.View);
            }
            SetText(row.Peer, peerLabels.GetValueOrDefault(receiver.Address, receiver.Address));
            if (row.Name.Text != file.Name) row.Name.Text = file.Name;
            row.Bar.Value = file.Size == 0 ? file.Complete ? 100 : 0 : Math.Clamp(100.0 * file.Sent / file.Size, 0, 100);
            string state = file.Active > 0 ? file.Sent >= file.Size ? "数据已发送 · 正在结束下载请求" : "发送中 · " + ShareText.Size(file.Speed) + "/s" : file.Complete ? "已发送" : file.Interrupted ? "已中断，可重新下载" : "等待继续下载";
            if (file.Direction == "upload") state = file.Active > 0 ? file.Sent >= file.Size ? "正在保存" : "接收中 · " + ShareText.Size(file.Speed) + "/s" : file.Complete ? "已接收" : file.Interrupted ? "已中断，请重新上传" : "等待上传";
            SetText(row.Detail, $"{row.Bar.Value:0}% · {ShareText.Size(file.Sent)} / {ShareText.Size(file.Size)} · {state}");
        }
        foreach (string stale in progressRows.Keys.Where(key => !visible.Contains(key)).ToArray()) {
            receiverRows.Children.Remove(progressRows[stale].View); progressRows.Remove(stale);
        }
        emptyMessage.Visibility = progressRows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (now - snapshotSavedAt >= 1000) {
            snapshotSavedAt = now;
            File.WriteAllText(Path.Combine(output,"downloads-snapshot.json"),JsonSerializer.Serialize(new { connected = ap?.ConnectedCount ?? 0, receivers = snapshot }));
        }
    }
    private static void SetText(TextBlock text, string value) { if (text.Text != value) text.Text = value; }
    public async ValueTask DisposeAsync()
    {
        if (!await EndAsync(ask:false)) throw new IOException("仍有下载或分享清理未完成。");
        timer.Stop();
    }
    private static object[] NetworkSnapshot() => NetworkInterface.GetAllNetworkInterfaces().Select(n => (object)new {
        id = n.Id, name = n.Name, description = n.Description, status = n.OperationalStatus.ToString(),
        ipv4 = n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Select(a => a.Address.ToString()).ToArray()
    }).ToArray();
}
