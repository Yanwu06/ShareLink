using System.Runtime.InteropServices;
using System.Text.Json;
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories)]
namespace ShareLink;

internal sealed class NativeCore : IDisposable
{
    private ulong handle;
    private readonly object gate = new();
    [DllImport("sharelink_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern ulong sl_start([MarshalAs(UnmanagedType.LPUTF8Str)] string config);
    [DllImport("sharelink_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sl_call(ulong handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string request);
    [DllImport("sharelink_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sl_free(IntPtr result);
    [DllImport("sharelink_core", CallingConvention = CallingConvention.Cdecl)]
    private static extern void sl_stop(ulong handle);

    public NativeCore(object config)
    {
        handle = sl_start(JsonSerializer.Serialize(config));
        if (handle == 0) throw new InvalidOperationException("传输服务启动失败。请确认没有运行另一个 ShareLink，且保存目录可以写入。");
    }
    public JsonElement Call(object request)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(handle == 0, this);
            var ptr = sl_call(handle, JsonSerializer.Serialize(request));
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("传输核心没有返回结果。");
            try
            {
                using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(ptr) ?? "{}");
                var root = document.RootElement.Clone();
                if (root.TryGetProperty("ok", out var ok) && !ok.GetBoolean())
                    throw new InvalidOperationException(root.TryGetProperty("error", out var error) ? error.GetString() : "操作未完成。");
                return root;
            }
            finally { sl_free(ptr); }
        }
    }
    public void Dispose()
    {
        lock (gate) { if (handle != 0) { sl_stop(handle); handle = 0; } }
    }
}
