using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using ShareLink.BrowserSharing;

namespace ShareLink.BrowserShareProbe;

internal static class HttpChecks
{
    public static async Task RunAsync(string output)
    {
        List<object> checks = [];
        void Check(string name, bool passed) { if (!passed) throw new Exception(name); lock (checks) checks.Add(new { name, passed }); }
        string path = Path.Combine(output, "测试-quoted_文件.bin");
        byte[] data = RandomNumberGenerator.GetBytes(65536); await File.WriteAllBytesAsync(path, data);
        string empty = Path.Combine(output, "empty.bin"); await File.WriteAllBytesAsync(empty, []);
        await using var server = new BrowserFileServer([path, empty], output, trackLocalRequests:true);
        await server.StartAsync(IPAddress.Loopback, 0, CancellationToken.None);
        var root = new Uri($"http://127.0.0.1:{server.Port}/");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }); client.BaseAddress = root; client.DefaultRequestHeaders.Add("X-ShareLink-Session",server.SessionId);
        string id = server.Files[0].Id;
        // A client can consume the last byte before the server runs its finally block.
        // Wait for that response finalizer before asserting terminal progress.
        async Task WaitForHttpCompletion()
        {
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (server.ActiveDownloads != 0 || server.Progress.Snapshot().Any(r => r.Files.Any(f => f.Active != 0))) {
                if (DateTime.UtcNow >= deadline) throw new Exception("HTTP response finalizer did not settle");
                await Task.Delay(10);
            }
        }
        var landing = await client.GetAsync("/");
        string landingHtml = await landing.Content.ReadAsStringAsync();
        await File.WriteAllTextAsync(Path.Combine(output, "download-page.html"), landingHtml);
        Check("download page has client links and only the supplied support destination", landingHtml.Contains("Windows端：") && landingHtml.Contains("HarmonyOS NEXT端：") && landingHtml.Contains("https://ifdian.net/a/yanwu06/plan") && !landingHtml.Contains("让文件自由流转"));
        Check("HarmonyOS NEXT provides store-search guidance without an empty copy control", landingHtml.Contains("请前往华为应用商店搜索ShareLink下载。") && !landingHtml.Contains("data-copy-value=\"\" disabled"));
        Check("Windows download address and copy control use the verified direct installer URL", landingHtml.Contains("href=\"https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/latest/ShareLink-Setup-x64.exe\"") && landingHtml.Contains("data-copy-value=\"https://gitee.com/yanwu06/win-sharelink-transfer-release/releases/download/latest/ShareLink-Setup-x64.exe\""));
        Check("copy script shares the page CSP nonce", landing.Headers.GetValues("Content-Security-Policy").Single().Contains("script-src 'nonce-") && landingHtml.Contains("execCommand('copy')"));
        Check("opening root immediately lists the selected files", landing.StatusCode == HttpStatusCode.OK && landingHtml.Contains(server.Files[0].Name));
        Check("download page has no pairing form", !landingHtml.Contains("<form") && !landingHtml.Contains("配对码"));
        Check("no session cookie is required or issued", !landing.Headers.Contains("Set-Cookie"));
        Check("direct download works before any other request or cookie", (await client.GetAsync("/files/" + id)).StatusCode == HttpStatusCode.OK);
        Check("removed authorization endpoint is unavailable", (await client.PostAsync("/authorize", new StringContent(""))).StatusCode == HttpStatusCode.NotFound);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", "null");
        Check("privacy-filtered Origin does not prevent direct listing", (await client.GetAsync("/")).StatusCode == HttpStatusCode.OK);
        using var nameBefore = new HttpRequestMessage(HttpMethod.Get, "/"); nameBefore.Headers.Host = "sl.local";
        Check("abandoned local domain is rejected", (int)(await client.SendAsync(nameBefore)).StatusCode == 421);
        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/files/" + id));
        Check("HEAD returns exact size without body", head.Content.Headers.ContentLength == data.Length && (await head.Content.ReadAsByteArrayAsync()).Length == 0);
        Check("Chinese download filename encoded by Content-Disposition", head.Content.Headers.ContentDisposition?.FileNameStar == server.Files[0].Name);
        Check("range support and stable validator", head.Headers.AcceptRanges.Contains("bytes") && head.Headers.ETag is not null);
        var full = await client.GetByteArrayAsync("/files/" + id);
        Check("streamed complete response matches source hash", SHA256.HashData(full).SequenceEqual(SHA256.HashData(data)));
        async Task<byte[]> Range(string range, HttpStatusCode expected)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/files/" + id); request.Headers.TryAddWithoutValidation("Range", range);
            using var result = await client.SendAsync(request);
            Check("range status " + range, result.StatusCode == expected);
            if (expected == HttpStatusCode.PartialContent) Check("range content length " + range, result.Content.Headers.ContentRange?.Length == data.Length);
            return await result.Content.ReadAsByteArrayAsync();
        }
        Check("explicit range bytes correct", (await Range("bytes=13-57", HttpStatusCode.PartialContent)).SequenceEqual(data[13..58]));
        Check("suffix range bytes correct", (await Range("bytes=-29", HttpStatusCode.PartialContent)).SequenceEqual(data[^29..]));
        Check("open-ended range bytes correct", (await Range("bytes=65520-", HttpStatusCode.PartialContent)).SequenceEqual(data[65520..]));
        await Range("bytes=65536-", HttpStatusCode.RequestedRangeNotSatisfiable);
        var concurrent = await Task.WhenAll(Range("bytes=100-199", HttpStatusCode.PartialContent), Range("bytes=300-599", HttpStatusCode.PartialContent));
        Check("parallel readers have independent positions", concurrent[0].SequenceEqual(data[100..200]) && concurrent[1].SequenceEqual(data[300..600]));
        Check("empty file downloads successfully", (await client.GetByteArrayAsync("/files/" + server.Files[1].Id)).Length == 0);
        var receiver = server.Progress.Snapshot().Single();
        var delivered = receiver.Files.Single(f => f.Name == server.Files[0].Name);
        Check("parallel HTTP ranges do not inflate per-device file progress", delivered.Sent == data.Length && delivered.Active == 0 && delivered.Complete);
        Check("page visitors and downloads are tracked separately", receiver.PageVisited && receiver.Files.Length == 2);
        var tracker = new DownloadProgress(); tracker.Begin("device",server.Files[0]);
        tracker.Bytes("device",id,0,1000); tracker.Bytes("device",id,500,1000); tracker.Finish("device",id,false);
        var paused = tracker.Snapshot().Single().Files.Single();
        Check("interrupted overlapping ranges retain their unique bytes", paused.Sent == 1500 && paused.Active == 0 && paused.Interrupted && !paused.Complete);
        tracker.Begin("device",server.Files[0],1000); tracker.Bytes("device",id,1000,data.Length-1000); tracker.Finish("device",id,true);
        Check("resumed transfer merges coverage and reaches complete once", tracker.Snapshot().Single().Files.Single() is { Sent:65536,Complete:true,Active:0 });
        long oldAttempt = tracker.Begin("device", server.Files[0]);
        tracker.Bytes("device", id, 0, 2000, oldAttempt);
        long freshAttempt = tracker.Begin("device", server.Files[0]);
        Check("a fresh GET clears previous coverage, completion and speed", tracker.Snapshot().Single().Files.Single() is { Sent:0, Complete:false, Active:1, Speed:0 });
        tracker.Bytes("device", id, 0, 1000, freshAttempt);
        tracker.Bytes("device", id, 2000, data.Length - 2000, oldAttempt);
        tracker.Finish("device", id, true, oldAttempt);
        Check("late old writes and finalizers cannot complete or unlock a fresh attempt", tracker.Snapshot().Single().Files.Single() is { Sent:1000, Complete:false, Active:1 });
        tracker.Bytes("device", id, 1000, data.Length - 1000, freshAttempt);
        var ending = tracker.Snapshot().Single().Files.Single();
        Check("full coverage with an open request has zero useful speed and is not terminal", ending.Sent == data.Length && ending.Active == 1 && !ending.Complete && ending.Speed == 0);
        tracker.Bytes("device", id, 0, data.Length, freshAttempt);
        Check("duplicate response bytes never inflate coverage or useful speed", tracker.Snapshot().Single().Files.Single() is { Sent:65536, Active:1, Speed:0, Complete:false });
        tracker.Finish("device", id, true, freshAttempt);
        Check("only the current request finishing marks full coverage complete", tracker.Snapshot().Single().Files.Single() is { Sent:65536, Active:0, Speed:0, Complete:true });
        tracker.Begin("device", server.Files[0], 0);
        Check("explicit zero-start range resets a completed download", tracker.Snapshot().Single().Files.Single() is { Sent:0, Active:1, Speed:0, Complete:false });
        tracker.Bytes("device", id, 0, 1000); tracker.Finish("device", id, false);
        tracker.Begin("device", server.Files[0], 1000);
        Check("a nonzero resume preserves the current attempt's coverage", tracker.Snapshot().Single().Files.Single() is { Sent:1000, Active:1, Complete:false });
        tracker.Bytes("device", id, 1000, data.Length - 1000); tracker.Finish("device", id, true);
        await Range("bytes=0-1023", HttpStatusCode.PartialContent);
        await WaitForHttpCompletion();
        Check("actual zero-start HTTP Range resets earlier completed coverage", server.Progress.Snapshot().Single().Files.Single(f => f.Id == id) is { Sent:1024, Complete:false, Active:0, Speed:0 });
        await Range("bytes=1024-2047", HttpStatusCode.PartialContent);
        await WaitForHttpCompletion();
        Check("actual nonzero HTTP Range continues the current attempt", server.Progress.Snapshot().Single().Files.Single(f => f.Id == id) is { Sent:2048, Complete:false, Active:0 });
        Check("actual full HTTP retry still delivers identical bytes", (await client.GetByteArrayAsync("/files/" + id)).SequenceEqual(data));
        await WaitForHttpCompletion();
        Check("actual HTTP completion clears speed and active requests", server.Progress.Snapshot().Single().Files.Single(f => f.Id == id) is { Sent:65536, Complete:true, Active:0, Speed:0 });
        Check("unselected files cannot be requested", (await client.GetAsync("/files/not-a-file-id")).StatusCode == HttpStatusCode.NotFound);
        using var hostAttack = new HttpRequestMessage(HttpMethod.Get, "/"); hostAttack.Headers.Host = "evil.example";
        Check("unrecognized host rejected", (int)(await client.SendAsync(hostAttack)).StatusCode == 421);
        await File.WriteAllBytesAsync(path, [1,2,3]);
        Check("source change revokes file response", (await client.GetAsync("/files/" + id)).StatusCode == HttpStatusCode.Gone);
        await server.DisposeAsync();
        bool closed = false; try { await client.GetAsync("/"); } catch (HttpRequestException) { closed = true; }
        Check("ending sharing closes the address", closed);
        Check("download page adapts file size units", ShareText.Size(65536)=="64.0 KB" && ShareText.Size(9112440832)=="8.49 GB");
        await File.WriteAllTextAsync(Path.Combine(output, "http-checks.json"), JsonSerializer.Serialize(new { passed = true, scope = "Actual local Kestrel HTTP server, direct listing without authentication, numeric Host validation and streamed file responses; no AP or phone simulated", checks }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
