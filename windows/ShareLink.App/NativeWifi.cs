using System.ComponentModel;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.Json;

namespace ShareLink;

// Native Wi-Fi is deliberately kept separate from BLE and the transfer core. A
// negotiated PHY rate is metadata, never an end-to-end throughput estimate.
internal sealed class NativeWifi(string stateDirectory)
{
    private const string Prefix = "ShareLink-session-";
    private readonly string recoveryPath = Path.Combine(stateDirectory, "network-recovery.json");
    private readonly SemaphoreSlim mutation = new(1, 1);
    public bool HasRecovery => File.Exists(recoveryPath);
    internal string DirectAddress(System.Net.IPAddress owner)
    {
        var saved = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(recoveryPath)) ?? throw new InvalidOperationException("缺少临时网络记录。");
        using var client = new Client();
        var adapter = client.Interfaces().Single(i => i.Id == saved.InterfaceId);
        var current = Read(client.Handle, adapter);
        if (!current.Connected || current.Profile != saved.TemporaryProfile)
            throw new InvalidOperationException("网卡未连接本次临时 Wi-Fi，禁止使用原局域网。");
        var nic = NetworkInterface.GetAllNetworkInterfaces().Single(i => Guid.TryParse(i.Id, out var id) && id == saved.InterfaceId);
        var remote = owner.GetAddressBytes();
        foreach (var ip in nic.GetIPProperties().UnicastAddresses.Where(i => i.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
        {
            var address = ip.Address.GetAddressBytes(); var mask = ip.IPv4Mask.GetAddressBytes();
            if (address[0] != 169 && Enumerable.Range(0, 4).All(i => (address[i] & mask[i]) == (remote[i] & mask[i]))) return ip.Address.ToString();
        }
        throw new InvalidOperationException("直连网卡尚未取得对应网段地址。");
    }

    internal ShareLink.BrowserSharing.JoinedHotspotNetwork JoinedHotspot(string ssid, System.Net.IPAddress owner)
    {
        var saved=JsonSerializer.Deserialize<Recovery>(File.ReadAllText(recoveryPath))??throw new InvalidOperationException("缺少临时网络记录。");
        return new(saved.InterfaceId,saved.TemporaryProfile,ssid,owner);
    }

    internal static IEnumerable<(System.Net.IPAddress Owner, string Local)> ExistingHotspotCandidates()
    {
        var connected = Collect().Interfaces.Where(i => i.Connected).Select(i => i.Id).ToHashSet();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (!Guid.TryParse(nic.Id, out var id) || !connected.Contains(id)) continue;
            var properties = nic.GetIPProperties();
            foreach (var gateway in properties.GatewayAddresses.Select(g => g.Address).Where(g => g.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            foreach (var local in properties.UnicastAddresses.Where(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork))
            {
                byte[] a = local.Address.GetAddressBytes(), b = gateway.GetAddressBytes(), mask = local.IPv4Mask.GetAddressBytes();
                if (a[0] == 169 || System.Net.IPAddress.IsLoopback(local.Address)) continue;
                if (Enumerable.Range(0,4).All(i => (a[i] & mask[i]) == (b[i] & mask[i]))) yield return (gateway, local.Address.ToString());
            }
        }
    }
    public static WifiReport Collect()
    {
        var captured = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            using var client = new Client();
            var interfaces = client.Interfaces().Select(i => Read(client.Handle, i)).ToArray();
            bool wired = NetworkInterface.GetAllNetworkInterfaces().Any(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType == NetworkInterfaceType.Ethernet);
            return new(captured, interfaces, wired, null);
        }
        catch (Exception ex) { return new(captured, [], false, ex.Message); }
    }

    public async Task JoinAsync(string ssid, string password, string securityType, CancellationToken cancellationToken)
    {
        if (Encoding.UTF8.GetByteCount(ssid) is < 1 or > 32 || password.Length is < 8 or > 63)
            throw new InvalidOperationException("手机提供的热点配置无效。");
        string authentication = securityType switch { "wpa2-psk" or "wpa3-transition" => "WPA2PSK", "wpa3-sae" => "WPA3SAE", _ => throw new InvalidOperationException("热点安全类型未知或不支持，已保留原网络。") };
        await mutation.WaitAsync(cancellationToken);
        try
        {
            if (HasRecovery) await RestoreInternalAsync(cancellationToken);
            using var client = new Client();
            var adapter = client.Interfaces().OrderBy(i => i.State == 1).FirstOrDefault()
                ?? throw new InvalidOperationException("没有可用的 Wi-Fi 网卡。");
            var previous = Read(client.Handle, adapter);
            var radio = Radios(client.Handle, adapter.Id);
            if (radio.Any(p => p.Hardware == 2)) throw new InvalidOperationException("Wi-Fi 硬件开关已关闭，请先开启硬件开关。");
            uint[] disabledPhys = radio.Where(p => p.Software == 2).Select(p => p.Index).ToArray();
            if (adapter.State == 1 && previous.Profile.Length == 0)
                throw new InvalidOperationException("系统未允许读取原 Wi-Fi 连接，无法可靠恢复。请在 Windows 设置中允许位置与无线信息访问，或保持当前网络。");
            string profile = Prefix + Guid.NewGuid().ToString("N");
            // Persist before touching profiles. No passwords or profile XML are kept.
            var record = new Recovery(adapter.Id, previous.Profile, previous.Connected, profile, disabledPhys);
            Directory.CreateDirectory(Path.GetDirectoryName(recoveryPath)!);
            WriteRecovery(record);
            foreach (uint phy in disabledPhys) SetRadio(client.Handle, adapter.Id, phy, 1);
            if (disabledPhys.Length > 0)
            {
                var radioUntil = DateTime.UtcNow.AddSeconds(5);
                while (DateTime.UtcNow < radioUntil && Radios(client.Handle, adapter.Id).Any(p => p.Software == 2)) await Task.Delay(200, cancellationToken);
            }
            // A freshly created peer hotspot may not be in Windows' scan cache yet.
            // Discover only the BLE-negotiated SSID before attempting association.
            using (StageTiming.Measure(stateDirectory, "wlan_scan"))
                await WaitForHotspotAsync(client.Handle, adapter.Id, ssid, cancellationToken, () => StageTiming.Measure(stateDirectory, "wlan_scan_busy").Dispose());
            string safeName = SecurityElement.Escape(profile)!, safeSsid = SecurityElement.Escape(ssid)!, safePassword = SecurityElement.Escape(password)!;
            string hex = Convert.ToHexString(Encoding.UTF8.GetBytes(ssid));
            // WPA3SAE is an official profile value. Older Windows or adapters can
            // reject it; that failure follows the same persistent restore path.
            string xml = $"<?xml version=\"1.0\"?><WLANProfile xmlns=\"http://www.microsoft.com/networking/WLAN/profile/v1\"><name>{safeName}</name><SSIDConfig><SSID><hex>{hex}</hex><name>{safeSsid}</name></SSID></SSIDConfig><connectionType>ESS</connectionType><connectionMode>manual</connectionMode><MSM><security><authEncryption><authentication>{authentication}</authentication><encryption>AES</encryption><useOneX>false</useOneX></authEncryption><sharedKey><keyType>passPhrase</keyType><protected>false</protected><keyMaterial>{safePassword}</keyMaterial></sharedKey></security></MSM></WLANProfile>";
            var id = adapter.Id;
            Check(WlanSetProfile(client.Handle, ref id, 2, xml, null, false, IntPtr.Zero, out var reason), $"创建临时网络配置失败（原因 {reason}）");
            Connect(client.Handle, id, profile);
            await WaitForProfile(client.Handle, adapter, profile, cancellationToken);
        }
        catch (Exception ex)
        {
            try { await RestoreInternalAsync(CancellationToken.None); } catch { /* Keep the recovery record for the next launch. */ }
            if (securityType == "wpa3-sae") throw new InvalidOperationException("手机热点使用 WPA3；当前 Windows 或无线网卡可能不支持。已尝试恢复原网络，请使用双方兼容的网络。" + "\n" + ex.Message, ex);
            throw;
        }
        finally { mutation.Release(); }
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        await mutation.WaitAsync(cancellationToken);
        try { await RestoreInternalAsync(cancellationToken); }
        finally { mutation.Release(); }
    }

    private async Task RestoreInternalAsync(CancellationToken cancellationToken)
    {
        if (!HasRecovery) return;
        var saved = JsonSerializer.Deserialize<Recovery>(File.ReadAllText(recoveryPath)) ?? throw new InvalidOperationException("网络恢复记录损坏，请在系统中重新连接原 Wi-Fi。");
        // Never delete a user profile, even if a recovery file was modified.
        if (!saved.TemporaryProfile.StartsWith(Prefix, StringComparison.Ordinal) || saved.TemporaryProfile.Length != Prefix.Length + 32)
            throw new InvalidOperationException("网络恢复记录不属于 ShareLink 临时配置。");
        using var client = new Client();
        var adapter = client.Interfaces().FirstOrDefault(i => i.Id == saved.InterfaceId) ?? throw new InvalidOperationException("原 Wi-Fi 网卡暂不可用，恢复记录已保留。");
        var current = Read(client.Handle, adapter);
        var id = saved.InterfaceId;
        // If the user has manually chosen another network, preserve that choice.
        if (current.Profile == saved.TemporaryProfile || !current.Connected)
        {
            if (saved.WasConnected && saved.PreviousProfile.Length > 0)
            {
                Connect(client.Handle, id, saved.PreviousProfile);
                await WaitForProfile(client.Handle, adapter, saved.PreviousProfile, cancellationToken);
            }
            else Check(WlanDisconnect(client.Handle, ref id, IntPtr.Zero), "断开临时网络失败");
            foreach (uint phy in saved.DisabledPhys ?? []) SetRadio(client.Handle, id, phy, 2);
        }
        var deletion = WlanDeleteProfile(client.Handle, ref id, saved.TemporaryProfile, IntPtr.Zero);
        if (deletion != 0 && deletion != 1168) Check(deletion, "清理临时网络失败");
        File.Delete(recoveryPath);
    }

    private void WriteRecovery(Recovery record)
    {
        string pending = recoveryPath + ".tmp";
        using (var stream = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, record);
            stream.Flush(true);
        }
        File.Move(pending, recoveryPath, true);
    }

    private static async Task WaitForHotspotAsync(IntPtr client, Guid id, string ssid, CancellationToken token, Action onScanBusy)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(ssid);
        var requested = Marshal.AllocHGlobal(36);
        try {
            Marshal.Copy(new byte[36], 0, requested, 36);
            Marshal.WriteInt32(requested, encoded.Length); Marshal.Copy(encoded, 0, IntPtr.Add(requested, 4), encoded.Length);
            await HotspotVisibility.WaitAsync(
                () => Task.Run(() => { var scanId = id; Check(WlanScan(client, ref scanId, requested, IntPtr.Zero, IntPtr.Zero), "无法扫描本次临时热点"); }),
                () => Task.Run(() => {
                    var scanId = id;
                    Check(WlanGetNetworkBssList(client, ref scanId, requested, 1, true, IntPtr.Zero, out var list), "无法查询本次临时热点");
                    try { return list != IntPtr.Zero && Marshal.ReadInt32(list) >= 8 && Marshal.ReadInt32(list, 4) > 0; }
                    finally { if (list != IntPtr.Zero) WlanFreeMemory(list); }
                }), Task.Delay, token, onScanBusy);
        } finally { Marshal.FreeHGlobal(requested); }
    }

    private static async Task WaitForProfile(IntPtr handle, Adapter adapter, string profile, CancellationToken token)
    {
        var deadline = DateTime.UtcNow.AddSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();
            var info = Read(handle, adapter);
            if (info.Connected && info.Profile == profile) return;
            await Task.Delay(350, token);
        }
        throw new TimeoutException("Wi-Fi 连接未在 25 秒内完成。请检查无线权限、网卡和手机热点。");
    }

    private static void Connect(IntPtr client, Guid id, string profile)
    {
        var parameters = new ConnectionParameters { Mode = 0, Profile = profile, BssType = 1 };
        Check(WlanConnect(client, ref id, ref parameters, IntPtr.Zero), "连接 Wi-Fi 失败");
    }

    private static WifiInterface Read(IntPtr handle, Adapter adapter)
    {
        var id = adapter.Id;
        string[] supported = [];
        if (WlanGetInterfaceCapability(handle, ref id, IntPtr.Zero, out var capability) == 0)
        {
            try
            {
                int count = Math.Clamp(Marshal.ReadInt32(capability, 16), 0, 64);
                supported = Enumerable.Range(0, count).Select(n => Standard(Marshal.ReadInt32(capability, 20 + n * 4))).Distinct().ToArray();
            }
            finally { WlanFreeMemory(capability); }
        }
        if (WlanQueryInterface(handle, ref id, 7, IntPtr.Zero, out var size, out var data, out _) != 0)
            return new(id, adapter.Description, adapter.State == 1, "", "unknown", null, null, null, null, supported);
        try
        {
            if (size < 604) throw new InvalidOperationException("系统返回的 Wi-Fi 信息不完整。");
            bool connected = Marshal.ReadInt32(data) == 1;
            string profile = Marshal.PtrToStringUni(IntPtr.Add(data, 8), 256)!.TrimEnd('\0');
            int phy = Marshal.ReadInt32(data, 568);
            uint quality = (uint)Marshal.ReadInt32(data, 576), rx = (uint)Marshal.ReadInt32(data, 580), tx = (uint)Marshal.ReadInt32(data, 584);
            var bssid = new byte[6]; Marshal.Copy(IntPtr.Add(data, 560), bssid, 0, 6);
            double? frequency = null;
            if (WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out var bss) == 0)
            {
                try
                {
                    int total = Marshal.ReadInt32(bss), count = Math.Clamp(Marshal.ReadInt32(bss, 4), 0, 4096);
                    for (int n = 0; n < count && 8 + (n + 1) * 360 <= total; n++)
                    {
                        var entry = IntPtr.Add(bss, 8 + n * 360);
                        var address = new byte[6]; Marshal.Copy(IntPtr.Add(entry, 40), address, 0, 6);
                        if (bssid.SequenceEqual(address)) { frequency = (uint)Marshal.ReadInt32(entry, 92) / 1000.0; break; }
                    }
                }
                finally { WlanFreeMemory(bss); }
            }
            return new(id, adapter.Description, connected, profile, Standard(phy), frequency, tx == 0 ? null : tx / 1000.0, rx == 0 ? null : rx / 1000.0, quality, supported);
        }
        finally { WlanFreeMemory(data); }
    }

    private static string Standard(int phy) => phy switch { 7 => "Wi-Fi 4", 8 => "Wi-Fi 5", 10 => "Wi-Fi 6", 11 => "Wi-Fi 7", 4 => "802.11a", 5 => "802.11b", 6 => "802.11g", _ => "unknown" };
    private static void Check(uint result, string message) { if (result != 0) throw new Win32Exception((int)result, message + $"（系统代码 {result}）"); }
    private record Recovery(Guid InterfaceId, string PreviousProfile, bool WasConnected, string TemporaryProfile, uint[]? DisabledPhys = null);
    [StructLayout(LayoutKind.Sequential)]
    private struct PhyRadio { public uint Index; public int Software; public int Hardware; }
    private static PhyRadio[] Radios(IntPtr client, Guid id)
    {
        Check(WlanQueryInterface(client, ref id, 4, IntPtr.Zero, out var size, out var data, out _), "无法读取 Wi-Fi 开关状态");
        try
        {
            int count = Marshal.ReadInt32(data);
            if (count is < 0 or > 64 || size < 4 + count * 12) throw new InvalidOperationException("Wi-Fi 开关状态无效。");
            return Enumerable.Range(0, count).Select(n => Marshal.PtrToStructure<PhyRadio>(IntPtr.Add(data, 4 + n * 12))).ToArray();
        }
        finally { WlanFreeMemory(data); }
    }
    private static void SetRadio(IntPtr client, Guid id, uint phy, int state)
    {
        var radio = new PhyRadio { Index = phy, Software = state, Hardware = 0 };
        Check(WlanSetInterface(client, ref id, 4, 12, ref radio, IntPtr.Zero), "设置 Wi-Fi 软件开关失败");
    }

    // Queries the chosen IPv4 internet interface without sending traffic or
    // editing metrics/routes. A direct link must not replace the wired exit.
    internal static uint? WiredInternetInterface()
    {
        try {
            uint index = InternetInterface();
            return NetworkInterface.GetAllNetworkInterfaces().Any(n => n.OperationalStatus == OperationalStatus.Up &&
                n.NetworkInterfaceType == NetworkInterfaceType.Ethernet && n.GetIPProperties().GetIPv4Properties()?.Index == index) ? index : null;
        } catch (Win32Exception) { return null; }
    }
    internal static uint InternetInterface()
    {
        var destination = new byte[16]; destination[0] = 2;
        destination[4] = 1; destination[5] = 1; destination[6] = 1; destination[7] = 1;
        Check(GetBestInterfaceEx(destination, out uint index), "无法检查上网出口");
        return index;
    }
    private record Adapter(Guid Id, string Description, int State);
    private sealed class Client : IDisposable
    {
        public IntPtr Handle { get; }
        public Client() { Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle), "无法访问 Wi-Fi 服务"); Handle = handle; }
        public Adapter[] Interfaces()
        {
            Check(WlanEnumInterfaces(Handle, IntPtr.Zero, out var list), "无法枚举 Wi-Fi 网卡");
            try
            {
                int count = Math.Clamp(Marshal.ReadInt32(list), 0, 64);
                return Enumerable.Range(0, count).Select(n =>
                {
                    var entry = IntPtr.Add(list, 8 + n * 532);
                    return new Adapter(Marshal.PtrToStructure<Guid>(entry), Marshal.PtrToStringUni(IntPtr.Add(entry, 16), 256)!.TrimEnd('\0'), Marshal.ReadInt32(entry, 528));
                }).ToArray();
            }
            finally { WlanFreeMemory(list); }
        }
        public void Dispose() => WlanCloseHandle(Handle, IntPtr.Zero);
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ConnectionParameters { public int Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile; public IntPtr Ssid, DesiredBssidList; public int BssType; public uint Flags; }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr client);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr client, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr client, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")] private static extern uint WlanQueryInterface(IntPtr client, ref Guid id, int opcode, IntPtr reserved, out uint size, out IntPtr data, out int type);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetInterfaceCapability(IntPtr client, ref Guid id, IntPtr reserved, out IntPtr capability);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr client, ref Guid id, IntPtr ssid, IntPtr ieData, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetNetworkBssList(IntPtr client, ref Guid id, IntPtr ssid, int bssType, [MarshalAs(UnmanagedType.Bool)] bool security, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanSetProfile(IntPtr client, ref Guid id, uint flags, string xml, string? security, [MarshalAs(UnmanagedType.Bool)] bool overwrite, IntPtr reserved, out uint reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanDeleteProfile(IntPtr client, ref Guid id, string profile, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanConnect(IntPtr client, ref Guid id, ref ConnectionParameters parameters, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanDisconnect(IntPtr client, ref Guid id, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanSetInterface(IntPtr client, ref Guid id, int opcode, uint size, ref PhyRadio radio, IntPtr reserved);
    [DllImport("iphlpapi.dll")] private static extern uint GetBestInterfaceEx(byte[] destination, out uint interfaceIndex);
}

internal record WifiInterface(Guid Id, string Description, bool Connected, string Profile, string Standard, double? FrequencyMhz, double? TxMbps, double? RxMbps, uint? Quality, string[] SupportedStandards)
{
    public string Band => FrequencyMhz is null ? "unknown" : FrequencyMhz < 3000 ? "2.4 GHz" : FrequencyMhz < 5925 ? "5 GHz" : "6 GHz";
    public string Display => !Connected ? "Wi-Fi 未连接" : $"{Standard.Replace("unknown", "标准未知")} · {Band.Replace("unknown", "频段未知")} · 收/发 {RxMbps?.ToString("0") ?? "未知"}/{TxMbps?.ToString("0") ?? "未知"} Mbps";
}
internal record WifiReport(long CapturedAtMs, WifiInterface[] Interfaces, bool WiredAvailable, string? Error)
{
    public WifiInterface? Connected => Interfaces.FirstOrDefault(i => i.Connected);
    public string Display => Connected?.Display ?? (WiredAvailable ? "有线网络可用 · 路径速度需要双方实测" : Error is not null ? "Wi-Fi 信息未知 · 系统权限或服务不可用" : "未连接 Wi-Fi · 可通过蓝牙发现手机");
    public object Payload => new { platform = "windows", capturedAtMs = CapturedAtMs, current = new { connected = Connected is not null, transport = Connected is not null ? "wifi" : WiredAvailable ? "ethernet" : "unknown", standard = Connected?.Standard ?? "unknown", frequencyMhz = (object?)Connected?.FrequencyMhz ?? "unknown", band = Connected?.Band ?? "unknown", txLinkMbps = (object?)Connected?.TxMbps ?? "unknown", rxLinkMbps = (object?)Connected?.RxMbps ?? "unknown", source = "Native-WLAN" }, interfaces = Interfaces.Select(i => new { id = i.Id, description = i.Description, connected = i.Connected, frequencyMhz = (object?)i.FrequencyMhz ?? "unknown", band = i.Band, standards = i.SupportedStandards }), capabilities = new { sta = new { standards = Interfaces.SelectMany(i => i.SupportedStandards).Distinct().ToArray(), bands = "unknown", source = "WlanGetInterfaceCapability" }, ap = new { available = false, standard = "unknown", bands = "unknown" } }, wiredAvailable = WiredAvailable, error = Error };
}
