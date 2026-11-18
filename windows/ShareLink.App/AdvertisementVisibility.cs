namespace ShareLink;
// Advertising ownership is independent of the existing GATT connection and keys.
internal sealed class AdvertisementVisibility(Action<bool> apply)
{
    private bool? applied;
    internal void Set(bool visible)
    {
        if(applied==visible)return;
        apply(visible);applied=visible;
    }
}
