using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace ShareLink;

// Persist intent before canceling active tasks. Native task state remains compatible;
// only matching cancelled rows receive this application's explicit exit reason.
internal sealed class UserExitHistory(string directory)
{
    internal const string Label = "已被用户终止";
    private const string FileName = "user-terminated-tasks.json";
    private HashSet<string> ids = new(DesktopPreferences.Read(directory,FileName,Array.Empty<string>()),StringComparer.Ordinal);
    internal string[] RememberActive(JsonElement snapshot)
    {
        var active=snapshot.GetProperty("transfers").EnumerateArray()
            .Where(t=>MainWindow.Text(t,"state") is not ("completed" or "failed" or "cancelled" or "rejected"))
            .Select(t=>MainWindow.Text(t,"id")).Where(id=>id.Length>0).Distinct(StringComparer.Ordinal).ToArray();
        if(active.All(ids.Contains))return active;
        var next=new HashSet<string>(ids,StringComparer.Ordinal);next.UnionWith(active);
        DesktopPreferences.Save(directory,FileName,next.Order(StringComparer.Ordinal).ToArray());
        ids=next;return active;
    }
    internal JsonElement Apply(JsonElement snapshot)
    {
        if(ids.Count==0||!snapshot.TryGetProperty("transfers",out var transfers)||
            !transfers.EnumerateArray().Any(t=>MainWindow.Text(t,"state")=="cancelled"&&ids.Contains(MainWindow.Text(t,"id"))))return snapshot;
        var copy=JsonNode.Parse(snapshot.GetRawText())!.AsObject();
        foreach(var node in copy["transfers"]!.AsArray()){
            if(node is JsonObject row&&row["state"]?.GetValue<string>()=="cancelled"&&ids.Contains(row["id"]?.GetValue<string>()??""))row["error"]=Label;
        }
        return JsonSerializer.SerializeToElement(copy);
    }
}
