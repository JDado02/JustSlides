namespace Regia.Core.Audio;

/// <summary>
/// Regola dell'avviso "PowerPoint suona sul predefinito di Windows": PowerPoint non permette di scegliere il dispositivo,
/// quindi se quello scelto per l'evento non è il predefinito l'operatore deve cambiare il predefinito di Windows.
/// </summary>
public static class AudioDeviceWarning
{
    /// <summary>Testo dell'avviso, o null se non serve.</summary>
    /// <param name="chosenId">ID endpoint scelto nelle impostazioni (vuoto = predefinito di Windows).</param>
    /// <param name="chosenName">Nome del dispositivo scelto (per il messaggio).</param>
    /// <param name="defaultId">ID del dispositivo predefinito attuale; null/vuoto se non ce n'è.</param>
    /// <param name="hasPpt">Almeno un PowerPoint in lista.</param>
    public static string? Evaluate(string chosenId, string chosenName, string? defaultId, bool hasPpt)
    {
        if (!hasPpt || string.IsNullOrEmpty(chosenId))
            return null;

        if (string.Equals(chosenId, defaultId, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = string.IsNullOrWhiteSpace(chosenName) ? "il dispositivo scelto" : $"\"{chosenName}\"";
        return $"PowerPoint suona solo sul dispositivo predefinito di Windows, che non è {name}. "
               + "Imposta " + (string.IsNullOrWhiteSpace(chosenName) ? "quel dispositivo" : "questo dispositivo")
               + " come predefinito nelle impostazioni audio di Windows, altrimenti l'audio delle presentazioni uscirà altrove.";
    }
}
