namespace ShareLink.BrowserSharing;
internal static class BrowserPageAssets
{
    internal const string SupportDeveloperUrl = "https://ifdian.net/a/yanwu06/plan";
    internal static readonly string Style = Read("browser-page.css");
    internal static readonly string Footer = Read("browser-footer.html");
    internal static readonly string CopyScript = Read("browser-copy.js");
    internal static readonly string UploadScript = Read("browser-upload.js");
    private static string Read(string name)
    {
        using var stream = typeof(BrowserPageAssets).Assembly.GetManifestResourceStream("ShareLink.BrowserSharing." + name)
            ?? throw new InvalidOperationException("Missing download page resource: " + name);
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }
}
