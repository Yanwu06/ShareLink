using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace ShareLink.BrowserSharing;

internal sealed class ShareStepCard : Border
{
    internal TextBlock OfflineHint { get; } = Text(14, muted: true);
    internal readonly Image Qr = new() { Width = 144, Height = 144 };
    internal readonly Image PlaceholderImage = new() { Width = 144, Height = 144 };
    internal readonly TextBlock Primary = Text(15);
    internal readonly TextBlock Secondary = Text(14, muted: true);
    internal readonly TextBlock Connection = Text(14, muted: true);
    private readonly Grid placeholder = new() { Width = 236, Height = 144 };
    private readonly TextBlock placeholderText = Text(15);
    private readonly TextBlock ascii = Text(13, muted: true);
    private readonly Popup helpPopup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };
    internal readonly Button HelpButton = new();
    private readonly int step;
    internal ShareStepCard(int step)
    {
        this.step = step; Background = Brushes.White; CornerRadius = new CornerRadius(8); Padding = new Thickness(18, 14, 18, 14); BorderBrush = new SolidColorBrush(Color.FromRgb(226,230,237)); BorderThickness = new Thickness(1);
        var column = new Grid();
        column.RowDefinitions.Add(new() { Height = GridLength.Auto });
        column.RowDefinitions.Add(new() { Height = new GridLength(152) });
        column.RowDefinitions.Add(new() { Height = GridLength.Auto });
        column.RowDefinitions.Add(new() { Height = GridLength.Auto });
        column.Children.Add(new TextBlock { Text = step == 1 ? "连接临时 Wi-Fi" : "打开下载页", FontSize = 21, FontWeight = FontWeights.Bold, Margin = new Thickness(0,0,0,4) });
        var details = new StackPanel { Margin = new Thickness(0,0,0,2) };
        if (step == 2) details.Children.Add(new TextBlock { Text = "网址：", FontSize = 15, Margin = new Thickness(0,0,0,4) });
        details.Children.Add(Primary);
        if (step == 1) details.Children.Add(Secondary);
        Grid.SetRow(details, 2); column.Children.Add(details);
        var body = new Grid();
        Qr.HorizontalAlignment = HorizontalAlignment.Center; Qr.VerticalAlignment = VerticalAlignment.Center;
        var qrSlot = new Grid { Width = 144, Height = 144, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        qrSlot.Children.Add(Qr); body.Children.Add(qrSlot);
        var symbol = new GeometryDrawing(new SolidColorBrush(Color.FromRgb(227,230,234)), null, LucideIconExtension.QrCode);
        symbol.Freeze(); var source = new DrawingImage(symbol); source.Freeze();
        PlaceholderImage.Source = source; qrSlot.Children.Add(PlaceholderImage);
        placeholderText.FontWeight = FontWeights.Bold; placeholderText.TextAlignment = TextAlignment.Left; placeholderText.VerticalAlignment = VerticalAlignment.Center;
        placeholder.Children.Add(placeholderText);
        placeholder.HorizontalAlignment = HorizontalAlignment.Center; placeholder.VerticalAlignment = VerticalAlignment.Center;
        body.Children.Add(placeholder); Grid.SetRow(body, 1); column.Children.Add(body);
        var instruction = new Grid(); instruction.ColumnDefinitions.Add(new()); instruction.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        instruction.Children.Add(new TextBlock { Text = step == 1 ? "第一步：扫描二维码或手动连接临时 Wi-Fi" : "第二步：扫描二维码或在浏览器中手动输入网址访问，下载文件", FontSize = 16, LineHeight = 22, FontWeight = FontWeights.Medium, Foreground = new SolidColorBrush(Color.FromRgb(36,99,238)), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Bottom });
        HelpButton.Name = step == 1 ? "BrowserWifiHelpButton" : "BrowserWebHelpButton";
        var helpIcon = new System.Windows.Shapes.Path
        {
            Data = LucideIconExtension.Info, Width = 18, Height = 18, Stretch = Stretch.Uniform
        };
        helpIcon.SetBinding(System.Windows.Shapes.Path.FillProperty, new Binding(nameof(Button.Foreground)) { Source = HelpButton });
        HelpButton.Content = helpIcon;
        HelpButton.Width = HelpButton.Height = 28; HelpButton.MinHeight = 0; HelpButton.Padding = new Thickness(0);
        HelpButton.Margin = new Thickness(8,0,0,0); HelpButton.VerticalAlignment = VerticalAlignment.Bottom;
        HelpButton.ToolTip = step == 1 ? "查看连接说明" : "查看下载说明";
        System.Windows.Automation.AutomationProperties.SetName(HelpButton, HelpButton.ToolTip.ToString());
        if (TryFindResource("Quiet") is Style quiet) HelpButton.Style = quiet;
        Grid.SetColumn(HelpButton,1); instruction.Children.Add(HelpButton);
        Grid.SetRow(instruction,3); column.Children.Add(instruction);
        var help = new StackPanel();
        help.Children.Add(new TextBlock { Text = step == 1 ? "连接说明" : "下载说明", FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,10) });
        if (step == 1) {
            help.Children.Add(Connection);
            OfflineHint.Text = "如果手机提示“当前WiFi无法上网”，请继续使用此网络，直至完成下载。";
            OfflineHint.Name = "SingleShareOfflineWifiHint"; help.Children.Add(OfflineHint);
            help.Children.Add(TextHint("Windows 若提示 PIN，请点击“改用安全密钥连接”，输入上方 Wi-Fi 密码。"));
            help.Children.Add(TextHint("为保证传输体验，仅可使用一台设备连接。"));
        } else {
            help.Children.Add(TextHint("如果扫描二维码无法访问网站，请改用在浏览器输入网址访问。"));
            ascii.Text = "请使用英文半角符号。"; help.Children.Add(ascii);
        }
        helpPopup.PlacementTarget = HelpButton;
        helpPopup.Child = new Border { Background = Brushes.White, BorderBrush = new SolidColorBrush(Color.FromRgb(219,225,234)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(16), Width = 350, Child = new ScrollViewer { Content = help, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
        HelpButton.Click += (_,_) => helpPopup.IsOpen = !helpPopup.IsOpen;
        Unloaded += (_,_) => helpPopup.IsOpen = false;
        Child = column; SetActive(false);
    }
    internal void OpenHelp() => helpPopup.IsOpen = true;
    internal void CloseHelp() => helpPopup.IsOpen = false;
    private static TextBlock TextHint(string value) { var block = Text(14, muted: true); block.Text = value; block.Margin = new Thickness(0,0,0,8); return block; }
    internal void SetActive(bool active, bool preparing = false)
    {
        Qr.Visibility = active ? Visibility.Visible : Visibility.Hidden;
        PlaceholderImage.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        placeholder.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        placeholderText.Text = preparing ? "正在准备分享…" : "请添加文件并开启分享";

        if (!active) { Primary.Text = step == 1 ? "Wi-Fi 名称：" : ""; Secondary.Text = step == 1 ? "密码：" : ""; Connection.Text = "当前无设备连接"; }
    }
    internal void SetWifi(string ssid, string password) { Primary.Text = "Wi-Fi 名称：" + ssid; Secondary.Text = "密码：" + password; }
    private static TextBlock Text(double size, bool muted = false) => new() { FontSize = size, Foreground = new SolidColorBrush(muted ? Color.FromRgb(117,131,153) : Color.FromRgb(32,37,44)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,2) };
}
