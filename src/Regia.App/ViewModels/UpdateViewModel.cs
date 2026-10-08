using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Regia.App.Services;
using Regia.Core.Update;
using Serilog;

namespace Regia.App.ViewModels;

/// <summary>
/// Sezione "Aggiornamenti" delle Impostazioni. Tutto su richiesta: controlla, scarica (con verifica), installa e riapre.
/// L'installazione è permessa solo con la regia a Tappo/Errore e solo dall'app installata dal Setup.
/// </summary>
public sealed partial class UpdateViewModel : ObservableObject
{
    private readonly UpdateService _service;
    private readonly Func<bool> _canInstallNow;
    private UpdateRelease? _release;
    private CancellationTokenSource? _cts;

    public UpdateViewModel(UpdateService service, Func<bool> canInstallNow)
    {
        _service = service;
        _canInstallNow = canInstallNow;
    }

    public string InstalledText => $"Versione installata: {UpdateService.CurrentVersion}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CheckButtonText))]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private bool _statusIsError;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private bool _showProgress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallButtonText))]
    [NotifyCanExecuteChangedFor(nameof(InstallCommand))]
    private bool _hasUpdate;

    [ObservableProperty]
    private string _notes = "";

    public bool HasStatus => !string.IsNullOrEmpty(Status);

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(HasStatus));

    public string CheckButtonText => IsBusy ? "Attendere..." : "Verifica aggiornamenti";

    public string InstallButtonText => _release is null ? "Scarica e installa" : $"Scarica e installa la versione {_release.Version}";

    /// <summary>Alla chiusura della finestra: si smette di aspettare controllo o download in corso.</summary>
    public void Cancel() => _cts?.Cancel();

    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsBusy = true;
        HasUpdate = false;
        _release = null;
        ShowProgress = false;
        SetStatus("Controllo su GitHub...", error: false);
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var result = await _service.CheckAsync(cts.Token);
            switch (result.Status)
            {
                case UpdateStatus.Available:
                    _release = result.Release;
                    Notes = result.Release!.Notes.Trim();
                    HasUpdate = true;
                    OnPropertyChanged(nameof(InstallButtonText));
                    SetStatus($"Disponibile la versione {result.Release.Version} (hai la {result.Current}).", error: false);
                    break;

                case UpdateStatus.UpToDate:
                    SetStatus($"JustSlides è aggiornato (versione {result.Current}).", error: false);
                    break;

                default:
                    SetStatus(result.Error ?? "Controllo non riuscito.", error: true);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // Finestra chiusa durante il controllo: niente da mostrare.
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCheck() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (_release is not { } release)
            return;

        if (!UpdateService.IsInstalledCopy)
        {
            SetStatus("L'installazione automatica funziona solo con JustSlides installato dal Setup (questa è una copia di sviluppo).", error: true);
            return;
        }

        if (!_canInstallNow())
        {
            SetStatus("Riporta prima la regia al Tappo: con qualcosa in onda non si può aggiornare.", error: true);
            return;
        }

        IsBusy = true;
        ShowProgress = true;
        Progress = 0;
        SetStatus("Download dell'aggiornamento...", error: false);
        var cts = _cts = new CancellationTokenSource();
        string setupPath;
        try
        {
            var progress = new Progress<double>(value =>
            {
                Progress = value;
                Status = $"Download dell'aggiornamento... {value:0}%";
            });
            setupPath = await _service.DownloadAsync(release, progress, cts.Token);
        }
        catch (OperationCanceledException)
        {
            IsBusy = false;
            return;
        }
        catch (InvalidDataException ex)
        {
            Fail(ex.Message);
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warning(ex, "Aggiornamenti: download non riuscito");
            Fail("Download non riuscito: " + ex.Message);
            return;
        }

        Progress = 100;
        SetStatus("Download completato e verificato.", error: false);

        // Il download può durare: lo stato della regia si ricontrolla subito prima di chiudere.
        if (!_canInstallNow())
        {
            Fail("Nel frattempo la regia non è più a Tappo: aggiornamento rimandato. Riporta la regia al Tappo e riprova.");
            return;
        }

        var answer = MessageBox.Show(Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current.MainWindow!,
            $"Installare JustSlides {release.Version}?\n\n" +
            "• JustSlides si CHIUDE ora.\n" +
            "• Compare una finestra \"Installazione di JustSlides\" con la barra di avanzamento: lasciala finire (pochi secondi).\n" +
            "• JustSlides si RIAPRE DA SOLO con la versione nuova e riprende l'ultimo show.\n\n" +
            "Non spegnere il PC e non riaprire JustSlides a mano durante l'installazione.",
            "Aggiornamento di JustSlides", MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.OK);
        if (answer != MessageBoxResult.OK)
        {
            Fail("Aggiornamento annullato. Il file scaricato resta pronto: premi di nuovo \"Scarica e installa\" quando vuoi.");
            return;
        }

        try
        {
            SetStatus("Chiusura di JustSlides... l'installazione parte da sola e JustSlides si riapre da solo.", error: false);
            UpdateService.LaunchInstaller(setupPath);
            Log.Information("Aggiornamenti: chiusura per installare la versione {Version}", release.Version);
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Aggiornamenti: impossibile avviare il Setup");
            Fail("Impossibile avviare l'installazione: " + ex.Message);
        }
    }

    private bool CanInstall() => HasUpdate && !IsBusy;

    private void Fail(string message)
    {
        IsBusy = false;
        ShowProgress = false;
        SetStatus(message, error: true);
    }

    private void SetStatus(string message, bool error)
    {
        Status = message;
        StatusIsError = error;
    }
}
