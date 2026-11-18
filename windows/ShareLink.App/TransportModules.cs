using System.IO;
using System.Text.Json;

namespace ShareLink;

internal interface ITransportModule { string Id { get; } }

internal sealed class TransportModules
{
    private readonly Dictionary<string, ITransportModule> loaded = [];
    internal bool LanEnabled => loaded.ContainsKey("lan");
    internal NearbyDirectTransportModule? Direct => loaded.GetValueOrDefault("nearby-direct") as NearbyDirectTransportModule;
    internal string[] Ids => loaded.Keys.ToArray();

    internal static TransportModules Load(bool localDiagnostic = false)
    {
        var modules = new TransportModules();
        string path = Path.Combine(AppContext.BaseDirectory, "transport-modules.json");
        string[] ids = localDiagnostic ? ["lan"] : File.Exists(path)
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(path)) ?? throw new InvalidOperationException("传输模块配置无效。")
            : ["nearby-direct"];
        foreach (string id in ids.Distinct())
            modules.loaded.Add(id, id switch {
                "lan" => new LanTransportModule(),
                "nearby-direct" => new NearbyDirectTransportModule(),
                _ => throw new InvalidOperationException("未知传输模块：" + id)
            });
        return modules;
    }
}
