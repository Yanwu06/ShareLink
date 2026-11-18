using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace ShareLink;

internal sealed class StageTiming(string directory, string stage) : IDisposable
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private bool disposed;
    private static readonly object gate = new();
    internal static StageTiming Measure(string directory, string stage) => new(directory, stage);
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        // Fixed stage names only: no SSIDs, credentials, keys or user file paths.
        try
        {
            lock (gate)
            {
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "stage-timings.jsonl");
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, JsonSerializer.Serialize(new { stage, durationMs = clock.ElapsedMilliseconds, at = DateTimeOffset.UtcNow }) + Environment.NewLine);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
