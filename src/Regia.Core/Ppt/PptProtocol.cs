using System.Text.Json;
using System.Text.Json.Serialization;

namespace Regia.Core.Ppt;

/// <summary>Nomi dei comandi inviati dalla regia a PptHost.</summary>
public static class PptCommands
{
    public const string Hello = "hello";
    public const string Ping = "ping";
    public const string Launch = "launch";
    public const string Open = "open";
    public const string StartShow = "start";
    public const string Next = "next";
    public const string Previous = "prev";
    public const string GoTo = "goto";
    public const string EndShow = "end";
    public const string Quit = "quit";

    /// <summary>Prova tecnica per "Verifica PowerPoint" (Impostazioni): crea, salva e riapre una presentazione di prova.</summary>
    public const string SelfTest = "selftest";
}

/// <summary>Nomi degli eventi inviati da PptHost alla regia.</summary>
public static class PptEvents
{
    public const string SlideChanged = "slide";
    public const string ShowEnded = "showEnded";
}

/// <summary>Codici di errore noti nelle risposte (campo <see cref="PptMessage.Error"/>).</summary>
public static class PptErrors
{
    /// <summary>PowerPoint è già aperto dall'utente: la regia non si aggancia a un'istanza non sua.</summary>
    public const string ForeignInstance = "foreign-instance";

    public const string NoPresentation = "no-presentation";
    public const string NoShow = "no-show";
    public const string FileNotFound = "file-not-found";
    public const string NotInstalled = "not-installed";

    /// <summary>Qualsiasi altro errore: il messaggio descrive il dettaglio.</summary>
    public const string Generic = "error";
}

public enum PptMessageKind
{
    Request,
    Response,
    Event
}

/// <summary>
/// Messaggio della named pipe: una riga di JSON. Richiesta (Id + Name = comando), risposta (stesso Id, Ok/Error/Data)
/// o evento (Name = evento, senza Id).
/// </summary>
public sealed record PptMessage
{
    public PptMessageKind Kind { get; init; }

    public long Id { get; init; }

    public string Name { get; init; } = "";

    public bool Ok { get; init; }

    /// <summary>Messaggio leggibile dell'errore (solo nelle risposte con Ok = false).</summary>
    public string? Error { get; init; }

    /// <summary>Codice dell'errore, uno di <see cref="PptErrors"/>.</summary>
    public string? Code { get; init; }

    public JsonElement? Data { get; init; }
}

public sealed record OpenArgs(string Path);

public sealed record OpenResult(int Slides, double SlideWidth, double SlideHeight);

/// <summary>Dove e come far partire lo slideshow. Rettangolo in pixel fisici del desktop virtuale.</summary>
public sealed record StartShowArgs(string? GdiDeviceName, int X, int Y, int Width, int Height, bool Windowed);

public sealed record SlideChangedData(int Slide, int Total);

/// <summary>Risposta a StartShow: slide iniziale e finestra dello slideshow (serve alla regia per tenerla sotto il Tappo).</summary>
public sealed record StartShowResult(int Slide, int Total, long Hwnd);

/// <summary>Fine dello slideshow decisa da PowerPoint. <c>Faulted</c> = PowerPoint è morto o non risponde più (non è una fine normale).</summary>
public sealed record ShowEndedData(bool Faulted, string? Reason);

public sealed record HelloResult(int HostPid, string Version);

/// <summary>Risposta a Next/Previous/GoTo: <c>Moved</c> false = fine (o inizio) raggiunta, nessun comando inviato a PowerPoint.</summary>
public sealed record NavigateResult(bool Moved, bool AtEnd, int Slide, int Total);

public sealed record LaunchResult(int PowerPointPid);

/// <summary>Nomi dei passi della prova tecnica (campo <see cref="PptSelfTestResult.FailedStep"/>).</summary>
public static class PptSelfTestSteps
{
    public const string Launch = "launch";
    public const string Create = "create";
    public const string Save = "save";
    public const string Open = "open";

    /// <summary>Un dialogo di PowerPoint ha bloccato la prova (<see cref="PptSelfTestResult.DialogTitle"/>).</summary>
    public const string Dialog = "dialog";
}

/// <summary>
/// Esito della prova tecnica. <c>FailedStep</c> null = tutti i passi riusciti. Con <c>FailedStep = "dialog"</c> la prova è rimasta
/// bloccata da una finestra di PowerPoint (<c>DialogTitle</c>) e PowerPoint va terminato.
/// </summary>
public sealed record PptSelfTestResult(
    string? FailedStep,
    string? Error,
    string? Version,
    string? Build,
    int Slides,
    string? DialogTitle,
    long ElapsedMs);

/// <summary>Risposta al Ping: serve al watchdog per distinguere un thread STA bloccato da uno solo impegnato.</summary>
public sealed record PingResult(string? BusyOperation, long BusyMs);

public static class PptProtocol
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };

    public static PptMessage Request(long id, string command, object? args = null) => new()
    {
        Kind = PptMessageKind.Request,
        Id = id,
        Name = command,
        Data = ToElement(args)
    };

    public static PptMessage Success(long id, object? data = null) => new()
    {
        Kind = PptMessageKind.Response,
        Id = id,
        Ok = true,
        Data = ToElement(data)
    };

    public static PptMessage Failure(long id, string code, string? message = null) => new()
    {
        Kind = PptMessageKind.Response,
        Id = id,
        Ok = false,
        Code = code,
        Error = message ?? code
    };

    public static PptMessage Event(string name, object? data = null) => new()
    {
        Kind = PptMessageKind.Event,
        Name = name,
        Ok = true,
        Data = ToElement(data)
    };

    /// <summary>Una riga di JSON, senza a-capo.</summary>
    public static string Serialize(PptMessage message) => JsonSerializer.Serialize(message, Options);

    /// <summary>Legge una riga. Una riga malformata non lancia: restituisce false.</summary>
    public static bool TryParse(string? line, out PptMessage message)
    {
        message = new PptMessage();
        if (string.IsNullOrWhiteSpace(line))
            return false;

        try
        {
            var parsed = JsonSerializer.Deserialize<PptMessage>(line, Options);
            if (parsed is null || string.IsNullOrEmpty(parsed.Name) && parsed.Kind != PptMessageKind.Response)
                return false;

            message = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>Legge i dati del messaggio come <typeparamref name="T"/>; null se mancano o non corrispondono.</summary>
    public static T? ReadData<T>(PptMessage message) where T : class
    {
        if (message.Data is not { } data || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        try
        {
            return data.Deserialize<T>(Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? ToElement(object? value) =>
        value is null ? null : JsonSerializer.SerializeToElement(value, value.GetType(), Options);
}
