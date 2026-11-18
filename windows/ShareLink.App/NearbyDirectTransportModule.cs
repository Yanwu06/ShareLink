using System.Net;
using System.Text.Json;

namespace ShareLink;

internal sealed class NearbyDirectTransportModule : ITransportModule
{
    public string Id => "nearby-direct";
    internal object? Evidence { get; set; }

    internal async Task<Peer> OpenAsync(NativeCore core, NativeWifi wifi, NearbySession session,
        Func<object, Task<JsonElement>> secure, Action<string> progress, string timingDirectory)
    {
        if (!session.AutomaticHotspot) throw new InvalidOperationException("对方尚不能自动建立 Wi-Fi 直连；文件已保留，局域网模块未启用。");
        using var setupTiming = StageTiming.Measure(timingDirectory, "direct_setup");
        using var groupTiming = StageTiming.Measure(timingDirectory, "p2p_group");
        JsonElement credentials = default;
        var deadline = DateTime.UtcNow.AddSeconds(40);
        progress("正在协商并创建临时 Wi-Fi 直连…");
        while (DateTime.UtcNow < deadline)
        {
            var result = await secure(new { op = "hotspot_start" });
            if (result.TryGetProperty("credentials", out credentials) && credentials.ValueKind == JsonValueKind.Object) break;
            await Task.Delay(500);
        }
        groupTiming.Dispose();
        if (credentials.ValueKind != JsonValueKind.Object) throw new TimeoutException("临时 Wi-Fi 直连未在规定时间内就绪。");
        if (!string.Equals(MainWindow.Text(credentials, "fingerprint"), session.Fingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("直连服务身份与 BLE 加密会话不一致。");
        var addresses = credentials.GetProperty("addresses").EnumerateArray().Select(v => v.GetString()).ToArray();
        if (addresses.Length != 1 || !IPAddress.TryParse(addresses[0], out var owner) || owner.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new InvalidOperationException("对方未提供唯一的直连服务地址。");
        uint? internetBefore = null;
        internetBefore = NativeWifi.WiredInternetInterface();
        Evidence = new { phoneGroupReady = true, transport = MainWindow.Text(credentials, "transport"),
            ownerAddress = owner.ToString(), frequencyMhz = MainWindow.Number(credentials, "frequencyMhz"), internetInterfaceBefore = internetBefore };
        progress("正在自动加入临时 Wi-Fi 直连…");
        using (StageTiming.Measure(timingDirectory, "wlan_join"))
        await wifi.JoinAsync(MainWindow.Text(credentials, "ssid"), MainWindow.Text(credentials, "password"), MainWindow.Text(credentials, "securityType"), CancellationToken.None);
        if (internetBefore.HasValue && NativeWifi.InternetInterface() != internetBefore.Value)
            throw new InvalidOperationException("临时 Wi-Fi 改变了原上网出口，已停止传输并恢复；没有修改有线网卡。");
        using var addressTiming = StageTiming.Measure(timingDirectory, "address_and_tls");
        var readyUntil = DateTime.UtcNow.AddSeconds(15);
        Exception? last = null;
        while (DateTime.UtcNow < readyUntil)
        {
            if (internetBefore.HasValue && NativeWifi.InternetInterface() != internetBefore.Value)
                throw new InvalidOperationException("上网出口发生变化，已停止直连传输并恢复原连接。");
            try
            {
                string local = wifi.DirectAddress(owner);
                Evidence = new { phoneGroupReady = true, transport = MainWindow.Text(credentials, "transport"),
                    ownerAddress = owner.ToString(), localAddress = local, frequencyMhz = MainWindow.Number(credentials, "frequencyMhz"),
                    internetInterfaceBefore = internetBefore, internetInterfaceAfter = internetBefore.HasValue ? NativeWifi.InternetInterface() : (uint?)null };
                await Task.Run(() => core.Call(new { op = "direct_bind", address = local }));
                var probe = await Task.Run(() => core.Call(new { op = "probe", host = owner.ToString(), port = (int)MainWindow.Number(credentials, "port") }));
                var peer = probe.GetProperty("peer");
                if (!string.Equals(MainWindow.Text(peer, "fingerprint"), session.Fingerprint, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("直连端点证书不匹配。");
                progress("临时 Wi-Fi 已就绪 · 文件通道绑定直连网卡");
                return new Peer(MainWindow.Text(peer, "id"), session.Name, owner.ToString(), (int)MainWindow.Number(peer, "port"), session.Fingerprint, true);
            }
            catch (Exception ex) { last = ex; await Task.Delay(300); }
        }
        throw new InvalidOperationException("临时网络已加入，但直连数据服务未能核验。", last);
    }
}
