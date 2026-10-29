# 13 — Telefono

Il telefono Android arriva nel notch attraverso **Collegamento al telefono** di Windows: è lui che parla con
il telefono (app "Collegamento a Windows" su Android) e trasforma chiamate e notifiche in notifiche di
Windows. La shell le legge con lo stesso `UserNotificationListener` della posta (docs/08-notch-mail-pages.md):
nessuna app in più sul telefono, nessuna rete propria. Si accende e spegne in Impostazioni › NOTCH › *Telefono*.

## Cosa si vede
| Notifica | Nel notch |
|---|---|
| Chiamata in arrivo | scheda *CHIAMATA* col nome o il numero per 20 s: *Rispondi* apre Collegamento al telefono, *Chiudi* |
| Chiamata persa | avviso rosso "PERSA: Mario" |
| Qualsiasi altra notifica del telefono (SMS, WhatsApp…) | avviso "Mittente: prima riga del testo" |

Le notifiche presenti all'avvio non danno avvisi; il contenuto resta solo in memoria. Il banner di Windows
compare comunque: per vederle solo nel notch si spengono i banner di Collegamento al telefono in Impostazioni
di Windows › Sistema › Notifiche.

## Riconoscimento (Core `PhoneLink`, testato)
- Mittente: AppUserModelID `Microsoft.YourPhone_…` (o `MicrosoftWindows.CrossDevice_…` di Windows 11).
- Chiamate: una riga con "chiamata in arrivo", "sta chiamando", "incoming call" (o "chiamata persa",
  "missed call"); l'altra riga è chi chiama, in qualsiasi ordine.
- Messaggi: prima riga = titolo, seconda = testo (solo la prima riga); con tre righe o più l'ultima, se breve,
  è il nome dell'app Android.

## Diagnostica
`NothingShell.exe --phone-check <file>`: per ogni notifica di Collegamento al telefono presente scrive i
testi **oscurati** (lettere → a/A, cifre → 9, tranne parole chiave come "chiamata" o i nomi delle app più
comuni) e come sono stati classificati. Serve a correggere il riconoscimento senza condividere i messaggi.

## Collegamento diretto (KDE Connect)
Acceso in Impostazioni › TELEFONO (spento di serie: la shell si mette in ascolto sulla rete locale). Sul telefono
serve l'app **KDE Connect** (Play Store o F-Droid), sulla stessa rete. Protocollo versione 8, compatibile con
l'app Android:

- **Scoperta**: identità JSON in UDP sulla porta 1716, mandata al broadcast di ogni rete IPv4 attiva (anche a
  un indirizzo scritto a mano, se la rete blocca i broadcast) e a ogni cambio di rete. Chi riceve l'annuncio si
  collega in TCP (1716–1764) e manda la sua identità in chiaro.
- **TLS**: chi ha aperto la connessione TCP fa da server TLS; certificati RSA 2048 autofirmati con CN = deviceId,
  richiesti a entrambi. Dopo il TLS le identità si ripetono cifrate e devono coincidere col certificato.
  Il PFX della shell è in `%USERPROFILE%\.nothing-shell\phone.pfx`, la sua password nel Gestore credenziali;
  identità e telefoni associati (col loro certificato) in `phone.json`.
- **Associazione**: `kdeconnect.pair` con timestamp (le richieste più vecchie di 30 minuti si rifiutano), da
  confermare sull'altro lato entro 30 s. Il codice di verifica (SHA-256 delle due chiavi pubbliche e del
  timestamp, 8 cifre esadecimali) compare sui due schermi: nel notch, scheda *ASSOCIA*. Da un telefono non
  associato si accettano solo richieste di associazione; un telefono associato che si presenta con un altro
  certificato viene rifiutato.

| Dal telefono | Nella shell |
|---|---|
| `kdeconnect.battery` | batteria in impostazioni e pannello rapido, avviso rosso quando è scarica |
| `kdeconnect.telephony` | scheda *CHIAMATA* con *Silenzia* (toglie la suoneria); si chiude quando si risponde |
| `kdeconnect.notification` | avviso come sopra; quelle già presenti al collegamento no |
| `kdeconnect.clipboard` | negli appunti del PC, avviso "APPUNTI DAL TELEFONO" |
| `kdeconnect.share.request` | file in Download (scheda *Scaricato*), link aperti nel browser, testo negli appunti |

Dal PC (riquadri del pannello rapido): **Telefono** lo fa squillare (`findmyphone`), **Invia file** (il file
viaggia su una porta 1739–1764 in TLS), **Appunti** manda il testo copiato. Gli appunti non partono mai da
soli: chiavi e password copiate non finiscono sul telefono per sbaglio.

Con un telefono collegato direttamente le notifiche di Collegamento al telefono si ignorano (arriverebbero due
volte). Se l'app KDE Connect per Windows è già aperta la porta UDP è occupata: la shell si annuncia comunque.

**Firewall**: il primo ascolto fa comparire la richiesta di Windows; altrimenti *Consenti nel firewall* aggiunge
(con l'elevazione) le regole TCP e UDP 1714–1764 per le reti private. Se la rete di casa è "Pubblica" va
cambiata in "Privata" nelle impostazioni di Windows.

`--phone-check` riporta anche identità, telefoni associati e chi ascolta sulla porta 1716.
