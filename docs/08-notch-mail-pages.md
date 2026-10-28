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

## Pagine del notch espanso (420 × 172)
| # | Pagina | Contenuto |
|---|---|---|
| 1 | Home | musica (come prima) oppure ora grande e data |
| 2 | Posta | ultime 3 mail: mittente grigio, oggetto bianco; se l'accesso alle notifiche manca, lo dice |
| 3 | Timer | conto alla rovescia a punti; preset 5 · 15 · 25 min, stop; a fine timer avviso "TEMPO" rosso |
| 4 | Sistema | CPU, RAM, batteria con barre a punti |

In basso, indicatori di pagina: pallino bianco = pagina corrente, grigi le altre.
Il cambio pagina scorre di lato (150 ms). Con un timer in corso la pillola compatta mostra il
tempo rimanente al posto dell'ora, con il punto rosso.

## Swipe
| Gesto | Avanti | Indietro |
|---|---|---|
| Touchpad, due dita in orizzontale (`WM_MOUSEHWHEEL`) | verso sinistra | verso destra |
| Rotellina del mouse sul notch | giù | su |
| Trascinamento (mouse o dito) di almeno 40 DIP | verso sinistra | verso destra |
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

## Impostazioni
Sezione **POSTA NEL NOTCH**: interruttore *Mail nel notch*, poi:
- **Collega la tua casella** (consigliato): indirizzo, server IMAP (proposto dall'indirizzo: provider noti
  o `imap.dominio`, Core `ImapSetup`), porta (993), utente se diverso dall'indirizzo, password.
  *Collega* prova l'accesso e salva solo se riesce, con l'errore in italiano altrimenti. Collegata: stato
  dal vivo (bianco se collegata, rosso se in errore), *Ricollega*, *Scollega*.
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

`settings.json`: `mailInNotch`, `extraMailApps`, `mailAccount` (`address`, `server`, `port`, `userName`).

## Logica nel Core (testata)
`NotchStateMachine` (stato Alert e scadenze), `NotchPager`, `SwipeDetector`, `Countdown`,
`MailDetector`, `ImapSetup`, `ImapCursor`, `MailFeed`.
