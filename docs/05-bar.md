# 05 — Barra al posto della taskbar (M3)

Barra nera a tutta larghezza in basso sul monitor principale, nello stesso spazio della taskbar
di Windows, che viene **nascosta** (non disattivata: Explorer resta in funzione).

## Come sostituisce la taskbar
- `ShowWindow(Shell_TrayWnd, SW_HIDE)`. Verificato: l'area di lavoro resta riservata
  (le finestre massimizzate non coprono la fascia in basso), quindi la barra occupa
  esattamente `[WorkArea.Bottom, altezza schermo]` del monitor principale.
- Nessuna modifica permanente: nessuna impostazione di sistema o chiave di registro.
- **Ripristino garantito** della taskbar: uscita normale, eccezione non gestita, fine sessione
  di Windows. Se il processo viene ucciso, `NothingShell.exe --restore-taskbar` la fa ricomparire
  (lo stesso fa un riavvio di Explorer).
- Explorer può rimostrare la taskbar (riavvio, cambio risoluzione): un controllo ogni 2 s e il
  messaggio `TaskbarCreated` la rinascondono.
- Taskbar in nascondimento automatico (nessuna fascia riservata): la barra si appoggia sopra gli
  ultimi 48 DIP senza riservare spazio. Monitor secondari: taskbar di Windows lasciata com'è.

## Contenuto (altezza = fascia riservata, di solito 48 DIP)
```
[⁘ start]  [icone app aperte, centrate, con indicatore]          [rete] [87%] [21:54 / 27.10]
```
| Zona | Clic sinistro | Clic destro |
|------|---------------|-------------|
| Start (griglia 3×3 di punti) | apre lo Start di Windows (tasto Win) — in M4 il launcher Nothing | — |
| Icona app | vedi regole sotto | *Chiudi finestra* |
| Rete / batteria | Impostazioni rapide (Win+A) | — |
| Orologio e data | Centro notifiche (Win+N) | — |
| Spazio vuoto | — | *Mostra taskbar di Windows* (per l'area di notifica), *Esci* |

- Icone in **scala di grigi** su sfondo nero (stile icone monocromatiche Nothing); hover: cerchio `Surface`.
- Indicatore sotto l'icona: punto **rosso** = app in primo piano, punto grigio = aperta.
- Tooltip con il titolo della finestra.

## Finestre elencate
Le stesse dell'Alt+Tab: visibili, senza proprietario, non `WS_EX_TOOLWINDOW`, non "cloaked"
(DWM: app sospese o su altri desktop virtuali), con titolo; escluse le finestre di Nothing Shell.
Raggruppate per eseguibile; l'ordine delle icone è stabile (le nuove app vanno in fondo).
Aggiornamento tramite `SetWinEventHook` (creazione, distruzione, mostra/nascondi, cambio titolo,
primo piano), con raggruppamento degli eventi ogni 150 ms. Nessun polling.

## Regole del clic su un'app (Core, testate)
| Finestre del gruppo | Situazione | Azione |
|---|---|---|
| 1 | in primo piano | minimizza |
| 1 | non in primo piano o minimizzata | attiva (ripristina se minimizzata) |
| N | nessuna in primo piano | attiva la più recente |
| N | una in primo piano | attiva la successiva (ciclo) |

## Schermo intero
Se la finestra in primo piano copre l'intero monitor (giochi, video, presentazioni), barra **e
notch** si nascondono; ricompaiono quando cambia il primo piano. Il desktop e le finestre della
shell non contano come schermo intero.

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
App appuntate, area di notifica completa (icone di terze parti), anteprime delle finestre,
barra su più monitor, volume.
