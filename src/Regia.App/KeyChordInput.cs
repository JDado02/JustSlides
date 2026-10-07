using System.Windows.Input;
using Regia.Core.Input;

namespace Regia.App;

/// <summary>Conversione dai tasti WPF ai <see cref="KeyChord"/> di Core (codici virtual-key di Windows).</summary>
public static class KeyChordInput
{
    public static KeyChord FromKey(Key key, ModifierKeys modifiers) =>
        new(KeyInterop.VirtualKeyFromKey(key), ToCore(modifiers));

    public static KeyModifiers ToCore(ModifierKeys modifiers)
    {
        var result = KeyModifiers.None;
        if (modifiers.HasFlag(ModifierKeys.Control))
            result |= KeyModifiers.Control;
        if (modifiers.HasFlag(ModifierKeys.Alt))
            result |= KeyModifiers.Alt;
        if (modifiers.HasFlag(ModifierKeys.Shift))
            result |= KeyModifiers.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows))
            result |= KeyModifiers.Windows;
        return result;
    }
}
