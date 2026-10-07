using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Regia.App.ViewModels;
using Regia.Core.Input;
using Regia.Core.Media;
using Regia.Core.Wave;

namespace Regia.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly KeyBindings _keys;

    public MainWindow(MainViewModel viewModel, KeyBindings keys)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _keys = keys;
        DataContext = viewModel;

        // Con Q/A (o le frecce) la selezione può uscire dalla zona visibile: si riporta in vista.
        ScalettaList.SelectionChanged += (_, _) =>
        {
            if (ScalettaList.SelectedItem is { } selected)
                ScalettaList.ScrollIntoView(selected);
        };
    }

    /// <summary>Permette la chiusura senza conferma (usata allo spegnimento controllato).</summary>
    public bool SkipCloseConfirmation { get; set; }

    // Tasti con la regia in primo piano; con il focus altrove (clicker, slideshow) arrivano dall'hook di tastiera.
    // Le associazioni tasto → azione sono in KeyMap (impostazioni).
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var chord = KeyChordInput.FromKey(key, Keyboard.Modifiers);

        // Si sta scrivendo in un campo (sessione, relatore): lettere, Spazio, Invio e frecce sono del campo, non azioni.
        // PANIC (Esc) resta sempre attivo. Invio conferma il testo e restituisce il focus alla scaletta.
        if (Keyboard.FocusedElement is TextBox box && key != Key.Escape)
        {
            if (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
                ScalettaList.Focus();
                e.Handled = true;
            }

            return;
        }

        if (key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            _viewModel.SimulateErrorCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (_keys.Current.Find(chord) is not { } action)
            return;

        // Tenere premuto il tasto non deve mandare in onda a raffica.
        if (e.IsRepeat && action == KeyAction.Go)
        {
            e.Handled = true;
            return;
        }

        if (_viewModel.PerformKeyAction(action))
            e.Handled = true;
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

    private async void OnAddFilesClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Aggiungi file alla cartella contenuti",
            Multiselect = true,
            Filter = "Immagini, PDF, PowerPoint e video|*.jpg;*.jpeg;*.png;*.pdf;*.pptx;*.ppt;*.ppsx;*.pps;*.mp4;*.mov;*.mkv;*.avi;*.wmv;*.m4v|Tutti i file|*.*"
        };

        if (dialog.ShowDialog(this) == true)
            await _viewModel.AddFilesAsync(dialog.FileNames);
    }

    // ---------------------------------------------------------------- scaletta: riordino e file trascinati da Esplora risorse

    private const string DragFormat = "RegiaScalettaItem";

    private Point _dragStart;
    private MediaItem? _dragItem;
    private ListBoxItem? _marked;

    private void OnListMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = ItemUnder(e.OriginalSource as DependencyObject)?.DataContext as MediaItem;
    }

    private void OnListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem is not { IsFixed: false } item)
            return;

        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragItem = null;
        try
        {
            DragDrop.DoDragDrop(ScalettaList, new DataObject(DragFormat, item), DragDropEffects.Move);
        }
        finally
        {
            ClearDropMark();
        }
    }

    private void OnListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat))
        {
            e.Effects = DragDropEffects.Move;
            MarkDropTarget(e);
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnListDragLeave(object sender, DragEventArgs e) => ClearDropMark();

    private async void OnListDrop(object sender, DragEventArgs e)
    {
        ClearDropMark();

        if (e.Data.GetData(DragFormat) is MediaItem item)
        {
            var container = ItemUnder(e.OriginalSource as DependencyObject);
            if (container?.DataContext is MediaItem target)
                _viewModel.MoveItem(item, target, after: IsLowerHalf(container, e));
            else if (ScalettaList.Items.Count > 0 && ScalettaList.Items[^1] is MediaItem last)
                _viewModel.MoveItem(item, last, after: true);

            e.Handled = true;
        }
        else if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            // File da Esplora risorse: si copiano nella cartella contenuti, poi li trova la scansione.
            e.Handled = true;
            await _viewModel.AddFilesAsync(files.Where(File.Exists));
        }
    }

    private void MarkDropTarget(DragEventArgs e)
    {
        var container = ItemUnder(e.OriginalSource as DependencyObject);
        if (container is null)
        {
            ClearDropMark();
            return;
        }

        if (!ReferenceEquals(container, _marked))
            ClearDropMark();

        _marked = container;
        DragDropState.SetDropPosition(container, IsLowerHalf(container, e) ? DropPosition.After : DropPosition.Before);
    }

    private void ClearDropMark()
    {
        if (_marked is not null)
            DragDropState.SetDropPosition(_marked, DropPosition.None);

        _marked = null;
    }

    private static bool IsLowerHalf(ListBoxItem container, DragEventArgs e) =>
        e.GetPosition(container).Y > container.ActualHeight / 2;

    private static ListBoxItem? ItemUnder(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source);

        return source as ListBoxItem;
    }

    // Cursore di scorrimento del video: finché è afferrato la posizione non si aggiorna da sola.
    private void OnScrubStart(object sender, RoutedEventArgs e) => _viewModel.BeginScrub();

    private void OnScrubEnd(object sender, RoutedEventArgs e) => _viewModel.EndScrub();

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var window = new SettingsWindow(_viewModel.CreateSettingsViewModel()) { Owner = this };
        window.ShowDialog();
    }
}
