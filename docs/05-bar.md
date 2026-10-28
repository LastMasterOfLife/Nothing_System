# 05 — Barra al posto della taskbar (M3)

Barra nera a tutta larghezza in basso su ogni monitor, nello stesso spazio della taskbar
di Windows, che viene **nascosta** (non disattivata: Explorer resta in funzione).

## Come sostituisce la taskbar
- `ShowWindow(SW_HIDE)` su `Shell_TrayWnd` (monitor principale) e su ogni `Shell_SecondaryTrayWnd`
  (altri monitor). Verificato su entrambe: l'area di lavoro resta riservata (le finestre massimizzate
  non coprono la fascia in basso), quindi ogni barra occupa esattamente `[WorkArea.Bottom, fondo]`
  del suo monitor.
- Nessuna modifica permanente: nessuna impostazione di sistema o chiave di registro.
- **Ripristino garantito** della taskbar: uscita normale, eccezione non gestita, fine sessione
  di Windows. Se il processo viene ucciso, `NothingShell.exe --restore-taskbar` la fa ricomparire
  (lo stesso fa un riavvio di Explorer).
- Explorer può rimostrare la taskbar (riavvio, cambio risoluzione): un controllo ogni 2 s e il
  messaggio `TaskbarCreated` la rinascondono.
- Taskbar in nascondimento automatico (nessuna fascia riservata): la barra si appoggia sopra gli
  ultimi 48 DIP senza riservare spazio.

## Più monitor (M8)
Una barra per ogni monitor che in Windows ha la taskbar (`BarHost`), con Start, app, stato e orologio.
- Segue l'impostazione di Windows *Mostra la barra delle applicazioni su tutti gli schermi*
  (`MMTaskbarEnabled` in `HKCU\…\Explorer\Advanced`; assente = sì): se è spenta, barra solo sul principale.
- *Mostra le app della barra delle applicazioni su* (`MMTaskbarMode`, Core `MultiMonitorBar.GroupsFor`, testato):

  | Modo | Barra principale | Altre barre |
  |---|---|---|
  | 0 tutte le barre (predefinito) | tutto | tutto |
  | 1 principale e dove è aperta | tutto | solo le finestre di quel monitor |
  | 2 dove è aperta | appuntate + finestre di quel monitor | solo le finestre di quel monitor |

  Come in Windows, con i modi 1 e 2 le app appuntate stanno solo sulla barra principale. Una finestra
  spostata su un altro monitor cambia barra (evento di posizione, nessun polling).
- Monitor collegati o scollegati, risoluzione, scala, impostazioni cambiate: le barre si ricreano o
  spostano (eventi di sistema, raggruppati in 500 ms).
- Posizioni in **pixel fisici** (`SetWindowPos`), così funziona anche con scale diverse tra i monitor.
- Il launcher si apre sopra la barra su cui si è cliccato Start; con il tasto Win, sul monitor del mouse.
  L'elenco dei recenti si apre sopra l'icona, sullo stesso monitor.
- Il notch resta solo sul monitor principale.

## Contenuto (altezza = fascia riservata, di solito 48 DIP)
```
[⁘ start]  [app appuntate + app aperte, centrate, con indicatore]  [rete] [87%] [21:54 / 27.10]
```
| Zona | Clic sinistro | Clic destro |
|------|---------------|-------------|
| Start (griglia 3×3 di punti) | apre lo Start di Windows (tasto Win) — in M4 il launcher Nothing | — |
| Icona app | vedi regole sotto | elenco dei recenti (vedi sotto) |
| Rete / batteria | Impostazioni rapide (Win+A) | — |
| Orologio e data | Centro notifiche (Win+N) | — |
| Spazio vuoto | — | *Mostra taskbar di Windows* (per l'area di notifica), *Esci* |

- Icone con i loro **colori originali** su sfondo nero; hover: cerchio `Surface`.
- Indicatore sotto l'icona: punto **rosso** = app in primo piano, punto grigio = aperta, nessun punto =
  appuntata ma chiusa.
- Tooltip con il titolo della finestra.

## Finestre elencate
Le stesse dell'Alt+Tab: visibili, senza proprietario, non `WS_EX_TOOLWINDOW`, non "cloaked"
(DWM: app sospese o su altri desktop virtuali), con titolo; escluse le finestre di Nothing Shell.
Raggruppate per AppUserModelID (sotto); l'ordine delle icone è stabile (le nuove app vanno in fondo).
Aggiornamento tramite `SetWinEventHook` (creazione, distruzione, mostra/nascondi, cambio titolo,
primo piano), con raggruppamento degli eventi ogni 150 ms. Nessun polling.

## App appuntate (M8)
Le stesse della taskbar di Windows, nello stesso ordine, prima delle altre app aperte.
- Fonte: valore `Favorites` di `HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband`,
  sequenza di `[0x00][lunghezza uint32][ITEMIDLIST]`: contiene anche le app dello Store e le PWA, che
  non hanno un collegamento. Se manca: i `.lnk` di `%APPDATA%\…\User Pinned\TaskBar` in ordine alfabetico.
- Riletto quando Windows lo cambia (`RegNotifyChangeKeyValue`, nessun polling): appuntare o togliere
  un'app dallo Start di Windows aggiorna subito la barra.
- Icona: quella del collegamento appuntato (`IShellItemImageFactory`), come in Windows.
- Clic su un'app appuntata chiusa: la avvia (collegamento, o `shell:AppsFolder\<AppId>` per lo Store).
- Appuntare e togliere si fa da Windows: Nothing Shell non scrive nel registro.

## Raggruppamento: AppUserModelID
Come Windows, la barra raggruppa per AppUserModelID: lega le finestre all'icona appuntata ed è la chiave
dei recenti. Le PWA di Chrome hanno così un'icona propria, separata da Chrome. Per ogni finestra:
1. ID impostato sulla finestra (`SHGetPropertyStoreForWindow`: Chrome, PWA, app UWP);
2. ID del collegamento appuntato o dello Start che avvia lo stesso eseguibile (VS Code e le app Electron
   lo impostano solo sul processo, invisibile da fuori); un collegamento con argomenti non conta;
3. `explorer.exe` → `Microsoft.Windows.Explorer`;
4. ID predefinito di Windows: percorso con la cartella nota più specifica sostituita dal suo GUID
   (`{6D809377-…}\KeePass Password Safe 2\KeePass.exe`).

Senza ID (processo inaccessibile, cornice UWP senza ID): per eseguibile, come prima.

## Anteprime (mouse su un'app aperta)
Come in Windows: dopo 400 ms sopra l'icona compare un pannello con una miniatura **dal vivo** per ogni
finestra dell'app (le stesse di Windows: `DwmRegisterThumbnail`, un video continua a muoversi).
```
 ● Titolo finestra 1…   [×]    ● Titolo finestra 2…   [×]
 ┌───────────────────┐        ┌───────────────────┐
 │    miniatura      │        │    miniatura      │
 └───────────────────┘        └───────────────────┘
```
- Punto rosso = finestra in primo piano, grigio le altre; clic sulla scheda: attiva la finestra;
  `×` (rosso al passaggio): la chiude, e il pannello si aggiorna.
- Miniature con le proporzioni della finestra, al massimo 240 × 150 DIP; se non stanno nel monitor si
  rimpiccioliscono insieme fino a 120 DIP di larghezza (Core `WindowPreviews.Fit`, testato). Finestra
  ridotta a icona: al posto della miniatura, l'icona dell'app.
- Passando a un'altra icona il pannello cambia subito; uscendo, resta 300 ms per poterci entrare col mouse.
  Clic sull'icona, clic destro, schermo intero: il pannello si chiude. Per le app aperte niente suggerimento
  col titolo: c'è il pannello.
- Il pannello è opaco (nero, angoli e bordo `Outline` disegnati da Windows 11): DWM non disegna miniature
  su finestre a trasparenza per pixel. Non prende il focus.
- Non c'è l'"anteprima del desktop" (Aero Peek) passando sopra una miniatura.
- Diagnostica: `NothingShell.exe --preview-demo <file.png>` apre il pannello dell'app con più finestre e
  ne cattura l'immagine dallo schermo.

## Recenti (clic destro su un'app)
Pannello nero con bordo `Outline`, angoli 24, sopra l'icona; si chiude cliccando altrove o con Esc.
```
 R E C E N T I          (titolo a punti rossi; FREQUENTI se l'app tiene solo quelli)
 [▣] istruzioni_report.md
 [▣] Totem_ENVI_park
 ───────────────
 [icona] Visual Studio Code   → nuova finestra
 [×]     Chiudi 2 finestre    → solo se l'app è aperta
```
- Elementi da `IApplicationDocumentLists` (API documentata della shell): recenti, o frequenti se i recenti
  sono vuoti; al massimo 10, senza URI interni (`mailto:`, `tel:`…) e senza doppioni.
- Le categorie personalizzate e le attività delle jump list (es. "Nuova finestra in incognito") e gli
  elementi bloccati nella jump list non hanno un'API di lettura: non sono mostrati.
- Un file si apre **con l'app** (il PDF in Chrome, la cartella in VS Code); per Esplora file e le app
  senza eseguibile si apre il percorso. Un collegamento (progetti di Android Studio, connessioni Desktop
  remoto) avvia il suo eseguibile con i suoi argomenti.
- Icone dei file dal tipo; sui percorsi di rete solo dall'estensione, senza accedere al disco.

## Regole del clic su un'app (Core, testate)
| Finestre del gruppo | Situazione | Azione |
|---|---|---|
| 0 (appuntata) | — | avvia l'app |
| 1 | in primo piano | minimizza |
| 1 | non in primo piano o minimizzata | attiva (ripristina se minimizzata) |
| N | nessuna in primo piano | attiva la più recente |
| N | una in primo piano | attiva la successiva (ciclo) |

## Schermo intero
Se la finestra in primo piano copre l'intero monitor (giochi, video, presentazioni), si nasconde la
barra **di quel monitor** (e il notch, se è il principale); ricompaiono quando cambia il primo piano.
Il desktop e le finestre della shell non contano come schermo intero.

## Uscita controllata
`NothingShell.exe --exit` chiede all'istanza in esecuzione di chiudersi in modo pulito
(evento con nome `Local\NothingShell.Exit`), così la taskbar viene sempre ripristinata.

## Diagnostica
`NothingShell.exe --dump-windows <file>` scrive ogni finestra visibile con `+` (in barra, con la
chiave dell'app) o `-` e il motivo dell'esclusione (cloaked, proprietario, tool window…).
Nota: alcune finestre "sempre in primo piano" delle app UWP (es. mini-lettore multimediale) vivono in
un livello a parte e non sono elencate da `EnumWindows`: possono essere in primo piano senza icona.

## Criteri di accettazione
- All'avvio la taskbar sparisce e al suo posto c'è la barra; all'uscita la taskbar torna.
- Le app aperte compaiono e scompaiono in tempo reale; il clic segue le regole sopra.
- Video a schermo intero: barra e notch non si vedono.
- CPU dell'app a riposo < 1%.

## Fuori portata (milestone successive)
Appuntare e togliere app dalla barra Nothing, area di notifica completa (icone di terze parti),
Aero Peek, notch su più monitor, volume.
