# OPERATIVO — Regia congressuale

Guida rapida per l'operatore. Per i dettagli tecnici: `CLAUDE.md` (specifica) e `PROGRESS.md` (decisioni).

---

## 0. Installare, aggiornare, disinstallare

File: `JustSlides-Setup-<versione>.exe` (circa 85 MB, in `artifacts\` sul PC di sviluppo; copialo su chiavetta). Si costruisce con `tools\Build-Installer.ps1`.

- **Installare**: doppio clic su Setup. Installa **per l'utente corrente, senza amministratore** (in `%LOCALAPPDATA%\Programs\JustSlides`; a richiesta "per tutti gli utenti" con UAC). Casella "Crea un collegamento sul Desktop" e voce nel menu Start. Se sul PC manca il **runtime .NET 10** lo installa da solo, anche offline (solo in quel caso chiede l'autorizzazione di amministratore).
- **Aggiornare (automatico, dalla versione 1.1.1)**: Impostazioni → **Aggiornamenti** → "Verifica aggiornamenti". Se su GitHub c'è una versione più nuova compare "Scarica e installa la versione X" con le novità: scarica il Setup (verifica SHA256), poi ti avvisa e **JustSlides si chiude, compare la finestra "Installazione di JustSlides" con la barra di avanzamento (pochi secondi) e JustSlides si riapre da solo** con l'ultimo show. Serve Internet; solo con la regia a Tappo/Errore, mai con qualcosa in onda; **solo su richiesta** (nessun controllo all'avvio). **Non spegnere il PC e non riaprire JustSlides a mano durante l'installazione.** Fallo **il giorno prima** dell'evento, non in sala. Dati (`%LOCALAPPDATA%\JustSlides`) e show non si toccano. Se qualcosa non va, l'esito è scritto in Impostazioni e il log del Setup è in `%LOCALAPPDATA%\JustSlides\logs\update-setup.log`.
- **Aggiornare a mano** (versioni precedenti alla 1.1.1, o se l'automatico non funziona): scarica `JustSlides-Setup.exe` dal link del README, chiudi JustSlides e lancialo: sovrascrive la precedente e non tocca i dati. Con JustSlides aperto il Setup si ferma e chiede di chiuderlo.
- **Disinstallare**: Impostazioni > App > JustSlides > Disinstalla. Chiede se eliminare anche i dati (**predefinito: No**).
- **Windows SmartScreen / antivirus**: il Setup **non è firmato**: compare "Windows ha protetto il PC" > *Ulteriori informazioni* > *Esegui comunque*. Se un antivirus lo blocca, ripristina il file dalla quarantena o aggiungi un'eccezione; non è un virus (SHA256 stampato a fine build per confronto).
- Prima di un evento: installa/aggiorna **il giorno prima**, non in sala.

---

## 1. Checklist pre-evento (30 minuti prima)

**Il PC**
- [ ] Windows Update in pausa; nessun riavvio programmato. Notifiche disattivate (Assistente focus / Non disturbare).
- [ ] Alimentazione: PC collegato alla corrente, piano "Prestazioni elevate"; standby e spegnimento schermo sono già bloccati dalla regia, ma verifica che non ci sia un'utility del produttore che li ripristina.
- [ ] Almeno 10 GB liberi su `C:` (copie locali dei file + log).
- [ ] **PowerPoint chiuso** (vedi "Regole d'oro"). Chiudi anche Teams/Zoom/OneDrive che possono aprire finestre.
- [ ] Salvaschermo e blocco schermo disattivati.
- [ ] **PowerPoint verificato**: Impostazioni → PowerPoint → "Verifica licenza PowerPoint" (a Tappo, con PowerPoint chiuso). Verde = pronto; **ambra** = funziona ma Office non risulta attivato (può comparire una richiesta di accesso che blocca la regia in onda: attivalo prima); **rosso** = non usare le PPT in questo stato (esporta in PDF/video). La verifica è solo su richiesta, la regia non la fa da sola all'avvio. Non prova lo slideshow a schermo intero né i file veri.

**Collegamenti**
- [ ] Proiettore / matrice / scaler acceso e collegato **prima** di avviare la regia, sorgente giusta.
- [ ] Audio: il dispositivo scelto per l'evento (Impostazioni → Audio) deve essere anche il **predefinito di Windows**, altrimenti PowerPoint suona altrove. Se vedi il banner arancione "dispositivo audio", usa il pulsante "Apri impostazioni audio".
- [ ] Clicker del relatore: provalo (frecce / PagGiù / PagSu) con uno slideshow a schermo.

**La regia**
- [ ] Avvio: `JustSlides.exe`. (La prima volta dopo il passaggio dal vecchio nome "Regia" i dati si portano da soli da `%LOCALAPPDATA%\Regia` a `%LOCALAPPDATA%\JustSlides`: show, impostazioni e tasti si copiano, la cache dei file si sposta.) Se è già aperta, una seconda copia non parte (porta in primo piano la prima).
- [ ] Impostazioni → Monitor di output: scegli il proiettore (pulsante "Identifica" per riconoscerlo); **niente banner arancione "simulazione"**.
- [ ] Impostazioni → Tappo (immagine, video in loop o **PowerPoint**), dissolvenza (default 500 ms), cartella contenuti dell'evento. **Tappo PowerPoint**: le slide diventano immagini UNA volta premendo Applica (a Tappo, PowerPoint dell'utente chiuso); poi il Tappo non usa più PowerPoint. Loop (default 6 s per slide) oppure "Fermo su una slide": nel Program compaiono ◀ ▶ (solo mouse) e la slide scelta si ricorda anche dopo un'onda e al riavvio. Se cambi il file .pptx del Tappo, premi di nuovo Applica.
- [ ] Scaletta: tutti i file con pallino **verde** (pronti). Rosso = non mandabile; arancione = avviso (leggi il dettaglio: font mancanti, collegamenti rotti...).
- [ ] **Prova generale**: manda in onda un PPT, un video e un PDF/immagine del vero evento; verifica audio, posizione sul proiettore, slide avanti/indietro con il clicker, **T** (Torna al Tappo) ed **Esc** (PANIC).
- [ ] **Prova hotplug**: a Tappo, spegni il proiettore (o stacca il cavo) → banner rosso "OUTPUT SCOLLEGATO", la regia non viene coperta; riaccendi → dopo ~2 s il Tappo riappare da solo. Poi GO funziona.
- [ ] **Stress breve sul PC dell'evento** (facoltativo ma consigliato, ~2 minuti): chiudi la regia e avviala con `JustSlides.exe --stress 20`. Parte da sola, banner viola; al termine scrive il riepilogo in `%LOCALAPPDATA%\JustSlides\logs\stress-*.txt`. Deve dire **Falliti: 0**. Esc lo interrompe.
  Per la prova completa (100 cicli, guasti iniettati): `--stress 100 --faults` (~10 minuti, **solo con la regia a vuoto, mai durante un evento**).

---

## 2. Durante lo show

**Tasti** (si cambiano in Impostazioni → Tasti)

| Azione | Tasto |
|---|---|
| GO (manda in onda il file selezionato) | **L**, Spazio, Invio |
| Slide / pagina avanti | Destra, Giù, PagGiù |
| Slide / pagina indietro | Sinistra, Su, PagSu |
| Selezione in scaletta su / giù | **Q** / **A** |
| **Torna al Tappo** (con dissolvenza) | **T** |
| **PANIC** (Tappo immediato, taglio netto) | **Esc** |

- I tasti funzionano con la regia in primo piano e anche se il focus è sullo slideshow o sull'uscita (clicker). Con un'altra app in primo piano non vengono toccati.
- Tenendo premuto un tasto si ripetono solo gli spostamenti (slide, pagina, selezione); GO, T, Esc non si ripetono.
- Se stai scrivendo in un campo (Sessione / Relatore), lettere, Spazio e frecce sono del campo; **Esc resta sempre PANIC**; Invio conferma.

**Colori e banner**
- Stato **Tappo** (neutro) / **In onda** / **Errore** (rosso). Dopo un errore la regia è già al Tappo e pronta: seleziona e premi GO.
- Banner **rosso "OUTPUT SCOLLEGATO"**: il monitor di output non c'è. GO è rifiutato finché non torna.
- Banner **arancione**: avviso (audio non predefinito, cartella contenuti, tasti globali non attivi...). Leggi il testo.
- Banner **viola "STRESS TEST"**: stress in corso, non è uno show.

**Regole d'oro**
1. **Non aprire PowerPoint durante lo show.** È a istanza singola: se lo apri tu, la regia si aggancia alla stessa istanza e, in caso di guasto, il watchdog potrebbe chiuderlo con i tuoi file aperti. Se all'avvio PowerPoint è già aperto, la regia lo segnala (banner rosso) e rifiuta i PPT.
2. **Non toccare la cartella contenuti del file in onda.** Un file modificato mentre è in onda resta "AGGIORNAMENTO IN ATTESA" e si aggiorna al ritorno al Tappo.
3. Si va in onda **sempre dalla copia locale**: non rimuovere la chiavetta *durante la copia* (barra % sul file); dopo la copia non serve più.
4. Un file nuovo in cartella compare dopo ~6 s (conferma in due scansioni).

---

## 3. Emergenze

| Cosa succede | Cosa fare |
|---|---|
| Qualcosa va storto in onda | **Esc** (PANIC): Tappo subito. Poi riprendi con GO. |
| PowerPoint si pianta / viene chiuso | Il watchdog (3 s) porta al Tappo, chiude PowerPoint e PptHost, stato Errore. Premi GO sullo stesso file: riparte da solo. |
| La regia si chiude | Riaprila: riparte con l'**ultimo show** (scaletta e impostazioni). I processi PowerPoint rimasti orfani vengono ripuliti all'avvio (solo se sono nostri). |
| Proiettore spento / cavo staccato | Tappo immediato + banner rosso. Riaccendi/ricollega: il Tappo torna da solo; poi GO. Se non torna entro 10 s: Impostazioni → Applica. |
| Il volume di PowerPoint resta basso | Può capitare se la regia muore a metà di una dissolvenza audio: il volume si ripristina al GO successivo; in alternativa Mixer di Windows → POWERPNT al 100%. |
| Video "sporco" o scatta | Prova il file in prova generale; in diretta: **T**, poi GO. Un file con errore di decodifica porta da solo a Errore + Tappo. |
| Output nero ma stato "Tappo" | Premi **T** o Esc; se persiste, Impostazioni → Applica (riposiziona le finestre). |

**Log**: `%LOCALAPPDATA%\JustSlides\logs\` (un file al giorno, con ora di ogni GO, comando ed errore). Se qualcosa è andato storto, tieni il file del giorno e annota l'ora.
Show corrente: `%LOCALAPPDATA%\JustSlides\show.json`; archivi degli eventi passati in `shows\`.

---

## 4. Limiti noti

- PowerPoint suona sul **dispositivo audio predefinito** di Windows, non su quello scelto nella regia.
- Una presentazione con **tempi automatici** che finisce da sola porta al Tappo: può comparire un attimo di nero.
- File PowerPoint con **password**: la regia resta ferma ~30 s in "Caricamento", poi Errore + Tappo.
- I `.ppt` vecchio formato hanno un controllo (pre-flight) limitato; le miniature PPT sono solo quelle incorporate nel file.
- **Esc è un taglio netto**, senza dissolvenza: è voluto (deve comportarsi sempre allo stesso modo). Per un'uscita morbida usa **T**. Attenzione ai clicker il cui tasto "fine" manda Esc.
- Se apri PowerPoint *dopo* l'avvio della regia, la tua apertura si unisce alla stessa istanza (non rilevabile): vedi regola d'oro 1.
- **Windows 10**: supportato solo 22H2 (build 19045), provato dall'utente su un PC Windows 10 (installazione e uso: funziona; non verificato nel dettaglio: stress test e secondo monitor). Windows 10 è fuori supporto Microsoft dal 14/10/2025 (aggiornamenti ESU per privati fino al 13/10/2026): usare un PC di regia dedicato, poco esposto in rete. Prima di un evento su Windows 10 fare una prova completa (installazione, `--stress 20 --faults`, tutti i tipi di file, secondo monitor).

---

## 5. Da provare sul monitor reale (non verificato in sviluppo: un solo schermo)

Da fare una volta con il secondo monitor/proiettore vero e segnare l'esito:

- [ ] Output sul monitor giusto fin dal primo istante (mai un lampo sulla regia), dissolvenza senza lampi dello slideshow sopra il Tappo.
- [ ] Anteprima dell'output nel Program (cattura ~5 fps) coerente con ciò che esce, senza scatti con un video 4K.
- [ ] Hotplug: scollega/ricollega a Tappo, con un video e con un PPT in onda; matrice che cambia sorgente (nessun falso "tornato"); cambio risoluzione.
- [ ] Tasti con il focus sullo slideshow del monitor di output; Alt+Tab e Win funzionano; altre app non toccate.
- [ ] Clicker vero (inclusi i modelli che mandano F5 o Esc).
- [ ] Monitor con DPI diverso dalla regia (posizione/dimensione esatte).
- [ ] CPU con il Tappo video a pieno schermo (obiettivo < 10%): `--stress 20` la misura a Tappo fermo e la scrive nel riepilogo.
