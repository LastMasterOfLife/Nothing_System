# 11 — Agenda, riepilogo del mattino e pannello rapido

Estende docs/03-notch.md, docs/05-bar.md e docs/10-live-activities.md. *Prossimo appuntamento* e
*Riepilogo del mattino* si accendono e spengono in Impostazioni › AGENDA (`disabledActivities`: `agenda`,
`morningBrief`); con entrambi spenti il calendario non viene letto.

## Calendari
| Fonte | Come | Limiti |
|---|---|---|
| Calendario di Windows | `AppointmentManager.RequestStoreAsync(AllCalendarsReadOnly)` | per le app desktop serve "Consenti alle app desktop di accedere al calendario"; il nuovo Outlook non ci scrive |
| Link iCal (`calendarFeeds` in `settings.json`) | download ogni 15 minuti, `webcal://` = `https://` | il link pubblicato da Outlook o Google può aggiornarsi con qualche minuto di ritardo |

Si legge da oggi a fra una settimana. Se un link non risponde restano gli eventi dell'ultima lettura. Lo
stesso appuntamento in due fonti (stesso titolo e inizio) conta una volta, tenendo quella con il link della
riunione. Si rilegge anche alla ripresa dalla sospensione e con *AGGIORNA* nelle impostazioni.

Lettura iCal (Core `IcsCalendar`, testato): righe spezzate, testo con escape, `DTSTART`/`DTEND`/`DURATION`,
eventi di tutto il giorno, UTC, `TZID` con nomi Windows o IANA (anche al cambio dell'ora legale), `RRULE`
DAILY/WEEKLY (`BYDAY`)/MONTHLY (`BYMONTHDAY`, `2TU`, `-1FR`)/YEARLY con `INTERVAL`, `COUNT`, `UNTIL`,
`EXDATE`, occorrenze spostate (`RECURRENCE-ID`), eventi annullati. I promemoria interni (`VALARM`) si ignorano.
Link della riunione: Teams, Zoom, Meet, Webex, GoToMeeting, Whereby, Jitsi, cercati in
`X-MICROSOFT-SKYPETEAMSMEETINGURL`, luogo, URL e descrizione.

## Prossimo appuntamento
- **Home**: sotto la data, `14:30  Revisione budget · tra 25 min` (rosso entro 10 minuti). Si vede l'appuntamento
  in corso finché non ne è passata metà, poi il prossimo nelle 24 ore.
- **Pagina Agenda** (seconda pagina, fra Home e Posta): fino a tre appuntamenti che restano oggi, con ora a
  punti (`--:--` per quelli di tutto il giorno) e quanto manca; se oggi non resta nulla, il prossimo giorno
  con appuntamenti (`DOMANI`, `VENERDÌ 30`). Clic: link della riunione, altrimenti il calendario (`outlookcal:`).
- **Promemoria** (scheda, 12 s): `TRA 10 MIN` dieci minuti prima, `INIZIA ORA` all'inizio, con *Partecipa* se
  c'è un link e *Calendario*. Ognuno una volta sola (Core `AppointmentReminder`, testato).

## Riepilogo del mattino
Scheda di 15 s con il giorno a punti, `BUONGIORNO`, gli appuntamenti di oggi (`3 appuntamenti · alle 9:30
Standup`, oppure `Oggi: Ferie`) e le mail da leggere della casella collegata; *Agenda* e *Posta* aprono il notch
su quella pagina. Una volta al giorno (`morningBriefShown`), fra le 5 e le 12, al primo di questi momenti:
avvio della shell, sblocco (dopo il bentornato), ripresa dalla sospensione, primo uso di mouse o tastiera
dopo le 5 se il PC è rimasto acceso. Con un'app a schermo intero si aspetta il momento successivo.
*PROVA RIEPILOGO* nelle impostazioni lo mostra subito, a qualsiasi ora.

## Pannello rapido
Il pulsante rete e batteria della barra apre il pannello Nothing al posto di Win+A (in fondo resta il
collegamento alle impostazioni rapide di Windows). Sopra l'angolo destro della barra di quel monitor; si chiude
cliccando fuori o con Esc.

| Parte | Funziona con |
|---|---|
| Ora e data a punti, ingranaggio | impostazioni di Nothing Shell |
| Wi-Fi, Bluetooth | `Windows.Devices.Radios` (nome della rete se Windows lo dà); senza permesso apre la pagina delle impostazioni |
| Microfono | muto del microfono predefinito (Core Audio, `eCapture`) |
| Screenshot | con Greenshot aperto preme Stamp (la sua cattura), altrimenti `ms-screenclip:` |
| Proietta, Blocca | Win+P; blocco con l'animazione Nothing (docs/09-access.md) |
| Volume | cursore a pillola; clic sull'icona = muto |
| Luminosità | WMI `WmiSetBrightness`, solo schermi dei portatili (altrimenti la riga non c'è) |

Riquadri accesi bianchi con icona e testo neri, spenti grigio scuro, non disponibili attenuati. I servizi audio
del pannello girano solo mentre è aperto.

## Diagnostica
`NothingShell.exe --live-check <file>` aggiunge: cartella di Greenshot, esito della lettura del calendario e
primi appuntamenti, stato di Wi-Fi e Bluetooth, luminosità, microfono.
