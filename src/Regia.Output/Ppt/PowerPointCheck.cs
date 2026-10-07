using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Regia.Core.Ppt;
using Serilog;

namespace Regia.Output.Ppt;

/// <summary>
/// "Verifica PowerPoint" (Impostazioni): raccoglie installazione, stato di attivazione (OSPP.VBS di Office) e l'esito di una prova
/// tecnica fatta da PptHost, poi lascia decidere l'esito a <see cref="PptReadiness"/> (logica pura, testata).
/// La regia non parla mai direttamente con PowerPoint: la prova passa da PptHost.
/// </summary>
public static class PowerPointCheck
{
    private static readonly TimeSpan OsppTimeout = TimeSpan.FromSeconds(15);

    public static async Task<PptCheckReport> RunAsync(PptHostClient client, CancellationToken cancellationToken)
    {
        Log.Information("Verifica PowerPoint: avviata dall'operatore");

        var install = ReadInstall();
        if (install is null)
        {
            Log.Warning("Verifica PowerPoint: PowerPoint non risulta installato");
            return PptReadiness.Evaluate(new PptCheckFacts());
        }

        if (client.ForeignPowerPointRunning)
        {
            Log.Warning("Verifica PowerPoint: c'è un PowerPoint non nostro già aperto");
            return PptReadiness.Evaluate(new PptCheckFacts { Install = install, ForeignInstance = true });
        }

        var (license, detail) = await ReadLicenseAsync(install, cancellationToken);
        Log.Information("Verifica PowerPoint: attivazione {State} ({Detail})", license, detail ?? "-");

        var facts = new PptCheckFacts { Install = install, License = license, LicenseDetail = detail };
        try
        {
            var result = await client.SelfTestAsync(cancellationToken);
            facts = facts with
            {
                SelfTest = result,
                Test = result.FailedStep switch
                {
                    null => PptTestOutcome.Passed,
                    PptSelfTestSteps.Dialog => PptTestOutcome.Blocked,
                    _ => PptTestOutcome.Failed
                }
            };
        }
        catch (PptException ex) when (ex.IsForeignInstance)
        {
            facts = facts with { ForeignInstance = true };
        }
        catch (PptException ex) when (ex.Code == PptErrors.NotInstalled)
        {
            facts = new PptCheckFacts();
        }
        catch (PptException ex) when (ex.Code == PptException.RequestTimeout)
        {
            facts = facts with { Test = PptTestOutcome.TimedOut, TestError = "nessuna risposta nel tempo massimo (" + ex.Message + ")." };
        }
        catch (PptException ex) when (ex.Code is PptException.HostFailure or PptException.HostStartFailed)
        {
            facts = facts with { Test = PptTestOutcome.HostFailed, TestError = "PowerPoint non risponde più o è stato terminato dal watchdog (" + ex.Message + ")." };
        }
        catch (PptException ex)
        {
            facts = facts with { Test = PptTestOutcome.Failed, TestError = ex.Message };
        }

        var report = PptReadiness.Evaluate(facts);
        Log.Information("Verifica PowerPoint: esito {Level} — {Summary}", report.Level, report.Summary);
        return report;
    }

    /// <summary>Cerca POWERPNT.EXE come fa Windows (App Paths), senza avviarlo; versione dal file, piattaforma e prodotti dal registro di Click-to-Run.</summary>
    public static PptInstallInfo? ReadInstall()
    {
        var exe = FindExe();
        if (exe is null)
            return null;

        string? fileVersion = null;
        try
        {
            fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Versione di POWERPNT.EXE non leggibile");
        }

        string? platform = null, products = null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Office\ClickToRun\Configuration");
            platform = key?.GetValue("Platform") as string;
            products = key?.GetValue("ProductReleaseIds") as string;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Configurazione Click-to-Run non leggibile");
        }

        return new PptInstallInfo(exe, fileVersion, platform, products);
    }

    private static string? FindExe()
    {
        const string appPaths = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\POWERPNT.EXE";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(appPaths);
                    if (key?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                        return path.Trim('"');
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "App Paths di POWERPNT.EXE non leggibile");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Stato di attivazione via OSPP.VBS (script di Office nella stessa cartella di POWERPNT.EXE). Non c'è un'API documentata più
    /// diretta; con le versioni dello Store o senza lo script resta "non leggibile", mai un falso "attivato".
    /// </summary>
    private static async Task<(PptLicenseState State, string? Detail)> ReadLicenseAsync(PptInstallInfo install, CancellationToken cancellationToken)
    {
        var script = Path.Combine(Path.GetDirectoryName(install.ExePath) ?? "", "OSPP.VBS");
        if (!File.Exists(script))
        {
            Log.Information("Verifica PowerPoint: OSPP.VBS non trovato accanto a POWERPNT.EXE, attivazione non leggibile");
            return (PptLicenseState.Unknown, null);
        }

        try
        {
            var startInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cscript.exe"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("//Nologo");
            startInfo.ArgumentList.Add(script);
            startInfo.ArgumentList.Add("/dstatus");

            using var process = Process.Start(startInfo);
            if (process is null)
                return (PptLicenseState.Unknown, null);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(OsppTimeout);
            try
            {
                var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                _ = process.StandardError.ReadToEndAsync(timeout.Token);   // si svuota per non bloccare il processo
                var output = await outputTask;
                await process.WaitForExitAsync(timeout.Token);
                var entries = OsppParser.Parse(output);
                var appSkus = ReadPowerPointSkus();
                Log.Information("Verifica PowerPoint: OSPP elenca {Count} licenze ({Skus}); SKU in uso da PowerPoint: {Used}",
                    entries.Count, string.Join(", ", entries.Select(e => e.SkuId ?? "?")), appSkus.Count == 0 ? "non letti" : string.Join(", ", appSkus));
                return OsppParser.Resolve(entries, appSkus);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Log.Warning("Verifica PowerPoint: OSPP.VBS non ha risposto in {Seconds} s", OsppTimeout.TotalSeconds);
                TryKill(process);
                return (PptLicenseState.Unknown, null);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warning(ex, "Verifica PowerPoint: lettura dell'attivazione non riuscita");
            return (PptLicenseState.Unknown, null);
        }
    }

    /// <summary>
    /// SKU della licenza che PowerPoint sta usando, dal registro dell'utente (valore non documentato da Microsoft, ma è ciò che Office
    /// stesso scrive per sapere con quale licenza lavora ogni applicazione). Vuoto se non leggibile.
    /// </summary>
    private static IReadOnlyList<string> ReadPowerPointSkus()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Office\16.0\Common\Licensing\CurrentSkuIdAggregationForApp");
            return key?.GetValue("PowerPoint") is string value
                ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : [];
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "SKU in uso da PowerPoint non leggibili");
            return [];
        }
    }

    /// <summary>Termina il solo cscript avviato qui (mai per nome).</summary>
    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "cscript non terminabile");
        }
    }
}
