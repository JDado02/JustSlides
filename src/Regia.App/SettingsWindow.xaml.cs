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
        _viewModel = viewModel;
        DataContext = viewModel;

        // Una verifica di PowerPoint ancora in corso non deve restare appesa alla finestra chiusa.
        Closed += (_, _) => _viewModel.CancelPptCheck();
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
}
