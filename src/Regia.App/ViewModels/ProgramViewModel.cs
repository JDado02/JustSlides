using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Regia.Core.Input;
using Regia.Core.Wave;
using Regia.Output.Capture;

namespace Regia.App.ViewModels;

/// <summary>
/// Dati del pannello Program che non stanno già in <see cref="MainViewModel"/>: cosa è in onda, prossima slide e
/// etichette dei pulsanti che seguono i tasti configurati. Tutto sul thread UI (gli eventi del controller arrivano da lì).
/// </summary>
public sealed partial class ProgramViewModel : ObservableObject
{
    private readonly WaveController _wave;
    private readonly KeyBindings _keys;
    private readonly OutputCaptureService _capture;

    /// <summary>Ciò che è davvero sul monitor di output (~5 fps); null finché non c'è un fotogramma.</summary>
    [ObservableProperty]
    private ImageSource? _captureImage;

    public ProgramViewModel(WaveController wave, KeyBindings keys, OutputCaptureService capture)
    {
        _wave = wave;
        _keys = keys;
        _capture = capture;
        _capture.FrameChanged += () => CaptureImage = _capture.Frame;

        // Rimandato a fine sequenza: il controller azzera CurrentItem poco dopo aver annunciato il ritorno al Tappo.
        _wave.StateChanged += (_, _) => RefreshSoon();
        _wave.PageChanged += RefreshSoon;
        _keys.Changed += RefreshLabels;
        Refresh();
        RefreshLabels();
    }

    /// <summary>Nome del file in onda (o in entrata); vuoto a Tappo.</summary>
    [ObservableProperty]
    private string _onAirName = "";

    /// <summary>"Prossima: 8/24 – titolo" (slide), "Prossima: pagina 3/10" (PDF); vuoto per gli altri contenuti.</summary>
    [ObservableProperty]
    private string _nextText = "";

    [ObservableProperty]
    private string _goLabel = "GO";

    [ObservableProperty]
    private string _panicLabel = "PANIC";

    [ObservableProperty]
    private string _backLabel = "TORNA AL TAPPO";

    private void RefreshSoon()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
            Refresh();
        else
            dispatcher.BeginInvoke(Refresh, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var item = _wave.CurrentItem;
        OnAirName = item?.DisplayName ?? "";
        NextText = item is null ? "" : ProgramText.NextPage(item.Kind, _wave.Page, item.SlideTitles, item.HiddenSlides);
    }

    private void RefreshLabels()
    {
        GoLabel = ProgramText.ButtonLabel("GO", _keys.Current, KeyAction.Go);
        PanicLabel = ProgramText.ButtonLabel("PANIC", _keys.Current, KeyAction.Panic);
        BackLabel = ProgramText.ButtonLabel("TORNA AL TAPPO", _keys.Current, KeyAction.BackToTappo);
    }
}
