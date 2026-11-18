using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;

namespace ShareLink;

// Native notification-area integration works with the portable desktop release.
internal sealed class DesktopNotifications : IDisposable
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct IconData
    {
        public uint Size; public IntPtr Window; public uint Id, Flags, Callback; public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State, StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Title;
        public uint InfoFlags; public Guid Guid; public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint message, ref IconData data);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern uint ExtractIconEx(string path, int index, out IntPtr large, out IntPtr small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string name);
    private IconData data;
    private readonly HwndSource source;
    private readonly Action open;
    private readonly IntPtr applicationIcon;
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private readonly ContextMenu menu = new();
    private bool disposed;
    internal DesktopNotifications(Window window, Action open, Action settings, Action folder, Action exit)
    {
        this.open = open;
        var handle = new WindowInteropHelper(window).EnsureHandle();
        source = HwndSource.FromHwnd(handle);
        applicationIcon = LoadApplicationIcon();
        source.AddHook(Hook);
        data = new() { Size = (uint)Marshal.SizeOf<IconData>(), Window = handle, Id = 37, Flags = 7, Callback = 0x8000 + 37,
            Icon = applicationIcon, Tip = "ShareLink · 点击打开", Info = "", Title = "" };
        foreach (var entry in new[] { ("打开 ShareLink", open), ("打开文件保存位置", folder), ("设置", settings) })
        {
            var item = new MenuItem { Header = entry.Item1 }; item.Click += (_, _) => entry.Item2(); menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var quit = new MenuItem { Header = "退出 ShareLink" }; quit.Click += (_, _) => exit(); menu.Items.Add(quit);
        if (!Shell_NotifyIcon(0, ref data)) { source.RemoveHook(Hook); DestroyIcon(applicationIcon); throw new InvalidOperationException("系统通知图标未能创建。"); }
    }
    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == taskbarCreated) { data.Flags = 7; Shell_NotifyIcon(0, ref data); }
        if (message == data.Callback && lParam.ToInt32() == 0x205) { menu.IsOpen = true; handled = true; }
        else if (message == data.Callback && (lParam.ToInt32() is 0x202 or 0x405)) { open(); handled = true; }
        return IntPtr.Zero;
    }
    internal void Progress(string text) { data.Flags = 4; data.Tip = text.Length > 127 ? text[..127] : text; Shell_NotifyIcon(1, ref data); }
    internal void Notify(string title, string text)
    {
        data.Flags = 0x10; data.Title = title.Length > 63 ? title[..63] : title; data.Info = text.Length > 255 ? text[..255] : text; data.InfoFlags = 1;
        Shell_NotifyIcon(1, ref data);
    }
    private static IntPtr LoadApplicationIcon()
    {
        if (ExtractIconEx(Environment.ProcessPath!, 0, out var large, out var small, 1) == 0)
            throw new InvalidOperationException("未能加载 ShareLink 应用图标。");
        var selected = small != IntPtr.Zero ? small : large;
        if (large != IntPtr.Zero && large != selected) DestroyIcon(large);
        if (selected == IntPtr.Zero) throw new InvalidOperationException("应用图标为空。");
        return selected;
    }
    internal static object DiagnosticIconChecks(string output)
    {
        var handle = LoadApplicationIcon();
        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(Path.Combine(output, "tray-application-icon.png")); png.Save(file);
            return new { passed = true, source = Environment.ProcessPath, applicationResource = true, width = bitmap.PixelWidth, height = bitmap.PixelHeight };
        }
        finally { DestroyIcon(handle); }
    }
    public void Dispose() { if (disposed) return; disposed = true; menu.IsOpen = false; source.RemoveHook(Hook); Shell_NotifyIcon(2, ref data); DestroyIcon(applicationIcon); }
}
