namespace Regia.Core.Monitors;

/// <summary>Ritrova il monitor salvato tra quelli attualmente collegati.</summary>
public static class MonitorMatcher
{
    /// <summary>
    /// Prima per device path; se non c'è, per EDID (produttore + prodotto + nome) ma solo
    /// se la corrispondenza è univoca. Altrimenti null.
    /// </summary>
    public static MonitorInfo? Find(MonitorId? saved, IReadOnlyList<MonitorInfo> available)
    {
        if (saved is null)
            return null;

        var byPath = available.FirstOrDefault(m =>
            string.Equals(m.DevicePath, saved.DevicePath, StringComparison.OrdinalIgnoreCase));
        if (byPath is not null)
            return byPath;

        var byEdid = available
            .Where(m => m.EdidProductCode == saved.EdidProductCode
                        && string.Equals(m.EdidManufacturerId, saved.EdidManufacturerId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(m.FriendlyName, saved.FriendlyName, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return byEdid.Count == 1 ? byEdid[0] : null;
    }
}
