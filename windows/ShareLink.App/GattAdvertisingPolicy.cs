using System.Runtime.InteropServices;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation.Metadata;
using WinRT;
namespace ShareLink;

internal static class GattAdvertisingPolicy
{
    // IGattServiceProvider2 in Windows.Devices.winmd (Windows 11 24H2+).
    // The app keeps its Windows 10 SDK projection and minimum OS dependency.
    private static readonly Guid UpdateInterface = new("9ef531a9-cf12-59a3-a81c-362f4aabaacf");
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate int UpdateParametersDelegate(IntPtr instance, IntPtr parameters);
    internal static GattServiceProviderAdvertisingParameters Parameters(bool visible) => new()
    {
        IsDiscoverable=visible,
        IsConnectable=visible
    };
    internal static string Apply(GattServiceProvider provider, bool visible)
    {
        var parameters=Parameters(visible);
        bool published=provider.AdvertisementStatus is GattServiceProviderAdvertisementStatus.Started or GattServiceProviderAdvertisementStatus.StartedWithoutAllAdvertisementData;
        if(published && ApiInformation.IsMethodPresent("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider","UpdateAdvertisingParameters")) {
            Update(provider,parameters);return "in-place-update";
        }
        // Older systems require republishing; this compatibility path needs its
        // own device acceptance and is not claimed as an in-place update.
        if(published)provider.StopAdvertising();
        provider.StartAdvertising(parameters);
        return published?"legacy-republish":"initial-publication";
    }
    private static void Update(GattServiceProvider provider,GattServiceProviderAdvertisingParameters parameters)
    {
        IntPtr instance=IntPtr.Zero,update=IntPtr.Zero,argument=IntPtr.Zero;
        try {
            instance=MarshalInspectable<GattServiceProvider>.FromManaged(provider);
            var iid=UpdateInterface;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance,ref iid,out update));
            argument=MarshalInspectable<GattServiceProviderAdvertisingParameters>.FromManaged(parameters);
            InvokeUpdate(update,argument);
        } finally {
            if(argument!=IntPtr.Zero)MarshalInspectable<GattServiceProviderAdvertisingParameters>.DisposeAbi(argument);
            if(update!=IntPtr.Zero)Marshal.Release(update);
            if(instance!=IntPtr.Zero)MarshalInspectable<GattServiceProvider>.DisposeAbi(instance);
        }
    }
    internal static void InvokeUpdate(IntPtr instance,IntPtr parameters)
    {
        // IInspectable has six entries; UpdateAdvertisingParameters is the sole
        // method on IGattServiceProvider2. Both ABI arguments are borrowed here.
        var table=Marshal.ReadIntPtr(instance);
        var method=Marshal.ReadIntPtr(table,6*IntPtr.Size);
        var invoke=Marshal.GetDelegateForFunctionPointer<UpdateParametersDelegate>(method);
        Marshal.ThrowExceptionForHR(invoke(instance,parameters));
    }
}
