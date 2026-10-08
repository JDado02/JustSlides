using System.Globalization;

namespace Regia.Core.Update;

/// <summary>Numero di versione maggiore.minore.patch (confronto per aggiornamenti). Logica pura.</summary>
public readonly record struct ReleaseVersion(int Major, int Minor, int Patch) : IComparable<ReleaseVersion>
{
    /// <summary>
    /// Accetta "1.1.0", "v1.1.0", "1.1.0.0", "1.1.0+abc123" (suffisso di build) e "1.1" (patch 0).
    /// Un suffisso di pre-release ("-beta") rende la versione non valida: gli aggiornamenti automatici non le installano.
    /// </summary>
    public static bool TryParse(string? text, out ReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
            s = s[1..];

        var plus = s.IndexOf('+');
        if (plus >= 0)
            s = s[..plus];

        var parts = s.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                return false;

            if (i < 3)
                numbers[i] = n;
            else if (n != 0)
                return false; // quarto numero diverso da zero: formato non nostro
        }

        version = new ReleaseVersion(numbers[0], numbers[1], numbers[2]);
        return true;
    }

    public int CompareTo(ReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
            return major;

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public bool IsNewerThan(ReleaseVersion other) => CompareTo(other) > 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
