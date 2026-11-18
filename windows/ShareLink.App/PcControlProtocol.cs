using System.Text.Json;
namespace ShareLink;

// Pairing exchanges keys before secure control. Normal PC mode follows the
// user-selected click-to-connect policy; diagnostic checkpoints can still require SAS.
internal sealed class PcControlProtocol : IAsyncDisposable
{
    private readonly NativeCore core;
    private readonly Func<string, string, Task<bool>>? confirmPeer;
    private readonly Func<JsonElement,Task<JsonElement>>? secureCommand;
    private readonly bool clickToConnect;
    private readonly Func<bool>? ready;
    private readonly Func<bool>? reservePeer;
    private readonly Action? releasePeer;
    private bool sessionReserved;
    public bool HasSession => pair.Length > 0 || sessionReserved;
    public bool ReleaseRequested { get; private set; }
    public void AcknowledgeRelease() => ReleaseRequested = false;
    private readonly SemaphoreSlim gate = new(1, 1);
    private string pair = "";
    private Task<bool>? confirmation;
    private bool confirmed, disposed;
    public string LocalName => MainWindow.Text(core.Call(new { op = "snapshot" }).GetProperty("info"), "name");
    public string LocalFingerprint => MainWindow.Text(core.Call(new { op = "snapshot" }).GetProperty("info"), "fingerprint");
    public string Name { get; private set; } = "";
    public string Fingerprint { get; private set; } = "";
    public string Sas { get; private set; } = "";
    public bool Authenticated => confirmed && !disposed;
    public PcControlProtocol(NativeCore core, Func<string, string, Task<bool>>? confirmPeer = null, Func<JsonElement,Task<JsonElement>>? secureCommand = null, Func<bool>? ready = null, Func<bool>? reservePeer = null, Action? releasePeer = null, bool clickToConnect = false) { if (!clickToConnect && confirmPeer is null) throw new ArgumentNullException(nameof(confirmPeer)); this.clickToConnect = clickToConnect; this.core = core; this.confirmPeer = confirmPeer; this.secureCommand = secureCommand; this.ready = ready; this.reservePeer = reservePeer; this.releasePeer = releasePeer; }
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    public async Task<JsonElement> RequestAsync(JsonElement request)
    {
        await gate.WaitAsync();
        bool beganHere = false;
        try {
            ObjectDisposedException.ThrowIf(disposed, this);
            switch (MainWindow.Text(request, "op")) {
                case "hello":
                    if (ready is not null && !ready()) throw new InvalidOperationException("当前设备正在处理连接或传输，请稍后重试");
                    if (pair.Length > 0) throw new InvalidOperationException("已有连接，请先断开再重试");
                    ReleaseRequested = false;
                    return Json(new { ok = true, info = core.Call(new { op = "snapshot" }).GetProperty("info"), controlMaxWrite = 512, requiresPairingConfirmation = !clickToConnect });
                case "pair_begin":
                    if (ready is not null && !ready()) throw new InvalidOperationException("当前设备不接受新的配对请求");
                    if (pair.Length > 0) throw new InvalidOperationException("已有加密连接");
                    if (reservePeer is not null) { if (!reservePeer()) throw new InvalidOperationException("当前设备正在处理另一项连接"); sessionReserved = true; }
                    beganHere = true; ReleaseRequested = false;
                    var begin = core.Call(new { op = "pair_begin" }); pair = MainWindow.Text(begin, "pairId");
                    var finish = core.Call(new { op = "pair_finish", pairId = pair, hello = request.GetProperty("hello") });
                    var peer = finish.GetProperty("peer"); Name = MainWindow.Text(peer, "name"); Fingerprint = MainWindow.Text(peer, "fingerprint"); Sas = MainWindow.Text(finish, "sas");
                    // The normal PC UI explicitly uses click-to-connect. Key activation
                    // still waits for pair_confirm and secure commands remain encrypted.
                    confirmation = clickToConnect ? Task.FromResult(true) : confirmPeer!(Name, Sas);
                    return Json(new { ok = true, hello = begin.GetProperty("hello"), requiresPairingConfirmation = !clickToConnect });
                case "pair_confirm":
                    if (pair.Length == 0 || !request.TryGetProperty("confirmed", out var yes) || yes.ValueKind != JsonValueKind.True) throw new InvalidOperationException("请先确认加密连接");
                    if (confirmation is null || !confirmation.IsCompleted) return Json(new { ok = true, confirmed = false, pending = true });
                    bool accepted = await confirmation;
                    if (!accepted) { ClosePair(); return Json(new { ok = false, error = "对方取消了校验码确认" }); }
                    core.Call(new { op = "pair_confirm", pairId = pair, confirmed = accepted }); confirmed = accepted;
                    return Json(new { ok = true, confirmed });
                case "secure":
                    if (!Authenticated) throw new InvalidOperationException("双方尚未确认校验码");
                    var decoded = core.Call(new { op = "pair_decrypt", pairId = pair, envelope = request.GetProperty("envelope") });
                    if (secureCommand is not null) {
                        var command = decoded.GetProperty("payload");
                        var handled = await secureCommand(command);
                        var encrypted = core.Call(new { op = "pair_encrypt", pairId = pair, payload = handled });
                        if (MainWindow.Text(command, "op") == "disconnect" && handled.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) ClosePair();
                        return encrypted;
                    }
                    var op = MainWindow.Text(decoded.GetProperty("payload"), "op");
                    // A radio checkpoint, not a fabricated file-transfer pass.
                    var payload = op == "status" ? Json(new { ok = true, info = core.Call(new { op = "snapshot" }).GetProperty("info"), capabilities = new { automaticHotspot = false }, hotspotState = "unsupported", pairingCheckpoint = true })
                        : Json(new { ok = false, error = "本检查仅验证电脑加密连接，尚未启动热点或文件传输" });
                    return core.Call(new { op = "pair_encrypt", pairId = pair, payload });
                default: throw new InvalidOperationException("不支持的控制操作");
            }
        } catch (Exception ex) {
            if (beganHere && !confirmed && (pair.Length > 0 || sessionReserved)) ClosePair();
            return Json(new { ok = false, error = ex.Message });
        }
        finally { gate.Release(); }
    }
    private void ClosePair()
    {
        try { if (pair.Length > 0) core.Call(new { op = "pair_close", pairId = pair }); }
        finally { pair = ""; confirmed = false; confirmation = null; Sas = Name = Fingerprint = ""; ReleaseRequested = true; if (sessionReserved) { sessionReserved = false; releasePeer?.Invoke(); } }
    }
    public async ValueTask DisposeAsync() { await gate.WaitAsync(); try { if (disposed) return; disposed = true; ClosePair(); } finally { gate.Release(); } }
}
