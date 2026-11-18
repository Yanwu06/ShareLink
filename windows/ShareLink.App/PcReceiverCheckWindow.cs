using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
namespace ShareLink;
internal sealed class PcReceiverCheckWindow : Window
{
    private readonly string output;
    private readonly TextBlock status = new() { Text = "正在准备蓝牙检查…", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,16,0,16) };
    private NativeCore? core;
    private PcControlProtocol? protocol;
    private PcGattHost? radio;
    private bool done, ending;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly DateTime deadline = DateTime.UtcNow.AddMinutes(10);
    public PcReceiverCheckWindow(string output)
    {
        this.output = Path.GetFullPath(output); Directory.CreateDirectory(this.output);
        Title = "ShareLink · 电脑连接检查"; Width = 620; Height = 340; WindowStartupLocation = WindowStartupLocation.CenterScreen; FontSize = 17;
        var panel = new StackPanel { Margin = new Thickness(26) };
        panel.Children.Add(new TextBlock { Text = "电脑连接检查", FontSize = 26, FontWeight = FontWeights.Bold });
        panel.Children.Add(new TextBlock { Text = "只检查蓝牙与双方校验码，不切换网络、不发送文件。请保持窗口打开，在另一台电脑 ShareLink 中选择本机。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,0) });
        panel.Children.Add(status); var close = new Button { Content = "结束检查", Padding = new Thickness(14,8,14,8), HorizontalAlignment = HorizontalAlignment.Left }; close.Click += (_,_) => Close(); panel.Children.Add(close); Content = panel;
        Loaded += Start; Closing += async (_,e) => { if (done) return; e.Cancel = true; if (ending) return; ending = true; timer.Stop(); if (radio is not null) await radio.DisposeAsync(); else if (protocol is not null) await protocol.DisposeAsync(); core?.Dispose(); done = true; Close(); };
    }
    private Task<bool> Confirm(string name, string code)
    {
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.BeginInvoke(new Action(() => {
            bool accepted = !ending && AppDialog.Show(this, "核对连接校验码", "对方设备：" + name + "\n\n校验码：" + code + "\n\n请核对两台电脑显示相同校验码；不一致请取消。", "相同，允许连接", "取消");
            answer.TrySetResult(accepted);
        })); return answer.Task;
    }
    private async void Start(object sender, RoutedEventArgs args)
    {
        try {
            core = new NativeCore(new { name = Environment.MachineName, device_type = "computer", state_dir = Path.Combine(output,"private-identity"), download_dir = Path.Combine(output,"received"), port = 0, discovery_port = 0, transport_modules = new[] { "nearby-direct" } });
            protocol = new(core, Confirm); radio = new(protocol); radio.Changed += text => Dispatcher.BeginInvoke(new Action(() => status.Text = text));
            bool supported = await radio.StartAsync();
            timer.Tick += (_,_) => {
                if (protocol.Authenticated) status.Text = "双方已确认校验码，加密蓝牙连接通过。\n本次没有发送文件或创建热点。";
                File.WriteAllText(Path.Combine(output,"receiver-check.json"),JsonSerializer.Serialize(new { peripheralSupported = supported, advertisementState = radio.AdvertisementState, authenticated = protocol.Authenticated, fileTransferTested = false, hotspotStarted = false, at = DateTimeOffset.Now }));
                if (DateTime.UtcNow > deadline || File.Exists(Path.Combine(output,"stop"))) Close();
            }; timer.Start();
        } catch (Exception ex) { status.Text = "检查未完成：" + ex.Message; File.WriteAllText(Path.Combine(output,"error-private.txt"),ex.ToString()); }
    }
}
