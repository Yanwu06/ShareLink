using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ShareLink;

public partial class MainWindow
{
    internal NativeCore DiagnosticCore => core ?? throw new InvalidOperationException("Core has not started.");
    internal static object DiagnosticDiscoveryChecks()
    {
        var lifetime = new DiscoveryLifetime { Enabled = true };
        lifetime.Activity(500);
        if (lifetime.Expired(600499, false) || !lifetime.Expired(600500, false) || lifetime.Expired(1200000, true))
            throw new InvalidOperationException("发现截止时间或活动任务保护错误。");
        lifetime.Activity(1200000);
        if (lifetime.Expired(1200001, false) || !lifetime.Expired(1800000, false))
            throw new InvalidOperationException("业务完成后空闲期限错误。");
        lifetime.Enabled = false;
        if (lifetime.Expired(3000000, false)) throw new InvalidOperationException("手动关闭后仍公开。");
        return new { passed = true, checks = 4, idleMinutes = 10, pollingExtendsDeadline = false, nearby = NearbyBluetooth.DiscoveryChecks() };
    }
    internal object? DiagnosticDirectSetup => modules.Direct?.Evidence;

    internal void RenderDiagnostic(string path, double width = 1180, double height = 800)
    {
        var content = (FrameworkElement)Content;
        // Hidden diagnostic windows can be remeasured by WPF with infinite
        // available size. Pin the client canvas so scroll regions are tested
        // at the requested viewport rather than rendered as a tall document.
        bool restoreLiveLayout = IsVisible;
        double previousWidth = content.Width, previousHeight = content.Height;
        try
        {
        content.Width = width;
        content.Height = height;
        content.Measure(new Size(width, height));
        content.Arrange(new Rect(0, 0, width, height));
        content.UpdateLayout();
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 247, 252)), null, new Rect(0, 0, width, height));
            var brush = new VisualBrush(content) { AutoLayoutContent = false,
                ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height) };
            drawing.DrawRectangle(brush, null, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap((int)width, (int)height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); png.Save(file);
        }
        finally
        {
            if (restoreLiveLayout)
            {
                content.Width = previousWidth; content.Height = previousHeight;
                content.InvalidateMeasure(); InvalidateMeasure(); UpdateLayout();
            }
        }
    }

    private static string CreateFixture(string output)
    {
        string path = Path.Combine(output, "ShareLink-test-" + DateTime.Now.ToString("HHmmss") + ".txt");
        File.WriteAllText(path, "ShareLink generated transfer test\r\n" + string.Concat(Enumerable.Repeat("0123456789abcdef\r\n", 3600)));
        return path;
    }

    internal async Task<object> DiagnosticLoopbackAsync(string output)
    {
        var previousIds = DiagnosticSnapshot().GetProperty("transfers").EnumerateArray().Select(t => Text(t, "id")).ToHashSet();
        string path = CreateFixture(output);
        var info = DiagnosticSnapshot().GetProperty("info");
        var probe = await Task.Run(() => DiagnosticCore.Call(new { op = "probe", host = "127.0.0.1", port = (int)Number(info, "port") }));
        selectedPeer = ReadPeer(probe.GetProperty("peer")); AddFiles([path]);
        string id = await SendSelectedFilesAsync();
        bool renderedPending = false, activeStorageChangeBlocked = false;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await RefreshState();
            var transfers = DiagnosticSnapshot().GetProperty("transfers").EnumerateArray().Where(t => !previousIds.Contains(Text(t, "id"))).ToArray();
            foreach (var t in transfers.Where(t => Text(t, "direction") == "receive" && Text(t, "state") == "awaiting_accept"))
            {
                RenderDiagnostic(Path.Combine(output, "transfer-pending.png")); renderedPending = true;
                try { await ApplyStorageAsync(downloadDir, storagePreference); }
                catch (InvalidOperationException) { activeStorageChangeBlocked = true; }
                if (!activeStorageChangeBlocked) throw new InvalidOperationException("Active transfer allowed a storage change.");
                await Task.Run(() => DiagnosticCore.Call(new { op = "accept", id = Text(t, "id") }));
            }
            if (transfers.Any(t => Text(t, "state") == "failed")) throw new InvalidOperationException("Loopback failed: " + JsonSerializer.Serialize(transfers));
            if (transfers.Length >= 2 && transfers.All(t => Text(t, "state") == "completed"))
            {
                RenderDiagnostic(Path.Combine(output, "transfer-completed.png"));
                var received = Directory.GetFiles(downloadDir, "*.txt", SearchOption.AllDirectories).Single();
                string sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
                string receivedHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(received)));
                if (!renderedPending || sourceHash != receivedHash || files.Count != 0 || transferRows.Count < 2) throw new InvalidOperationException("Loopback rendering/selection/hash regression.");
                return new { passed = true, id, bytes = new FileInfo(path).Length, sha256 = sourceHash, pendingTemplateRendered = renderedPending, completedTemplateRendered = true, activeStorageChangeBlocked, historyRows = transferRows.Count };
            }
            await Task.Delay(150);
        }
        throw new TimeoutException("Loopback transfer did not complete.");
    }

    internal async Task<object> DiagnosticPhoneSendAsync(NearbySession session, NearbyPhone phone, JsonElement status, string output, bool directWifi = false, bool backgroundTransfer = false, string? selectedPath = null, string? scenario = null)
    {
        nearbySession = session; connectedPhone = phone;
        phoneInfo = status.GetProperty("info").Clone();
        session.Name = Text(phoneInfo, "name"); session.Authenticated = true;
        session.AutomaticHotspot = ConnectionPolicy.SupportsAutomaticHotspot(status);
        if (!directWifi && session.AutomaticHotspot) throw new InvalidOperationException("This diagnostic only permits the existing network, without hotspot changes.");
        if (directWifi) wifi = new NativeWifi(stateDir);
        selectedIsNearby = true;
        selectedPeer = directWifi ? null : await FindPhonePeerAsync(phoneInfo, session);
        string path = selectedPath ?? CreateFixture(output);
        using var proxy = backgroundTransfer ? await CreateBackgroundProxyAsync(output, selectedPath) : null;
        if (backgroundTransfer && selectedPath is null) {
            path = Path.Combine(output, "ShareLink-generated-background.bin");
            using var generated = File.Create(path);
            byte[] chunk = Enumerable.Range(0, 65536).Select(i => (byte)(i % 251)).ToArray();
            for (int index = 0; index < 512; index++) generated.Write(chunk);
        }
        string[] sources = [path];
        if (scenario == "batch") {
            string name = "ShareLink-batch-" + DateTime.Now.ToString("HHmmss") + ".txt";
            string first = Path.Combine(output, "source-a", name), second = Path.Combine(output, "source-b", name);
            Directory.CreateDirectory(Path.GetDirectoryName(first)!); Directory.CreateDirectory(Path.GetDirectoryName(second)!);
            File.WriteAllText(first, "Generated same-name fixture A\r\n" + new string('A', 65536));
            File.WriteAllText(second, "Generated same-name fixture B\r\n" + new string('B', 32768));
            string empty = Path.Combine(output, "ShareLink-empty-" + DateTime.Now.ToString("HHmmss") + ".txt");
            File.WriteAllBytes(empty, []);
            sources = [first, second, empty];
        }
        var sourceEvidence = sources.Where(_ => scenario is not null).Select(source => {
            using var input = File.OpenRead(source);
            return new { path = source, name = Path.GetFileName(source), bytes = new FileInfo(source).Length, sha256 = Convert.ToHexString(SHA256.HashData(input)) };
        }).ToArray();
        if (scenario is not null) File.WriteAllText(Path.Combine(output, "generated-sources.json"), JsonSerializer.Serialize(sourceEvidence, new JsonSerializerOptions { WriteIndented = true }));
        var previousRemoteIds = status.GetProperty("transfers").EnumerateArray().Select(t => Text(t, "id")).ToHashSet();
        AddFiles(sources);
        string id = await SendSelectedFilesAsync();
        var peer = sentPeers[id];
        var deadline = DateTime.UtcNow.AddSeconds(selectedPath is not null ? 900 : backgroundTransfer ? 240 : 110);
        bool pendingRendered = false;
        while (DateTime.UtcNow < deadline)
        {
            await RefreshState();
            RenderDiagnostic(Path.Combine(output, "phone-send-progress.png"));
            var transfer = DiagnosticSnapshot().GetProperty("transfers").EnumerateArray().First(t => Text(t, "id") == id);
            File.WriteAllText(Path.Combine(output, "phone-send-live.json"), transfer.GetRawText());
            if (Text(transfer, "state") == "awaiting_accept") pendingRendered = true;
            if (Text(transfer, "state") is "failed" or "rejected" or "cancelled")
            {
                if (scenario is "reject" or "cancel") {
                    var remote = await SecureAsync(new { op = "status" });
                    File.WriteAllText(Path.Combine(output, "phone-terminal-status.json"), remote.GetRawText());
                    string expected = scenario == "reject" ? "rejected" : "cancelled";
                    var remoteTask = remote.GetProperty("transfers").EnumerateArray().Single(t =>
                        !previousRemoteIds.Contains(Text(t, "id")) && Text(t, "direction") == "receive");
                    if (Text(remoteTask, "state") != expected || Number(remoteTask, "total") != sourceEvidence.Sum(f => f.bytes))
                        throw new InvalidOperationException("手机终态或大小与测试场景不符。");
                    if (scenario == "reject" && (Text(transfer, "state") != "rejected" || Number(remoteTask, "bytes") != 0 || Number(transfer, "bytes") != 0))
                        throw new InvalidOperationException("拒收后传输状态或字节数错误。");
                    if (scenario == "cancel" && (Number(remoteTask, "bytes") <= 0 || Number(remoteTask, "bytes") >= Number(remoteTask, "total")))
                        throw new InvalidOperationException("取消测试必须发生在文件传输过程中。");
                    return new { passed = true, scenario, id, state = Text(transfer, "state"), phoneState = expected,
                        phoneId = Text(remoteTask, "id"), bytes = Number(remoteTask, "bytes"), sources = sourceEvidence,
                        transportModules = modules.Ids, pendingRendered, completeFileExpected = false };
                }
                try { File.WriteAllText(Path.Combine(output, "phone-failed-status.json"), (await SecureAsync(new { op = "status" })).GetRawText()); } catch { }
                throw new InvalidOperationException("Phone transfer: " + transfer.GetRawText());
            }
            if (Text(transfer, "state") == "completed")
            {
                if (scenario is "reject" or "cancel") throw new InvalidOperationException("预期拒收/取消，文件却已完成。");
                bool remoteStatusAvailable = false;
                // Closing discovery deliberately releases BLE after the file's final TLS acknowledgement.
                // That optional status read must not invalidate an already committed transfer.
                try {
                    var remote = await SecureAsync(new { op = "status" });
                    File.WriteAllText(Path.Combine(output, "phone-completed-status.json"), remote.GetRawText());
                    remoteStatusAvailable = true;
                    if (scenario == "batch") {
                        var remoteTask = remote.GetProperty("transfers").EnumerateArray().Single(t =>
                            !previousRemoteIds.Contains(Text(t, "id")) && Text(t, "direction") == "receive");
                        if (Text(remoteTask, "state") != "completed" || Number(remoteTask, "bytes") != sourceEvidence.Sum(f => f.bytes))
                            throw new InvalidOperationException("批量收件尚未完整提交。");
                    }
                } catch (Exception ex) {
                    if (scenario == "batch") throw;
                    File.WriteAllText(Path.Combine(output, "phone-completed-status-unavailable.json"), JsonSerializer.Serialize(new {
                        transportState = "completed", error = ex.Message, proof = "peer durable-commit acknowledgement over pinned TLS"
                    }));
                }
                if (scenario == "batch") return new { passed = true, scenario, id, state = "completed", sources = sourceEvidence,
                    bytes = sourceEvidence.Sum(f => f.bytes), pendingRendered, remoteStatusAvailable,
                    transportModules = modules.Ids, historyRows = transferRows.Count };
                string sourceHash = await Task.Run(() => {
                    using var source = File.OpenRead(path);
                    return Convert.ToHexString(SHA256.HashData(source));
                });
                return new { passed = true, id, peer.Name, peer.Host, peer.Port, filename = Path.GetFileName(path), bytes = new FileInfo(path).Length,
                    sha256 = sourceHash, pendingRendered, remoteStatusAvailable,
                    automaticHotspot = session.AutomaticHotspot, networkChanged = wifi?.HasRecovery == true,
                    transportModules = modules.Ids, directAddresses = DiagnosticSnapshot().GetProperty("info").GetProperty("addresses").Clone(),
                    historyRows = transferRows.Count, state = "completed" };
            }
            await Task.Delay(300);
        }
        await Task.Run(() => DiagnosticCore.Call(new { op = "cancel", id }));
        throw new TimeoutException("等待手机接收测试文件超时。");
    }

    private async Task<DiagnosticTransferProxy> CreateBackgroundProxyAsync(string output, string? selectedPath = null, bool receiveFile = false)
    {
        var destination = await StartPhoneHotspotAsync(nearbySession!);
        string local = wifi!.DirectAddress(System.Net.IPAddress.Parse(destination.Host));
        int delayMs = selectedPath is null ? 80 : 4;
        var proxy = new DiagnosticTransferProxy(System.Net.IPAddress.Parse(local), destination, delayMs, paceDownload: receiveFile);
        selectedPeer = destination with { Host = local, Port = proxy.Port };
        File.WriteAllText(Path.Combine(output, "paced-relay.json"), JsonSerializer.Serialize(new {
            diagnosticOnly = true, generatedMiB = selectedPath is null ? (int?)32 : null, delayPerReadMs = delayMs,
            pacedDirection = receiveFile ? "phone-to-PC" : "PC-to-phone",
            localAddress = local, targetAddress = destination.Host, targetPort = destination.Port,
            pinnedFingerprint = destination.Fingerprint
        }));
        return proxy;
    }

    internal async Task<object> CleanupDirectDiagnosticAsync()
    {
        foreach (var transfer in DiagnosticSnapshot().GetProperty("transfers").EnumerateArray())
            if (Text(transfer, "state") is not ("completed" or "failed" or "cancelled" or "rejected"))
                await Task.Run(() => DiagnosticCore.Call(new { op = "cancel", id = Text(transfer, "id") }));
        await RefreshState();
        await RestoreNetworkAsync();
        if (wifi?.HasRecovery == true) throw new InvalidOperationException("原 Wi-Fi 恢复未完成，记录已保留。");
        return new { restored = true, directAddressesCleared = DiagnosticSnapshot().GetProperty("info").GetProperty("addresses").GetArrayLength() == 0 };
    }

    private sealed record DiagnosticReceipt(string Name, long Bytes, string Sha256);

    private static DiagnosticReceipt[] ReadGeneratedBatch(string manifest)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(manifest));
        var entries = document.RootElement.EnumerateArray().ToArray();
        if (entries.Length != 3) throw new InvalidOperationException("批量诊断只接受已生成的三个文件。");
        string root = Path.GetDirectoryName(Path.GetFullPath(manifest))! + Path.DirectorySeparatorChar;
        var seen = new Dictionary<string, int>();
        return entries.Select(entry => {
            string path = Path.GetFullPath(Text(entry, "path")), name = Text(entry, "name");
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path) != name ||
                !System.Text.RegularExpressions.Regex.IsMatch(name, "^ShareLink-(batch|empty)-[0-9]{6}\\.txt$") ||
                Number(entry, "bytes") is < 0 or > 1048576)
                throw new InvalidOperationException("批量清单必须指向本次诊断目录内的生成文件。");
            using var source = File.OpenRead(path);
            string hash = Convert.ToHexString(SHA256.HashData(source));
            if (source.Length != Number(entry, "bytes") || hash != Text(entry, "sha256"))
                throw new InvalidOperationException("生成文件已改变，不能用于批量回传校验。");
            int index = seen.GetValueOrDefault(name); seen[name] = index + 1;
            string receiptName = index == 0 ? name : Path.GetFileNameWithoutExtension(name) + $" ({index})" + Path.GetExtension(name);
            return new DiagnosticReceipt(receiptName, source.Length, hash);
        }).ToArray();
    }

    internal async Task<object> DiagnosticPhoneReceiveAsync(NearbySession session, NearbyPhone phone, JsonElement status, string output, bool receiveDialog = false, bool directWifi = false, string? selectedPath = null, bool backgroundTransfer = false, string? batchManifest = null)
    {
        if (directWifi) wifi = new NativeWifi(stateDir);
        nearbySession = session; connectedPhone = phone;
        phoneInfo = status.GetProperty("info").Clone();
        session.Name = Text(phoneInfo, "name"); session.Authenticated = true;
        session.AutomaticHotspot = ConnectionPolicy.SupportsAutomaticHotspot(status);
        if (!directWifi && session.AutomaticHotspot) throw new InvalidOperationException("This diagnostic only permits the existing network, without hotspot changes.");
        selectedIsNearby = true;
        selectedPeer = directWifi ? null : await FindPhonePeerAsync(phoneInfo, session);
        if (!directWifi && selectedPeer is null) throw new InvalidOperationException("手机文件服务在当前网络不可达。");
        string fixture = selectedPath ?? CreateFixture(output);
        long expectedBytes = new FileInfo(fixture).Length;
        string expectedHash = await Task.Run(() => { using var source = File.OpenRead(fixture); return Convert.ToHexString(SHA256.HashData(source)); });
        string expectedName = Path.GetFileName(fixture);
        bool MatchesName(string name) => selectedPath is null
            ? name.StartsWith("ShareLink-test-", StringComparison.Ordinal) && name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, expectedName, StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(name,
                "^" + System.Text.RegularExpressions.Regex.Escape(Path.GetFileNameWithoutExtension(expectedName)) +
                " \\([1-9][0-9]{0,3}\\)" + System.Text.RegularExpressions.Regex.Escape(Path.GetExtension(expectedName)) + "$");
        var batch = batchManifest is null ? null : ReadGeneratedBatch(batchManifest);
        if (batch is not null) expectedBytes = batch.Sum(receipt => receipt.Bytes);
        bool MatchesManifest(JsonElement[] offered) => batch is null
            ? offered.Length == 1 && Number(offered[0], "size") == expectedBytes && MatchesName(Text(offered[0], "name"))
            : offered.Length == batch.Length && batch.All(receipt => offered.Count(file =>
                Text(file, "name") == receipt.Name && Number(file, "size") == receipt.Bytes) == 1);
        string acceptance = receiveDialog ? "user-receive-dialog" : selectedPath is null ? "diagnostic-generated-fixture-only" : "diagnostic-user-authorized-file-only";
        if (batch is not null) acceptance = "diagnostic-generated-batch-manifest-only";
        DiagnosticTransferProxy? proxy = null;
        using var proxyScope = new DiagnosticProxyScope(() => proxy?.Dispose());
        if (receiveDialog)
        {
            Application.Current.MainWindow = this;
            BluetoothStatus.Text = "已连接测试手机：" + session.Name;
            NetworkSummary.Text = "小文件回传验证 · 保持现有网络";
            Show();
        }
        File.WriteAllText(Path.Combine(output, "phone-receive-ready.json"), JsonSerializer.Serialize(new {
            phone = session.Name, expectedBytes, expectedHash = batch is null ? expectedHash : null,
            expectedName = batch is null ? expectedName : null, expectedFiles = batch, acceptance, networkChanged = false }));
        string? id = null, phoneId = null;
        bool pendingRendered = false, accepted = false;
        var deadline = DateTime.UtcNow.AddMinutes(5);
        long lastStatus = 0;
        while (DateTime.UtcNow < deadline)
        {
            long now = Environment.TickCount64;
            if (id is null && now - lastStatus >= 2500)
            {
                lastStatus = now;
                var remote = await SecureAsync(new { op = "status" });
                if (remote.TryGetProperty("pullOffer", out var offer) && offer.ValueKind == JsonValueKind.Object)
                {
                    var offered = offer.GetProperty("files").EnumerateArray().ToArray();
                    if (!MatchesManifest(offered))
                    {
                        await SecureAsync(new { op = "pull_reject", id = Text(offer, "id") });
                        throw new InvalidOperationException("文件不在此次诊断授权范围内。");
                    }
                    phoneId = Text(offer, "id");
                    if (backgroundTransfer) proxy = await CreateBackgroundProxyAsync(output, selectedPath, receiveFile: true);
                    id = await ReceivePhoneOfferAsync(offer);
                    if (id is null) throw new InvalidOperationException("未能创建手机回传任务。");
                    deadline = DateTime.UtcNow.AddSeconds(selectedPath is null ? 110 : 900);
                }
            }
            await RefreshState();
            if (id is not null)
            {
                var transfer = DiagnosticSnapshot().GetProperty("transfers").EnumerateArray().First(t => Text(t, "id") == id);
                File.WriteAllText(Path.Combine(output, "phone-receive-live.json"), transfer.GetRawText());
                string state = Text(transfer, "state");
                if (state == "awaiting_accept" && !accepted)
                {
                    var actualFiles = transfer.GetProperty("files").EnumerateArray().ToArray();
                    // BLE offer hints are untrusted. Check the TLS manifest as well.
                    if (!MatchesManifest(actualFiles) || Number(transfer, "total") != expectedBytes)
                    {
                        await Task.Run(() => DiagnosticCore.Call(new { op = "reject", id }));
                        throw new InvalidOperationException("TLS 文件请求不在此次诊断授权范围内。");
                    }
                    RenderDiagnostic(Path.Combine(output, "phone-receive-pending.png")); pendingRendered = true;
                    if (receiveDialog) await PromptReceiveAsync(transfer.Clone());
                    else await Task.Run(() => DiagnosticCore.Call(new { op = "accept", id }));
                    accepted = true;
                }
                if (state is "failed" or "rejected" or "cancelled")
                    throw new InvalidOperationException("Phone reverse transfer: " + transfer.GetRawText());
                if (state == "completed")
                {
                    if (batch is not null) {
                        var receipts = batch.Select(expected => {
                            string received = Path.Combine(downloadDir, expected.Name);
                            using var input = File.OpenRead(received);
                            string hash = Convert.ToHexString(SHA256.HashData(input));
                            if (input.Length != expected.Bytes || hash != expected.Sha256)
                                throw new InvalidOperationException("批量回传文件内容不一致：" + expected.Name);
                            return new { expected.Name, expected.Bytes, sha256 = hash, received };
                        }).ToArray();
                        if (!pendingRendered || !accepted) throw new InvalidOperationException("批量接收确认未完成。");
                        var remoteBatch = await SecureAsync(new { op = "status" });
                        File.WriteAllText(Path.Combine(output, "phone-reverse-completed-status.json"), remoteBatch.GetRawText());
                        if (!remoteBatch.GetProperty("transfers").EnumerateArray().Any(t => Text(t, "id") == phoneId && Text(t, "state") == "completed"))
                            throw new InvalidOperationException("手机批量发送尚未确认完成。");
                        RenderDiagnostic(Path.Combine(output, "phone-receive-completed.png"));
                        return new { passed = true, id, phoneId, bytes = expectedBytes, receipts, acceptance, state = "completed" };
                    }
                    var received = Directory.GetFiles(downloadDir, "*", SearchOption.AllDirectories).Single(file => MatchesName(Path.GetFileName(file)));
                    string receivedHash = await Task.Run(() => { using var source = File.OpenRead(received); return Convert.ToHexString(SHA256.HashData(source)); });
                    if (!pendingRendered || !accepted || new FileInfo(received).Length != expectedBytes || receivedHash != expectedHash)
                        throw new InvalidOperationException("手机回传文件与本次授权源文件不一致。");
                    RenderDiagnostic(Path.Combine(output, "phone-receive-completed.png"));
                    var remote = await SecureAsync(new { op = "status" });
                    File.WriteAllText(Path.Combine(output, "phone-reverse-completed-status.json"), remote.GetRawText());
                    if (!remote.GetProperty("transfers").EnumerateArray().Any(t => Text(t, "id") == phoneId && Text(t, "state") == "completed"))
                        throw new InvalidOperationException("电脑已保存文件，但手机尚未确认完成。");
                    return new { passed = true, id, phoneId, phone = session.Name, bytes = expectedBytes, sha256 = receivedHash,
                        received, pendingTemplateRendered = pendingRendered, completedTemplateRendered = true,
                        acceptance, receiveDialogTested = receiveDialog,
                        automaticHotspot = session.AutomaticHotspot, networkChanged = wifi?.HasRecovery == true, state = "completed" };
                }
            }
            await Task.Delay(150);
        }
        if (id is not null) await Task.Run(() => DiagnosticCore.Call(new { op = "cancel", id }));
        throw new TimeoutException("等待手机回传测试文件超时。");
    }

    private sealed class DiagnosticProxyScope(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }

    internal static async Task<object> DiagnosticRelayChecksAsync()
    {
        var results = new List<object>();
        foreach (bool reverse in new[] { false, true })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var target = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            target.Start(1);
            try
            {
                var destination = new Peer("fixture", "loopback", "127.0.0.1", ((System.Net.IPEndPoint)target.LocalEndpoint).Port, "test");
                using var proxy = new DiagnosticTransferProxy(System.Net.IPAddress.Loopback, destination, 4, paceDownload: reverse);
                using var front = new System.Net.Sockets.TcpClient();
                await front.ConnectAsync(System.Net.IPAddress.Loopback, proxy.Port, timeout.Token);
                using var back = await target.AcceptTcpClientAsync(timeout.Token);
                byte[] payload = Enumerable.Range(0, 196613).Select(index => (byte)(index % 251)).ToArray();
                byte[] received = new byte[payload.Length];
                var upload = front.GetStream().WriteAsync(payload, timeout.Token).AsTask();
                await back.GetStream().ReadExactlyAsync(received, timeout.Token); await upload;
                if (!received.SequenceEqual(payload)) throw new InvalidOperationException("Diagnostic relay altered upload bytes.");
                var download = back.GetStream().WriteAsync(payload, timeout.Token).AsTask();
                await front.GetStream().ReadExactlyAsync(received, timeout.Token); await download;
                if (!received.SequenceEqual(payload)) throw new InvalidOperationException("Diagnostic relay altered download bytes.");
                results.Add(new { passed = true, pacedDirection = reverse ? "phone-to-PC" : "PC-to-phone", bytesEachDirection = payload.Length });
            }
            finally { target.Stop(); }
        }
        return new { passed = true, scope = "loopback only; transparent relay in both pacing modes", checks = results };
    }

    internal static object DiagnosticPolicyChecks()
    {
        JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
        bool legacy = ConnectionPolicy.SupportsAutomaticHotspot(Parse("{\"hotspotState\":\"idle\"}"));
        bool harmony = !ConnectionPolicy.SupportsAutomaticHotspot(Parse("{\"hotspotState\":\"unsupported\"}"));
        bool explicitFalse = !ConnectionPolicy.SupportsAutomaticHotspot(Parse("{\"hotspotState\":\"idle\",\"capabilities\":{\"automaticHotspot\":false}}"));
        bool unknown = !ConnectionPolicy.SupportsAutomaticHotspot(default);
        var prefix = "ABCDEF0123456789";
        var phone = new NearbyPhone(1, "test", -55, 0, FingerprintPrefix: prefix);
        var peer = new Peer("test", "native", "192.168.3.5", 53317, prefix + new string('0', 48));
        bool merge = ConnectionPolicy.UniquePresentationPeer(phone, [peer]) == peer;
        bool ambiguous = ConnectionPolicy.UniquePresentationPeer(phone, [peer, peer with { Fingerprint = prefix + new string('1', 48) }]) is null;
        bool oldInvite = ConnectionPolicy.UniquePresentationPeer(phone with { FingerprintPrefix = "", InstanceId = prefix }, [peer]) is null;
        bool route = ConnectionPolicy.OrderEndpoints(["172.17.1.1", "192.168.3.5"], ["192.168.3.5"])[0] == "192.168.3.5";
        bool pullSizes = ConnectionPolicy.OfferedBytes(Parse("{\"files\":[{\"size\":64835},{\"size\":268435456}]}")) == 268500291;
        bool pullTotal = ConnectionPolicy.OfferedBytes(Parse("{\"totalBytes\":268435456,\"files\":[{\"size\":1}]}")) == 268435456 &&
            ConnectionPolicy.OfferedBytes(Parse("{\"total\":64835}")) == 64835;
        bool pullSizeBounds = ConnectionPolicy.OfferedBytes(Parse("{\"files\":[{\"size\":9223372036854775807},{\"size\":1}]}")) == long.MaxValue &&
            ConnectionPolicy.OfferedBytes(Parse("{\"files\":[{\"size\":-1},{\"size\":\"invalid\"},null]}")) == 0 && ConnectionPolicy.OfferedBytes(default) == 0;
        if (!(legacy && harmony && explicitFalse && unknown && merge && ambiguous && oldInvite && route && pullSizes && pullTotal && pullSizeBounds)) throw new InvalidOperationException("Connection policy regression.");
        return new { passed = true, legacy, harmony, explicitFalse, unknown, merge, ambiguous, oldInvite, route, pullSizes, pullTotal, pullSizeBounds };
    }
}
