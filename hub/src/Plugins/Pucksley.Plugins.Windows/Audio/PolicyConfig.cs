using System.Runtime.InteropServices;
using static Pucksley.Plugins.Windows.Common.NativeMethods;

namespace Pucksley.Plugins.Windows.Audio;

/// <summary>
/// Sets the default audio endpoint through Windows' undocumented <c>IPolicyConfig</c> (what the
/// Sound control panel uses). Stable since Windows 7, and used by SoundSwitch, AudioDeviceCmdlets
/// and others. There is no documented API for this.
/// </summary>
internal static class PolicyConfig
{
    // ERole
    const int eConsole = 0, eMultimedia = 1, eCommunications = 2;

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    class CPolicyConfigClient { }

    // Method order is the vtable order: it must not change
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, out nint format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool useDefault, out nint format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool useDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string deviceId, nint mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, nint key, nint value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool fxStore, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string deviceId, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }

    /// <summary>Makes <paramref name="deviceId"/> the default output for every app, calls included.</summary>
    public static void SetDefaultEndpoint(string deviceId) => Use(policy =>
    {
        foreach (var role in (int[])[eConsole, eMultimedia, eCommunications])
        {
            int hr = policy.SetDefaultEndpoint(deviceId, role);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
        }
        return true;
    });

    /// <summary>
    /// The endpoint's friendly name read through <c>IPolicyConfig::GetPropertyValue</c>. Read-only:
    /// it checks the interface really is laid out as declared (that method is two slots before
    /// <c>SetDefaultEndpoint</c>) without changing anything.
    /// </summary>
    internal static string? ReadFriendlyName(string deviceId) => Use(policy =>
    {
        // PROPERTYKEY PKEY_Device_FriendlyName = {a45c254e-df1c-4efd-8020-67d146a850e0}, 14
        var key = Marshal.AllocHGlobal(20);
        var value = Marshal.AllocHGlobal(32); // a PROPVARIANT is 24 bytes on x64
        try
        {
            Marshal.Copy(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0").ToByteArray(), 0, key, 16);
            Marshal.WriteInt32(key, 16, 14);
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(value, i, 0);
            if (policy.GetPropertyValue(deviceId, false, key, value) < 0) return null;
            const short VT_LPWSTR = 31;
            string? name = Marshal.ReadInt16(value) == VT_LPWSTR ? Marshal.PtrToStringUni(Marshal.ReadIntPtr(value, 8)) : null;
            PropVariantClear(value);
            return name;
        }
        finally
        {
            Marshal.FreeHGlobal(value);
            Marshal.FreeHGlobal(key);
        }
    });

    static T Use<T>(Func<IPolicyConfig, T> action)
    {
        object client = new CPolicyConfigClient();
        try
        {
            return action((IPolicyConfig)client);
        }
        finally
        {
            Marshal.ReleaseComObject(client);
        }
    }
}
