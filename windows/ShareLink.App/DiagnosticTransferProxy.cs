using System.Net;
using System.Net.Sockets;

namespace ShareLink;

// A diagnostic-only encrypted TCP relay gives the user time to background/lock the phone.
// Production transfers never use this relay or delay. It forwards to the already pinned direct endpoint.
internal sealed class DiagnosticTransferProxy : IDisposable
{
    private readonly TcpListener listener;
    private readonly CancellationTokenSource cancellation = new();
    private TcpClient? front, back;
    private readonly int delayMs;
    private readonly bool paceDownload;
    internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal DiagnosticTransferProxy(IPAddress local, Peer destination, int delayMs = 80, bool paceDownload = false)
    {
        this.delayMs = delayMs;
        this.paceDownload = paceDownload;
        listener = new TcpListener(local, 0); listener.Start(1);
        _ = RunAsync(destination);
    }
    private async Task RunAsync(Peer destination)
    {
        try
        {
            front = await listener.AcceptTcpClientAsync(cancellation.Token);
            back = new TcpClient(new IPEndPoint((IPAddress)((IPEndPoint)listener.LocalEndpoint).Address, 0));
            await back.ConnectAsync(destination.Host, destination.Port, cancellation.Token);
            var upload = ForwardAsync(front.GetStream(), back.GetStream(), !paceDownload);
            var download = ForwardAsync(back.GetStream(), front.GetStream(), paceDownload);
            await Task.WhenAny(upload, download);
        }
        catch (Exception) when (cancellation.IsCancellationRequested) { }
        catch (Exception) { }
        finally { front?.Dispose(); back?.Dispose(); }
    }
    private async Task ForwardAsync(NetworkStream input, NetworkStream output, bool pace)
    {
        byte[] buffer = new byte[65536];
        while (!cancellation.IsCancellationRequested)
        {
            int count = await input.ReadAsync(buffer, cancellation.Token);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellation.Token);
            if (pace) await Task.Delay(delayMs, cancellation.Token);
        }
    }
    public void Dispose() { cancellation.Cancel(); listener.Stop(); front?.Dispose(); back?.Dispose(); }
}
