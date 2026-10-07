namespace Regia.Core.Input;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8
}

/// <summary>
/// Un tasto con i suoi modificatori. Usa i codici virtual-key di Windows (int) così Core non dipende da WPF:
/// la regia li ricava da <c>KeyInterop</c>, l'hook di tastiera li riceve già in questa forma.
/// </summary>
public readonly record struct KeyChord(int VirtualKey, KeyModifiers Modifiers = KeyModifiers.None)
{
    public const int VkEscape = 0x1B;
    public const int VkSpace = 0x20;
    public const int VkEnter = 0x0D;
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;
    public const int VkLeft = 0x25;
    public const int VkUp = 0x26;
    public const int VkRight = 0x27;
    public const int VkDown = 0x28;

    private static readonly (int Vk, string Name)[] SpecialNames =
    [
        (0x08, "Backspace"), (0x09, "Tab"), (VkEnter, "Invio"), (VkEscape, "Esc"), (VkSpace, "Spazio"),
        (VkPageUp, "PagSu"), (VkPageDown, "PagGiù"), (0x23, "Fine"), (0x24, "Home"),
        (VkLeft, "Sinistra"), (VkUp, "Su"), (VkRight, "Destra"), (VkDown, "Giù"),
        (0x2D, "Ins"), (0x2E, "Canc"),
        (0xBE, "Punto"), (0xBC, "Virgola"), (0xBD, "Meno"), (0xBB, "Più")
    ];

    /// <summary>Tasti che da soli non sono una scelta valida (solo modificatori).</summary>
    public static bool IsModifierKey(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    /// <summary>Testo leggibile e rilegibile da <see cref="TryParse"/>: "Ctrl+Shift+L", "Spazio", "F5".</summary>
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(KeyModifiers.Control))
            parts.Add("Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt))
            parts.Add("Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift))
            parts.Add("Shift");
        if (Modifiers.HasFlag(KeyModifiers.Windows))
            parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join('+', parts);
    }

    public static string KeyName(int vk)
    {
        foreach (var (key, name) in SpecialNames)
        {
            if (key == vk)
                return name;
        }

        return vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            >= 0x60 and <= 0x69 => "Num" + (vk - 0x60),
            _ => "0x" + vk.ToString("X2")
        };
    }

    public static bool TryParse(string? text, out KeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = KeyModifiers.None;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);

        // "Ctrl++" non esiste: il tasto "Più" ha un nome, quindi il separatore non è ambiguo.
        for (var i = 0; i < parts.Length - 1; i++)
        {
            switch (parts[i].ToLowerInvariant())
            {
                case "ctrl":
                    modifiers |= KeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= KeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= KeyModifiers.Shift;
                    break;
                case "win":
                    modifiers |= KeyModifiers.Windows;
                    break;
                default:
                    return false;
            }
        }

        if (!TryParseKey(parts[^1], out var vk))
            return false;

        chord = new KeyChord(vk, modifiers);
        return true;
    }

    private static bool TryParseKey(string name, out int vk)
    {
        vk = 0;
        if (name.Length == 0)
            return false;

        foreach (var (key, special) in SpecialNames)
        {
            if (string.Equals(special, name, StringComparison.OrdinalIgnoreCase))
            {
                vk = key;
                return true;
            }
        }

        if (name.Length == 1 && (char.IsAsciiLetterOrDigit(name[0])))
        {
            vk = char.ToUpperInvariant(name[0]);
            return true;
        }

        if (name.Length is 2 or 3 && (name[0] is 'F' or 'f') && int.TryParse(name.AsSpan(1), out var f) && f is >= 1 and <= 24)
        {
            vk = 0x6F + f;
            return true;
        }

        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsAsciiDigit(name[3]))
        {
            vk = 0x60 + (name[3] - '0');
            return true;
        }

        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.AsSpan(2), System.Globalization.NumberStyles.HexNumber, null, out var raw) && raw is > 0 and < 0x100)
        {
            vk = raw;
            return true;
        }

        return false;
    }
}
