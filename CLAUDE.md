# JustSlides — Specifica di progetto

Software di regia congressuale per Windows 11: l'operatore carica i file (PPT, PDF, MP4, JPG/PNG),
li vede in Preview e li manda in onda su un monitor di Output, sempre passando per un "Tappo"
con dissolvenza morbida. Priorità assoluta: stabilità. La regia non deve MAI bloccarsi o chiudersi
per colpa di un media.

Stack: C# / WPF / .NET 10 LTS, solo Windows 11 x64.

---

## Regole di lavoro per Claude Code

- Lavora per milestone (sezione in fondo). Una milestone alla volta.
- Per ogni milestone: prima proponi il piano (file da creare/modificare, pacchetti NuGet,
  rischi), aspetta la mia approvazione, poi implementa.
- Dopo ogni modifica: `dotnet build` senza errori né warning nuovi. Dove possibile, test unitari
  (xUnit) per la logica non-UI (macchina a stati, scaletta, file show, parsing).
- A fine milestone: commit git con messaggio `Mx: descrizione`, poi fermati e dammi una
  checklist di test manuale da eseguire (io ho il monitor di output fisico, tu no).
- Non passare alla milestone successiva finché non confermo che i test sono ok.
- Se una scelta della specifica risulta tecnicamente impossibile o rischiosa, dimmelo e
  proponi l'alternativa prima di implementarla.
- Commenti nel codice e messaggi di log in italiano; nomi di classi/metodi in inglese.

---

## Configurazione di riferimento

- Due schermi: monitor principale = regia, secondo monitor = Output (proiettore / matrice
  Roland / scaler). Nessun confidence monitor per il relatore.
- Output tipico: 1080p (1920×1080). Il codice deve comunque adattarsi a qualsiasi risoluzione
  e aspect ratio (letterbox/pillarbox per contenuti 4:3 o diversi).
- Tappo: immagine fissa OPPURE loop video, scelto per evento nelle impostazioni.
- Uscita audio: cambia da evento a evento → selezione del dispositivo audio nelle impostazioni.

---

## Architettura (vincoli non negoziabili)

### Struttura della soluzione
- `Regia.App` — WPF, UI operatore, MVVM con CommunityToolkit.Mvvm.
- `Regia.Core` — modelli, macchina a stati dell'onda, scaletta, file show, impostazioni, logging.
- `Regia.Output` — finestre di output, gestione monitor, pipeline di transizione.
- `Regia.PptHost` — exe separato che pilota PowerPoint via COM. Comunica con la regia via
  named pipe (messaggi JSON con id richiesta, timeout, risposta).
- `Regia.Tests` — xUnit.

### Isolamento di PowerPoint
- La regia NON chiama mai PowerPoint direttamente: solo tramite PptHost.
- Watchdog: heartbeat regia ↔ PptHost. Se PptHost non risponde entro un timeout
  configurabile (default 3 s), la regia: (1) va sul Tappo, (2) termina PptHost,
  (3) termina il POWERPNT.EXE associato, (4) logga, (5) è pronta per il file successivo.
- PptHost viene riavviato automaticamente alla richiesta successiva.

### Output a finestre impilate (transizioni)
Sul monitor di output ci sono due livelli, entrambi borderless e a schermo intero:
1. **Finestra contenuto** (sotto): slideshow PowerPoint, video VLC, immagine o pagina PDF.
2. **Finestra Tappo** (sopra, topmost): immagine o loop video.

La transizione si fa SOLO animando l'opacità della finestra Tappo:
- Messa in onda: contenuto caricato sotto il Tappo → attesa "pronto" (primo frame / slide
  renderizzata) → Tappo 1→0 → Tappo diventa click-through (WS_EX_TRANSPARENT).
- Ritorno al Tappo: Tappo 0→1 → solo dopo, chiusura del media sotto.
- NON tentare di animare l'Opacity del VideoView di LibVLCSharp né di sovrapporre elementi
  WPF allo slideshow: non funziona (airspace / finestra di un altro processo).
- Se il Tappo è un loop video: durante la dissolvenza congelalo su un fotogramma (snapshot)
  per tenere leggero il rendering della finestra trasparente; riprende a girare quando è
  tornato pienamente visibile.
- Durata dissolvenza configurabile 300–1000 ms (default 500 ms) + opzione "taglio secco".
- Il Tappo deve tornare topmost dopo che PowerPoint si è portato in primo piano.

### Monitor
- Identificare i monitor per device path / EDID, non per indice ("Display 2" cambia).
- Salvare nelle impostazioni il monitor di output scelto.
- Hotplug (WM_DISPLAYCHANGE): se l'output sparisce e ricompare (proiettore spento,
  cambio sorgente della matrice), riposizionare le finestre di output automaticamente.
- Modalità simulazione: output in una finestra ridimensionabile sul monitor principale,
  per sviluppo e test senza secondo schermo.

### Macchina a stati dell'onda
Stati espliciti: `Tappo`, `Caricamento`, `InTransizioneIn`, `InOnda`, `InTransizioneOut`,
`Errore`. Ogni comando (GO, PANIC, Stop, slide successiva…) è valido solo in certi stati;
comandi non validi vengono ignorati e loggati. Testare la macchina a stati con xUnit.

---

## PowerPoint (dentro PptHost)

- Interop su un thread STA dedicato con message pump. Mai Task.Run per chiamate COM.
- IMessageFilter registrato per gestire RPC_E_CALL_REJECTED / server occupato con retry.
- Late binding o COMReference, per non dipendere dalla versione di Office installata.
- Apertura: ReadOnly, WithWindow=false, DisplayAlerts=ppAlertsNone,
  AutomationSecurity=msoAutomationSecurityForceDisable. Nessun prompt per collegamenti esterni.
- Slideshow: ppShowTypeSpeaker, ShowPresenterView=false. NON usare Kiosk
  (si riavvia dalla prima slide dopo 5 minuti di inattività).
- Posizionamento sul monitor di output: chiave di registro DisplayMonitor di PowerPoint
  prima del lancio e/o SetWindowPos sulla finestra dello slideshow; verificare la posizione
  effettiva e correggerla.
- Controllo slide SOLO via COM (Next / Previous / GotoSlide). Dopo il lancio, restituire
  il focus alla finestra della regia.
- Comando "avanti" sull'ultima slide: dissolvenza al Tappo, poi chiusura dello slideshow
  (mai mostrare la schermata nera "Fine della presentazione").
- Eventi COM (SlideShowBegin, SlideShowNextSlide, SlideShowEnd) inoltrati alla regia per
  aggiornare slide N/M.
- Se all'avvio PowerPoint è già aperto dall'utente: avviso chiaro all'operatore
  (PowerPoint è single-instance, la regia si aggancerebbe a quell'istanza).
- Processi orfani:
  - PID ricavato dall'HWND dell'applicazione (GetWindowThreadProcessId).
  - Processo assegnato a un Job Object con JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE.
  - PID salvato su file; all'avvio successivo, se il processo esiste ancora ed è nostro,
    viene terminato.
  - Mai terminare processi Office non avviati da noi.

---

## Video, immagini, PDF

- Video: LibVLCSharp (+ VideoLAN.LibVLC.Windows) in una finestra di contenuto sotto il Tappo.
  - Dispositivo audio di uscita selezionabile.
  - Fine video configurabile per file: torna al Tappo / fermo sull'ultimo fotogramma / loop.
  - Countdown tempo restante e tempo trascorso inviati alla UI.
- Immagini: JPG/PNG, adattate con letterbox, decodifica in background.
- PDF: rendering con Windows.Data.Pdf (TargetFramework net10.0-windows10.0.19041.0 o superiore),
  pagine pre-renderizzate in background alla risoluzione dell'output, navigazione con le
  stesse frecce delle slide.

---

## Audio

- Impostazioni: scelta del dispositivo di uscita per l'evento (elenco da CoreAudio).
- Fader nel pannello Program + Mute:
  - agisce sul player VLC;
  - agisce sulla sessione audio di POWERPNT.EXE (per PID) tramite CoreAudio (NAudio).
- Fade audio sincronizzato con la dissolvenza video in uscita.
- Limite noto: PowerPoint suona sul dispositivo predefinito di Windows. Se il dispositivo
  scelto per l'evento non è quello predefinito, mostrare un avviso visibile all'operatore
  con l'indicazione di impostarlo come predefinito.

---

## UI (Dark Mode, monitor principale)

- **Sinistra — Media Bin + Scaletta**: lista ordinabile (drag&drop) con sessione, relatore,
  file, tipo, esito del pre-flight (ok / avviso / errore) e miniatura.
- **Destra alto — Preview**: miniature delle slide PPT (esportate in background, solo per
  anteprima, mai per l'onda), primo frame dei video, pagine PDF, immagini.
- **Destra basso — Program**:
  - cattura a bassa frequenza (~5 fps) dello schermo di output, per vedere cosa è davvero in onda;
  - stato onda ben visibile (colori: Tappo / In onda / Errore);
  - slide N/M e slide successiva per PPT, pagina N/M per PDF;
  - countdown video grande;
  - Play / Pausa / Stop, fader audio, Mute;
  - pulsante PANIC grande.
- **Tasti**:
  - GO (Spazio o Invio) = manda in onda il file selezionato;
  - PANIC (Esc) = Tappo immediato da qualsiasi stato;
  - Frecce / PageUp / PageDown = slide o pagina (anche dal clicker del relatore);
  - hotkey globali via RegisterHotKey, così funzionano anche se il focus è finito altrove.
  - Tasti configurabili nelle impostazioni.

---

## Import, file show e affidabilità

- Import: copia del file in una cache locale dello show (mai riprodurre da chiavetta o rete),
  rimozione del Mark-of-the-Web (Zone.Identifier) per evitare la Visualizzazione protetta.
- Pre-flight in background per ogni file:
  - PPT: numero slide, aspect ratio, media collegati mancanti, font mancanti se rilevabili;
  - video: durata, risoluzione, codec leggibile da VLC;
  - PDF: numero pagine.
- File show (JSON): scaletta, impostazioni evento (monitor, Tappo, audio, durata dissolvenza).
  Autosave a ogni modifica; al riavvio dopo un crash, ripristino automatico dell'ultimo show.
- Errori durante la proiezione (eccezione, crash del player, timeout di PptHost):
  chiudi il media difettoso, Tappo immediato, log, la regia resta operativa.
- Handler globali: DispatcherUnhandledException, AppDomain.UnhandledException,
  TaskScheduler.UnobservedTaskException → log + ritorno al Tappo, mai chiusura silenziosa.
- Logging con Serilog su file a rotazione giornaliera: ogni messa in onda, ogni comando,
  ogni errore, con timestamp.
- Istanza singola dell'app (Mutex).
- SetThreadExecutionState per impedire standby e spegnimento schermo durante lo show.

---

## Pacchetti NuGet previsti

- CommunityToolkit.Mvvm
- LibVLCSharp, LibVLCSharp.WPF, VideoLAN.LibVLC.Windows
- NAudio (CoreAudio: dispositivi e sessioni)
- Serilog, Serilog.Sinks.File
- Microsoft.Extensions.Hosting (DI e ciclo di vita)
- xUnit (test)
Proponi alternative motivate se trovi qualcosa di più adatto.

---

## Milestone

1. **Base e Tappo**: struttura soluzione, impostazioni, rilevamento monitor (device path),
   finestra Tappo (immagine e loop video) a schermo intero sul monitor scelto, dissolvenza,
   PANIC, modalità simulazione, logging, handler globali, istanza singola.
2. **Macchina a stati + immagini e PDF**: messa in onda di immagini e PDF tramite la
   pipeline Tappo, navigazione pagine, test xUnit della macchina a stati.
3. **Video**: LibVLCSharp sotto il Tappo, fine video configurabile, countdown,
   dispositivo audio, fader e fade audio sul player.
4. **PptHost**: processo separato, thread STA, IMessageFilter, named pipe, watchdog,
   Job Object, apertura sicura, slideshow sul monitor di output, controllo slide,
   gestione ultima slide, eventi.
5. **Audio PowerPoint**: controllo sessione CoreAudio di POWERPNT.EXE, avviso dispositivo.
6. **UI completa e scaletta**: Media Bin, Preview con miniature, import con copia locale e
   pre-flight, file show, autosave e recovery.
7. **Program e tasti**: cattura dello schermo di output, slide successiva, hotkey globali,
   clicker, tasti configurabili, hotplug del monitor.
8. **Hardening**: test di stress (100 messe in onda di fila, file corrotti, PowerPoint
   killato a mano durante l'onda, monitor scollegato), correzioni, checklist operativa
   pre-evento in un file OPERATIVO.md.
