using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.Core.Input;

namespace Regia.App.ViewModels;

/// <summary>Un tasto assegnato a un'azione (la "pastiglia" con la × per toglierlo).</summary>
public sealed class KeyChipViewModel
{
    public KeyChipViewModel(KeyAction action, KeyChord chord)
    {
        Action = action;
        Chord = chord;
    }

    public KeyAction Action { get; }

    public KeyChord Chord { get; }

    public string Text => Chord.ToString();

    /// <summary>Esc su PANIC non si toglie mai.</summary>
    public bool CanRemove => !(Action == KeyAction.Panic && Chord == KeyMap.PanicChord);

    // Accessibilità: l'elemento stampa il suo testo, non il nome del tipo.
    public override string ToString() => Text;
}

/// <summary>Una riga dell'editor: azione, tasti assegnati, stato "premi un tasto".</summary>
public sealed class KeyRowViewModel
{
    public KeyRowViewModel(KeyAction action, IReadOnlyList<KeyChipViewModel> chips, bool isCapturing)
    {
        Action = action;
        Chips = chips;
        IsCapturing = isCapturing;
    }

    public KeyAction Action { get; }

    public string Name => KeyActionInfo.DisplayName(Action);

    public IReadOnlyList<KeyChipViewModel> Chips { get; }

    public bool IsCapturing { get; }

    public bool HasNoKeys => Chips.Count == 0;

    public override string ToString() => Name;
}

/// <summary>
/// Editor dei tasti (Impostazioni → Tasti). I tasti sono globali dell'app: ogni modifica si salva e vale subito
/// (finestra della regia e hook di tastiera leggono <see cref="KeyBindings.Current"/> a ogni tasto).
/// </summary>
public sealed partial class KeyBindingsViewModel : ObservableObject
{
    private readonly KeyBindings _keys;
    private KeyAction? _capturing;

    public KeyBindingsViewModel(KeyBindings keys)
    {
        _keys = keys;
        Rebuild();
    }

    public ObservableCollection<KeyRowViewModel> Rows { get; } = [];

    /// <summary>Esito dell'ultima operazione (tasto già usato, ecc.).</summary>
    [ObservableProperty]
    private string _status = "";

    /// <summary>Si sta aspettando un tasto: la finestra delle impostazioni lo inoltra a <see cref="HandleCapturedKey"/>.</summary>
    public bool IsCapturing => _capturing is not null;

    private void Rebuild()
    {
        Rows.Clear();
        foreach (var action in Enum.GetValues<KeyAction>())
        {
            var chips = _keys.Current.ChordsFor(action).Select(c => new KeyChipViewModel(action, c)).ToList();
            Rows.Add(new KeyRowViewModel(action, chips, _capturing == action));
        }

        OnPropertyChanged(nameof(IsCapturing));
    }

    [RelayCommand]
    private void StartCapture(KeyRowViewModel row)
    {
        _capturing = row.Action;
        Status = $"Premi il tasto da assegnare a «{row.Name}» (Esc per annullare).";
        Rebuild();
    }

    [RelayCommand]
    private void CancelCapture()
    {
        if (_capturing is null)
            return;

        _capturing = null;
        Status = "";
        Rebuild();
    }

    [RelayCommand]
    private void Remove(KeyChipViewModel chip)
    {
        _keys.Update(_keys.Current.Without(chip.Action, chip.Chord));
        Status = $"Tolto «{chip.Text}» da «{KeyActionInfo.DisplayName(chip.Action)}».";
        Rebuild();
    }

    [RelayCommand]
    private void ResetDefaults()
    {
        _capturing = null;
        _keys.Update(KeyMap.Default);
        Status = "Tasti predefiniti ripristinati.";
        Rebuild();
    }

    /// <summary>
    /// Un tasto premuto mentre si cattura. Restituisce true se è stato consumato (la finestra non deve fare altro).
    /// Esc annulla; un tasto già usato da un'altra azione è rifiutato con il nome dell'azione che lo possiede.
    /// </summary>
    public bool HandleCapturedKey(KeyChord chord)
    {
        if (_capturing is not { } action)
            return false;

        // Il solo premere Ctrl/Alt/Shift/Win non è una scelta: si aspetta il tasto vero.
        if (chord.VirtualKey == 0 || KeyChord.IsModifierKey(chord.VirtualKey))
            return true;

        if (chord == KeyMap.PanicChord)
        {
            // Esc annulla la cattura (è sempre PANIC e non si può assegnare ad altro).
            CancelCapture();
            return true;
        }

        _capturing = null;

        var owner = _keys.Current.Find(chord);
        if (owner is { } other && other != action)
        {
            Status = $"«{chord}» è già usato da «{KeyActionInfo.DisplayName(other)}»: toglilo da lì prima.";
        }
        else if (owner == action)
        {
            Status = $"«{chord}» è già assegnato a «{KeyActionInfo.DisplayName(action)}».";
        }
        else
        {
            _keys.Update(_keys.Current.With(action, chord));
            Status = $"«{chord}» assegnato a «{KeyActionInfo.DisplayName(action)}».";
        }

        Rebuild();
        return true;
    }
}
