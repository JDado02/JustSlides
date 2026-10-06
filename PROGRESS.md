# PROGRESS — stato del progetto

Integra CLAUDE.md (specifica e regole di lavoro). Aggiornare a fine di ogni milestone.

## Stato
- **M1 Base e Tappo: COMPLETATA** e testata a mano dall'utente ("funziona tutto"). Commit `3efbf4c`.
- **M2 Macchina a stati + immagini e PDF: COMPLETATA** e testata a mano dall'utente ("funziona tutto"). Build 0 avvisi, 96 test xUnit verdi. Commit `efdfc2c`.
- **M3 Video: COMPLETATA** e testata a mano dall'utente ("funziona tutto"). Commit `0d1fcb6`. Rifinitura successiva richiesta dall'utente (volume per video + scorrimento), vedi "Decisioni prese in M3". Build 0 avvisi, 131 test xUnit verdi.
- Prossima: **M4 PptHost**. Non iniziare finché l'utente non approva il piano.

## Ambiente
- .NET SDK 10.0.401, PowerPoint 16 (M365, x64), git 2.53. Identità git impostata solo nel repo (Davide / chatbotdt@gmail.com).
- Build: `dotnet build Regia.slnx` (TreatWarningsAsErrors attivo: 0 avvisi richiesti).
- Test: `dotnet test --project tests\Regia.Tests` (xunit.v3 + Microsoft.Testing.Platform, vedi `global.json`). 96 test (macchina a stati, orchestratore con finti, rilevamento tipo file, M1).
- Avvio: `src\Regia.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Regia.App.exe`.
- Dati: impostazioni `%LOCALAPPDATA%\Regia\settings.json`, log `%LOCALAPPDATA%\Regia\logs`.
- Il PC di sviluppo ha spesso un solo monitor attivo (pannello 3000x2000, 192 DPI): l'output reale sul secondo schermo lo prova solo l'utente.

## Struttura
- `Regia.Core` (net10.0): `AppSettings`, `SettingsStore`, `MonitorId/MonitorInfo/MonitorMatcher`, `LogSetup`; `Wave/`: `WaveState`, `WaveTrigger`, `WaveStateMachine` (tabella transizioni), `WaveController` (orchestratore async), `IWaveOutput.cs` (`ITappoTransitions`, `IContentPresenter`, `IContentPresenterFactory`, `PageInfo`); `Media/`: `MediaItem`, `MediaKind`, `MediaKindDetector`.
- `Regia.Output` (net10.0-windows10.0.19041.0, WPF): `Audio/AudioDeviceEnumerator` (NAudio.Wasapi), `Content/VideoPresenter` + `VideoHost` (HwndHost), `OutputHost`, `ContentWindow`, `TappoWindow`, `SimulationFrameWindow`, `IdentifyOverlay`, `TappoFader`, `ImageTappoSource`, `VideoTappoSource`, `VlcService`, `DisplayEnumerator`, `TestPatternView`, interop Win32 via CsWin32; `Content/`: `ImagePresenter`, `PdfPresenter`, `PdfPageCache`, `TestPatternPresenter`, `ContentPresenterFactory`, `RenderWait`; `Transitions/TappoTransitions` (adattatore di `TappoFader`).
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

## Decisioni prese in M3 (da rispettare)
- **Niente LibVLCSharp.WPF / VideoView**: `VideoHost` è un `HwndHost` minimo (finestra figlia "static" creata con CsWin32) e `MediaPlayer.Hwnd` punta lì. Una sola finestra figlia nella finestra contenuto, nessun rischio di z-order col Tappo. Il Tappo video resta a video callbacks (decisione M1).
- **Interfaccia `IPlaybackContent`** (Core, estende `IContentPresenter`): il controller la riconosce con `is`; immagini/PDF non cambiano. Eventi `ProgressChanged`/`EndRequested`/`Faulted` sul thread UI, mai dopo `Close()`.
- **Sequenza video**: `LoadAsync` = Play a volume 0 -> primo `Vout` -> pausa sul primo fotogramma (pronto, muto). `BeginPlayback()` (volume + play) è chiamato **prima** di `RevealAsync`: il video parte con la dissolvenza. Senza `Vout` entro 3 s dopo `Playing` il file si considera solo audio e si prosegue. Timeout caricamento 10 s.
- **Fine video**: scelta dell'utente = "fermo sull'ultimo fotogramma, poi dissolvenza". Dato che a `EndReached` la finestra VLC diventa nera, il presenter fa polling a 100 ms e mette in pausa quando mancano 120 ms alla fine (`EndMarginMs`); `EndReached` è solo rete di sicurezza (log di avviso). `VideoEndAction`: `ReturnToTappo` (default) / `HoldLastFrame` / `Loop` (`:input-repeat=65535`), per file, in `MediaItem.VideoEnd` (record: cambiarlo = `with`, vale dal GO successivo).
- **Fine durante la dissolvenza in entrata** (video più corto del fade): `_endPending` nel controller, lo Stop parte appena lo stato è `InOnda`.
- **Fade audio** = rampa del volume VLC (timer 30 ms) in parallelo a `CoverAsync`, durata = `ITappoTransitions.FadeDuration` (zero con taglio secco). PANIC: nessun fade, volume a 0 e chiusura.
- **Chiusura sicura**: `Close()` azzera il volume, fa Stop/Dispose del player in `Task.Run`; la finestra video passa a `ContentWindow.Retire()` (griglia `Retired`, nascosta) e viene distrutta solo a Stop finito.
- **Dispositivo audio**: `AppSettings.AudioDeviceId/Name` (ID endpoint CoreAudio, vuoto = predefinito Windows). `SetAudioOutput("mmdevice")` prima del Play, `SetOutputDevice(id)` dopo `Playing`. Dispositivo non più collegato -> predefinito + avviso arancione (`ContentPresenterFactory.Warning`). Lista nelle impostazioni = ListBox (nessun tema per ComboBox in `Dark.xaml`: non usarne senza stilizzarlo).
- **Volume per singolo video** (richiesta dell'utente dopo il test): `MediaItem.Volume` (0-100, default 100) è l'unica proprietà *mutabile* del record, di proposito: la voce in lista e quella in onda restano la stessa istanza (`RemoveSelected`/`SetVideoEnd` usano `ReferenceEquals` con `WaveController.CurrentItem`). Il controller applica `item.Volume` a ogni GO; `WaveController.SetVolume` agisce sul video in onda **e** scrive il valore nell'item (ricordato per le volte dopo); con niente in onda non fa nulla. Nella lista c'è lo slider "Volume del video" (`SelectedVolume`, vale prima del GO; se il file è in onda si applica dal vivo); nel pannello video lo slider dal vivo (`Volume`). I due restano sincronizzati. Il **Mute resta globale** (non per video). Il volume sopravvive solo in sessione: la lista non si salva fino a M6 (file show), poi `Volume` va serializzato insieme a `VideoEnd`.
- **Scorrimento video** (richiesta dell'utente): `IPlaybackContent.Seek`, `WaveController.SeekTo/SeekBy`, valido solo in `InOnda` (come Play/Pausa, trigger `Transport`), quindi anche in pausa; ignorato durante le dissolvenze. UI: cursore di posizione (trascinabile, seek dal vivo ogni ≥80 ms + posizione definitiva al rilascio) e pulsanti ±10 s (`MainViewModel.SeekStepSeconds`). Mentre il cursore è afferrato la posizione non si aggiorna da sola (`BeginScrub/EndScrub`, rete di sicurezza se il rilascio del mouse si perde). `VideoPresenter.Seek` non va mai oltre `durata - 2*EndMarginMs`; se il video era finito e fermo (HoldLastFrame) lo scorrimento lo riapre restando in pausa. Le frecce della tastiera NON scorrono il video (restano pagina/slide).
- Volume/Mute: stato Mute nel `WaveController` (`SetMuted`), riapplicato a ogni video; nuovo trigger `Transport` (Play/Pausa e scorrimento) valido solo in `InOnda`.
- NuGet aggiunto: `NAudio.Wasapi` 2.4.0 (la 3.x è una ristrutturazione, non usata).
- UI provvisoria di M3: pannello video in `MainWindow` (countdown, trascorso/durata, Play/Pausa, volume, Mute) e scelta "A fine video" (RadioButton) per il file selezionato; il pannello Program vero è M7.

## Da verificare a mano in M3 (non provabile da me)
- (Fatto dall'utente per la prima parte di M3: R1/R2/R3 ok.) Da provare: scorrimento in pausa (il fotogramma si aggiorna?), scorrimento verso la fine, volume per video.
- R1: l'ultimo fotogramma resta davvero visibile (non nero) con "Torna al Tappo" e "Fermo".
- R2/R3: volume 0 durante il caricamento (nessun suono sotto il Tappo) e uscita sul dispositivo scelto.
- CPU/GPU con video 4K/HEVC a pieno schermo.

## Trappole incontrate (evitare di rifarle)
- `dotnet new xunit3` non esiste: il csproj dei test è scritto a mano; serve `<Using Include="Xunit" />`.
- Nei progetti WPF `System.IO` non è un using implicito: aggiungerlo a mano. `MediaPlayer` è ambiguo tra WPF e LibVLCSharp (alias in `VideoTappoSource`).
- Dentro `Regia.Output` `Core` si confonde col namespace `Regia.Core`: usare `LibVLCSharp.Shared.Core.Initialize()`.
- Il tool PowerShell rifiuta comandi che contengono XML con `/>` (scambiato per un percorso): scrivere i file XML con lo strumento Write.
- In PowerShell `FindWindow(null, ...)` va chiamato con `[NullString]::Value`, non `$null`.
- `winget` nel contesto `!` non può rispondere a prompt: servono `--source winget --accept-package-agreements --accept-source-agreements`.
- Versioni NuGet: `dotnet package search` dà le più vecchie; usare l'indice `api.nuget.org/v3-flatcontainer`.
- `Dispatcher.Yield` è statico (`await Dispatcher.Yield(...)`); `MediaPlayer.SetOutputDevice` in LibVLCSharp 3.10 restituisce `void`.
- Se la regia è aperta, `dotnet build` della soluzione fallisce con file bloccati (MSB3021): chiudere la regia, oppure verificare solo la compilazione con `dotnet build src\Regia.App\Regia.App.csproj -p:OutDir=<cartella temporanea>\`.
- Negli script Python di patch su file del repo aprire con `newline=''` (i file in working copy sono LF, git li converte in CRLF).

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

## Prossimo passo
Attendere l'esito del test manuale di M3 e correggere eventuali problemi; poi aggiornare questo file (M3 completata) e **proporre il piano di M4 (PptHost)**, attendendo l'approvazione prima di scrivere codice.
