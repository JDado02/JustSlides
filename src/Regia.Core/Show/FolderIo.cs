namespace Regia.Core.Show;

/// <summary>Elenco ricorsivo dei file della cartella collegata. Sempre da un thread di background.</summary>
public static class FolderScanner
{
    /// <summary>I file della cartella, oppure <c>null</c> se non è raggiungibile (non esiste, rete caduta, permessi).</summary>
    public static IReadOnlyList<SourceFile>? Scan(string root)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                return null;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System
            };

            var result = new List<SourceFile>();
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                var info = new FileInfo(path);
                result.Add(new SourceFile(Path.GetRelativePath(root, path), info.Length, info.LastWriteTimeUtc));
            }

            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Copia di un file con scrittura su <c>.part</c> e rinomina finale. È una copia a flusso (NON <c>File.Copy</c>):
/// non porta con sé il flusso alternativo <c>Zone.Identifier</c>, quindi la copia locale non ha mai il
/// Mark-of-the-Web e PowerPoint non la apre in Visualizzazione protetta.
/// </summary>
public static class FileCopier
{
    private const int BufferSize = 1 << 20;

    /// <summary>Spazio da lasciare libero sul disco della cache oltre al file copiato.</summary>
    public const long FreeSpaceMargin = 200L * 1024 * 1024;

    /// <summary>Copia su <c>destination + ".part"</c> e restituisce il percorso del file parziale (da spostare con <see cref="Commit"/>).</summary>
    public static async Task<string> CopyToPartAsync(string source, string destination, Action<int>? progress, CancellationToken token)
    {
        var dir = Path.GetDirectoryName(destination);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var part = destination + ".part";
        try
        {
            // Il sorgente si apre in condivisione piena: un file aperto in PowerPoint si legge comunque.
            await using var input = new FileStream(source, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);

            var length = input.Length;
            EnsureFreeSpace(destination, length);

            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None,
                             BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[BufferSize];
                long done = 0;
                var lastPercent = -1;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;

                    var percent = length > 0 ? (int)(done * 100 / length) : 100;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress?.Invoke(percent);
                    }
                }
            }

            return part;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>Rende definitiva la copia (sostituisce quella vecchia). Può fallire se la vecchia è aperta da qualcuno.</summary>
    public static void Commit(string part, string destination) => File.Move(part, destination, overwrite: true);

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Resta un file orfano nella cache: non è un problema, sarà sovrascritto o cancellato con la cache.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Nome libero nella cartella: "file.pdf", poi "file (2).pdf"...</summary>
    public static string UniqueName(string directory, string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        var candidate = fileName;

        for (var i = 2; File.Exists(Path.Combine(directory, candidate)); i++)
            candidate = $"{name} ({i}){ext}";

        return candidate;
    }

    private static void EnsureFreeSpace(string destination, long needed)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(destination));
            if (string.IsNullOrEmpty(root))
                return;

            var free = new DriveInfo(root).AvailableFreeSpace;
            if (free < needed + FreeSpaceMargin)
                throw new IOException($"Spazio insufficiente sul disco della cache ({free / (1024 * 1024)} MB liberi, ne servono {needed / (1024 * 1024)})");
        }
        catch (ArgumentException)
        {
            // Percorso non interpretabile come unità: si prova a copiare lo stesso.
        }
    }
}
