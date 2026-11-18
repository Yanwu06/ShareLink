using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using ShareLink.BrowserSharing;
namespace ShareLink.BrowserShareProbe;
internal static class LargeCapacityChecks
{
    internal static async Task RunAsync(string output)
    {
        var checks = new List<object>();
        void Check(string name, bool passed) { if (!passed) throw new Exception(name); checks.Add(new { name, passed }); }
        const long largeSize = 41L * 1024 * 1024 * 1024 + 97, markerOffset = 40L * 1024 * 1024 * 1024 + 13;
        string source = Path.Combine(output, "generated-41GiB-sparse.bin"), received = Path.Combine(output, "received");
        Directory.CreateDirectory(received);
        using (File.Create(source)) { }
        var start = new ProcessStartInfo("fsutil.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "sparse", "setflag", source }) start.ArgumentList.Add(arg);
        using (var process = Process.Start(start)!) { string stdout = await process.StandardOutput.ReadToEndAsync(), stderr = await process.StandardError.ReadToEndAsync(); await process.WaitForExitAsync(); File.WriteAllText(Path.Combine(output, "sparse-creation.txt"), stdout + stderr); Check("generated fixture marked sparse before extending", process.ExitCode == 0 && File.GetAttributes(source).HasFlag(FileAttributes.SparseFile)); }
        byte[] marker = Encoding.ASCII.GetBytes("ShareLink above 40GiB range marker");
        using (var file = new FileStream(source, FileMode.Open, FileAccess.Write)) { file.SetLength(largeSize); file.Position = markerOffset; file.Write(marker); }
        await using var server = new BrowserFileServer([source], output, true);
        await server.StartAsync(IPAddress.Loopback, 0, CancellationToken.None); server.EnableUploads(received);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}/"), Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.Add("X-ShareLink-Session", server.SessionId);
        using var manifest = JsonDocument.Parse(await client.GetStringAsync("manifest.json"));
        string token = manifest.RootElement.GetProperty("uploadToken").GetString()!, id = manifest.RootElement.GetProperty("files")[0].GetProperty("id").GetString()!;
        Check("manifest preserves actual file length above 40GiB", manifest.RootElement.GetProperty("files")[0].GetProperty("size").GetInt64() == largeSize);
        using (var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "files/" + id))) Check("download HEAD preserves 64-bit Content-Length", head.IsSuccessStatusCode && head.Content.Headers.ContentLength == largeSize);
        using (var request = new HttpRequestMessage(HttpMethod.Get, "files/" + id)) {
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(markerOffset, markerOffset + marker.Length - 1);
            using var response = await client.SendAsync(request);
            Check("download reads correct bytes beyond 40GiB", response.StatusCode == HttpStatusCode.PartialContent && (await response.Content.ReadAsByteArrayAsync()).SequenceEqual(marker));
            Check("range reports full 64-bit file length", response.Content.Headers.ContentRange?.Length == largeSize && response.Content.Headers.ContentRange.From == markerOffset);
        }
        foreach (var entry in new[] { (Name: "41GiB-upload.bin", Size: largeSize), (Name: "above-old-8TiB-upload.bin", Size: 8L * 1024 * 1024 * 1024 * 1024 + 1), (Name: "int64-max-upload.bin", Size: long.MaxValue) }) {
            using var socket = new TcpClient(); await socket.ConnectAsync(IPAddress.Loopback, server.Port); using var stream = socket.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /upload HTTP/1.1\r\nHost: 127.0.0.1:{server.Port}\r\nX-ShareLink-Session: {server.SessionId}\r\nX-ShareLink-Upload: {token}\r\nX-ShareLink-Filename: {entry.Name}\r\nContent-Length: {entry.Size}\r\nConnection: close\r\n\r\n")); await stream.WriteAsync(new byte[8192]);
            DownloadFileStatus? progress = null;
            for (int n = 0; n < 200; n++) { progress = server.Progress.Snapshot().SelectMany(r => r.Files).FirstOrDefault(f => f.Name == entry.Name); if (progress?.Sent == 8192) break; await Task.Delay(10); }
            Check(entry.Name + " admitted to streaming reception", progress is { Active: 1, Complete: false } && progress.Size == entry.Size && progress.Sent == 8192);
            socket.Client.Shutdown(SocketShutdown.Send); using var reader = new StreamReader(stream); await reader.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            for (int n = 0; n < 100 && server.ActiveDownloads > 0; n++) await Task.Delay(10);
            var interrupted = server.Progress.Snapshot().SelectMany(r => r.Files).Single(f => f.Name == entry.Name);
            Check(entry.Name + " interruption removes incomplete file", interrupted.Interrupted && !interrupted.Complete && interrupted.Active == 0 && !File.Exists(Path.Combine(received, entry.Name)) && !Directory.GetFiles(received, "*.part").Any());
        }
        async Task<HttpResponseMessage> Post(string name, long? declared = null) {
            using var request = new HttpRequestMessage(HttpMethod.Post, "upload") { Content = new ByteArrayContent([]) };
            request.Headers.Add("X-ShareLink-Upload", token); request.Headers.Add("X-ShareLink-Filename", name);
            if (declared is long length) request.Content.Headers.ContentLength = length;
            if (declared is not null) request.Headers.ExpectContinue = true;
            return await client.SendAsync(request);
        }
        for (int n = 0; n < 64; n++) { using var response = await Post($"empty-{n}.txt"); Check("committed file slot " + (n + 1), response.IsSuccessStatusCode); }
        using (var response = await Post("over-count.txt")) Check("64-file count limit preserved after huge aborted uploads", response.StatusCode == HttpStatusCode.RequestEntityTooLarge);
        Check("only committed files remain", Directory.GetFiles(received).Length == 64);
        File.WriteAllText(Path.Combine(output, "large-capacity-checks.json"), JsonSerializer.Serialize(new { passed = true, largeSize, markerOffset, perFileUploadMaximum = (long?)null, totalByteQuota = (long?)null, actualLargePayloadTransferred = false, checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
