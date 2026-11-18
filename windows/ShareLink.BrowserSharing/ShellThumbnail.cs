using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareLink.BrowserSharing;

// Shell extensions can access disk and run arbitrary handlers. Extract on one
// background STA so file lists never wait on Explorer thumbnail generation.
public static class ShellThumbnail
{
    public static readonly DependencyProperty FilePathProperty = DependencyProperty.RegisterAttached("FilePath", typeof(string), typeof(ShellThumbnail), new PropertyMetadata(null, Changed));
    public static void SetFilePath(DependencyObject target, string value) => target.SetValue(FilePathProperty, value);
    public static string GetFilePath(DependencyObject target) => (string)target.GetValue(FilePathProperty);
    private static readonly BlockingCollection<Action> work = new();
    private static readonly ConcurrentDictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    static ShellThumbnail()
    {
        var worker = new Thread(() => { foreach (var action in work.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "ShareLink shell thumbnails" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        if (target is not Image image || args.NewValue is not string path || string.IsNullOrWhiteSpace(path)) return;
        var weak = new WeakReference<Image>(image);
        work.Add(() => {
            try {
                var file = new FileInfo(path);
                string key = path + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks;
                if (!cache.TryGetValue(key, out var source)) {
                    source = Read(path);
                    if (source is null) return;
                    if (cache.Count >= 256) cache.Clear();
                    cache[key] = source;
                }
                if (weak.TryGetTarget(out var current)) current.Dispatcher.BeginInvoke(new Action(() => { if (GetFilePath(current) == path) current.Source = source; }));
            } catch { /* Keep the existing generic icon if Explorer has no image. */ }
        });
    }
    private static ImageSource? Read(string path)
    {
        Guid iid = typeof(IImageFactory).GUID;
        if (SHCreateItemFromParsingName(path, 0, ref iid, out var factory) != 0) return null;
        nint bitmap = 0;
        try {
            if (factory.GetImage(new SizeNative { Width = 64, Height = 64 }, 0, out bitmap) != 0 || bitmap == 0) return null;
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()); image.Freeze(); return image;
        } finally { if (bitmap != 0) DeleteObject(bitmap); Marshal.ReleaseComObject(factory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int Width, Height; }
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageFactory { [PreserveSig] int GetImage(SizeNative size, uint flags, out nint bitmap); }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHCreateItemFromParsingName(string path, nint bind, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IImageFactory factory);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint image);
}
