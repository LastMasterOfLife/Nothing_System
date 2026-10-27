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

## Impostazioni
Nuove voci: *Mail nel notch* (interruttore) e stato dell'accesso alle notifiche, con il pulsante
*Consenti accesso* se Windows non l'ha ancora concesso. `settings.json`: `mailInNotch`,
`extraMailApps`.

## Logica nel Core (testata)
`NotchStateMachine` (stato Alert e scadenze), `NotchPager`, `SwipeDetector`, `Countdown`,
`MailDetector`.
