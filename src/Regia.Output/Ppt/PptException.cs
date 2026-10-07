using Regia.Core.Ppt;

namespace Regia.Output.Ppt;

/// <summary>Errore nella comunicazione con PptHost o riportato da PowerPoint. <see cref="Code"/> è uno di <see cref="PptErrors"/>.</summary>
public sealed class PptException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>PptHost è stato terminato dal watchdog (o la pipe è caduta): la richiesta in corso non avrà risposta.</summary>
    public const string HostFailure = "host-failure";

    /// <summary>PptHost non è partito o non si è collegato in tempo.</summary>
    public const string HostStartFailed = "host-start-failed";

    /// <summary>La richiesta non ha avuto risposta nel tempo previsto.</summary>
    public const string RequestTimeout = "request-timeout";

    public bool IsForeignInstance => Code == PptErrors.ForeignInstance;
}
