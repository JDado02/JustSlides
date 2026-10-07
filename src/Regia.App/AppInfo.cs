namespace Regia.App;

internal static class AppInfo
{
    /// <summary>Nome del prodotto: titolo della finestra principale, intestazioni, messaggi.</summary>
    public const string ProductName = "JustSlides";

    /// <summary>Nome del processo (senza .exe): serve a ritrovare la prima istanza alla seconda esecuzione.</summary>
    public const string ProcessName = "JustSlides";

    public const string MutexName = @"Local\JustSlides.SingleInstance";
}
