using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShareLink;

internal sealed class ReleaseVersion : IComparable<ReleaseVersion>
{
    private readonly int[] numbers;
    private readonly string[] prerelease;
    private ReleaseVersion(int[] numbers, string[] prerelease) { this.numbers = numbers; this.prerelease = prerelease; }
    internal static ReleaseVersion Parse(string text)
    {
        var match = Regex.Match(text.Trim(), @"^[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new FormatException("Invalid release version.");
        var numbers = Enumerable.Range(1, 3).Select(i => int.Parse(match.Groups[i].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : [];
        if (pre.Any(p => IsNumeric(p) && p.Length > 1 && p[0] == '0')) throw new FormatException("Invalid numeric prerelease identifier.");
        return new(numbers, pre);
    }
    internal bool IsPrerelease => prerelease.Length > 0;
    private static bool IsNumeric(string text) => text.All(char.IsAsciiDigit);
    public int CompareTo(ReleaseVersion? other)
    {
        if (other is null) return 1;
        for (int i = 0; i < 3; i++) { int result = numbers[i].CompareTo(other.numbers[i]); if (result != 0) return result; }
        if (prerelease.Length == 0 || other.prerelease.Length == 0) return (prerelease.Length == 0 ? 1 : 0).CompareTo(other.prerelease.Length == 0 ? 1 : 0);
        for (int i = 0; i < Math.Min(prerelease.Length, other.prerelease.Length); i++)
        {
            string a = prerelease[i], b = other.prerelease[i]; bool na = IsNumeric(a), nb = IsNumeric(b);
            int result = na && nb ? BigInteger.Parse(a).CompareTo(BigInteger.Parse(b)) : na != nb ? (na ? -1 : 1) : StringComparer.Ordinal.Compare(a, b);
            if (result != 0) return result;
        }
        return prerelease.Length.CompareTo(other.prerelease.Length);
    }
}

internal enum UpdateStatus { Available, Current, NotPublished, MissingInstaller, RateLimited }
internal sealed record ReleaseUpdate(UpdateStatus Status, string Version = "", string Notes = "", Uri? Download = null);
internal static class UpdateDownloadFlow
{
    internal const string Notice = "即将通过浏览器下载安装包。\n下载完成后请手动运行。";
    internal static async Task OpenAsync(Uri address, Action<string> notify, Func<Task> showNotice, Action<Uri> openBrowser)
    {
        if (!ReleaseUpdateClient.IsInstallerAddress(address)) throw new InvalidOperationException("Invalid installer address.");
        notify(Notice);
        await showNotice();
        openBrowser(address);
    }
}

internal sealed class ReleaseUpdateClient
{
    internal const string Repository = "https://gitee.com/yanwu06/win-sharelink-transfer-release";
    internal const string InstallerName = "ShareLink-Setup-x64.exe";
    internal static readonly Uri Api = new("https://gitee.com/api/v5/repos/yanwu06/win-sharelink-transfer-release/releases/latest");
    internal static readonly Uri ReleasePage = new(Repository + "/releases");
    private static readonly HttpClient SharedHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly HttpClient http;
    private readonly TimeSpan timeout;
    internal ReleaseUpdateClient(HttpClient? http = null, TimeSpan? timeout = null) { this.http = http ?? SharedHttp; this.timeout = timeout ?? TimeSpan.FromSeconds(8); }
    internal static string LocalVersion => typeof(ReleaseUpdateClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? throw new InvalidOperationException("Missing product version.");
    internal static bool IsInstallerAddress(Uri address) => address.IsAbsoluteUri && address.Scheme == "https" && address.Host.Equals("gitee.com", StringComparison.OrdinalIgnoreCase)
        && address.IsDefaultPort && address.UserInfo.Length == 0 && address.Fragment.Length == 0
        && address.AbsolutePath.StartsWith("/yanwu06/win-sharelink-transfer-release/releases/download/", StringComparison.Ordinal)
        && Uri.UnescapeDataString(address.Segments.LastOrDefault() ?? "") == InstallerName;
    internal async Task<ReleaseUpdate> CheckAsync(string localVersion, CancellationToken cancellation = default)
    {
        var local = ReleaseVersion.Parse(localVersion);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellation); budget.CancelAfter(timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, Api);
        request.Headers.Accept.ParseAdd("application/json"); request.Headers.UserAgent.ParseAdd("ShareLink/" + localVersion);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return new(UpdateStatus.NotPublished);
        if ((int)response.StatusCode == 429) return new(UpdateStatus.RateLimited);
        response.EnsureSuccessStatusCode();
        const int limit = 256 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new JsonException("Release response too large.");
        using var input = await response.Content.ReadAsStreamAsync(budget.Token).ConfigureAwait(false);
        using var data = new System.IO.MemoryStream(); var buffer = new byte[8192];
        while (true) { int read = await input.ReadAsync(buffer, budget.Token).ConfigureAwait(false); if (read == 0) break; if (data.Length + read > limit) throw new JsonException("Release response too large."); data.Write(buffer, 0, read); }
        using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 24 });
        var json = document.RootElement;
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String) throw new JsonException("Release has no version.");
        string remote = tag.GetString()!; if (remote.Length > 128) throw new FormatException("Release version too long.");
        var version = ReleaseVersion.Parse(remote);
        // Public releases use the normal latest channel; a prerelease cannot silently replace a stable build.
        if ((version.IsPrerelease || (json.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)) && !local.IsPrerelease) return new(UpdateStatus.Current, remote);
        if (local.CompareTo(version) >= 0) return new(UpdateStatus.Current, remote);
        string notes = json.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString()! : "";
        if (notes.Length > 40000) notes = notes[..40000] + "\n…完整说明请查看发行版页面。";
        var matches = new List<Uri>();
        if (json.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var asset in assets.EnumerateArray())
                if (asset.ValueKind == JsonValueKind.Object && asset.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && name.GetString() == InstallerName
                    && asset.TryGetProperty("browser_download_url", out var url) && url.ValueKind == JsonValueKind.String && Uri.TryCreate(url.GetString(), UriKind.Absolute, out var address) && IsInstallerAddress(address)) matches.Add(address);
        return matches.Count == 1 ? new(UpdateStatus.Available, remote, notes, matches[0]) : new(UpdateStatus.MissingInstaller, remote, notes);
    }
}
