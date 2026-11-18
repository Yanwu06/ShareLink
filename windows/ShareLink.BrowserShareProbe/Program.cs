using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using ShareLink.BrowserSharing;

namespace ShareLink.BrowserShareProbe;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--configure-browser-firewall") return BrowserShareFirewall.RunHelper(args);
        if (args.Length < 2) return 2;
        string output = Path.GetFullPath(args[1]); Directory.CreateDirectory(output);
        try {
            if (args[0] == "--check-short-ap") {
                var checkApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                checkApp.Startup += async (_, _) => {
                    try { await ShortAccessPointCheck(output); checkApp.Shutdown(0); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(output, "fatal-error.txt"), ex.ToString()); checkApp.Shutdown(1); }
                };
                return checkApp.Run();
            }
            if (args[0] == "--session-page-host") { SessionPageHost.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--upload-progress-host") return UploadProgressHost.Run(output);
            if (args[0] == "--check-session-restart") { SessionRestartChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--check-large-capacity") { LargeCapacityChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--check-upload") { UploadChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--check-firewall") { FirewallChecks.Run(output); return 0; }
            if (args[0] == "--check-append") { AppendChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--check-qr") { _ = new Application(); File.WriteAllText(Path.Combine(output, "qr-checks.json"), JsonSerializer.Serialize(BrowserShareView.DiagnosticQrChecks(output), new JsonSerializerOptions { WriteIndented = true })); return 0; }
            if (args[0] == "--check-single-receiver") { SingleReceiverChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] == "--self-test") { HttpChecks.RunAsync(output).GetAwaiter().GetResult(); return 0; }
            if (args[0] is not ("--probe" or "--share")) return 2;
            var app = new Application();
            string[] files = args[0] == "--probe" ? MakeFixtures(output) : args.Skip(2).Where(a => !a.StartsWith("--")).ToArray();
            var view = new BrowserShareView(output, files, files.Length > 0);
            var window = new Window { Title = "ShareLink · 分享验证", Width = 800, Height = 890, Content = view };
            bool done = false;
            window.Closing += async (_, e) => { if (!done) { e.Cancel = true; if (await view.EndAsync()) { await view.DisposeAsync(); done = true; window.Close(); } } };
            return app.Run(window);
        } catch (Exception ex) { File.WriteAllText(Path.Combine(output, "fatal-error.txt"), ex.ToString()); return 1; }
    }
    private static async Task ShortAccessPointCheck(string output)
    {
        var ap = new LegacyAccessPoint();
        bool ssid = System.Text.RegularExpressions.Regex.IsMatch(ap.Ssid, "^SL[0-9]{3}$");
        bool password = System.Text.RegularExpressions.Regex.IsMatch(ap.Password, "^SL[0-9]{6}$");
        if (!ssid || !password) throw new InvalidOperationException("Short Wi-Fi credential format mismatch.");
        try {
            await ap.StartAsync(CancellationToken.None);
            await File.WriteAllTextAsync(Path.Combine(output, "short-ap-started.json"), JsonSerializer.Serialize(new { passed = true, ssid = ap.Ssid, passwordLength = ap.Password.Length, format = password, status = ap.Status }));
        }
        finally { await ap.DisposeAsync(); }
        for (int i=0;i<50 && LegacyAccessPoint.DirectAddresses().Any();i++) await Task.Delay(200);
        if (LegacyAccessPoint.DirectAddresses().Any()) throw new InvalidOperationException("Temporary AP adapter still active after shutdown.");
        await File.WriteAllTextAsync(Path.Combine(output, "short-ap-cleanup.json"), "{\"passed\":true,\"activeTemporaryAdapters\":0}");
    }
    private static string[] MakeFixtures(string output)
    {
        string root = Path.Combine(output, "selected-files"); Directory.CreateDirectory(root);
        string small = Path.Combine(root, "ShareLink-二维码分享-64KB.txt");
        string medium = Path.Combine(root, "ShareLink-browser-10MB.bin");
        string empty = Path.Combine(root, "ShareLink-空文件.txt");
        foreach (var entry in new[] { (small, 65536), (medium, 10 * 1024 * 1024), (empty, 0) })
        {
            // Never overwrite an existing fixture or user file.
            if (File.Exists(entry.Item1)) continue;
            using var file = new FileStream(entry.Item1, FileMode.CreateNew, FileAccess.Write);
            byte[] block = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
            for (int left = entry.Item2; left > 0; left -= Math.Min(left, block.Length)) file.Write(block, 0, Math.Min(left, block.Length));
        }
        string[] files = [small, medium, empty];
        File.WriteAllText(Path.Combine(output, "selected-files.json"), JsonSerializer.Serialize(files.Select(p => new {
            name = Path.GetFileName(p), path = p, bytes = new FileInfo(p).Length,
            sha256 = HashFile(p)
        }), new JsonSerializerOptions { WriteIndented = true }));
        return files;
    }
    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
