using Regia.Core.Input;
using Regia.Core.Media;
using Regia.Core.Wave;

namespace Regia.Tests;

public sealed class ProgramTextTests
{
    private static readonly string[] Titles = ["Benvenuti", "Risultati 2025", "", "Conclusioni"];

    [Fact]
    public void NextPage_Slide_NumeroETitolo() =>
        Assert.Equal("Prossima: 2/4 – Risultati 2025", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 4), Titles));

    [Fact]
    public void NextPage_SlideSenzaTitolo_SoloNumero() =>
        Assert.Equal("Prossima: 3/4", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(2, 4), Titles));

    [Fact]
    public void NextPage_TitoliNonCoerentiConLeSlide_SoloNumero()
    {
        // 4 titoli ma 5 slide: l'indice potrebbe cadere su una slide sbagliata → niente titolo.
        Assert.Equal("Prossima: 2/5", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 5), Titles));
    }

    [Fact]
    public void NextPage_SenzaTitoli_SoloNumero()
    {
        Assert.Equal("Prossima: 2/4", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 4), null));
        Assert.Equal("Prossima: 2/4", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 4), []));
    }

    [Fact]
    public void NextPage_SaltaLeSlideNascoste_ConIlTitoloGiusto()
    {
        // Slide 2 e 3 nascoste: dopo la 1 PowerPoint va alla 4.
        string[] titles = ["Uno", "Due (nascosta)", "Tre (nascosta)", "Quattro", "Cinque"];

        Assert.Equal("Prossima: 4/5 – Quattro", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 5), titles, [2, 3]));
    }

    [Fact]
    public void NextPage_NascosteInFondo_LUltimaMostrataEUltima()
    {
        // Slide 4 e 5 nascoste: dalla 3 "avanti" porta al Tappo.
        Assert.Equal("Ultima slide: avanti = Tappo", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(3, 5), null, [4, 5]));
    }

    [Fact]
    public void NextPage_NascosteSoloPerLePpt()
    {
        // Un PDF non ha slide nascoste: la lista, se arrivasse, non conta.
        Assert.Equal("Prossima: pagina 2/10", ProgramText.NextPage(MediaKind.Pdf, new PageInfo(1, 10), null, [2]));
    }

    [Fact]
    public void NextPage_UltimaSlide_AvantiPortaAlTappo() =>
        Assert.Equal("Ultima slide: avanti = Tappo", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(4, 4), Titles));

    [Fact]
    public void NextPage_Pdf_Pagina() =>
        Assert.Equal("Prossima: pagina 3/10", ProgramText.NextPage(MediaKind.Pdf, new PageInfo(2, 10), null));

    [Fact]
    public void NextPage_UltimaPaginaPdf() =>
        Assert.Equal("Ultima pagina", ProgramText.NextPage(MediaKind.Pdf, new PageInfo(10, 10), null));

    [Fact]
    public void NextPage_ContenutoSenzaPagine_Vuoto()
    {
        Assert.Equal("", ProgramText.NextPage(MediaKind.Video, null, null));
        Assert.Equal("", ProgramText.NextPage(MediaKind.Image, null, null));
        Assert.Equal("", ProgramText.NextPage(MediaKind.Ppt, new PageInfo(1, 0), null));
    }

    [Fact]
    public void ButtonLabel_SegueITastiConfigurati()
    {
        Assert.Equal("GO  (L / Spazio / Invio)", ProgramText.ButtonLabel("GO", KeyMap.Default, KeyAction.Go));
        Assert.Equal("PANIC  (Esc)", ProgramText.ButtonLabel("PANIC", KeyMap.Default, KeyAction.Panic));

        var custom = KeyMap.Default.Without(KeyAction.Go, new KeyChord(KeyChord.VkSpace)).Without(KeyAction.Go, new KeyChord(KeyChord.VkEnter));
        Assert.Equal("GO  (L)", ProgramText.ButtonLabel("GO", custom, KeyAction.Go));
    }

    [Fact]
    public void ButtonLabel_SenzaTasti_SoloIlNome() =>
        Assert.Equal("Mute", ProgramText.ButtonLabel("Mute", KeyMap.Default, KeyAction.Mute));
}
