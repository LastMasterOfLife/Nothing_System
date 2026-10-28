# 10 — Attività dal vivo nel notch

Estende docs/03-notch.md e docs/08-notch-mail-pages.md. Ogni attività si accende e spegne in
Impostazioni › NOTCH (`disabledActivities` in `settings.json`); spenta, il suo servizio è fermo.
Con un'app a schermo intero sul monitor principale (notch nascosto) non si mostra nulla.

## Come si presentano
| Forma | Stato | Uso |
|---|---|---|
| **Pillola compatta** (stato Activity, 300 × 34) | finché dura | microfono e webcam (icone rosse a sinistra, al posto del punto rosso), download (freccia e MB a destra, al posto dell'equalizzatore) |
| **Avviso** (Alert, 400 × 40) | 1,5 s per i livelli, 4 s per gli avvisi | volume e luminosità (icona, barra a punti, valore; ogni tasto rinnova la durata), cuffie |
| **Scheda** (Card, 420 × 112) | 8 s; col mouse sopra resta aperta, poi 1,5 s | screenshot e download completato, con pulsanti |

La scheda non espande il notch al passaggio del mouse (altrimenti i pulsanti sparirebbero): la macchina a
stati ha `OnCard`, `DismissCard` e la pausa col mouse sopra (Core, testata). Notch espanso: avvisi e schede
non lo interrompono.

## Volume
Core Audio (`IAudioEndpointVolume` dell'uscita predefinita, `IMMNotificationClient` per seguirne i cambi):
ogni cambio di volume o muto, da qualsiasi fonte, mostra il livello. I tasti del volume sono presi dall'hook
di tastiera della shell (`WH_KEYBOARD_LL`, lo stesso del tasto Win) e applicati con `VolumeStepUp/Down` e
`SetMute`, come fa Windows (anche: un tasto riattiva l'audio se era muto). Così l'indicatore di Windows non
compare. Quando il notch non si vede, i tasti tornano a Windows.

## Luminosità
Evento WMI `WmiMonitorBrightnessEvent` (root\wmi): solo schermi dei portatili. I monitor esterni non
comunicano la luminosità a Windows, quindi lì non succede nulla. L'indicatore di Windows per la luminosità
non si può sostituire (i tasti sono gestiti dal firmware).

## Microfono e webcam
`HKCU\…\CapabilityAccessManager\ConsentStore\{microphone,webcam}`: per ogni app (dello Store per nome di
pacchetto, desktop sotto `NonPackaged` con il percorso con `#` al posto di `\`) Windows scrive
`LastUsedTimeStart` e azzera `LastUsedTimeStop` finché l'uso dura (Core `PrivacyUsage`, testato).
Si rilegge quando il registro cambia (`RegNotifyChangeKeyValue` sul sottoalbero, nessun polling). Le app
desktop contano solo se il processo esiste (un crash lascia l'uso "aperto"). Nomi: descrizione
dell'eseguibile, per le app dello Store il nome nello Start. Nella pagina Sistema: "Microfono e webcam in uso: Zoom".

## Download
`FileSystemWatcher` sulla cartella Download (known folder, anche se spostata): Chrome, Edge, Brave, Opera e
Vivaldi scrivono in `.crdownload`, Firefox in `.part`, poi rinominano nel file vero (Core `DownloadTracker`,
testato). La dimensione si legge ogni secondo solo mentre c'è un download; velocità con media mobile.
Il totale non è noto dal file, quindi niente percentuale: freccia e MB scaricati nella pillola, nome,
dimensione e velocità nella pagina Sistema. Fermo da 2 minuti (pausa) → tolto. Finito → scheda *Scaricato*
con *Apri* e *Cartella*.

## Screenshot
- Win+Maiusc+S e Stamp: lo Strumento di cattura mette la cattura negli appunti. Con
  `AddClipboardFormatListener` si guarda chi ha scritto gli appunti: contano solo `ScreenClippingHost` e
  `SnippingTool`, non ogni immagine copiata. Formato PNG se c'è (alfa corretto), altrimenti la bitmap.
- Win+Stamp, e lo Strumento di cattura con il salvataggio automatico: file nuovo in Immagini\Screenshot.
- **Greenshot**: conta anche quando è Greenshot a scrivere gli appunti (destinazione *Copia negli appunti*),
  e si osserva la sua cartella di salvataggio, letta da `OutputFilePath` in `%APPDATA%\Greenshot\Greenshot.ini`
  (o accanto all'eseguibile nella versione portatile; vuota = Desktop). La cartella è spesso il Desktop: un
  file nuovo conta solo se Greenshot è aperto. Formati: PNG, JPG, BMP, GIF.
- Appunti e file della stessa cattura (entro 1,5 s; con Greenshot il file può arrivare prima, entro 5 s)
  danno una sola scheda, con il file.

Scheda: anteprima da **trascinare** in qualsiasi app (file e immagine), *Apri*, *Cartella*. Se Windows non
ha salvato il file, lo salva Nothing Shell in Immagini\Screenshot solo quando serve (Apri, Cartella, trascina).

## Cuffie Bluetooth
`DeviceWatcher` sui dispositivi Bluetooth associati con `System.Devices.Aep.IsConnected`: contano quelli
di classe Audio/Video. Collegamento → avviso con nome e batteria (`{104EA319-…} 2`, la stessa delle
impostazioni di Windows, via profilo vivavoce; attesa fino a 4 s perché arriva dopo il collegamento),
icona rossa sotto il 20%. Scollegamento → avviso grigio. Quelli già collegati all'avvio non danno avvisi.

## Diagnostica
`NothingShell.exe --live-check <file>`: volume attuale, chi usa microfono e webcam, cartelle osservate
(anche quella di Greenshot), dispositivi Bluetooth associati con stato, classe e batteria. Sola lettura.
