using System.Windows;
using System.Windows.Input;
using Regia.App.ViewModels;

namespace Regia.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();

        // Finestra grande: quasi tutta l'area di lavoro (senza barra delle applicazioni), così si scorre il meno possibile.
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, Math.Max(MinWidth, area.Width - 40));
        Height = Math.Min(area.Height * 0.94, 1000);

        _viewModel = viewModel;
        DataContext = viewModel;

        // Una verifica di PowerPoint ancora in corso non deve restare appesa alla finestra chiusa.
        Closed += (_, _) =>
        {
            _viewModel.CancelPptCheck();
            _viewModel.Updates?.Cancel();
        };
    }

    // Con "Premi un tasto" attivo ogni tasto va all'editor dei tasti (anche Spazio e Invio, che altrimenti premerebbero un pulsante).
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        if (_viewModel.Keys is not { IsCapturing: true } keys)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (keys.HandleCapturedKey(KeyChordInput.FromKey(key, Keyboard.Modifiers)))
            e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // Campo dei secondi: solo cifre (anche incollando).
    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnPasteDigitsOnly(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.All(char.IsAsciiDigit))
            e.CancelCommand();
    }
}
