using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Regia.App.ViewModels;
using Regia.Core.Media;
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

        // Si sta scrivendo in un campo (sessione, relatore): Spazio, Invio e frecce sono del campo, non GO o pagina.
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
        else if (Keyboard.Modifiers == ModifierKeys.None && IsPageKey(key) && _viewModel.State is not (WaveState.Tappo or WaveState.Errore))
        {
            // Frecce / PageUp / PageDown: pagina o slide (solo con qualcosa in onda; altrimenti scorrono la lista).
            if (key is Key.Right or Key.Down or Key.PageDown)
                _viewModel.NextPageCommand.Execute(null);
            else
                _viewModel.PreviousPageCommand.Execute(null);

            e.Handled = true;
        }
        else if (key == Key.F12 && ctrlShift)
        {
            _viewModel.SimulateErrorCommand.Execute(null);
            e.Handled = true;
        }
    }

    private static bool IsPageKey(Key key) =>
        key is Key.Right or Key.Left or Key.Down or Key.Up or Key.PageDown or Key.PageUp;

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
