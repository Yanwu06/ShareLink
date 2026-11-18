using System.Globalization;

namespace ShareLink.BrowserSharing;

public static class ShareText
{
    public static string Size(double bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (bytes >= 1024 && unit < units.Length - 1) { bytes /= 1024; unit++; }
        return bytes.ToString(unit == 0 ? "0" : bytes < 10 ? "0.00" : "0.0", CultureInfo.InvariantCulture) + " " + units[unit];
    }
}
