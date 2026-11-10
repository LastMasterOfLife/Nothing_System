# 08 — Notch: avvisi mail e pagine con swipe

Estende docs/03-notch.md.

## Avviso mail
Quando arriva una mail il notch passa allo stato **Alert** per 6 s: pillola 400 × 40 con la busta
rossa a punti e **l'oggetto** che scorre a punti. Passandoci sopra si apre direttamente sulla pagina
Posta. Dopo 6 s torna allo stato precedente (Idle o Activity).

### Sorgente: notifiche di Windows
`UserNotificationListener` legge le notifiche toast (verificato: accesso consentito anche alla
nostra app non pacchettizzata). Così funziona con **qualsiasi client che mostra notifiche**,
senza credenziali:
| App (nome visualizzato) | Riconosciuta come posta |
|---|---|
| Outlook, Posta, Mail, Thunderbird, eM Client, Mailbird, Spark, Gmail | sempre |
| Browser (Brave, Chrome, Edge, Firefox, Opera, Vivaldi) | solo se la notifica cita un sito di posta: `mail.google.com`, `outlook.live.com`, `outlook.office.com`, `mail.yahoo.com`, `mail.proton.me` |
| altre | solo se aggiunte in `extraMailApps` nelle impostazioni |

Estrazione: riga 1 = mittente, prima riga del testo successivo = **oggetto** (formato di Outlook,
Posta e Gmail via browser). Se c'è una sola riga, quella è l'oggetto.
Le notifiche già presenti all'avvio entrano nell'elenco senza avviso. Il contenuto resta **solo
in memoria** (ultime 10), mai su disco.

Aggiornamento: evento `NotificationChanged` se disponibile, altrimenti controllo ogni 3 s
(una chiamata WinRT, costo trascurabile).

## Pagine del notch espanso (420 × 172, le pagine con un elenco 420 × 340)
| Pagina | Contenuto |
|---|---|
| Home | musica (come prima) oppure ora grande e data |
| Agenda | appuntamenti di oggi (docs/11-agenda-pannello-rapido.md) |
| Posta (alta) | **ultime 10 mail**: mittente grigio, ora (o giorno), oggetto bianco, punto rosso se non letta; in alto quante da leggere; se l'accesso alle notifiche manca, lo dice |
| Telefono (alta) | docs/13-telefono.md |
| Lavoro, Server, Appunti | docs/12-sviluppo.md |
| Agenti (alta), Dispositivi (alta) | docs/14-agenti-connettori.md |
| Timer | conto alla rovescia a punti; preset 5 · 15 · 25 min, stop; a fine timer avviso "TEMPO" rosso |
| Sistema | CPU, RAM, batteria con barre a punti |

Le pagine con un elenco (Posta, Telefono, Agenti, Dispositivi) **allungano il notch in verticale** fino a 340: l'altezza
anima con la stessa molla della pillola quando si arriva sulla pagina e quando se ne esce (Core `NotchLayout.Expanded`).
L'elenco **scorre con la rotellina** (o due dita in verticale sul touchpad), a pixel; una sottile barra grigia a destra
dice dove si è. Riaprendo la Posta l'elenco torna in cima.

### Quali pagine e in che ordine
In Impostazioni › Notch, sezione **PAGINE**, una riga per pagina nell'ordine dello swipe:
- **frecce su/giù** spostano la pagina di un posto; scavalcano le pagine con la funzione spenta, che restano dove sono;
- **interruttore** per tenerla o toglierla dal notch, solo fra quelle con la funzione accesa (le altre sono in grigio
  con dove si accendono: Sviluppo, Agenti, Connettori, Telefono, Ore);
- la **Home** è sempre la prima e non si toglie.

Le pagine nuove o mai ordinate seguono nell'ordine di serie. Anche i pallini degli indicatori seguono l'ordine.
`settings.json`: `notchPageOrder` (elenco di pagine), `hiddenNotchPages`. Core: `NotchPageOrder.Resolve` / `Move`,
`ShellSettings.WithNotchPage` / `WithNotchPageMoved` (testati).

In basso, indicatori di pagina: pallino bianco = pagina corrente, grigi le altre.
Il cambio pagina scorre di lato (150 ms). Con un timer in corso la pillola compatta mostra il
tempo rimanente al posto dell'ora, con il punto rosso.

## Swipe
| Gesto | Avanti | Indietro |
|---|---|---|
| Touchpad, due dita in orizzontale (`WM_MOUSEHWHEEL`) | verso sinistra | verso destra |
| Rotellina del mouse sul notch (non sopra un elenco che scorre) | giù | su |
| Trascinamento (mouse o dito) di almeno 40 DIP | verso sinistra | verso destra |
Sopra l'elenco di una pagina alta la rotellina fa scorrere l'elenco; se l'elenco ci sta tutto torna a cambiare pagina.
Fra le pagine ci si muove sempre con le due dita in orizzontale o trascinando, e con la rotellina sopra l'intestazione.
Regola (Core, testata): gli spostamenti si sommano fino a una soglia (120 unità di rotellina);
dopo un cambio pagina c'è una pausa di 400 ms, così uno swipe sposta **una** sola pagina.
Le pagine non ricominciano dall'inizio: alla prima e all'ultima lo swipe si ferma.
Un trascinamento che cambia pagina non attiva i pulsanti sotto il dito.

### Sorgente: casella collegata (IMAP)
Per le caselle la cui webmail non manda notifiche (tipicamente Exchange aziendale, es. OWA on-premise),
Nothing Shell si collega direttamente al server IMAP con MailKit:
- TLS su 993 (`SslOnConnect`), STARTTLS se offerto sulle altre porte; utente = indirizzo o quello indicato
  (`DOMINIO\nome` per alcuni Exchange). PLAIN/NTLM scelti da MailKit in base al server.
- **Posta in arrivo in sola lettura**: nessun flag cambiato, nessuna mail segnata come letta.
- **IDLE**: il server avvisa appena arriva una mail; l'attesa si rinnova ogni 9 minuti. Senza IDLE,
  controllo ogni minuto.
- Primo controllo: le ultime 10 mail riempiono la pagina Posta senza avvisi. Poi sono nuove le mail con
  UID maggiore dell'ultimo visto; se il server cambia `UIDVALIDITY` si riparte senza avvisi
  (Core `ImapCursor`, testato).
- Rete assente o server giù: nuovo tentativo dopo 10 s, raddoppiando fino a 5 minuti. Accesso rifiutato:
  nessun nuovo tentativo (eviterebbe il blocco dell'account), lo stato chiede di ricollegare.
- Password nel **Gestore credenziali di Windows** (credenziale generica `NothingShell:imap:<utente>@<server>`),
  mai in `settings.json`; *Scollega* la cancella.
- Con casella collegata e notifiche insieme (es. anche Outlook aperto), la stessa mail (stesso oggetto,
  stesso mittente, entro 3 minuti) dà un solo avviso e una sola riga (Core `MailFeed`, testato).

## Aprire una mail
Un clic su una mail della pagina Posta (righe evidenziate al passaggio del mouse, punto rosso = non letta):
- **casella collegata** → **lettore di posta** di Nothing (960 × 640 la prima volta): a sinistra le ultime mail divise
  per giorno (OGGI, IERI, QUESTA SETTIMANA, PRIMA; Core `MailGroups`) con avatar a punti delle iniziali (Core
  `MailSearch.Initials`), punto rosso se non letta e trattino rosso sulla scelta; a destra la mail scelta. La mail si scarica con una
  connessione a parte, Posta in arrivo in sola lettura e `BODY.PEEK`: **aprirla non la segna come letta**.
  Se la mail ha una parte HTML si mostra **com'è** (vedi *Mail in HTML* sotto); il testo semplice (o l'HTML ripulito,
  Core `MailText`) serve al codice di verifica e quando l'HTML non si può mostrare. Degli allegati solo i nomi.
  Contenuti solo in memoria, finché il lettore è aperto.
- **notifica** → si apre l'app che l'ha mandata (AppUserModelID della notifica, se è nello Start); altrimenti
  il lettore mostra oggetto e mittente.

Finestra del lettore:
| Cosa | Come |
|---|---|
| Ridimensionare | dai bordi e dagli angoli (puntini in basso a destra); minimo 560 × 420 |
| Ingrandire | doppio clic sull'intestazione o il quadrato in alto: occupa l'area di lavoro del suo monitor, senza angoli; trascinando l'intestazione torna alla misura di prima |
| Larghezza dell'elenco | trascinando il divisore (220–440 DIP) |
| Finestra stretta (< 700 DIP) | un riquadro alla volta: elenco, poi la mail con `<` (o Esc) per tornare |
| Cercare | Ctrl+F: tutte le parole in mittente, indirizzo, oggetto o app, senza badare a maiuscole e accenti (Core `MailSearch.Matches`); Invio apre la prima, Esc svuota |
| Solo da leggere | controllo a segmenti TUTTE · NON LETTE |
| Testo più grande | Ctrl+rotellina o Ctrl + / Ctrl − (11–22 punti), Ctrl+0 torna a 13 |
| Muoversi, chiudere | ↑↓ tra le mail visibili; Esc chiude (sfumando) |

Misure, posizione, larghezza dell'elenco e dimensione del testo restano in `settings.json` (`mailReader`, Core
`MailReaderLayout`, testato): alla riapertura la finestra torna lì, riportata dentro lo schermo se un monitor non c'è più.
Il testo della mail è in una scheda che occupa lo spazio rimasto e segue il ridimensionamento, con una sottile barra
grigia che dice dove si è. Il **codice di verifica** compare in grande, a punti, in una scheda rossa con *Copia*; gli
allegati sono gettoni (un clic apre la webmail).

### Mail in HTML
Le mail con pulsanti (per esempio *Allow* / *Deny* per autorizzare un accesso), tabelle e colori si vedono come le ha
preparate il mittente, su una scheda bianca, con **WebView2 in composizione** (`MailHtmlView`: il lettore è una finestra
trasparente, dove il WebView2 classico non si vedrebbe). Il controllo disegna con Direct3D, quindi la shell, che di
solito disegna in software per non caricare il driver GPU, passa all'hardware solo mentre un lettore con l'HTML è aperto.
Sicurezza (Core `MailHtml.Document`, testato):
- **script spenti** (impostazione di WebView2 e `script-src 'none'`), tolti `<script>`, `<iframe>`, `<object>`,
  `<form>`, `<base>`, `<meta http-equiv>` (redirect) e gli attributi `on…`; i link `javascript:` diventano `#`;
- **nessuna navigazione dentro il lettore**: ogni link si apre nel **browser** (o nell'app di posta per `mailto:`), solo
  `http`, `https`, `mailto` (Core `MailHtml.Openable`); i redirect fatti da codice si bloccano e basta. Passando sopra un
  link, in basso si vede dove porta;
- **immagini da internet bloccate** (Content-Security-Policy: solo `data:`): i pixel di tracciamento direbbero al
  mittente che la mail è stata aperta. Se la mail ne ha, compare *Mostra immagini*, che vale per quella mail finché il
  lettore è aperto. Le immagini incorporate (`cid:`, loghi e firme) si vedono subito, sostituite dai loro dati
  (Core `MailHtml.InlineImages`, fino a 5 MB);
- profilo di WebView2 in `~\.nothing-shell\WebView2`, senza compilazione automatica né password salvate.

Ctrl+rotellina ingrandisce anche l'HTML (zoom della pagina). Senza WebView2 Runtime, o se l'HTML supera 1,5 MB, la mail
si mostra come testo.

Nel **testo semplice** gli indirizzi web (`https://…`, `www.…`) e di posta sono link (Core `MailHtml.Linkify`, testato):
un clic li apre nel browser o nell'app di posta, la punteggiatura finale resta fuori.

`--mail-check <cartella>` apre il lettore vero con una mail di consenso in HTML e la fotografa (`mail-html.png`,
`mail-testo.png`).

Azioni del lettore:
| Azione | Cosa fa |
|---|---|
| *Mostra immagini* | solo per le mail in HTML con immagini da internet: le scarica (vedi sopra) |
| *Rispondi* | `mailto:` al mittente (o Reply-To) con "Re: oggetto", nell'app di posta predefinita (Core `MailLinks.Reply`) |
| *Apri in …* | la webmail: quella indicata nelle impostazioni, dei provider noti o `https://<server>/`. Se il sito è installato come **app di Edge** (AppUserModelID `host-…`) si apre l'app (Core `MailLinks`) |
| *Segna come letta* | aggiunge `\Seen` sul server (connessione in lettura e scrittura solo per questo): l'unica modifica alla casella, e solo su richiesta |
| *Copia codice* | codici di verifica di 4–8 cifre vicini a parole come "codice", "OTP", "verification" (non anni, date o importi; Core `MailCodes`) negli appunti |

Non lette: a ogni controllo si rileggono busta e flag delle ultime 10 mail e il numero di `UNSEEN`;
IDLE si sveglia anche quando una mail viene letta o eliminata da un altro client.

## Impostazioni
Sezione **POSTA NEL NOTCH**: interruttore *Mail nel notch*, poi:
- **Collega la tua casella** (consigliato): indirizzo, server IMAP (proposto dall'indirizzo: provider noti
  o `imap.dominio`, Core `ImapSetup`), porta (993), utente se diverso dall'indirizzo, password.
  *Collega* prova l'accesso e salva solo se riesce, con l'errore in italiano altrimenti. Collegata: stato
  dal vivo (bianco se collegata, rosso se in errore), campo *Webmail* (proposto, modificabile), *Ricollega*,
  *Scollega*.
- **Oppure: le notifiche di un'app di posta**, in tre passi numerati a punti (rosso = da fare, grigio = fatto):
  1. **Accesso alle notifiche**: stato; *Consenti accesso* se Windows non l'ha ancora chiesto,
     altrimenti, se negato, *Apri Privacy › Notifiche* (`ms-settings:privacy-notifications`).
  2. **App di posta con notifiche attive**: client riconosciuti, come abilitare le notifiche della
     webmail nel browser, *Apri notifiche di Windows* (`ms-settings:notifications`, anche per Non disturbare).
  3. **App non riconosciuta**: le app che hanno mandato notifiche da quando la shell è avviata (solo il
     nome, in memoria, ultime 8), esclusi i client già riconosciuti; un interruttore le aggiunge a
     `extraMailApps`. Core: `MailDetector.SetupCandidates`, `WithExtraApp` (testati).
- **Verifica**: ultima mail rilevata da qualsiasi fonte; *Prova l'avviso* mostra un avviso finto nel
  notch; *Aggiorna* rilegge lo stato.

`settings.json`: `mailInNotch`, `extraMailApps`, `mailAccount` (`address`, `server`, `port`, `userName`, `webmail`).

## Logica nel Core (testata)
`NotchStateMachine` (stato Alert e scadenze), `NotchPager`, `NotchPageOrder`, `SwipeDetector`, `Countdown`,
`MailDetector`, `ImapSetup`, `ImapCursor`, `MailFeed` (dei doppioni tiene la mail della casella, che si
può riaprire), `MailText`, `MailCodes`, `MailLinks`.
