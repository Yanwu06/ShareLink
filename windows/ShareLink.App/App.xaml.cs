using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
namespace ShareLink;
public partial class App : Application
{
    // Keep the handle alive for the whole process so Setup/Uninstall can request a normal exit.
    private System.Threading.Mutex? installationMutex;
    protected override async void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Length == 2 && e.Args[0] == "--desktop-test") {
            base.OnStartup(e); MainWindow = new MainWindow(Path.GetFullPath(e.Args[1]), desktopTest:true); MainWindow.Show(); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--pc-native-test") {
            base.OnStartup(e); MainWindow = new MainWindow(Path.GetFullPath(e.Args[1]), directDiagnostic:true, pcNativeTest:true); MainWindow.Show(); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--pc-client-check") {
            base.OnStartup(e); MainWindow = new PcClientCheckWindow(e.Args[1]); MainWindow.Show(); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--pc-receiver-check") {
            base.OnStartup(e); MainWindow = new PcReceiverCheckWindow(e.Args[1]); MainWindow.Show(); return;
        }
        if(e.Args.Length==2&&e.Args[0]=="--diagnose-upload-switch"){
            ShutdownMode=ShutdownMode.OnExplicitShutdown;base.OnStartup(e);string output=Path.GetFullPath(e.Args[1]);Directory.CreateDirectory(output);
            try{File.WriteAllText(Path.Combine(output,"upload-switch-checks.json"),JsonSerializer.Serialize(await ShareLink.BrowserSharing.BrowserShareView.DiagnosticUploadSwitchAsync(output),new JsonSerializerOptions{WriteIndented=true}));Shutdown(0);}
            catch(Exception ex){File.WriteAllText(Path.Combine(output,"error-private.txt"),ex.ToString());Shutdown(1);}return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-pc-control") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try { File.WriteAllText(Path.Combine(output,"control-checks.json"),JsonSerializer.Serialize(await PcControlChecks.RunAsync(output),new JsonSerializerOptions { WriteIndented = true })); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output,"error-private.txt"),ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length > 0 && e.Args[0] is ShareLink.BrowserSharing.InstalledNetworkAccess.InstallSwitch or ShareLink.BrowserSharing.InstalledNetworkAccess.RemoveSwitch) {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            Shutdown(ShareLink.BrowserSharing.InstalledNetworkAccess.RunHelper(e.Args)); return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--configure-browser-firewall") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            Shutdown(ShareLink.BrowserSharing.BrowserShareFirewall.RunHelper(e.Args)); return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-storage-policy") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try { File.WriteAllText(Path.Combine(output, "storage-policy.json"), JsonSerializer.Serialize(StoragePaths.DiagnosticChecks(output))); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "fatal-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-updates") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            var updateWindow = new MainWindow(output);
            try { await updateWindow.InitializeCoreAsync(); File.WriteAllText(Path.Combine(output, "windows-update-checks.json"), JsonSerializer.Serialize(await updateWindow.DiagnosticUpdatesAsync(output), new JsonSerializerOptions { WriteIndented = true })); Shutdown(0); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output,"update-error.txt"),ex.ToString());Shutdown(1); }
            finally { updateWindow.StopDiagnostic(); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-ui-refresh") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            var refreshWindow = new MainWindow(output);
            try {
                await refreshWindow.InitializeCoreAsync();
                File.WriteAllText(Path.Combine(output, "ui-refresh-checks.json"), JsonSerializer.Serialize(await refreshWindow.DiagnosticUiRefreshAsync(output), new JsonSerializerOptions { WriteIndented = true })); Shutdown(0);
            } catch (Exception ex) { File.WriteAllText(Path.Combine(output, "fatal-error.txt"), ex.ToString()); Shutdown(1); }
            finally { refreshWindow.StopDiagnostic(); }
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-progress") {
            ShutdownMode = ShutdownMode.OnExplicitShutdown; base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            try {
                File.WriteAllText(Path.Combine(output, "windows-progress-checks.json"), JsonSerializer.Serialize(new {
                    browser = ShareLink.BrowserSharing.BrowserShareView.DiagnosticProgressChecks(output),
                    dual = ShareLink.MainWindow.DiagnosticProgressChecks()
                }, new JsonSerializerOptions { WriteIndented = true })); Shutdown(0);
            } catch (Exception ex) { File.WriteAllText(Path.Combine(output, "fatal-error.txt"), ex.ToString()); Shutdown(1); }
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] == "--diagnose-direct-receive-batch")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Shutdown(await NearbyRadioDiagnostic.RunAsync(Path.GetFullPath(e.Args[1]), receiveFile: true, directWifi: true,
                scenario: "batch-receive", batchManifest: Path.GetFullPath(e.Args[2])));
            return;
        }
        if (e.Args.Length == 3 && e.Args[0] is "--diagnose-direct-file" or "--diagnose-direct-file-paced" or "--diagnose-direct-receive-file-paced")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            bool receiveFile = e.Args[0] == "--diagnose-direct-receive-file-paced";
            Shutdown(await NearbyRadioDiagnostic.RunAsync(Path.GetFullPath(e.Args[1]), sendFile: !receiveFile, receiveFile: receiveFile, directWifi: true,
                backgroundTransfer: e.Args[0] != "--diagnose-direct-file",
                selectedPath: Path.GetFullPath(e.Args[2])));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--diagnose-direct-reject" or "--diagnose-direct-cancel" or "--diagnose-direct-batch")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Shutdown(await NearbyRadioDiagnostic.RunAsync(Path.GetFullPath(e.Args[1]), sendFile: true, directWifi: true,
                backgroundTransfer: e.Args[0] == "--diagnose-direct-cancel", scenario: e.Args[0]["--diagnose-direct-".Length..]));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] is "--diagnose-nearby" or "--diagnose-phone-send" or "--diagnose-phone-receive" or "--diagnose-phone-receive-dialog" or "--diagnose-direct-send" or "--diagnose-direct-receive" or "--diagnose-direct-background")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            Shutdown(await NearbyRadioDiagnostic.RunAsync(Path.GetFullPath(e.Args[1]), e.Args[0] is "--diagnose-phone-send" or "--diagnose-direct-send" or "--diagnose-direct-background",
                (e.Args[0].StartsWith("--diagnose-phone-receive", StringComparison.Ordinal) || e.Args[0] == "--diagnose-direct-receive"), e.Args[0] == "--diagnose-phone-receive-dialog", e.Args[0].StartsWith("--diagnose-direct-", StringComparison.Ordinal), e.Args[0] == "--diagnose-direct-background"));
            return;
        }
        if (e.Args.Length == 2 && e.Args[0] == "--diagnose-presentation")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            string output = Path.GetFullPath(e.Args[1]); Directory.CreateDirectory(output);
            var presentation = new ShareLink.MainWindow(output);
            try {
                await presentation.InitializeCoreAsync();
                File.WriteAllText(Path.Combine(output, "windows-presentation-checks.json"), JsonSerializer.Serialize(await presentation.DiagnosticPresentationChecksAsync(output), new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(output, "windows-qr-checks.json"), JsonSerializer.Serialize(ShareLink.BrowserSharing.BrowserShareView.DiagnosticQrChecks(output), new JsonSerializerOptions { WriteIndented = true }));
                File.WriteAllText(Path.Combine(output, "windows-motion-checks.json"), JsonSerializer.Serialize(await ShareLink.MainWindow.DiagnosticMotionChecksAsync(), new JsonSerializerOptions { WriteIndented = true }));
                Shutdown(0);
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(output, "windows-presentation-error.txt"), ex.ToString()); Shutdown(1); }
            finally { presentation.StopDiagnostic(); }
            return;
        }
        if (e.Args.Length != 2 || e.Args[0] != "--diagnose")
        {
            base.OnStartup(e);
            installationMutex = new System.Threading.Mutex(false, @"Local\ShareLink.Desktop.Runtime");
            MainWindow = new ShareLink.MainWindow();
            MainWindow.Show();
            return;
        }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(e);
        MainWindow? window = null;
        try
        {
            string output = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(output);
            window = new MainWindow(output);
            await window.InitializeCoreAsync();
            File.WriteAllText(Path.Combine(output, "windows-transfer-check.json"), JsonSerializer.Serialize(await window.DiagnosticLoopbackAsync(output), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-storage-checks.json"), JsonSerializer.Serialize(StoragePaths.DiagnosticChecks(output), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-storage-integration.json"), JsonSerializer.Serialize(await window.DiagnosticStorageSwitchAsync(output), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-cross-volume-check.json"), JsonSerializer.Serialize(await ShareLink.MainWindow.DiagnosticCrossVolumeAsync(output), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-tray-icon-checks.json"), JsonSerializer.Serialize(DesktopNotifications.DiagnosticIconChecks(output), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-policy-checks.json"), JsonSerializer.Serialize(ShareLink.MainWindow.DiagnosticPolicyChecks(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-discovery-checks.json"), JsonSerializer.Serialize(ShareLink.MainWindow.DiagnosticDiscoveryChecks(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-relay-checks.json"), JsonSerializer.Serialize(await ShareLink.MainWindow.DiagnosticRelayChecksAsync(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-presentation-checks.json"), JsonSerializer.Serialize(await window.DiagnosticPresentationChecksAsync(output), new JsonSerializerOptions { WriteIndented = true }));
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            window.RenderDiagnostic(Path.Combine(output, "windows-preview.png"));
            File.WriteAllText(Path.Combine(output, "windows-smoke.json"), JsonSerializer.Serialize(window.DiagnosticSnapshot(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-control-checks.json"), JsonSerializer.Serialize(ControlFrames.DiagnosticChecks(), new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(output, "windows-responsiveness.json"), JsonSerializer.Serialize(await BluetoothWorker.DiagnosticChecksAsync(), new JsonSerializerOptions { WriteIndented = true }));
            window.StopDiagnostic();
            Shutdown(0);
        }
        catch (Exception ex)
        {
            window?.StopDiagnostic();
            Directory.CreateDirectory(Path.GetFullPath(e.Args[1]));
            File.WriteAllText(Path.Combine(Path.GetFullPath(e.Args[1]), "windows-smoke-error.txt"), ex.ToString());
            Shutdown(1);
        }
    }
}
