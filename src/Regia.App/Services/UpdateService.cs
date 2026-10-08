using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using Regia.Core.Update;
using Serilog;

namespace Regia.App.Services;

public enum UpdateStatus
{
    UpToDate,
    Available,
    Failed
}

/// <summary>Esito del controllo: versione installata, release trovata (se più nuova) o errore leggibile.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, ReleaseVersion Current, UpdateRelease? Release, string? Error);

/// <summary>
/// Aggiornamenti da GitHub, solo su richiesta dell'operatore (nessun controllo all'avvio): controlla l'ultima release,
/// scarica il Setup verificandone l'SHA256 e lo avvia in modalità guidata-silenziosa. Mai eccezioni verso l'alto dal controllo.
/// </summary>
public sealed class UpdateService
{
    /// <summary>Solo per prove/sviluppo: indirizzo alternativo dell'API (stesso formato di <c>releases/latest</c> di GitHub).</summary>
    public const string UrlVariable = "JUSTSLIDES_UPDATE_URL";

    private const string DefaultUrl = "https://api.github.com/repos/JDado02/JustSlides/releases/latest";

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly Uri _apiUrl;

    public UpdateService()
    {
        var custom = Environment.GetEnvironmentVariable(UrlVariable);
        _apiUrl = Uri.TryCreate(custom, UriKind.Absolute, out var uri) ? uri : new Uri(DefaultUrl);
    }

    /// <summary>Versione di questa installazione (dall'assembly di JustSlides.exe).</summary>
    public static ReleaseVersion CurrentVersion
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
            var text = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? assembly.GetName().Version?.ToString();
            return ReleaseVersion.TryParse(text, out var version) ? version : new ReleaseVersion(0, 0, 0);
        }
    }

    /// <summary>
    /// L'aggiornamento automatico si fa solo dall'app installata dal Setup (c'è il disinstallatore accanto all'exe):
    /// da una cartella di sviluppo si limiterebbe a rovinare l'installazione vera.
    /// </summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        var current = CurrentVersion;
        try
        {
            using var client = CreateClient(CheckTimeout, github: true);
            using var response = await client.GetAsync(_apiUrl, cancellationToken);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new UpdateCheckResult(UpdateStatus.Failed, current, null, "Nessuna versione pubblicata trovata su GitHub.");

            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Failed, current, null, $"GitHub ha risposto con un errore ({(int)response.StatusCode}). Riprova tra poco.");

            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            var release = UpdateRelease.Parse(json);
            if (release is null)
                return new UpdateCheckResult(UpdateStatus.Failed, current, null, "La risposta di GitHub non contiene una versione installabile.");

            Log.Information("Aggiornamenti: installata {Current}, ultima pubblicata {Latest}", current, release.Version);
            return release.Version.IsNewerThan(current)
                ? new UpdateCheckResult(UpdateStatus.Available, current, release, null)
                : new UpdateCheckResult(UpdateStatus.UpToDate, current, release, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, current, null, "GitHub non risponde (tempo scaduto). Controlla la connessione a Internet.");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "Aggiornamenti: controllo non riuscito");
            return new UpdateCheckResult(UpdateStatus.Failed, current, null, "Impossibile contattare GitHub. Controlla la connessione a Internet.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error(ex, "Aggiornamenti: errore imprevisto nel controllo");
            return new UpdateCheckResult(UpdateStatus.Failed, current, null, "Controllo non riuscito: " + ex.Message);
        }
    }

    /// <summary>
    /// Scarica il Setup in una cartella temporanea e ne verifica l'SHA256 (scritto nelle note della release). Il file viene scritto
    /// come <c>.part</c> e rinominato solo se il controllo riesce: un file corrotto non resta mai pronto all'uso.
    /// Lancia <see cref="InvalidDataException"/> (SHA256 mancante o diverso) o <see cref="HttpRequestException"/>.
    /// </summary>
    public async Task<string> DownloadAsync(UpdateRelease release, IProgress<double> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(release.Sha256))
            throw new InvalidDataException("La release non riporta l'SHA256 del file: per sicurezza non si installa.");

        var dir = Path.Combine(Path.GetTempPath(), "JustSlides-update");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"JustSlides-Setup-{release.Version}.exe");
        var part = target + ".part";
        TryDelete(target);
        TryDelete(part);

        try
        {
            using var client = CreateClient(DownloadTimeout, github: false);
            using var response = await client.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? release.Size;
            using var sha = SHA256.Create();
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            {
                var buffer = new byte[1 << 20];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0)
                        progress.Report(Math.Min(done * 100.0 / total, 100));
                }

                sha.TransformFinalBlock([], 0, 0);
            }

            var actual = Convert.ToHexString(sha.Hash!);
            if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("Aggiornamenti: SHA256 diverso (atteso {Expected}, ottenuto {Actual})", release.Sha256, actual);
                throw new InvalidDataException("Il file scaricato non corrisponde a quello pubblicato (SHA256 diverso): scartato.");
            }

            File.Move(part, target, overwrite: true);
            Log.Information("Aggiornamenti: {File} scaricato e verificato ({Sha})", target, actual);
            return target;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>
    /// Avvia il Setup (finestra di avanzamento senza domande) e il chiamante deve chiudere subito l'app: il Setup aspetta la fine di
    /// questo processo (<c>/WAITPID</c>), aggiorna sopra l'installazione e rilancia JustSlides da solo (<c>/RESTART=1</c>).
    /// </summary>
    public static void LaunchInstaller(string setupPath)
    {
        var perMachine = AppContext.BaseDirectory.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), StringComparison.OrdinalIgnoreCase);

        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "JustSlides", "logs");
        Directory.CreateDirectory(logDir);

        var arguments = string.Join(' ',
            "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/NOCANCEL",
            perMachine ? "/ALLUSERS" : "/CURRENTUSER",
            $"/WAITPID={Environment.ProcessId}", "/RESTART=1",
            $"/LOG=\"{Path.Combine(logDir, "update-setup.log")}\"",
            $"/DIR=\"{AppContext.BaseDirectory.TrimEnd('\\')}\"");

        Log.Information("Aggiornamenti: avvio del Setup {Setup} {Args}", setupPath, arguments);
        Process.Start(new ProcessStartInfo(setupPath, arguments) { UseShellExecute = false })?.Dispose();
    }

    private static HttpClient CreateClient(TimeSpan timeout, bool github)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"JustSlides/{CurrentVersion}");
        if (github)
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "File temporaneo non rimosso: {Path}", path);
        }
    }
}
