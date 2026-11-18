using System.Text.Json;
namespace ShareLink;
// One prompt belongs to one pending receive. A later snapshot can end it, never revive it.
internal sealed class ReceiveConfirmation(string id) : IDisposable
{
    private readonly CancellationTokenSource ended = new();
    internal CancellationToken Cancellation => ended.Token;
    internal bool Ended => ended.IsCancellationRequested;
    internal void Cancel() => ended.Cancel();
    internal void Observe(JsonElement snapshot)
    {
        if (!IsPending(snapshot,id)) ended.Cancel();
    }
    internal static bool IsPending(JsonElement snapshot,string id) =>
        snapshot.TryGetProperty("transfers",out var transfers) && transfers.ValueKind==JsonValueKind.Array &&
        transfers.EnumerateArray().Any(t=>MainWindow.Text(t,"id")==id && MainWindow.Text(t,"direction")=="receive" && MainWindow.Text(t,"state")=="awaiting_accept");
    public void Dispose() => ended.Dispose();
}
