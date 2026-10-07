using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Regia.App.Services;
using Regia.App.ViewModels;
using Regia.Core.Logging;
using Regia.Core.Media;
using Regia.Core.Settings;
using Regia.Core.Show;
using Regia.Core.Wave;
using Regia.Output;
using Regia.Output.Content;
using Regia.Output.Interop;
using Regia.Output.Ppt;
using Regia.Output.Preflight;
using Regia.Output.Tappo;
using Regia.Output.Transitions;
using Serilog;

namespace Regia.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private IHost? _host;
    private MainViewModel? _viewModel;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Istanza singola: se la regia è già aperta si porta in primo piano quella.
        _singleInstanceMutex = new Mutex(initiallyOwned: true, AppInfo.MutexName, out var isFirstInstance);
        if (!isFirstInstance)
        {
            SystemIntegration.BringWindowToFront(AppInfo.MainWindowTitle);
            Shutdown();
            return;
        }

        try
        {
            LogSetup.Configure(Path.Combine(SettingsStore.DefaultDirectory, "logs"));
            Log.Information("=== Avvio Regia (versione {Version}) ===", typeof(App).Assembly.GetName().Version);

            RegisterGlobalHandlers();
            SystemIntegration.PreventSleep();

            // Lo show contiene scaletta e impostazioni dell'evento; al primo avvio dopo M5 le impostazioni
            // vengono prese dal vecchio settings.json.
            var showStore = new ShowStore(ShowStore.DefaultPath, SettingsStore.DefaultPath);
            var show = showStore.Load();
            var settings = show.Settings;

            _host = Host.CreateDefaultBuilder()
                .UseSerilog(Log.Logger)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(showStore);
                    services.AddSingleton(show);
                    services.AddSingleton(settings);
                    services.AddSingleton<Scaletta>();
                    services.AddSingleton<VlcService>();
                    services.AddSingleton<PptHostClient>();
                    services.AddSingleton<OutputHost>();
                    services.AddSingleton<WaveStateMachine>();
                    services.AddSingleton<TappoTransitions>();
                    services.AddSingleton<ITappoTransitions>(sp => sp.GetRequiredService<TappoTransitions>());
                    services.AddSingleton<ContentPresenterFactory>();
                    services.AddSingleton<IContentPresenterFactory>(sp => sp.GetRequiredService<ContentPresenterFactory>());
                    services.AddSingleton<WaveController>();
                    services.AddSingleton(sp =>
                    {
                        var wave = sp.GetRequiredService<WaveController>();
                        return new PreflightService(sp.GetRequiredService<VlcService>(), () => PreflightLoadFor(wave));
                    });
                    services.AddSingleton(sp =>
                    {
                        var wave = sp.GetRequiredService<WaveController>();
                        return new SourceFolderSync(sp.GetRequiredService<Scaletta>(), () => wave.CurrentItem, () => IsCritical(wave));
                    });
                    services.AddSingleton<ShowController>();
                    services.AddSingleton<PreviewViewModel>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            // PptHost/PowerPoint rimasti da un crash precedente (solo se sono davvero nostri).
            _host.Services.GetRequiredService<PptHostClient>().CleanupOrphans();

            await _host.StartAsync();

            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
            _host.Services.GetRequiredService<ShowController>().Start();
            _mainWindow = _host.Services.GetRequiredService<MainWindow>();
            MainWindow = _mainWindow;
            _mainWindow.Show();

            await _viewModel.InitializeAsync();

            // La finestra di simulazione, se c'è, ha rubato il focus: lo restituisco alla regia.
            _mainWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Errore irreversibile all'avvio");
            Log.CloseAndFlush();
            MessageBox.Show($"La regia non è riuscita ad avviarsi:\n\n{ex.Message}", "Regia", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Log.Information("=== Chiusura Regia ===");
            _host?.Services.GetService<ShowController>()?.Dispose(); // ultimo salvataggio dello show
            _host?.Services.GetService<PptHostClient>()?.Dispose();
            _host?.Services.GetService<OutputHost>()?.Dispose();
            _host?.Services.GetService<VlcService>()?.Dispose();
            _host?.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            _host?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Errore in chiusura");
        }
        finally
        {
            Log.CloseAndFlush();
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Mutex non posseduto da questo thread: nessuna conseguenza in chiusura.
            }

            _singleInstanceMutex?.Dispose();
        }

        base.OnExit(e);
    }

    /// <summary>Caricamento e dissolvenze: niente I/O pesante (copie, pre-flight).</summary>
    private static bool IsCritical(WaveController wave) =>
        wave.State is WaveState.Caricamento or WaveState.InTransizioneIn or WaveState.InTransizioneOut;

    /// <summary>Quanto pre-flight si può fare ora: i video (VLC) non mentre un video o un PowerPoint sono in onda.</summary>
    private static PreflightLoad PreflightLoadFor(WaveController wave)
    {
        if (IsCritical(wave))
            return PreflightLoad.None;

        return wave.CurrentItem is { Kind: MediaKind.Video or MediaKind.Ppt } ? PreflightLoad.Light : PreflightLoad.Full;
    }

    /// <summary>Handler globali: log + ritorno al Tappo, mai chiusura silenziosa.</summary>
    private void RegisterGlobalHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Eccezione non gestita sul thread UI");
            args.Handled = true;
            ReturnToTappo("eccezione non gestita (UI)", args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "Eccezione non gestita fatale (IsTerminating={Terminating})", args.IsTerminating);
            Log.CloseAndFlush();
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error(args.Exception, "Eccezione di task non osservata");
            args.SetObserved();
            ReturnToTappo("eccezione di task non osservata", args.Exception);
        };
    }

    private void ReturnToTappo(string context, Exception ex)
    {
        void Action()
        {
            if (_viewModel is not null)
                _viewModel.HandleError(context, ex);
        }

        // Può arrivare da un thread qualsiasi (es. finalizzatore dei task): si passa dal thread UI.
        if (Dispatcher.CheckAccess())
            Action();
        else
            Dispatcher.BeginInvoke(Action, DispatcherPriority.Send);
    }
}
