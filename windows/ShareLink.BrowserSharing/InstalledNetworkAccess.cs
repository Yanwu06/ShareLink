using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ShareLink.BrowserSharing;

// Setup invokes this EXE after copying the payload. No caller-supplied program,
// address, port, command or output path is accepted by the elevated entry.
public static class InstalledNetworkAccess
{
    public const string InstallSwitch = "--install-network-access";
    public const string RemoveSwitch = "--remove-installed-network-access";
    internal const string RuleDescription = "ShareLink installed local-network TCP access v1.";
    internal static string RuleName(string program) => "ShareLink-InstalledLocalTcp-" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(program).ToUpperInvariant())))[..24];

    internal static dynamic CreateRule(string program)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwRule", true)!)!;
        rule.Name = RuleName(program);
        rule.Description = RuleDescription;
        rule.ApplicationName = program;
        rule.Protocol = 6;
        // The actual hotspot IPv4 and fallback port are only known at share time.
        // LocalSubnet restricts remote peers without hardcoding either value.
        rule.LocalPorts = "*";
        rule.LocalAddresses = "*";
        rule.RemoteAddresses = "LocalSubnet";
        rule.Direction = 1;
        rule.Action = 1;
        rule.Profiles = BrowserShareFirewall.AllProfiles;
        rule.InterfaceTypes = "All";
        rule.EdgeTraversal = false;
        rule.Enabled = true;
        return rule;
    }

    internal static string[] GeneratedBlocks(ShareFirewallRule[] rules)
    {
        var blocks = rules.Where(r => r.Enabled && r.Direction == 1 && r.Action == 0 &&
            r.Protocol is 6 or 256).ToArray();
        if (blocks.Any(r => r.Service.Length != 0 || !r.Generated))
            throw new ShareFirewallException("已有手动阻止规则，安装器不会修改。");
        return blocks.Select(r => r.Key).Distinct(StringComparer.Ordinal).ToArray();
    }

    internal static void Configure(dynamic policy, string program)
    {
        if ((int)policy.LocalPolicyModifyState != 0)
            throw new ShareFirewallException("系统策略不允许配置本地防火墙规则。");
        foreach (int profile in new[] { 1, 2, 4 })
            if ((bool)policy.FirewallEnabled[profile] && (bool)policy.BlockAllInboundTraffic[profile])
                throw new ShareFirewallException("系统策略阻止所有传入连接。");
        EnsureOwnedName(policy, program);
        ShareFirewallRule[] rules = BrowserShareFirewall.ReadRules(policy, program);
        string[] generated = GeneratedBlocks(rules);
        Action verify = () => {
            ShareFirewallRule[] after = BrowserShareFirewall.ReadRules(policy, program);
            var installed = after.SingleOrDefault(r => r.Name == RuleName(program));
            if (installed is null || !installed.Enabled || installed.Direction != 1 || installed.Action != 1 ||
                installed.Protocol != 6 || installed.Ports != "*" || installed.Local != "*" ||
                !installed.Remote.Equals("LocalSubnet", StringComparison.OrdinalIgnoreCase) ||
                (installed.Profiles & 7) != 7 || installed.InterfaceTypes != "All" ||
                installed.Interfaces.Length != 0 || installed.Service.Length != 0 ||
                after.Any(r => generated.Contains(r.Key) && r.Enabled))
                throw new IOException("Windows 未保存完整网络权限。");
        };
        BrowserShareFirewall.ApplyNamedRule(policy, CreateRule(program), RuleName(program), program, generated, verify);
    }

    private static void EnsureOwnedName(dynamic policy, string program)
    {
        dynamic existing;
        try { existing = policy.Rules.Item(RuleName(program)); }
        catch (Exception ex) when (BrowserShareFirewall.MissingRule(ex)) { return; }
        if (!string.Equals((string)existing.ApplicationName, program, StringComparison.OrdinalIgnoreCase) ||
            (string)existing.Description != RuleDescription)
            throw new IOException("网络规则名称已被其他规则占用。");
    }

    internal static void Remove(dynamic policy, string program)
    {
        EnsureOwnedName(policy, program);
        try { _ = policy.Rules.Item(RuleName(program)); }
        catch (Exception ex) when (BrowserShareFirewall.MissingRule(ex)) { return; }
        policy.Rules.Remove(RuleName(program));
    }

    public static int RunHelper(string[] arguments)
    {
        if (arguments.Length != 1 || arguments[0] is not (InstallSwitch or RemoveSwitch)) return 2;
        try {
            if (!BrowserShareFirewall.IsAdministrator()) return 1;
            string program = Path.GetFullPath(Environment.ProcessPath ?? throw new IOException("无法读取程序位置。"));
            dynamic policy = BrowserShareFirewall.Policy();
            if (arguments[0] == RemoveSwitch) Remove(policy, program);
            else Configure(policy, program);
            return 0;
        }
        catch (ShareFirewallException) { return 3; }
        catch (Exception ex) { return ex.HResult; }
    }
}
