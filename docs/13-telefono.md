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

## Prossimi passi
Batteria del telefono, appunti condivisi, invio di file e "trova il telefono" non passano dalle notifiche:
richiedono un collegamento diretto col telefono (es. protocollo KDE Connect sulla rete locale).
