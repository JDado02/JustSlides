<p align="center">
  <img src="docs/logo.png" alt="JustSlides" width="160">
</p>

<h1 align="center">JustSlides</h1>

> ## ⬇️ [Scarica JustSlides-Setup.exe](https://github.com/JDado02/JustSlides/releases/latest/download/JustSlides-Setup.exe)
> Doppio clic sul file scaricato e segui la procedura (italiano, senza diritti di amministratore, il runtime .NET 10 è incluso).
> Tutte le versioni: [Releases](https://github.com/JDado02/JustSlides/releases).
> Windows mostrerà "Windows ha protetto il PC" perché il programma non è firmato: *Ulteriori informazioni* → *Esegui comunque*.

Software di regia congressuale per Windows 11 e Windows 10 22H2 (x64): carichi PowerPoint, PDF, video e immagini, li vedi in Preview e li mandi in onda sul monitor di Output, sempre attraverso un "Tappo" con dissolvenza morbida. Priorità assoluta: stabilità.

Dalla versione 1.1.1 gli aggiornamenti sono **automatici su richiesta**: Impostazioni → Aggiornamenti → *Verifica aggiornamenti* scarica e installa la versione nuova e riapre JustSlides da solo (non serve più scaricare nulla a mano da qui).

Novità della versione 1.2.1: il **Tappo immagine** ha la scelta **Fissa** (una sola immagine, sempre quella) o **Loop** (più immagini che scorrono); la striscia delle pagine del PDF in Preview non si allunga più verso il basso.

Novità della versione 1.2.0: un **PDF che arriva all'ultima pagina e va "avanti" ancora torna da solo al Tappo** (come PowerPoint e video); nella Preview dei PDF le pagine si scelgono con un clic e appaiono grandi; il **Tappo immagine può avere più immagini in loop** (secondi per immagine a scelta); i secondi del Tappo si scrivono con la tastiera; Impostazioni più grandi e più essenziali; la verifica PowerPoint non legge più la licenza (solo prova tecnica); la barra video e il volume compaiono solo quando servono.

Novità della versione 1.1.0: il **Tappo può essere una presentazione PowerPoint** (esportata una volta in immagini: in loop oppure ferma su una slide scelta con le frecce a schermo, ricordata anche dopo un'onda); le presentazioni con "ripeti fino a Esc" ricominciano dalla prima slide; la riga "A fine video" compare solo con un video selezionato.

- Uso in sala: [OPERATIVO.md](OPERATIVO.md) (installare, aggiornare, checklist pre-evento, emergenze).
- Specifica e regole di sviluppo: [CLAUDE.md](CLAUDE.md); stato e decisioni: [PROGRESS.md](PROGRESS.md).

## Sviluppo
Stack: C# / WPF / .NET 10. Build e test: `dotnet build Regia.slnx` e `dotnet test --project tests/Regia.Tests`.
Pacchetto di installazione: `tools\Build-Installer.ps1` (richiede Inno Setup 6) → `artifacts\JustSlides-Setup-<versione>.exe`.
