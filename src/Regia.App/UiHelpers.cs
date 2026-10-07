using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Regia.Output.Preflight;

namespace Regia.App;

/// <summary>Dove cadrebbe la voce trascinata rispetto a quella sotto il mouse.</summary>
public enum DropPosition
{
    None,
    Before,
    After
}

/// <summary>Proprietà associata per disegnare la linea di inserimento durante il riordino della scaletta.</summary>
public static class DragDropState
{
    public static readonly DependencyProperty DropPositionProperty = DependencyProperty.RegisterAttached(
        "DropPosition", typeof(DropPosition), typeof(DragDropState), new PropertyMetadata(DropPosition.None));

    public static DropPosition GetDropPosition(DependencyObject element) => (DropPosition)element.GetValue(DropPositionProperty);

    public static void SetDropPosition(DependencyObject element, DropPosition value) => element.SetValue(DropPositionProperty, value);
}

/// <summary>Percorso della miniatura (cache dello show) → immagine piccola per la lista. Il file non resta aperto.</summary>
public sealed class ThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var width = parameter is string text && int.TryParse(text, out var w) ? w : 160;
        return value is string path ? Thumbnails.Load(path, width) : null;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Colore del pallino del pre-flight: grigio (in corso), verde, giallo, rosso.</summary>
public sealed class PreflightBrushConverter : IValueConverter
{
    private static readonly Brush Pending = Make(0xFF, 0x6B, 0x72, 0x80);
    private static readonly Brush Ok = Make(0xFF, 0x43, 0xA0, 0x47);
    private static readonly Brush Warning = Make(0xFF, 0xF9, 0xA8, 0x25);
    private static readonly Brush Error = Make(0xFF, 0xE5, 0x39, 0x35);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        Regia.Core.Media.PreflightStatus.Ok => Ok,
        Regia.Core.Media.PreflightStatus.Warning => Warning,
        Regia.Core.Media.PreflightStatus.Error => Error,
        _ => Pending
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Make(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
