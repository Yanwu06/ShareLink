using System.Text;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
namespace ShareLink;
internal record PcPublicMetadata(string Fingerprint, string Name);
internal static class PcDeviceName
{
    internal static readonly Guid Characteristic = Guid.Parse("7c3d0022-6b4a-4f9d-9a31-534c4e4b0001");
    internal static readonly Guid FingerprintCharacteristic = Guid.Parse("7c3d0023-6b4a-4f9d-9a31-534c4e4b0001");
    internal static bool ValidMetadata(PcPublicMetadata? value) => value is not null && value.Fingerprint.Length == 64 && value.Fingerprint.All(Uri.IsHexDigit) && Valid(value.Name);
    internal static bool Valid(string name) => !string.IsNullOrWhiteSpace(name) && Encoding.UTF8.GetByteCount(name) <= 128 && !name.Any(char.IsControl);
    internal static byte[] Encode(string name) => Valid(name) ? Encoding.UTF8.GetBytes(name) : throw new ArgumentException("设备名称无效");
    internal static string? Decode(byte[] value)
    {
        if (value.Length is 0 or > 128) return null;
        try { string name = new UTF8Encoding(false, true).GetString(value); return Valid(name) ? name : null; }
        catch (DecoderFallbackException) { return null; }
    }
    // The fixed phone invitation service exposes public PC identity/name reads too.
    // This discovers presentation metadata only; it cannot authenticate or start a session.
    internal static Task<PcPublicMetadata?> ReadSharedAsync(NearbyPhone peer, Action<string>? trace = null) => BluetoothWorker.RunAsync(async () => {
        trace?.Invoke("device");
        using var device = await BluetoothWorker.AwaitAsync(BluetoothLEDevice.FromBluetoothAddressAsync(peer.Address, peer.AddressType), TimeSpan.FromSeconds(4));
        if (device is null) { trace?.Invoke("device:null"); return null; }
        trace?.Invoke("services");
        var found = await BluetoothWorker.AwaitAsync(device.GetGattServicesForUuidAsync(NearbyInvitation.ServiceUuid, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
        try {
            trace?.Invoke($"services:{found.Status}:{found.Services.Count}");
            if (found.Status != GattCommunicationStatus.Success || found.Services.Count != 1) return null;
            async Task<byte[]?> Read(Guid id, uint maximum) {
                string label=id==FingerprintCharacteristic?"fingerprint":"name";
                trace?.Invoke(label+":characteristics");
                var chars = await BluetoothWorker.AwaitAsync(found.Services[0].GetCharacteristicsForUuidAsync(id, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
                trace?.Invoke($"{label}:characteristics:{chars.Status}:{chars.Characteristics.Count}");
                if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count != 1) return null;
                trace?.Invoke(label+":read");
                var result = await BluetoothWorker.AwaitAsync(chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
                trace?.Invoke($"{label}:read:{result.Status}:{result.Value?.Length}");
                if (result.Status != GattCommunicationStatus.Success || result.Value is null || result.Value.Length > maximum) return null;
                using var data = DataReader.FromBuffer(result.Value); byte[] bytes = new byte[data.UnconsumedBufferLength]; data.ReadBytes(bytes); return bytes;
            }
            var fingerprint = await Read(FingerprintCharacteristic, 64); var name = await Read(Characteristic, 128);
            if (fingerprint is null || name is null || Decode(name) is not string decoded) return null;
            var metadata = new PcPublicMetadata(Encoding.ASCII.GetString(fingerprint), decoded);
            trace?.Invoke(ValidMetadata(metadata)?"valid":"invalid");
            return ValidMetadata(metadata) ? metadata : null;
        } finally { foreach (var service in found.Services) service.Dispose(); }
    });
    // Public display metadata only. No control write, pairing, hotspot or file access.
    internal static Task<string?> ReadAsync(NearbyPhone peer) => BluetoothWorker.RunAsync(async () => {
        if (peer.ControlService is not Guid id) return null;
        using var device = await BluetoothWorker.AwaitAsync(BluetoothLEDevice.FromBluetoothAddressAsync(peer.Address, peer.AddressType), TimeSpan.FromSeconds(4));
        if (device is null) return null;
        var found = await BluetoothWorker.AwaitAsync(device.GetGattServicesForUuidAsync(id, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
        try {
            if (found.Status != GattCommunicationStatus.Success || found.Services.Count != 1) return null;
            var chars = await BluetoothWorker.AwaitAsync(found.Services[0].GetCharacteristicsForUuidAsync(Characteristic, BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
            if (chars.Status != GattCommunicationStatus.Success || chars.Characteristics.Count != 1) return null;
            var result = await BluetoothWorker.AwaitAsync(chars.Characteristics[0].ReadValueAsync(BluetoothCacheMode.Uncached), TimeSpan.FromSeconds(4));
            if (result.Status != GattCommunicationStatus.Success || result.Value.Length > 128) return null;
            using var reader = DataReader.FromBuffer(result.Value); byte[] bytes = new byte[reader.UnconsumedBufferLength]; reader.ReadBytes(bytes);
            return Decode(bytes);
        } finally { foreach (var service in found.Services) service.Dispose(); }
    });
}
