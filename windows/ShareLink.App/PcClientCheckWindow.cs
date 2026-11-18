using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace ShareLink;
internal sealed class PcClientCheckWindow : Window
{
    private readonly string output;
    private readonly ComboBox devices = new() { DisplayMemberPath = "Display", Margin = new Thickness(0,12,0,12), MinWidth = 400 };
    private readonly TextBlock status = new() { Text = "正在寻找开启检查窗口的电脑…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,12) };
    private readonly Button connect = new() { Content = "连接选中电脑", Padding = new Thickness(12,8,12,8) };
    private readonly PcCheckDiscovery discovery = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private NativeCore? core;
    private NearbySession? session;
    private bool connecting, ending, passed;
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(10);
    public PcClientCheckWindow(string output)
    {
        this.output = Path.GetFullPath(output); Directory.CreateDirectory(this.output);
        Title = "ShareLink · 发起电脑连接检查"; Width = 650; Height = 370; FontSize = 17; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "发起电脑连接检查", FontSize = 25, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "选择另一台电脑，并在两边核对相同校验码。本检查不发送文件、不切换网络。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) });
        panel.Children.Add(devices); panel.Children.Add(connect); panel.Children.Add(status); Content = panel;
        connect.Click += async (_,_) => await ConnectAsync();
        Loaded += (_,_) => { try { core = new NativeCore(new { name = Environment.MachineName, device_type = "computer", state_dir = Path.Combine(output,"private-identity"), download_dir = Path.Combine(output,"received"), port = 0, discovery_port = 0, transport_modules = new[] { "nearby-direct" } }); discovery.SetLocalFingerprint(MainWindow.Text(core.Call(new {op="snapshot"}).GetProperty("info"),"fingerprint")); discovery.Start(); timer.Tick += Tick; timer.Start(); } catch (Exception ex) { status.Text = ex.Message; } };
        Closing += (_,_) => { ending = true; timer.Stop(); discovery.Dispose(); session?.Dispose(); core?.Dispose(); };
    }
    private void Tick(object? sender, EventArgs args)
    {
        if (DateTime.UtcNow > deadline || File.Exists(Path.Combine(output,"stop"))) { Close(); return; }
        if (connecting || passed) return;
        File.WriteAllText(Path.Combine(output,"client-scan.json"),JsonSerializer.Serialize(new {state=discovery.State,error=discovery.Error,totalAdvertisements=discovery.Received,computerServiceAdvertisements=discovery.Matched,visibleDevices=discovery.Snapshot().Length,at=DateTimeOffset.Now}));
        status.Text = "扫描状态："+discovery.State+"；已收到 "+discovery.Received+" 条广播，发现 "+discovery.Snapshot().Length+" 台检查电脑。"+(discovery.Error.Length>0?" 错误："+discovery.Error:"");
        var found = discovery.Snapshot(); var old = devices.SelectedItem as NearbyPhone;
        if (devices.ItemsSource is not NearbyPhone[] previous || !previous.Select(p=>(p.Address,p.Display)).SequenceEqual(found.Select(p=>(p.Address,p.Display)))) {
            devices.ItemsSource = found; devices.SelectedItem = found.FirstOrDefault(p=>p.Address==old?.Address) ?? found.FirstOrDefault();
        }
    }
    private async Task ConnectAsync()
    {
        if (core is null || connecting || devices.SelectedItem is not NearbyPhone peer) return;
        connecting = true; connect.IsEnabled = false;
        try {
            status.Text = "正在建立电脑蓝牙控制连接…"; session = await NearbySession.ConnectAsync(peer.Address,peer.AddressType,peer.ControlService ?? throw new InvalidOperationException("电脑广播身份不完整"));
            if (ending) { session.Dispose(); return; }
            var hello = await session.RequestAsync(new { op = "hello" });
            if (MainWindow.Number(hello,"controlMaxWrite")>=20) session.ControlWriteLimit=(int)Math.Clamp(MainWindow.Number(hello,"controlMaxWrite"),20,512);
            var begin = core.Call(new { op = "pair_begin" }); session.PairId=MainWindow.Text(begin,"pairId");
            var remote = await session.RequestAsync(new { op = "pair_begin", hello = begin.GetProperty("hello") });
            if (!remote.TryGetProperty("requiresPairingConfirmation",out var required) || required.ValueKind != JsonValueKind.True) throw new InvalidOperationException("对方不是本轮电脑连接检查，请选择正确设备。");
            var finish=core.Call(new { op="pair_finish",pairId=session.PairId,hello=remote.GetProperty("hello") });
            session.Name=MainWindow.Text(finish.GetProperty("peer"),"name");session.Fingerprint=MainWindow.Text(finish.GetProperty("peer"),"fingerprint");
            bool userAccepted=!ending && AppDialog.Show(this,"核对连接校验码","对方设备："+session.Name+"\n\n校验码："+MainWindow.Text(finish,"sas")+"\n\n请核对两台电脑显示相同校验码；不一致请取消。","相同，允许连接","取消");
            if(!userAccepted)throw new InvalidOperationException("已取消校验码确认");
            core.Call(new {op="pair_confirm",pairId=session.PairId,confirmed=userAccepted});
            bool remoteAccepted=false;var until=DateTime.UtcNow.AddSeconds(90);
            while(!ending&&DateTime.UtcNow<until){var result=await session.RequestAsync(new {op="pair_confirm",confirmed=userAccepted});if(result.GetProperty("confirmed").GetBoolean()){remoteAccepted=true;break;}await Task.Delay(500);}
            if(!remoteAccepted)throw new TimeoutException("等待另一台电脑确认超时");
            var encrypted=core.Call(new {op="pair_encrypt",pairId=session.PairId,payload=new {op="status",deviceType="computer"}});
            var reply=await session.RequestAsync(new {op="secure",envelope=encrypted.GetProperty("envelope")});
            var decoded=core.Call(new {op="pair_decrypt",pairId=session.PairId,envelope=reply.GetProperty("envelope")}).GetProperty("payload");
            passed=decoded.GetProperty("pairingCheckpoint").GetBoolean()&&MainWindow.Text(decoded.GetProperty("info"),"fingerprint")==session.Fingerprint;
            if(!passed)throw new InvalidOperationException("电脑端身份与加密会话不一致");
            status.Text="双方已确认校验码，加密蓝牙连接通过。\n本次没有创建热点或传输文件。";
            File.WriteAllText(Path.Combine(output,"client-check.json"),JsonSerializer.Serialize(new {passed,explicitClientConsent=userAccepted,explicitReceiverConsent=remoteAccepted,authenticatedStatus=true,fileTransferTested=false,networkChanged=false,controlMode=session.ControlMode,at=DateTimeOffset.Now},new JsonSerializerOptions {WriteIndented=true}));
        }catch(Exception ex){status.Text="检查未通过："+ex.Message;File.WriteAllText(Path.Combine(output,"error-private.txt"),ex.ToString());session?.Dispose();session=null;connect.IsEnabled=true;}
        finally{connecting=false;}
    }
}
