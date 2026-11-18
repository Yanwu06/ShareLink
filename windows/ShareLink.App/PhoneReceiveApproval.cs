using System.Text.Json;
namespace ShareLink;

// Approval is for the authenticated BLE offer's exact manifest, not any later TLS request.
internal sealed class PhoneReceiveApproval
{
    internal const string NetworkWarning = TemporaryWifiNotice.Message;
    private readonly record struct Entry(string Name, long Size);
    private readonly Entry[] files;
    internal string Id { get; }
    internal string Token { get; }
    internal string Fingerprint { get; }
    internal long Total { get; }
    private PhoneReceiveApproval(JsonElement offer, string fingerprint)
    {
        Id = MainWindow.Text(offer,"id"); Token = MainWindow.Text(offer,"token"); Fingerprint = fingerprint;
        if (Id.Length == 0 || Token.Length != 64 || !Token.All(Uri.IsHexDigit) || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit)) throw new InvalidOperationException("接收请求信息不完整，请重新发送。");
        files = Manifest(offer);
        Total = files.Aggregate(0L,(sum,file)=>checked(sum+file.Size));
    }
    internal static PhoneReceiveApproval Create(JsonElement offer,string fingerprint) => new(offer,fingerprint);
    private static Entry[] Manifest(JsonElement value)
    {
        if (!value.TryGetProperty("files",out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0) throw new InvalidOperationException("接收文件列表为空。");
        return items.EnumerateArray().Select(file=>{
            string name=MainWindow.Text(file,"name");
            if (string.IsNullOrWhiteSpace(name) || !file.TryGetProperty("size",out var size) || !size.TryGetInt64(out var bytes) || bytes<0) throw new InvalidOperationException("接收文件信息不完整。");
            return new Entry(name,bytes);
        }).ToArray();
    }
    private bool SameManifest(JsonElement value)
    {
        try { return files.SequenceEqual(Manifest(value)); }
        catch (Exception error) when (error is InvalidOperationException or FormatException or OverflowException) { return false; }
    }
    internal bool MatchesOffer(JsonElement offer,string fingerprint) =>
        Fingerprint.Equals(fingerprint,StringComparison.OrdinalIgnoreCase) && MainWindow.Text(offer,"id")==Id && MainWindow.Text(offer,"token")==Token && SameManifest(offer);
    internal bool MatchesTransfer(JsonElement transfer) => MainWindow.Text(transfer,"direction")=="receive" && MainWindow.Text(transfer,"state")=="awaiting_accept" &&
        transfer.TryGetProperty("total",out var total) && total.TryGetInt64(out var bytes) && bytes==Total && SameManifest(transfer);
    internal string Message(string peerName,string directory,bool needsTemporaryWifi)
    {
        var preview=string.Join("\n",files.Take(8).Select(f=>f.Name));
        if(files.Length>8)preview+=$"\n……共 {files.Length} 个文件";
        string message=$"来自「{peerName}」\n\n{preview}\n\n{MainWindow.FormatBytes(Total)} · 保存到 {directory}";
        return needsTemporaryWifi ? message+"\n\n"+NetworkWarning : message;
    }
}
