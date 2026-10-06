using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Regia.Core.Monitors;
using Serilog;
using Windows.Win32;
using Windows.Win32.Devices.Display;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Regia.Output.Monitors;

/// <summary>
/// Elenca i monitor attivi con device path e dati EDID (via DisplayConfig),
/// abbinati alla geometria GDI in pixel fisici.
/// </summary>
public static unsafe class DisplayEnumerator
{
    private sealed record GdiMonitor(string DeviceName, int X, int Y, int Width, int Height, bool IsPrimary, int Dpi);

    public static IReadOnlyList<MonitorInfo> GetMonitors()
    {
        try
        {
            var gdiMonitors = EnumerateGdiMonitors();
            var result = new List<MonitorInfo>();

            foreach (var (gdiName, devicePath, friendlyName, manufacturer, product) in EnumerateTargets())
            {
                var gdi = gdiMonitors.FirstOrDefault(g => string.Equals(g.DeviceName, gdiName, StringComparison.OrdinalIgnoreCase));
                if (gdi is null)
                    continue;

                var name = string.IsNullOrWhiteSpace(friendlyName) ? $"Monitor {gdiName.TrimStart('\\', '.')}" : friendlyName;
                result.Add(new MonitorInfo(
                    devicePath, name, manufacturer, product, gdiName,
                    gdi.X, gdi.Y, gdi.Width, gdi.Height, gdi.IsPrimary, gdi.Dpi));
            }

            // Se DisplayConfig non ha dato nulla (driver strani) ripiego sui soli dati GDI.
            if (result.Count == 0)
            {
                foreach (var g in gdiMonitors)
                    result.Add(new MonitorInfo(g.DeviceName, g.DeviceName, "", 0, g.DeviceName, g.X, g.Y, g.Width, g.Height, g.IsPrimary, g.Dpi));
            }

            return result.OrderBy(m => !m.IsPrimary).ThenBy(m => m.X).ToList();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore nel rilevamento dei monitor");
            return [];
        }
    }

    private static List<GdiMonitor> EnumerateGdiMonitors()
    {
        var list = new List<GdiMonitor>();
        var handle = GCHandle.Alloc(list);
        try
        {
            PInvoke.EnumDisplayMonitors(HDC.Null, null, &EnumMonitorProc, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return list;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL EnumMonitorProc(HMONITOR hMonitor, HDC hdc, RECT* rect, LPARAM data)
    {
        try
        {
            var list = (List<GdiMonitor>)GCHandle.FromIntPtr((nint)data.Value).Target!;

            var info = new MONITORINFOEXW();
            info.monitorInfo.cbSize = (uint)sizeof(MONITORINFOEXW);
            if (PInvoke.GetMonitorInfo(hMonitor, (MONITORINFO*)&info))
            {
                uint dpiX = 96, dpiY = 96;
                PInvoke.GetDpiForMonitor(hMonitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY);

                var r = info.monitorInfo.rcMonitor;
                list.Add(new GdiMonitor(
                    info.szDevice.ToString(),
                    r.left, r.top, r.right - r.left, r.bottom - r.top,
                    (info.monitorInfo.dwFlags & 1) != 0,
                    (int)dpiX));
            }
        }
        catch
        {
            // Un'eccezione non può attraversare il confine nativo: salto questo monitor.
        }

        return true;
    }

    private static List<(string GdiName, string DevicePath, string FriendlyName, string Manufacturer, int Product)> EnumerateTargets()
    {
        var list = new List<(string, string, string, string, int)>();

        uint pathCount = 0, modeCount = 0;
        const QUERY_DISPLAY_CONFIG_FLAGS flags = QUERY_DISPLAY_CONFIG_FLAGS.QDC_ONLY_ACTIVE_PATHS;

        if (PInvoke.GetDisplayConfigBufferSizes(flags, &pathCount, &modeCount) != WIN32_ERROR.ERROR_SUCCESS)
            return list;

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        fixed (DISPLAYCONFIG_PATH_INFO* pathPtr = paths)
        fixed (DISPLAYCONFIG_MODE_INFO* modePtr = modes)
        {
            if (PInvoke.QueryDisplayConfig(flags, &pathCount, pathPtr, &modeCount, modePtr, null) != WIN32_ERROR.ERROR_SUCCESS)
                return list;
        }

        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_SOURCE_NAME;
            source.header.size = (uint)sizeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME);
            source.header.adapterId = path.sourceInfo.adapterId;
            source.header.id = path.sourceInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&source.header) != 0)
                continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header.type = DISPLAYCONFIG_DEVICE_INFO_TYPE.DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
            target.header.size = (uint)sizeof(DISPLAYCONFIG_TARGET_DEVICE_NAME);
            target.header.adapterId = path.targetInfo.adapterId;
            target.header.id = path.targetInfo.id;
            if (PInvoke.DisplayConfigGetDeviceInfo(&target.header) != 0)
                continue;

            list.Add((
                source.viewGdiDeviceName.ToString(),
                target.monitorDevicePath.ToString(),
                target.monitorFriendlyDeviceName.ToString(),
                DecodeManufacturer(target.edidManufactureId),
                target.edidProductCodeId));
        }

        return list;
    }

    /// <summary>L'ID produttore EDID è un codice a 3 lettere (5 bit ciascuna), memorizzato big-endian.</summary>
    private static string DecodeManufacturer(ushort raw)
    {
        var v = (ushort)((raw >> 8) | (raw << 8));
        return new string(
        [
            (char)(((v >> 10) & 0x1F) + '@'),
            (char)(((v >> 5) & 0x1F) + '@'),
            (char)((v & 0x1F) + '@')
        ]);
    }
}
