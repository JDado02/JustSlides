using Serilog;

namespace Regia.Core.Input;

/// <summary>Lettura/scrittura atomica di <c>keys.json</c> (tasti globali dell'app, fuori dal file show).</summary>
public sealed class KeyMapStore
{
    private readonly string _path;

    public KeyMapStore(string path)
    {
        _path = path;
    }

    public static string DefaultPath =>
        System.IO.Path.Combine(Settings.SettingsStore.DefaultDirectory, "keys.json");

    /// <summary>Mai un'eccezione: file assente o corrotto → tasti predefiniti (il corrotto resta come .bak).</summary>
    public KeyMap Load()
    {
        if (!File.Exists(_path))
            return KeyMap.Default;

        try
        {
            return KeyMap.FromJson(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "keys.json non leggibile ({Path}): uso i tasti predefiniti", _path);
            try
            {
                File.Copy(_path, _path + ".bak", overwrite: true);
            }
            catch (Exception copyEx)
            {
                Log.Warning(copyEx, "Impossibile salvare la copia .bak di keys.json");
            }

            return KeyMap.Default;
        }
    }

    public void Save(KeyMap map)
    {
        var dir = System.IO.Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, map.ToJson());

        if (File.Exists(_path))
            File.Replace(tmp, _path, null);
        else
            File.Move(tmp, _path);
    }
}
