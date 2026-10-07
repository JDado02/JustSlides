# JustSlides

> ## ⬇️ [Scarica JustSlides-Setup.exe](https://github.com/JDado02/JustSlides/releases/latest/download/JustSlides-Setup.exe)
> Doppio clic sul file scaricato e segui la procedura (italiano, senza diritti di amministratore, il runtime .NET 10 è incluso).
> Tutte le versioni: [Releases](https://github.com/JDado02/JustSlides/releases).
> Windows mostrerà "Windows ha protetto il PC" perché il programma non è firmato: *Ulteriori informazioni* → *Esegui comunque*.

Software di regia congressuale per Windows 11 (x64): carichi PowerPoint, PDF, video e immagini, li vedi in Preview e li mandi in onda sul monitor di Output, sempre attraverso un "Tappo" con dissolvenza morbida. Priorità assoluta: stabilità.

- Uso in sala: [OPERATIVO.md](OPERATIVO.md) (installare, aggiornare, checklist pre-evento, emergenze).
- Specifica e regole di sviluppo: [CLAUDE.md](CLAUDE.md); stato e decisioni: [PROGRESS.md](PROGRESS.md).

## Sviluppo
Stack: C# / WPF / .NET 10. Build e test: `dotnet build Regia.slnx` e `dotnet test --project tests/Regia.Tests`.
Pacchetto di installazione: `tools\Build-Installer.ps1` (richiede Inno Setup 6) → `artifacts\JustSlides-Setup-<versione>.exe`.
