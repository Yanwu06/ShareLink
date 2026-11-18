using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace ShareLink;

internal record StoragePreference(string? Directory = null);
internal record StorageChoice(string Directory, bool Custom, string? Notice = null);

internal static class StoragePaths
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");
    [DllImport("shell32.dll")] private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    internal static string? SystemDownloads()
    {
        IntPtr pointer = IntPtr.Zero;
        try
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out pointer) == 0)
                return Marshal.PtrToStringUni(pointer);
        }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");
        return key?.GetValue("{" + DownloadsId + "}") is string value ? Environment.ExpandEnvironmentVariables(value) : null;
    }

    internal static StoragePreference Read(string stateDirectory)
    {
        string path = Path.Combine(stateDirectory, "storage-settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<StoragePreference>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { return new(); }
    }

    internal static void Save(string stateDirectory, StoragePreference preference)
    {
        Directory.CreateDirectory(stateDirectory);
        string path = Path.Combine(stateDirectory, "storage-settings.json");
        string temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, preference);
            stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }

    internal static bool Usable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || !Directory.Exists(path)) return false;
        try
        {
            // Probe only a new application-owned temporary file, never user data.
            using var probe = new FileStream(Path.Combine(path, ".sharelink-access-" + Guid.NewGuid().ToString("N") + ".tmp"),
                FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    internal static IEnumerable<string> FallbackCandidates()
    {
        // Existing writable folders only. Do not invent or create a ShareLink folder.
        var drives = DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .OrderBy(d => string.Equals(d.Name, @"C:\", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var drive in drives)
        {
            yield return Path.Combine(drive.Name, "Downloads");
            yield return Path.Combine(drive.Name, "下载");
            yield return drive.RootDirectory.FullName;
        }
    }

    internal static string? DefaultReceiveDirectory(string? documentsOverride = null)
    {
        string documents = documentsOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(documents)) return null;
        string path = Path.Combine(documents, "ShareLink", "接收文件");
        try { Directory.CreateDirectory(path); return path; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static StorageChoice Resolve(StoragePreference preference)
    {
        // Preserve an explicit custom location, including an explicitly chosen Downloads.
        if (Usable(preference.Directory)) return new(Path.GetFullPath(preference.Directory!), true);
        return Choose(preference, DefaultReceiveDirectory(), [], Usable);
    }

    internal static StorageChoice Choose(StoragePreference preference, string? downloads, IEnumerable<string> alternatives, Func<string?, bool> usable)
    {
        if (usable(preference.Directory)) return new(Path.GetFullPath(preference.Directory!), true);
        if (usable(downloads)) return new(Path.GetFullPath(downloads!), false,
            preference.Directory is null ? null : "原保存位置不可用，本次使用ShareLink 接收文件夹。可在设置中重新选择。");
        foreach (string path in alternatives)
            if (usable(path)) return new(Path.GetFullPath(path), false, "未找到可用的ShareLink 接收文件夹，本次保存到：" + path);
        throw new IOException("没有找到可用的保存位置，请在设置中选择一个已有且可写的文件夹。");
    }

    internal static object DiagnosticChecks(string root)
    {
        string first = Path.Combine(root, "storage-first"), second = Path.Combine(root, "storage-second");
        Directory.CreateDirectory(first); Directory.CreateDirectory(second);
        int checks = 0;
        void Check(bool condition) { if (!condition) throw new InvalidOperationException("Storage policy check failed."); checks++; }
        bool Exists(string? path) => path == first || path == second;
        Check(Choose(new(), first, [second], Exists) is { Directory: var defaultPath, Custom: false } && defaultPath == first);
        Check(Choose(new(second), first, [], Exists) is { Directory: var selected, Custom: true } && selected == second);
        Check(Choose(new("missing"), first, [], Exists).Directory == first);
        Check(Choose(new(), "missing", ["missing", second, first], Exists).Directory == second);
        string? owned = DefaultReceiveDirectory(first);
        Check(owned == Path.Combine(first, "ShareLink", "接收文件") && Directory.Exists(owned));
        Save(root, new(second)); Check(Read(root).Directory == second);
        Save(root, new()); Check(Read(root).Directory is null);
        Check(!Usable(Path.Combine(root, "not-created")) && !Directory.Exists(Path.Combine(root, "not-created")));
        Check(Usable(first) && !Directory.EnumerateFiles(first, ".sharelink-access-*").Any());
        return new { passed = true, checks, scope = "isolated folders; dedicated Documents subfolder creation, custom override, persistence and no user-file migration" };
    }
}
