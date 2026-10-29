# Nothing Shell

Strato grafico in stile Nothing OS sopra Windows 11: notch interattivo, widget a punti, barra.
Non sostituisce `explorer.exe`. Le spec sono in [docs/](docs/).

## Avvio
```
dotnet run --project src/NothingShell.App
```
All'avvio la taskbar di Windows viene nascosta e sostituita dalla barra Nothing, su ogni monitor
(seguendo le impostazioni multi-monitor della taskbar di Windows); torna visibile all'uscita. Per chiudere: tasto destro sul notch, su un widget o sulla barra → *Esci da Nothing Shell*,
oppure da terminale:
```
NothingShell.exe --exit
```
**Ctrl+Win** spegne la shell (torna la taskbar di Windows, Win apre di nuovo lo Start) e la
riaccende: da spenta resta solo un processo dormiente che ascolta Ctrl+Win; `--exit` chiude anche quello.

Se il processo viene terminato di forza e la taskbar resta nascosta:
```
NothingShell.exe --restore-taskbar
```

Nella barra ci sono le stesse app appuntate alla taskbar di Windows, nello stesso ordine (si
appuntano e tolgono da Windows, la barra si aggiorna da sola). Il clic destro su un'app apre i suoi
**recenti** (file, cartelle, progetti) come la jump list di Windows, più nuova finestra e chiudi.
Col mouse sopra un'app aperta compaiono le **anteprime dal vivo** delle sue finestre.
Dettagli in [docs/05-bar.md](docs/05-bar.md).

Quando arriva una mail il notch mostra per qualche secondo la busta e l'oggetto: dalla casella collegata
direttamente (IMAP, nelle impostazioni) oppure dalle notifiche (da qualsiasi
client che manda notifiche di Windows: Outlook, Posta, Thunderbird, Gmail nel browser).
Un clic su una mail della pagina Posta la apre nel **lettore** di Nothing (senza segnarla come letta),
con *Rispondi*, *Apri nella webmail* (o nella sua app di Edge), *Segna come letta* e *Copia codice* per i
codici di verifica; le mail arrivate da una notifica aprono l'app che l'ha mandata.
Sul notch aperto, uno swipe (touchpad a due dita, rotellina o trascinamento) scorre le pagine
Home · Agenda · Posta · Telefono · Lavoro · Agenti · Server · Dispositivi · Appunti · Timer · Sistema. Le pagine con un
elenco (Posta con le ultime 10 mail, Telefono, Agenti, Dispositivi) allungano il notch in verticale e l'elenco scorre con
la rotellina. Dettagli in [docs/08-notch-mail-pages.md](docs/08-notch-mail-pages.md).

**Attività dal vivo** nel notch: volume (i tasti del volume mostrano il livello a punti al posto
dell'indicatore di Windows), luminosità dei portatili, icone rosse quando un'app usa microfono o webcam,
download in corso con la scheda *Scaricato*, scheda dello screenshot appena fatto (anteprima da
trascinare, Apri, Cartella), cuffie Bluetooth collegate con la batteria. Ognuna si spegne in
Impostazioni › Notch. Lo screenshot funziona anche con **Greenshot** (appunti e cartella di salvataggio).
Dettagli in [docs/10-live-activities.md](docs/10-live-activities.md).

**Per chi programma**: `ns dotnet build` (modulo PowerShell `tools\NothingShell.psm1`) mostra il tempo
nella pillola e alla fine una scheda *Fatto* o *Errore* con il primo errore, che si apre alla riga in VS Code;
con l'hook del prompt arrivano anche i comandi lunghi e, dopo un `git push`, l'esito della CI di GitHub
(con `gh`). Gli **appunti** riconoscono JSON, stack trace, colori, timestamp, link e percorsi (pagina
Appunti con Formatta, Apri, Copia HEX…) e tolgono dagli appunti dopo 30 s chiavi API e token copiati.
La pagina **Server** elenca le porte in ascolto (node, dotnet, Postgres…) con Apri e Termina.
Tutto si spegne in Impostazioni › Sviluppo. Dettagli in [docs/12-sviluppo.md](docs/12-sviluppo.md).

**Telefono Android** tramite Collegamento al telefono di Windows: chiamata in arrivo come scheda nel notch
(*Rispondi* apre Collegamento al telefono), chiamate perse e notifiche del telefono come avvisi.
Con il **collegamento diretto** (protocollo KDE Connect, app KDE Connect sul telefono, Impostazioni ›
TELEFONO) anche batteria, appunti, file avanti e indietro e *Trova il telefono* dal pannello rapido. La pagina
**Telefono** del notch mostra batteria, rete e segnale, cosa il telefono condivide (i permessi dati nell'app), il file in
arrivo con l'avanzamento, gli ultimi file ricevuti (Apri, Cartella) e le ultime notifiche.
Dettagli in [docs/13-telefono.md](docs/13-telefono.md).

**Agenti**: mentre Claude Code lavora, la pillola mostra il costo del turno e la pagina **Agenti** i token in entrata e in
uscita e il costo di ogni sessione (e di oggi), aggiornati a ogni risposta; quando l'agente ha finito arriva la scheda
*HA FINITO*. Claude Code si legge da solo dai transcript (solo token, modello e stato); altri agenti mandano i loro totali
con `NothingShell.exe --task '{"event":"agent",…}'`.
**Connettori**: una **libreria** di connettori pronti in Impostazioni › Connettori (disco, servizi e app di Windows, Docker,
ping, siti, pagine di stato, Home Assistant, MQTT, Shelly, Tasmota, stampanti 3D, server via SSH, Prometheus, GitHub,
Ollama, meteo, aria, cambi…) si attiva scrivendo pochi dati. Sotto, un file JSON per connettore in `%USERPROFILE%\.nothing-shell\connectors\` legge valori da Home Assistant,
Prometheus, MQTT, ping, API http, file e comandi qualsiasi (PowerShell, ssh…), con soglie e avvisi nella pagina
**Dispositivi**. Comandi e azioni (accendi, riavvia, pubblica) partono solo col permesso dato nelle impostazioni, legato
al contenuto del file. Esempi in [connectors/](connectors/), dettagli in [docs/14-agenti-connettori.md](docs/14-agenti-connettori.md).

**Agenda**: il prossimo appuntamento sotto l'ora nella Home, la pagina Agenda con quelli di oggi e un
promemoria 10 minuti prima con *Partecipa* per Teams, Meet e Zoom. Al primo accesso del mattino il
**riepilogo** con gli appuntamenti della giornata e le mail da leggere. Gli appuntamenti arrivano dal
calendario di Windows e dai link iCal (.ics) di Outlook, Google o iCloud aggiunti in Impostazioni › Agenda.
Il pulsante rete e batteria della barra apre il **pannello rapido** Nothing: Wi-Fi, Bluetooth, microfono,
screenshot, proietta, blocca, volume e luminosità. Dettagli in [docs/11-agenda-pannello-rapido.md](docs/11-agenda-pannello-rapido.md).

**Accesso Nothing**: *Blocca* nel launcher (o `NothingShell.exe --lock`, comodo in una scorciatoia)
chiude lo schermo con un'animazione a iride e la card del tuo account, poi passa al blocco di
Windows. Allo sblocco (PIN, viso, password: sempre Windows) parte il bentornato e l'iride si apre
sul desktop. Con Win+L c'è solo il bentornato. Si spegne in Impostazioni › Accesso.
Dettagli e limiti in [docs/09-access.md](docs/09-access.md).

Il launcher si apre con il tasto Win (da solo) o con il pulsante ⁘ della barra: si scrive per
cercare, ↑↓ e Invio per avviare. Le combinazioni Win+qualcosa restano a Windows; lo Start di
Windows è nel menu col tasto destro della barra, dove si può anche disattivare l'uso del tasto Win.
Uso e preferenze in `%USERPROFILE%\.nothing-shell\launcher.json`.

Le impostazioni (tasto destro su barra, notch o widget → *Impostazioni…*, oppure "Impostazioni
Nothing Shell" nel launcher) hanno un menù laterale con una pagina per funzione (Generale, Notch, Telefono, Posta,
Agenda, Sviluppo, Agenti, Connettori, Accesso, Widget, Plugin, Info) e applicano subito ogni modifica.

**Plugin**: widget aggiuntivi descritti da una cartella con `plugin.json` in
`%USERPROFILE%\.nothing-shell\plugins\`; i dati arrivano da una URL JSON o da un comando qualsiasi
(Python, PowerShell…) che stampa JSON. Formato in [docs/07-settings-plugins.md](docs/07-settings-plugins.md),
esempi pronti in [plugins/](plugins/).

I widget si trascinano col tasto sinistro; dal menu col tasto destro si bloccano o si
riportano alla disposizione iniziale. Le posizioni sono in `%USERPROFILE%\.nothing-shell\widgets.json`.

Per controllare la grafica (widget, launcher, plugin con dati veri, impostazioni) senza avviare la shell:
```
dotnet run --project src/NothingShell.App -- --snapshot out
```

## Test
```
dotnet test
```

## Struttura
- `src/NothingShell.Core`: logica pura e testata (font a punti, layout testo, macchina a stati del notch, molle,
  disposizione e salvataggio widget, calendario, CPU/batteria)
- `src/NothingShell.App`: WPF (notch, widget, barra, launcher, impostazioni, host dei plugin, interop Win32);
  `ShellController` collega i componenti e applica le impostazioni dal vivo
- `plugins/`: plugin d'esempio (`meteo` http, `disco` comando)
- `tests/NothingShell.Core.Tests`: xUnit

## Note tecniche
- Rendering WPF in sola modalità software (`RenderMode.SoftwareOnly`): evita di caricare il driver GPU
  (memoria privata da ~265 MB a ~62 MB), con costo trascurabile su superfici così piccole.
- La finestra del notch è `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`: non ruba il focus e non compare in Alt+Tab.
- Media tramite `GlobalSystemMediaTransportControlsSessionManager` (Spotify, browser, Media Player…).
- La barra nasconde `Shell_TrayWnd` (nessuna modifica permanente): l'area di lavoro resta riservata,
  e la barra occupa esattamente quella fascia. Finestre elencate e aggiornate con `SetWinEventHook`, senza polling;
  `--dump-windows <file>` spiega quali finestre entrano nella barra e perché.
- Launcher: app da `shell:AppsFolder` (COM della shell su thread STA), icone da
  `IShellItemImageFactory` lette con `GetDIBits` per non perdere l'alfa, avvio tramite
  `explorer.exe shell:AppsFolder\<id>`. Tasto Win: hook `WH_KEYBOARD_LL` su un thread dedicato;
  il rilascio di Win da solo viene sostituito da maschera `0xE8` + rilascio, così lo Start non si apre.
- I widget non usano il trucco `WorkerW` (lì le icone del desktop rubano i click): sono finestre
  di proprietà di `Progman`, forzate in fondo allo z-order con `WM_WINDOWPOSCHANGING`.
