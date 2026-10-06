# PROGRESS — stato del progetto

Integra CLAUDE.md (specifica e regole di lavoro). Aggiornare a fine di ogni milestone.

## Stato
- **M1 Base e Tappo: COMPLETATA** e testata a mano dall'utente ("funziona tutto"). Commit `3efbf4c`.
- Prossima: **M2 Macchina a stati + immagini e PDF**. Non iniziata: serve prima il piano approvato.

## Ambiente
- .NET SDK 10.0.401, PowerPoint 16 (M365, x64), git 2.53. Identità git impostata solo nel repo (Davide / chatbotdt@gmail.com).
- Build: `dotnet build Regia.slnx` (TreatWarningsAsErrors attivo: 0 avvisi richiesti).
- Test: `dotnet test --project tests\Regia.Tests` (xunit.v3 + Microsoft.Testing.Platform, vedi `global.json`). 15 test al momento.
- Avvio: `src\Regia.App\bin\Debug\net10.0-windows10.0.19041.0\win-x64\Regia.App.exe`.
- Dati: impostazioni `%LOCALAPPDATA%\Regia\settings.json`, log `%LOCALAPPDATA%\Regia\logs`.
- Il PC di sviluppo ha spesso un solo monitor attivo (pannello 3000x2000, 192 DPI): l'output reale sul secondo schermo lo prova solo l'utente.

## Struttura
- `Regia.Core` (net10.0): `AppSettings`, `SettingsStore`, `MonitorId/MonitorInfo/MonitorMatcher`, `LogSetup`, enum `WaveState` (già con tutti e 6 gli stati).
- `Regia.Output` (net10.0-windows10.0.19041.0, WPF): `OutputHost`, `ContentWindow`, `TappoWindow`, `SimulationFrameWindow`, `IdentifyOverlay`, `TappoFader`, `ImageTappoSource`, `VideoTappoSource`, `VlcService`, `DisplayEnumerator`, `TestPatternView`, interop Win32 via CsWin32.
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
- **Stato onda in M1 è provvisorio**: la logica sta dentro `MainViewModel` (GO valido solo da Tappo/Errore, Torna al Tappo solo da InOnda, PANIC sempre; comandi non validi ignorati e loggati). In M2 va sostituita da una macchina a stati vera in Core, con test xUnit.
- La finestra contenuto mostra ancora `TestPatternView` (barre colore + orologio): in M2 va sostituita dal contenuto reale (immagine/PDF).
- Conferma di chiusura della regia solo se qualcosa è in onda (non da Tappo/Errore).
- CsWin32 con `allowMarshaling: false` (firme a puntatori): le callback native sono `[UnmanagedCallersOnly]`. Le costanti `HWND_TOP/TOPMOST/NOTOPMOST` sono definite a mano in `WindowPlacement`.

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

## Prossimo passo
Leggere CLAUDE.md e questo file, poi **proporre il piano di M2** (file, pacchetti, rischi) e attendere l'approvazione prima di scrivere codice.
