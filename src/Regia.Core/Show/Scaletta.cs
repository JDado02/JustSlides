using System.Collections.ObjectModel;
using Regia.Core.Media;

namespace Regia.Core.Show;

/// <summary>
/// La scaletta: lista ordinata di <see cref="MediaItem"/> (logica pura, testata). La voce fissa "Schermata di
/// prova" sta sempre in testa, non si sposta e non si salva.
/// </summary>
public sealed class Scaletta
{
    public Scaletta()
    {
        Items.Add(MediaItem.TestPattern);
    }

    public ObservableCollection<MediaItem> Items { get; } = [];

    /// <summary>Sessione proposta per un file: la sottocartella (le annidate unite da " / "); vuota per la radice.</summary>
    public static string SessionFromRelativePath(string relativePath)
    {
        var dir = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (string.IsNullOrEmpty(dir))
            return "";

        return string.Join(" / ", dir.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries));
    }

    public MediaItem? Find(string relativePath) =>
        Items.FirstOrDefault(i => !i.IsFixed && string.Equals(i.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Aggiunge una voce nuova dopo l'ultima della stessa sessione, altrimenti in fondo.</summary>
    public void Insert(MediaItem item)
    {
        var index = Items.Count;

        if (item.Session.Length > 0)
        {
            for (var i = Items.Count - 1; i >= 1; i--)
            {
                if (string.Equals(Items[i].Session, item.Session, StringComparison.OrdinalIgnoreCase))
                {
                    index = i + 1;
                    break;
                }
            }
        }

        Items.Insert(index, item);
    }

    /// <summary>Sposta <paramref name="item"/> davanti (o dopo) a <paramref name="target"/>. Mai sopra la voce fissa.</summary>
    public bool Move(MediaItem item, MediaItem target, bool after)
    {
        if (item.IsFixed || ReferenceEquals(item, target))
            return false;

        var from = Items.IndexOf(item);
        var to = Items.IndexOf(target);
        if (from < 0 || to < 0)
            return false;

        if (target.IsFixed)
            to = 1;
        else if (after)
            to++;

        // Dopo la rimozione gli indici successivi scalano di uno.
        if (from < to)
            to--;

        to = Math.Clamp(to, 1, Items.Count - 1);
        if (to == from)
            return false;

        Items.Move(from, to);
        return true;
    }

    /// <summary>
    /// Toglie una voce sparita dalla cartella. Se è in onda non si tocca: si segna <see cref="MediaItem.RemovedFromSource"/>
    /// e sparisce con <see cref="FlushPending"/>. Restituisce true se è stata tolta subito.
    /// </summary>
    public bool Remove(MediaItem item, MediaItem? live)
    {
        if (item.IsFixed)
            return false;

        if (ReferenceEquals(item, live))
        {
            item.RemovedFromSource = true;
            return false;
        }

        return Items.Remove(item);
    }

    /// <summary>Esegue le rimozioni rimandate (tranne la voce in onda) e restituisce le voci tolte.</summary>
    public IReadOnlyList<MediaItem> FlushPending(MediaItem? live)
    {
        var gone = Items.Where(i => i.RemovedFromSource && !ReferenceEquals(i, live)).ToList();
        foreach (var item in gone)
            Items.Remove(item);

        return gone;
    }

    /// <summary>Voci con un aggiornamento della copia rimandato perché erano in onda (e ora non lo sono più).</summary>
    public IReadOnlyList<MediaItem> PendingUpdates(MediaItem? live) =>
        Items.Where(i => i.PendingUpdate && !ReferenceEquals(i, live)).ToList();

    public List<ShowItemDto> ToDtos() =>
        Items.Where(i => !i.IsFixed && !i.RemovedFromSource)
            .Select(i => new ShowItemDto
            {
                RelativePath = i.RelativePath,
                Session = i.Session,
                SessionEdited = i.SessionEdited,
                Speaker = i.Speaker,
                Volume = i.Volume,
                VideoEnd = i.VideoEnd,
                Excluded = i.Excluded,
                SourceSize = i.SourceSize,
                SourceMtimeUtc = i.SourceMtimeUtc
            })
            .ToList();

    /// <summary>Crea la voce per un file dello show: <paramref name="cacheRoot"/> è la cartella della copia locale.</summary>
    public static MediaItem CreateItem(ShowItemDto dto, string cacheRoot)
    {
        var path = Path.Combine(cacheRoot, dto.RelativePath);
        var kind = MediaKindDetector.FromPath(path);
        return new MediaItem(path, kind)
        {
            RelativePath = dto.RelativePath,
            Session = dto.Session,
            SessionEdited = dto.SessionEdited,
            Speaker = dto.Speaker,
            Volume = dto.Volume,
            VideoEnd = dto.VideoEnd,
            Excluded = dto.Excluded,
            SourceSize = dto.SourceSize,
            SourceMtimeUtc = dto.SourceMtimeUtc,
            CopyState = kind == MediaKind.Unknown ? CopyState.Unsupported : CopyState.Copying
        };
    }

    /// <summary>Riempie la scaletta dallo show salvato, nell'ordine salvato.</summary>
    public void Load(IEnumerable<ShowItemDto> dtos, string cacheRoot)
    {
        for (var i = Items.Count - 1; i >= 1; i--)
            Items.RemoveAt(i);

        foreach (var dto in dtos.Where(d => !string.IsNullOrWhiteSpace(d.RelativePath)))
        {
            if (Find(dto.RelativePath) is null)
                Items.Add(CreateItem(dto, cacheRoot));
        }
    }
}
