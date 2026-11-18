using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace ShareLink.BrowserSharing;

// COM Name is a display name. Local persistent IDs are read without changing
// the registry; mutations use NetSecurity's unique Name, never DisplayName.
internal static class GeneratedFirewallBlocks
{
    internal static bool ValidId(string id, string program)
    {
        const string prefix = "TCP Query User{";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            id.Length > prefix.Length + 37 &&
            Guid.TryParseExact(id.Substring(prefix.Length - 1, 38), "B", out _) &&
            id[(prefix.Length + 37)..].Equals(program, StringComparison.OrdinalIgnoreCase);
    }

    internal static KeyValuePair<string,string>[] ReadLocal()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy\FirewallRules")
            ?? throw new IOException("无法读取本地防火墙规则标识。");
        return key.GetValueNames().Select(id => new KeyValuePair<string,string>(id, key.GetValue(id) as string ?? "")).ToArray();
    }

    private static bool Matches(ShareFirewallRule rule, string raw)
    {
        // Automatic Query User rules have no address/port/service/interface
        // restrictions. Unknown fields or ambiguous duplicates fail closed.
        var fields = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw.Split('|').Skip(1).Where(s => s.Length > 0)) {
            int equals = item.IndexOf('=');
            if (equals < 1 || !fields.TryAdd(item[..equals], item[(equals+1)..])) return false;
        }
        string Get(string name) => fields.GetValueOrDefault(name, "");
        if (fields.Keys.Any(k => k is not ("Action" or "Active" or "Dir" or "Protocol" or "Profile" or "App" or "Name" or "Desc"))) return false;
        int profiles = Get("Profile") switch { "Private" => 2, "Public" => 4, "Domain" => 1, "" => BrowserShareFirewall.AllProfiles, _ => 0 };
        return rule.Action == 0 && rule.Direction == 1 && rule.Protocol == 6 && rule.Service.Length == 0 &&
            rule.Ports == "*" && rule.Local == "*" && rule.Remote == "*" && rule.Interfaces.Length == 0 &&
            rule.InterfaceTypes.Equals("All", StringComparison.OrdinalIgnoreCase) &&
            rule.Program.Equals(Get("App"), StringComparison.OrdinalIgnoreCase) && rule.Name == Get("Name") &&
            (rule.Profiles & 7) == (profiles & 7) && Get("Action") == "Block" && Get("Dir") == "In" &&
            Get("Protocol") == "6" && Get("Active").Equals(rule.Enabled ? "TRUE" : "FALSE", StringComparison.OrdinalIgnoreCase);
    }

    internal static ShareFirewallRule[] Bind(IEnumerable<ShareFirewallRule> source, KeyValuePair<string,string>[] local)
    {
        var rules = source.ToArray();
        return rules.Select(rule => {
            if (rule.Action != 0 || rule.Protocol != 6) return rule;
            var matches = local.Where(pair => Matches(rule, pair.Value)).ToArray();
            if (matches.Length != 1 || !ValidId(matches[0].Key, rule.Program) ||
                rules.Count(other => other == rule || Matches(other, matches[0].Value)) != 1) return rule;
            return rule with { InternalId = matches[0].Key };
        }).ToArray();
    }

    internal static void SetEnabled(string id, string program, bool enabled)
    {
        if (!ValidId(id, program)) throw new IOException("拒绝修改非系统自动 TCP 规则。");
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        string script = "$ErrorActionPreference='Stop';" +
            "$r=Get-NetFirewallRule -PolicyStore PersistentStore -Name " + Literal(id) + ";" +
            "$a=$r|Get-NetFirewallApplicationFilter;$p=$r|Get-NetFirewallPortFilter;" +
            "if(@($r).Count -ne 1 -or [string]$r.Direction -ne 'Inbound' -or [string]$r.Action -ne 'Block' " +
            "-or [string]$r.PolicyStoreSourceType -ne 'Local' -or $a.Program -ine " + Literal(program) +
            " -or [string]$p.Protocol -ne 'TCP'){throw 'Rule identity changed'};" +
            "$r|Set-NetFirewallRule -Enabled " + (enabled ? "True" : "False") + ";" +
            "$check=Get-NetFirewallRule -PolicyStore PersistentStore -Name " + Literal(id) + ";" +
            "if([string]$check.Enabled -ne '" + (enabled ? "True" : "False") + "'){throw 'Rule state not saved'}";
        string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var start = new ProcessStartInfo(Path.Combine(system, @"WindowsPowerShell\v1.0\powershell.exe")) {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException("无法配置系统自动网络规则。");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30000)) { process.Kill(true); throw new IOException("配置系统自动网络规则超时。"); }
        if (process.ExitCode != 0) throw new IOException("系统自动 TCP 规则配置失败：" + error.GetAwaiter().GetResult());
        _ = output.GetAwaiter().GetResult();
    }
}
