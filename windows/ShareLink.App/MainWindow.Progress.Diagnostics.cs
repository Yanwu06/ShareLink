using System.Text.Json;
namespace ShareLink;
public partial class MainWindow {
    public static object DiagnosticProgressChecks() {
        var row = new TransferView("progress-check");
        JsonElement Snapshot(long bytes) => JsonSerializer.SerializeToElement(new { id = "progress-check", direction = "send", peerName = "Test phone", state = "transferring", bytes, total = 8388608, speed = bytes * 10, files = new[] { new { name = "stable-file.bin", size = 8388608 } } });
        row.Update(Snapshot(0)); int titleChanges = 0, detailChanges = 0, broadChanges = 0;
        row.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(row.Title)) titleChanges++; if (e.PropertyName == nameof(row.Detail)) detailChanges++; if (string.IsNullOrEmpty(e.PropertyName)) broadChanges++; };
        for (int i = 1; i <= 30; i++) row.Update(Snapshot(i * 65536));
        if (titleChanges != 0 || detailChanges != 30 || broadChanges != 0) throw new InvalidOperationException("Dual mode progress invalidates stable titles");
        var history = JsonSerializer.SerializeToElement(new { id = "batch", direction = "receive", peerName = "Test", state = "failed", total = 30, bytes = 10,
            error = "later file failed", createdAt = 10, finishedAt = 20,
            files = new[] { new { name = "first.txt", size = 10, path = "saved/first.txt" }, new { name = "second.bin", size = 20, path = "" } } });
        var first = new TransferView("batch", 0); var second = new TransferView("batch", 1);
        first.Update(history); second.Update(history);
        if (first.Title != "first.txt" || second.Title != "second.bin" || !first.Detail.StartsWith("10 B") || !second.Detail.StartsWith("20 B")
            || first.StateLabel != "\u5df2\u4fdd\u5b58" || first.Detail.Contains("later file failed") || !second.Detail.Contains("later file failed"))
            throw new InvalidOperationException("Per-file history lost file size or saved-prefix status");
        return new { passed = true, titleChanges, detailChanges, broadChanges, individualHistory = true, savedPrefix = true };
    }
}
