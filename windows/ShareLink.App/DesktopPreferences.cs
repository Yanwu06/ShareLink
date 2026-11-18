using System.IO;
using System.Text.Json;

namespace ShareLink;

internal record UiPreference(bool SingleMode = false);
internal record HistoryRecordPreference(string[] Hidden);

internal static class DesktopPreferences
{
    internal static T Read<T>(string directory, string name, T fallback) =>
        File.Exists(Path.Combine(directory, name)) ? JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(directory, name))) ?? fallback : fallback;

    internal static void Save<T>(string directory, string name, T value)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name), temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) {
            JsonSerializer.Serialize(stream, value); stream.Flush(true);
        }
        File.Move(temporary, path, true);
    }
}
