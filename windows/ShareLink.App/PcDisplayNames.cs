using System.IO;
using System.Text.Json;
using Windows.Devices.Bluetooth;
namespace ShareLink;
internal static class PcDisplayNames
{
    private const string FileName="nearby-device-names.json";
    internal static bool Valid(string fingerprint,string name)=>fingerprint.Length==64&&fingerprint.All(Uri.IsHexDigit)&&PcDeviceName.Valid(name);
    internal static Dictionary<string,string> Read(string directory)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        try {
            string path=Path.Combine(directory,FileName);
            if(!File.Exists(path)||new FileInfo(path).Length>65536)return result;
            var stored=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(path));
            if(stored is not null)foreach(var entry in stored)if(result.Count<64&&entry.Key is not null&&entry.Value is not null&&Valid(entry.Key,entry.Value))result[entry.Key]=entry.Value;
        } catch { /* A display cache is optional and never changes the trusted peer identity. */ }
        return result;
    }
    internal static void Save(string directory,Dictionary<string,string> authenticatedNames)=>
        DesktopPreferences.Save(directory,FileName,authenticatedNames.Where(e=>Valid(e.Key,e.Value)).Take(64).ToDictionary(e=>e.Key,e=>e.Value,StringComparer.OrdinalIgnoreCase));
    internal static async Task<string[]> ChecksAsync(string output)
    {
        var checks=new List<string>();
        void Check(string name,bool passed){if(!passed)throw new InvalidOperationException(name);checks.Add(name);}
        string fingerprint="0102030405060708"+new string('a',48),other="1112131415161718"+new string('b',48);
        string directory=Path.Combine(output,"display-name-cache");
        Save(directory,new(){{fingerprint,"Verified Chinese 电脑"}});var stored=Read(directory);
        Check("confirmed display name persists without replacing device identity",stored.Count==1&&stored[fingerprint]=="Verified Chinese 电脑");
        int reads=0;long now=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using(var scanner=new PcCheckDiscovery(_=>{reads++;return Task.FromResult<string?>("Fresh name");})) {
            foreach(var item in stored)scanner.RememberName(PcServiceIdentity.Hint(PcServiceIdentity.Create(item.Key)),item.Value);
            scanner.Record(1,"",-40,now,BluetoothAddressType.Public,PcServiceIdentity.Create(fingerprint));
            Check("known peer displays its name immediately before any Bluetooth read",reads==0&&scanner.Snapshot().Single().Name=="Verified Chinese 电脑");
            await scanner.RefreshNamesAsync();
            Check("background metadata can refresh cached name",scanner.Snapshot().Single().Name=="Fresh name");
            scanner.SnapshotAt(now+7000);
            scanner.Record(2,"",-40,now,BluetoothAddressType.Random,PcServiceIdentity.Create(fingerprint));
            Check("device rediscovery retains resolved name across rotating endpoints",scanner.Snapshot().Single().Name=="Fresh name"&&scanner.Snapshot().Single().Address==2);
            scanner.Record(3,"",-40,now,BluetoothAddressType.Public,PcServiceIdentity.Create(other));
            Check("cached name is not assigned to another device identity",scanner.Snapshot().Single(p=>p.Address==3).Name!="Fresh name");
        }
        long ticks=1000;reads=0;
        using(var scanner=new PcCheckDiscovery(_=>Task.FromResult<string?>(++reads==1?null:"Recovered name"),()=>ticks)) {
            scanner.Record(1,"",-40,now,BluetoothAddressType.Public,PcServiceIdentity.Create(fingerprint));
            await scanner.RefreshNamesAsync();ticks=2999;await scanner.RefreshNamesAsync();
            Check("failed metadata read uses a bounded short retry delay",reads==1);
            ticks=3000;scanner.Record(2,"",-40,now,BluetoothAddressType.Random,PcServiceIdentity.Create(fingerprint));await scanner.RefreshNamesAsync();
            Check("failed read retries after two seconds using current endpoint",reads==2&&scanner.Snapshot().Single().Name=="Recovered name");
            ticks=4000;await scanner.RefreshNamesAsync();Check("successful name lookup still avoids continuous connections",reads==2);
        }
        reads=0;
        using(var scanner=new PcCheckDiscovery(_=>{reads++;return Task.FromResult<string?>("Fixture");}){CanReadName=()=>false}) {
            scanner.Record(1,"",-40,now,BluetoothAddressType.Public,PcServiceIdentity.Create(fingerprint));await scanner.RefreshNamesAsync();
            Check("name lookup pauses during pairing or transfer",reads==0);
        }
        File.WriteAllText(Path.Combine(directory,FileName),"broken generated fixture");
        Check("bad optional name cache cannot block startup",Read(directory).Count==0);
        Check("invalid names and fingerprints cannot enter display cache",!Valid("bad","Name")&&!Valid(fingerprint,"bad\nname"));
        return checks.ToArray();
    }
}
