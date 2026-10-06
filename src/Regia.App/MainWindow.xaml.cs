using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Regia.App.ViewModels;
using Regia.Core.Wave;

namespace Regia.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    /// <summary>Permette la chiusura senza conferma (usata allo spegnimento controllato).</summary>
    public bool SkipCloseConfirmation { get; set; }

    // Tasti locali alla finestra di regia. Gli hotkey globali arrivano nella Milestone 7.
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrlShift = Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift);

        if (key == Key.Escape)
        {
            _viewModel.PanicCommand.Execute(null);
            e.Handled = true;
        }
        else if (key is Key.Space or Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            _viewModel.GoCommand.Execute(null);
            e.Handled = true;
        }
        else if (key == Key.F12 && ctrlShift)
        {
            _viewModel.SimulateErrorCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        // Si chiede conferma solo se qualcosa è (o sta andando) in onda.
        if (SkipCloseConfirmation || _viewModel.State is WaveState.Tappo or WaveState.Errore)
            return;

        var answer = MessageBox.Show(
            this,
            "Qualcosa è ancora in onda. Chiudere davvero la regia?",
            "Regia",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
            e.Cancel = true;
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }
}
