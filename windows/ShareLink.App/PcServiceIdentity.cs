namespace ShareLink;
// The UUID fits in one legacy AD service field: a fixed application marker and
// the same 64-bit presentation fingerprint used by existing phone discovery.
// This is never a trust decision; full pairing/SAS and TLS pins remain required.
internal static class PcServiceIdentity
{
    private static readonly byte[] Marker=Guid.Parse("7c3d0021-6b4a-4f9d-0000-000000000000").ToByteArray();
    public static Guid Create(string fingerprint)
    {
        if(fingerprint.Length!=64||!fingerprint.All(Uri.IsHexDigit))throw new InvalidOperationException("电脑广播身份不完整");
        var bytes=(byte[])Marker.Clone();Convert.FromHexString(fingerprint[..16]).CopyTo(bytes,8);return new Guid(bytes);
    }
    public static string Hint(Guid service)
    {
        var bytes=service.ToByteArray();return bytes.AsSpan(0,8).SequenceEqual(Marker.AsSpan(0,8))?Convert.ToHexString(bytes.AsSpan(8,8)):"";
    }
}
