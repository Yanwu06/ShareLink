namespace ShareLink;

// Only constructed when explicitly listed in transport-modules.json.
internal sealed class LanTransportModule : ITransportModule
{
    public string Id => "lan";
    internal static async Task<Peer> ProbeAsync(NativeCore core, string host, int port)
    {
        var reply = await Task.Run(() => core.Call(new { op = "probe", host, port }));
        var peer = reply.GetProperty("peer");
        return new Peer(MainWindow.Text(peer, "id"), MainWindow.Text(peer, "name"), host, port, MainWindow.Text(peer, "fingerprint"), true);
    }
}
