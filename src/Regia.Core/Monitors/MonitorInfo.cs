namespace Regia.Core.Monitors;

/// <summary>Identità stabile di un monitor, salvata nelle impostazioni.</summary>
public sealed record MonitorId(
    string DevicePath,
    string FriendlyName,
    string EdidManufacturerId,
    int EdidProductCode);

/// <summary>Monitor rilevato, con geometria in pixel fisici.</summary>
public sealed record MonitorInfo(
    string DevicePath,
    string FriendlyName,
    string EdidManufacturerId,
    int EdidProductCode,
    string GdiDeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    bool IsPrimary,
    int Dpi)
{
    public MonitorId ToId() => new(DevicePath, FriendlyName, EdidManufacturerId, EdidProductCode);
}
