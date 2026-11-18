using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
using System.Diagnostics;
using System.Windows.Threading;

namespace ShareLink;

internal static class NearbyRadioDiagnostic
{
    public static async Task<int> RunAsync(string output, bool sendFile = false, bool receiveFile = false, bool receiveDialog = false, bool directWifi = false, bool backgroundTransfer = false, string? selectedPath = null, string? scenario = null, string? batchManifest = null)
    {
        Directory.CreateDirectory(output);
        var report = new Dictionary<string, object> { ["scope"] = receiveFile ? "BLE + phone pull offer through the WPF receive/history workflow; generated 64 KB fixture only, diagnostic acceptance, existing network only" : sendFile ? "BLE + generated fixture through the actual WPF send/history workflow; existing network only" : "BLE discovery, GATT and encrypted status only; no files or Wi-Fi changes", ["startedAt"] = DateTimeOffset.Now };
        string stage = "读取电脑蓝牙能力";
        if (directWifi) report["scope"] = "BLE + automatic temporary Wi-Fi + generated 64 KB fixture; LAN module disabled; restores original Wi-Fi after the test";
        if (backgroundTransfer) report["scope"] = "BLE + temporary Wi-Fi + generated 32 MiB diagnostic fixture, paced encrypted relay for phone background/lock testing; no user files; restores WLAN";
        if (selectedPath is not null) report["scope"] = backgroundTransfer ?
            "BLE + temporary Wi-Fi + user-requested file, paced encrypted diagnostic relay for termination/notification checks; source read-only; restores WLAN" :
            "BLE + temporary Wi-Fi + user-requested file, unpaced transfer; source read-only; restores WLAN";
        if (scenario is not null) {
            report["scenario"] = scenario;
            report["scope"] = $"BLE + temporary Wi-Fi + generated fixtures only; real phone {scenario} workflow; restores WLAN; existing phone files and history retained";
        }
        NearbySession? session = null;
        NativeCore? core = null;
        MainWindow? window = null;
        var clock = new Stopwatch();
        long lastTick = 0, maxGap = 0;
        int ticks = 0;
        var heartbeat = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        heartbeat.Tick += (_, _) => { long now = clock.ElapsedMilliseconds; maxGap = Math.Max(maxGap, now - lastTick); lastTick = now; ticks++; };
        using var discovery = new NearbyBluetooth();
        using var invitation = new NearbyInvitation(discovery);
        try
        {
            var adapter = await BluetoothAdapter.GetDefaultAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            if (adapter is null) throw new InvalidOperationException("电脑没有可用蓝牙适配器。");
            report["centralSupported"] = adapter.IsCentralRoleSupported;
            report["peripheralSupported"] = adapter.IsPeripheralRoleSupported;
            stage = "检查电脑可发现广播";
            await invitation.StartAsync();
            await Task.Delay(500);
            report["computerAdvertisement"] = invitation.Status;
            stage = "扫描手机广播"; discovery.Start();
            NearbyPhone? phone = null;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                await Task.Delay(500);
                phone = discovery.Snapshot().FirstOrDefault();
                if (phone is not null) break;
            }
            if (phone is null) throw new InvalidOperationException("15 秒内没有发现 ShareLink 手机广播。" + discovery.Status);
            report["addressType"] = phone.AddressType.ToString(); report["rssi"] = phone.Rssi;
            clock.Start(); heartbeat.Start();
            stage = "建立 GATT 连接及控制响应读取";
            session = await NearbySession.ConnectAsync(phone.Address, phone.AddressType);
            report["controlMode"] = session.ControlMode;
            stage = "请求手机 hello";
            var hello = await session.RequestAsync(new { op = "hello" });
            if (MainWindow.Number(hello, "controlMaxWrite") >= 20) session.ControlWriteLimit = (int)Math.Clamp(MainWindow.Number(hello, "controlMaxWrite"), 20, 512);
            report["phoneName"] = MainWindow.Text(hello.GetProperty("info"), "name");
            if (sendFile || receiveFile) { window = new MainWindow(output, directWifi); await window.InitializeCoreAsync(); core = window.DiagnosticCore; }
            else core = new NativeCore(new { name = Environment.MachineName, device_type = "computer", download_dir = Path.Combine(output, "received"), state_dir = Path.Combine(output, "identity"), port = 0, discovery_port = 0 });
            stage = "交换会话密钥";
            var begin = core.Call(new { op = "pair_begin" }); session.PairId = MainWindow.Text(begin, "pairId");
            var remote = await session.RequestAsync(new { op = "pair_begin", hello = begin.GetProperty("hello") });
            var finish = core.Call(new { op = "pair_finish", pairId = session.PairId, hello = remote.GetProperty("hello") });
            session.Fingerprint = MainWindow.Text(finish.GetProperty("peer"), "fingerprint");
            core.Call(new { op = "pair_confirm", pairId = session.PairId, confirmed = true });
            var confirmation = await session.RequestAsync(new { op = "pair_confirm", confirmed = true });
            if (!confirmation.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != JsonValueKind.True) throw new InvalidOperationException("手机未自动建立加密会话。");
            stage = "交换加密 status";
            var encoded = core.Call(new { op = "pair_encrypt", pairId = session.PairId, payload = new { op = "status", deviceType = "computer" } });
            var response = await session.RequestAsync(new { op = "secure", envelope = encoded.GetProperty("envelope") }, 30);
            var decoded = core.Call(new { op = "pair_decrypt", pairId = session.PairId, envelope = response.GetProperty("envelope") }).GetProperty("payload");
            if (MainWindow.Text(decoded.GetProperty("info"), "fingerprint") != session.Fingerprint) throw new InvalidOperationException("手机服务证书与会话不一致。");
            report["phoneStatus"] = decoded.Clone();
            report["advertisedName"] = phone.Display;
            report["fingerprintPrefix"] = phone.FingerprintPrefix;
            if (sendFile)
            {
                stage = selectedPath is not null ? "创建临时 Wi-Fi 直连并发送用户指定文件" : directWifi ? "创建临时 Wi-Fi 直连并发送生成测试文件" : "通过当前网络发送生成的测试文件并等待接收";
                File.WriteAllText(Path.Combine(output, "phone-send-started.json"), decoded.GetRawText());
                report["fileTransfer"] = await window!.DiagnosticPhoneSendAsync(session, phone, decoded, output, directWifi, backgroundTransfer, selectedPath, scenario);
            }
            if (receiveFile)
            {
                stage = selectedPath is not null ? "等待手机回传用户指定文件并校验" : "等待手机发送生成的测试文件并接收";
                report["fileTransfer"] = await window!.DiagnosticPhoneReceiveAsync(session, phone, decoded, output, receiveDialog, directWifi, selectedPath, backgroundTransfer, batchManifest);
            }
            report["passed"] = true;
        }
        catch (Exception ex)
        {
            report["passed"] = false; report["failedStage"] = stage;
            report["error"] = string.IsNullOrWhiteSpace(ex.Message) ? ex.GetType().Name : ex.Message;
            report["hresult"] = $"0x{ex.HResult:X8}"; report["exception"] = ex.ToString();
            if (ex.Data["controlMode"] is string mode) report["controlMode"] = mode;
            if (ex.Data["gattTrace"] is Dictionary<string, object> trace) report["gattTrace"] = trace;
        }
        finally
        {
            heartbeat.Stop();
            if (clock.IsRunning) {
                report["uiHeartbeatTicks"] = ticks;
                report["uiHeartbeatMaxGapMs"] = Math.Max(maxGap, clock.ElapsedMilliseconds - lastTick);
            }
            if (directWifi && window is not null)
            {
                report["directSetup"] = window.DiagnosticDirectSetup!;
                try { report["directCleanup"] = await window.CleanupDirectDiagnosticAsync(); }
                catch (Exception ex) { report["passed"] = false; report["cleanupError"] = ex.Message; }
            }
            if (core is not null && session?.PairId.Length > 0) try { core.Call(new { op = "pair_close", pairId = session.PairId }); } catch { }
            session?.Dispose();
            if (window is not null) window.StopDiagnostic(); else core?.Dispose();
            File.WriteAllText(Path.Combine(output, "nearby-radio.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        }
        return report.TryGetValue("passed", out var passed) && passed is true ? 0 : 1;
    }
}
