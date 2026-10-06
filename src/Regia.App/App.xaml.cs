using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Regia.App.ViewModels;
using Regia.Core.Logging;
using Regia.Core.Settings;
using Regia.Output;
using Regia.Output.Interop;
using Regia.Output.Tappo;
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

            var store = new SettingsStore(SettingsStore.DefaultPath);
            var settings = store.Load();

            _host = Host.CreateDefaultBuilder()
                .UseSerilog(Log.Logger)
                .ConfigureServices(services =>
                {
                    services.AddSingleton(store);
                    services.AddSingleton(settings);
                    services.AddSingleton<VlcService>();
                    services.AddSingleton<OutputHost>();
                    services.AddSingleton<MainViewModel>();
                    services.AddSingleton<MainWindow>();
                })
                .Build();

            await _host.StartAsync();

            _viewModel = _host.Services.GetRequiredService<MainViewModel>();
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
