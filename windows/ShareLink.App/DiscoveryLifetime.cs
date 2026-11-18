namespace ShareLink;

internal sealed class DiscoveryLifetime
{
    internal bool Enabled { get; set; }
    internal long Deadline { get; private set; }
    internal void Activity(long now) => Deadline = now + 600_000;
    internal bool Expired(long now, bool busy) => Enabled && !busy && now >= Deadline;
}
