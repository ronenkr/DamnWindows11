using System.Runtime.InteropServices;

namespace DamnWindows11;

/// <summary>
/// Identifies the current monitor setup. The Dell and the KVM/adapter's fake EDID (AGO0001)
/// sit on the same port but have different hardware IDs, so they produce different keys.
/// </summary>
internal static class Topology
{
    public sealed record Monitor(string Id, Native.RECT Bounds, bool Primary);

    public static List<Monitor> Monitors()
    {
        var list = new List<Monitor>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref Native.RECT _, IntPtr _) =>
        {
            var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf<Native.MONITORINFOEX>() };
            if (Native.GetMonitorInfo(h, ref mi))
                list.Add(new Monitor(HardwareId(mi.szDevice), mi.rcMonitor, (mi.dwFlags & Native.MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static string Key(IEnumerable<Monitor> monitors) =>
        string.Join(" | ", monitors.Select(m => $"{m.Id}@{m.Bounds}").Order(StringComparer.Ordinal));

    public static string CurrentKey() => Key(Monitors());

    // "\\.\DISPLAY2" -> "DELD0D8", taken from "\\?\DISPLAY#DELD0D8#5&17ffe506&0&UID336135#{...}".
    private static string HardwareId(string adapterDevice)
    {
        var dd = new Native.DISPLAY_DEVICE { cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>() };
        for (uint i = 0; Native.EnumDisplayDevices(adapterDevice, i, ref dd, Native.EDD_GET_DEVICE_INTERFACE_NAME); i++)
        {
            if ((dd.StateFlags & Native.DISPLAY_DEVICE_ACTIVE) != 0)
            {
                var parts = dd.DeviceID.Split('#');
                return parts.Length > 1 ? parts[1] : dd.DeviceID;
            }
            dd.cb = Marshal.SizeOf<Native.DISPLAY_DEVICE>();
        }
        return adapterDevice;
    }
}
