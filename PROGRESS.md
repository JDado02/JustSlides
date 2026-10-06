# PROGRESS — stato del progetto

Integra CLAUDE.md (specifica e regole di lavoro). Aggiornare a fine di ogni milestone.

## Stato
- **M1 Base e Tappo: COMPLETATA** e testata a mano dall'utente ("funziona tutto"). Commit `3efbf4c`.
- **M2 Macchina a stati + immagini e PDF: IMPLEMENTATA**, build 0 avvisi, 96 test xUnit verdi. In attesa del test manuale dell'utente (immagini e PDF reali mai provati: nessuno schermo/file di prova per Claude).
- Prossima: **M3 Video**, solo dopo conferma dei test di M2 e piano approvato.

## Ambiente
- .NET SDK 10.0.401, PowerPoint 16 (M365, x64), git 2.53. Identità git impostata solo nel repo (Davide / chatbotdt@gmail.com).
- Build: `dotnet build Regia.slnx` (TreatWarningsAsErrors attivo: 0 avvisi richiesti).
- Test: `dotnet test --project tests\Regia.Tests` (xunit.v3 + Microsoft.Testing.Platform, vedi `global.json`). 96 test (macchina a stati, orchestratore con finti, rilevamento tipo file, M1).
- Avvio: `src\Regia.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Regia.App.exe`.
- Dati: impostazioni `%LOCALAPPDATA%\Regia\settings.json`, log `%LOCALAPPDATA%\Regia\logs`.
- Il PC di sviluppo ha spesso un solo monitor attivo (pannello 3000x2000, 192 DPI): l'output reale sul secondo schermo lo prova solo l'utente.

## Struttura
- `Regia.Core` (net10.0): `AppSettings`, `SettingsStore`, `MonitorId/MonitorInfo/MonitorMatcher`, `LogSetup`; `Wave/`: `WaveState`, `WaveTrigger`, `WaveStateMachine` (tabella transizioni), `WaveController` (orchestratore async), `IWaveOutput.cs` (`ITappoTransitions`, `IContentPresenter`, `IContentPresenterFactory`, `PageInfo`); `Media/`: `MediaItem`, `MediaKind`, `MediaKindDetector`.
- `Regia.Output` (net10.0-windows10.0.19041.0, WPF): `OutputHost`, `ContentWindow`, `TappoWindow`, `SimulationFrameWindow`, `IdentifyOverlay`, `TappoFader`, `ImageTappoSource`, `VideoTappoSource`, `VlcService`, `DisplayEnumerator`, `TestPatternView`, interop Win32 via CsWin32; `Content/`: `ImagePresenter`, `PdfPresenter`, `PdfPageCache`, `TestPatternPresenter`, `ContentPresenterFactory`, `RenderWait`; `Transitions/TappoTransitions` (adattatore di `TappoFader`).
- `Regia.App`: `App.xaml.cs` (mutex, log, handler globali, anti-standby, DI), `MainViewModel`, `SettingsViewModel`, `MainWindow`, `SettingsWindow`, tema `Themes/Dark.xaml`.
- `Regia.Tests` (net10.0, referenzia solo Core): `SettingsStoreTests`, `MonitorMatcherTests`.
- `Regia.PptHost` non esiste ancora (M4).

## Decisioni prese in M1 (da rispettare)
- **Tappo video senza VideoView**: LibVLC con video callbacks -> `WriteableBitmap` in un `Image`, perché il Tappo è una finestra `AllowsTransparency`. Audio del Tappo sempre muto (`:no-audio`). Il video del *contenuto* (M3) potrà invece usare `VideoView` nella finestra sotto.
- **Posizionamento in pixel fisici** con `SetWindowPos` (classe `PixelPlacement`), mai Left/Top/Width/Height, per il DPI misto. Manifest PerMonitorV2. Su `OnDpiChanged` si riapplica il rettangolo.
- **Il monitor primario (regia) non è mai selezionabile come output**: in quel caso si ripiega sulla simulazione con avviso.
- **Monitor mancante o non scelto** -> simulazione con avviso arancione, mai crash.
- **Z-order**: in modalità reale contenuto e Tappo sono entrambi topmost, Tappo applicato per ultimo; `OutputHost.EnsureTopmost()` esiste già per M4. In simulazione sono finestre possedute (owner Win32 via `SetWindowLongPtr GWLP_HWNDPARENT`) e non topmost. Chiudere la cornice di simulazione la riduce a icona (chiuderla distruggerebbe le finestre possedute).
- **Dissolvenza**: solo animazione di `Opacity` del Tappo (SineEase). `TappoFader` restituisce `true` se completata, `false` se interrotta. A fine FadeOut il Tappo diventa click-through (`WS_EX_TRANSPARENT`), a inizio FadeIn torna normale. `Panic()` annulla tutto e mette opacità 1 subito. Il video del Tappo si congela durante la dissolvenza.
- Conferma di chiusura della regia solo se qualcosa è in onda (non da Tappo/Errore).
- CsWin32 con `allowMarshaling: false` (firme a puntatori): le callback native sono `[UnmanagedCallersOnly]`. Le costanti `HWND_TOP/TOPMOST/NOTOPMOST` sono definite a mano in `WindowPlacement`.

## Decisioni prese in M2 (da rispettare)
- **Macchina a stati** pura in Core (`WaveStateMachine`): `Panic` e `Fail` validi da ogni stato; `Go` valido da Tappo/Errore (-> Caricamento) e da InOnda (cambio file via Tappo, scelto dall'utente). Il GO "in attesa" non esiste: GO durante Caricamento/transizioni è ignorato e loggato. Dopo il cambio file il controller fa `FadeCompleted` (-> Tappo) e subito `Go`.
- **WaveController**: un solo thread (UI), contatore di generazione + CTS per annullare le operazioni; PANIC/errore = `CoverNow` + chiusura del media; il media si chiude solo dopo che il Tappo è tornato pieno. Mai eccezioni verso l'alto. `Close()` dei presenter è idempotente e un presenter vecchio non svuota il contenuto del nuovo (`ContentWindow.ClearContent(owner)`).
- **PDF**: "avanti" sull'ultima pagina (e "indietro" sulla prima) è ignorato e loggato (scelta dell'utente). Cambio pagina a taglio secco; pagine renderizzate a risoluzione output (1080p in simulazione) da un worker con priorità alla pagina richiesta; PNG in memoria, bitmap decodificate solo corrente ±2. Se il render di una pagina fallisce resta visibile la precedente (solo log).
- **Immagini**: decodifica ridotta alla risoluzione dell'output, EXIF orientation applicato (specchio + rotazione con `TransformedBitmap` annidati).
- "Pronto" = 2 cicli di `CompositionTarget.Rendering` dopo aver messo il contenuto (tetto 1 s).
- UI di M2 provvisoria: lista file non salvata (voce fissa "Schermata di prova" + file aggiunti), frecce/PageUp/PageDown navigano solo se qualcosa è in onda (altrimenti scorrono la lista). Video/PPT rifiutati con avviso in lista e `NotSupportedException` nella factory (M3/M4).

## Trappole incontrate (evitare di rifarle)
- `dotnet new xunit3` non esiste: il csproj dei test è scritto a mano; serve `<Using Include="Xunit" />`.
- Nei progetti WPF `System.IO` non è un using implicito: aggiungerlo a mano. `MediaPlayer` è ambiguo tra WPF e LibVLCSharp (alias in `VideoTappoSource`).
- Dentro `Regia.Output` `Core` si confonde col namespace `Regia.Core`: usare `LibVLCSharp.Shared.Core.Initialize()`.
- Il tool PowerShell rifiuta comandi che contengono XML con `/>` (scambiato per un percorso): scrivere i file XML con lo strumento Write.
- In PowerShell `FindWindow(null, ...)` va chiamato con `[NullString]::Value`, non `$null`.
- `winget` nel contesto `!` non può rispondere a prompt: servono `--source winget --accept-package-agreements --accept-source-agreements`.
- Versioni NuGet: `dotnet package search` dà le più vecchie; usare l'indice `api.nuget.org/v3-flatcontainer`.

## Rischi della specifica già discussi (non ancora implementati)
- **R3 hotkey globali** (M7): `RegisterHotKey` su tasti nudi li ruba al sistema; proposta: hook `WH_KEYBOARD_LL` attivo solo se in primo piano regia/output/slideshow, e `RegisterHotKey` solo con modificatori.
- **R4 miniature PPT** (M4/M6): niente export miniature mentre un PPT è in onda (PowerPoint è single-instance).
- **R5 Tappo vs slideshow** (M4): richiamare `EnsureTopmost()` su `EVENT_SYSTEM_FOREGROUND` e dopo ogni lancio.
- **R6 PowerPoint in simulazione** (M4): usare `ppShowTypeWindow` sul rettangolo della cornice.
- PID di PowerPoint salvato: verificare PID + ora di avvio del processo prima di terminarlo.

## Aperto / da chiarire
- Il selettore file del Tappo mostra solo immagini finché non si sceglie "Video in loop": proposto di mostrare sempre entrambi (non deciso).
- Audio del Tappo video: muto per scelta; opzione nelle impostazioni solo se l'utente la chiede.
- La CPU del Tappo video a pieno schermo non è stata misurata con precisione (obiettivo < 10%).

## Da verificare a mano in M2 (non provato da Claude)
- `Windows.Data.Pdf` in app non pacchettizzata con PDF reali (se non carica: alternativa PDFium, da concordare).
- Immagini e PDF reali sul monitor di output, PANIC durante caricamento/dissolvenza, GO su altro file mentre uno è in onda.

## Prossimo passo
Attendere l'esito dei test manuali di M2; poi **piano di M3 (Video)**.
