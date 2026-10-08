using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using Regia.Core.Ppt;
using Serilog;

namespace Regia.Output.Ppt;

/// <summary>
/// "Verifica PowerPoint" (Impostazioni): raccoglie installazione, e l'esito di una prova
/// tecnica fatta da PptHost, poi lascia decidere l'esito a <see cref="PptReadiness"/> (logica pura, testata).
/// La regia non parla mai direttamente con PowerPoint: la prova passa da PptHost.
/// </summary>
public static class PowerPointCheck
{
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

        var facts = new PptCheckFacts { Install = install };
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
}
