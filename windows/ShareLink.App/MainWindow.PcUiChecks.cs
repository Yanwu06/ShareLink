using System.IO;
using ShareLink.BrowserSharing;
using Windows.Devices.Bluetooth;
namespace ShareLink;
public partial class MainWindow
{
    internal static async Task<string[]> CheckPcUiAsync(string output)
    {
        var checks = new List<string>();
        void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); checks.Add(name); }
        // Never show/initialize this normal desktop fixture: no watcher or network starts.
        var window = new MainWindow(Path.Combine(output, "ui-fixture"), desktopTest: true);
        Check("ordinary desktop uses the integrated PC path without the native-test flag", !window.diagnostic && !window.pcNativeTest && !window.modules.LanEnabled);
        using var discovery = new PcCheckDiscovery(); window.pcDiscovery = discovery;
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Guid a = PcServiceIdentity.Create("0102030405060708" + new string('a', 48));
        Guid b = PcServiceIdentity.Create("1112131415161718" + new string('b', 48));
        discovery.Record(1, "Generated peer A", -50, now, BluetoothAddressType.Public, a);
        discovery.Record(2, "Generated peer B", -40, now, BluetoothAddressType.Public, b);
        window.RefreshNearbyList();
        var original = (NearbyPhone[])window.NearbyList.ItemsSource;
        for (int i = 0; i < 40; i++) {
            discovery.Record((ulong)(10 + i), "", (short)(-20 - i), now, BluetoothAddressType.Random, a);
            discovery.Record(2, "Generated peer B", (short)(-60 + i), now, BluetoothAddressType.Public, b);
            window.RefreshNearbyList();
            Check("stable WPF device rows during address/name/signal changes " + i, ReferenceEquals(original, window.NearbyList.ItemsSource));
        }
        var clicked = original.Single(p => p.FingerprintPrefix == PcServiceIdentity.Hint(a));
        var resolved = discovery.Resolve(clicked);
        Check("stable row connects using latest BLE endpoint", clicked.Address == 1 && resolved?.Address == 49 && resolved.AddressType == BluetoothAddressType.Random);
        Check("nameless advertisements retain known peer name", resolved?.Name == "Generated peer A");
        discovery.Record(49, "Generated renamed peer", -40, now, BluetoothAddressType.Random, a);
        window.RefreshNearbyList();
        Check("actual name changes still update displayed row", !ReferenceEquals(original, window.NearbyList.ItemsSource) && ((NearbyPhone[])window.NearbyList.ItemsSource).Any(p => p.Name == "Generated renamed peer"));
        // Exercise the real periodic snapshot path while an async PC refresh is still pending.
        // This core has no LAN discovery or radio; synthetic BLE rows remain visible.
        using (var snapshotCore = new NativeCore(window.CoreConfiguration())) {
            window.core = snapshotCore; window.pcRefreshing = true;
            try {
                for (int i = 0; i < 10; i++) {
                    await window.RefreshState();
                    Check("periodic empty LAN snapshot cannot reveal search hint over BLE rows " + i,
                        window.NoPeers.Visibility == System.Windows.Visibility.Collapsed && window.NoPeersPanel.Visibility == System.Windows.Visibility.Collapsed
                        && ((NearbyPhone[])window.NearbyList.ItemsSource).Length == 2);
                }
                discovery.SnapshotAt(now + 7000);
                await window.RefreshState();
                Check("real device expiry restores the search hint and icon",window.NoPeers.Visibility == System.Windows.Visibility.Visible && window.NoPeersPanel.Visibility == System.Windows.Visibility.Visible);
            } finally { window.core = null; window.pcRefreshing = false; }
        }
        discovery.SnapshotAt(now + 7000); window.RefreshNearbyList();
        Check("expired rows are removed rather than kept stale", ((NearbyPhone[])window.NearbyList.ItemsSource).Length == 0 && discovery.Resolve(clicked) is null);
        checks.AddRange(await BrowserShareView.DiagnosticSharingLifecycleAsync(Path.Combine(output, "share-lifecycle"), (view, active) => {
            window.browserSharing = true; window.browserView = view; window.UpdateShareModeAvailability();
            if (window.BrowserShareButton.IsEnabled == active) throw new InvalidOperationException("Mode button does not match actual share lifecycle");
            if (active) {
                window.SelectShareMode(false);
                if (!window.browserSharing || !ReferenceEquals(window.browserView, view) || !window.StatusMessage.Text.Contains("结束分享"))
                    throw new InvalidOperationException("Active single share changed mode without explicit end");
            }
        }));
        window.browserView = null; window.timer.Stop(); window.pcDiscovery = null;
        return checks.ToArray();
    }
}
