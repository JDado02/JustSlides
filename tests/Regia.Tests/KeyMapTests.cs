using Regia.Core.Input;

namespace Regia.Tests;

public sealed class KeyMapTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "RegiaTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData('L', KeyAction.Go)]
    [InlineData(KeyChord.VkSpace, KeyAction.Go)]
    [InlineData(KeyChord.VkEnter, KeyAction.Go)]
    [InlineData(KeyChord.VkEscape, KeyAction.Panic)]
    [InlineData(KeyChord.VkRight, KeyAction.Next)]
    [InlineData(KeyChord.VkPageDown, KeyAction.Next)]
    [InlineData(KeyChord.VkLeft, KeyAction.Previous)]
    [InlineData(KeyChord.VkPageUp, KeyAction.Previous)]
    [InlineData('Q', KeyAction.SelectUp)]
    [InlineData('A', KeyAction.SelectDown)]
    public void Default_AssociaITastiPrevisti(int vk, KeyAction expected) =>
        Assert.Equal(expected, KeyMap.Default.Find(new KeyChord(vk)));

    [Fact]
    public void Default_NonHaConflitti() => Assert.Empty(KeyMap.Default.Conflicts());

    [Fact]
    public void Find_TastoNonAssociato_Null() => Assert.Null(KeyMap.Default.Find(new KeyChord('Z')));

    [Fact]
    public void Find_ModificatoriContano()
    {
        // Ctrl+L non è L.
        Assert.Null(KeyMap.Default.Find(new KeyChord('L', KeyModifiers.Control)));
    }

    [Theory]
    [InlineData("L", 'L', KeyModifiers.None)]
    [InlineData("Ctrl+Shift+L", 'L', KeyModifiers.Control | KeyModifiers.Shift)]
    [InlineData("Spazio", KeyChord.VkSpace, KeyModifiers.None)]
    [InlineData("Alt+F5", 0x74, KeyModifiers.Alt)]
    [InlineData("PagGiù", KeyChord.VkPageDown, KeyModifiers.None)]
    [InlineData("0xBF", 0xBF, KeyModifiers.None)]
    public void TryParse_TestiValidi(string text, int vk, KeyModifiers modifiers)
    {
        Assert.True(KeyChord.TryParse(text, out var chord));
        Assert.Equal(new KeyChord(vk, modifiers), chord);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Pippo+L")]
    [InlineData("Ctrl+")]
    [InlineData("F99")]
    public void TryParse_TestiNonValidi(string text) => Assert.False(KeyChord.TryParse(text, out _));

    [Fact]
    public void ToString_ERilettura_SonoInversi()
    {
        foreach (var action in Enum.GetValues<KeyAction>())
        {
            foreach (var chord in KeyMap.Default.ChordsFor(action))
            {
                Assert.True(KeyChord.TryParse(chord.ToString(), out var parsed));
                Assert.Equal(chord, parsed);
            }
        }
    }

    [Fact]
    public void With_AggiungeUnTasto()
    {
        var map = KeyMap.Default.With(KeyAction.Mute, new KeyChord('M'));

        Assert.Equal(KeyAction.Mute, map.Find(new KeyChord('M')));
    }

    [Fact]
    public void With_EscNonSiDaAdAltreAzioni()
    {
        var map = KeyMap.Default.With(KeyAction.Go, KeyMap.PanicChord);

        Assert.DoesNotContain(KeyMap.PanicChord, map.ChordsFor(KeyAction.Go));
        Assert.Equal(KeyAction.Panic, map.Find(KeyMap.PanicChord));
    }

    [Fact]
    public void With_SoloModificatore_Rifiutato()
    {
        var map = KeyMap.Default.With(KeyAction.Go, new KeyChord(0x11)); // Ctrl da solo

        Assert.Equal(KeyMap.Default.ChordsFor(KeyAction.Go), map.ChordsFor(KeyAction.Go));
    }

    [Fact]
    public void Without_EscNonSiTogliDaPanic()
    {
        var map = KeyMap.Default.Without(KeyAction.Panic, KeyMap.PanicChord);

        Assert.Contains(KeyMap.PanicChord, map.ChordsFor(KeyAction.Panic));
    }

    [Fact]
    public void Without_TogliIlTasto()
    {
        var map = KeyMap.Default.Without(KeyAction.Go, new KeyChord(KeyChord.VkSpace));

        Assert.Null(map.Find(new KeyChord(KeyChord.VkSpace)));
        Assert.Equal(KeyAction.Go, map.Find(new KeyChord('L')));
    }

    [Fact]
    public void Conflicts_StessoTastoSuDueAzioni()
    {
        var map = KeyMap.Default.With(KeyAction.Mute, new KeyChord('L'));

        var conflict = Assert.Single(map.Conflicts());
        Assert.Equal(new KeyChord('L'), conflict.Chord);
        Assert.Equal(KeyAction.Go, conflict.First);
        Assert.Equal(KeyAction.Mute, conflict.Second);
    }

    [Fact]
    public void Json_Roundtrip()
    {
        var map = KeyMap.Default
            .With(KeyAction.Mute, new KeyChord('M', KeyModifiers.Control))
            .Without(KeyAction.Go, new KeyChord(KeyChord.VkEnter));

        var loaded = KeyMap.FromJson(map.ToJson());

        foreach (var action in Enum.GetValues<KeyAction>())
            Assert.Equal(map.ChordsFor(action), loaded.ChordsFor(action));
    }

    [Fact]
    public void FromJson_Corrotto_TornaAiDefault()
    {
        var loaded = KeyMap.FromJson("{ non è json");

        Assert.Equal(KeyMap.Default.ChordsFor(KeyAction.Go), loaded.ChordsFor(KeyAction.Go));
    }

    [Fact]
    public void FromJson_AzioneMancante_UsaIlDefault()
    {
        var loaded = KeyMap.FromJson("""{ "Go": ["G"] }""");

        Assert.Equal([new KeyChord('G')], loaded.ChordsFor(KeyAction.Go));
        Assert.Equal(KeyMap.Default.ChordsFor(KeyAction.Next), loaded.ChordsFor(KeyAction.Next));
    }

    [Fact]
    public void FromJson_TastoSconosciuto_Ignorato_EscSempreSuPanic()
    {
        var loaded = KeyMap.FromJson("""{ "Go": ["Pippo", "Esc", "G"], "Panic": [] }""");

        Assert.Equal([new KeyChord('G')], loaded.ChordsFor(KeyAction.Go));
        Assert.Equal(KeyAction.Panic, loaded.Find(KeyMap.PanicChord));
    }

    [Theory]
    [InlineData(0, -1, 1, -1)]   // lista vuota
    [InlineData(5, -1, 1, 0)]    // nessuna selezione, giù → prima
    [InlineData(5, -1, -1, 4)]   // nessuna selezione, su → ultima
    [InlineData(5, 2, 1, 3)]
    [InlineData(5, 2, -1, 1)]
    [InlineData(5, 4, 1, 4)]     // bordo basso: resta
    [InlineData(5, 0, -1, 0)]    // bordo alto: resta
    public void SelectionStep_SiFermaAiBordi(int count, int current, int delta, int expected) =>
        Assert.Equal(expected, SelectionStep.Move(count, current, delta));

    [Fact]
    public void Store_FileAssente_Default()
    {
        var map = new KeyMapStore(Path.Combine(_dir, "keys.json")).Load();

        Assert.Equal(KeyMap.Default.ChordsFor(KeyAction.Go), map.ChordsFor(KeyAction.Go));
    }

    [Fact]
    public void Store_SaveLoad_Roundtrip()
    {
        var store = new KeyMapStore(Path.Combine(_dir, "keys.json"));
        var map = KeyMap.Default.With(KeyAction.PlayPause, new KeyChord('P'));

        store.Save(map);

        Assert.Equal(KeyAction.PlayPause, store.Load().Find(new KeyChord('P')));
    }

    [Fact]
    public void Store_FileCorrotto_DefaultEBak()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "keys.json");
        File.WriteAllText(path, "{{{{");

        var map = new KeyMapStore(path).Load();

        Assert.Equal(KeyMap.Default.ChordsFor(KeyAction.Go), map.ChordsFor(KeyAction.Go));
    }
}
