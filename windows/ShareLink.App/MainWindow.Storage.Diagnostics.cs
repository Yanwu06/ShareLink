using System.IO;

namespace ShareLink;

public partial class MainWindow
{
    internal static async Task<object> DiagnosticCrossVolumeAsync(string output)
    {
        string staging = Path.Combine(Path.GetTempPath(), "ShareLink-staging-check-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(output, "cross-volume");
        var window = new MainWindow(root) { diagnosticPartialDirectory = staging };
        try
        {
            if (string.Equals(Path.GetPathRoot(staging), Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase))
                return new { passed = true, skipped = true, reason = "Only one test volume is available." };
            await window.InitializeCoreAsync();
            var transfer = await window.DiagnosticLoopbackAsync(root);
            bool empty = !Directory.EnumerateFiles(staging).Any();
            if (!empty) throw new InvalidOperationException("Cross-volume staging was not cleaned.");
            return new { passed = true, skipped = false, stagingVolume = Path.GetPathRoot(staging), receiveVolume = Path.GetPathRoot(root),
                transfer, noDownloadSubfolder = !Directory.Exists(Path.Combine(window.downloadDir, ".sharelink-partials")), stagingClean = empty };
        }
        finally
        {
            window.StopDiagnostic();
            if (Directory.Exists(staging) && !Directory.EnumerateFileSystemEntries(staging).Any()) Directory.Delete(staging);
        }
    }

    internal async Task<object> DiagnosticStorageSwitchAsync(string output)
    {
        if (!diagnostic || hasActiveTransfers) throw new InvalidOperationException("Storage checks require an isolated idle core.");
        string previous = downloadDir;
        var preference = storagePreference;
        string target = Path.Combine(output, "custom-received"); Directory.CreateDirectory(target);
        string historyBefore = DiagnosticSnapshot().GetProperty("transfers").GetRawText();
        string[] filesBefore = Directory.GetFiles(previous);
        await ApplyStorageAsync(target, new(target));
        if (historyBefore != DiagnosticSnapshot().GetProperty("transfers").GetRawText() || filesBefore.Any(p => !File.Exists(p)))
            throw new InvalidOperationException("Changing storage modified previous history or receipts.");
        var transfer = await DiagnosticLoopbackAsync(output);
        string[] received = Directory.GetFiles(target, "*.txt");
        if (received.Length != 1 || StoragePaths.Read(stateDir).Directory != target)
            throw new InvalidOperationException("New receipt was not saved directly to the selected folder.");
        string historyAfter = DiagnosticSnapshot().GetProperty("transfers").GetRawText();
        await ApplyStorageAsync(previous, preference);
        if (historyAfter != DiagnosticSnapshot().GetProperty("transfers").GetRawText() || filesBefore.Any(p => !File.Exists(p)))
            throw new InvalidOperationException("Restoring storage changed history or old files.");
        return new { passed = true, oldFilesPreserved = true, historyPreserved = true, persisted = true,
            receivedDirectlyInSelectedFolder = true, restored = downloadDir == previous, transfer };
    }
}
