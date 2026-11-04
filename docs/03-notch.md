# 03 — Notch (M1)

> Esteso da docs/08-notch-mail-pages.md: stato Alert (mail, timer), notch espanso a 420 × 172
> con pagine (Home, Posta, Timer, Sistema) e swipe. La Home è più alta perché ha il campo per chiedere
> (docs/16-assistente-notch.md). Forma, misura Grande, mascotte, ombra e movimento come Nusa: docs/20-stile-nusa.md; le
> misure qui sono quelle della versione Piccola.

Finestra a pillola (colori e vetro della pagina Aspetto, docs/02-design-system.md), in alto al centro del monitor principale, sempre in primo piano,
che non ruba il focus (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`: non appare in Alt+Tab).

## Stati
| Stato | Dimensione (px logici) | Contenuto |
|-------|------------------------|-----------|
| `Idle` | 144 × 28 (96 × 6 col notch discreto) | orologio HH:MM, o la mascotte |
| `Activity` | 256 × 28 | orologio + indicatore musica (punto rosso pulsante se in riproduzione) |
| `Expanded` | 420 × 150 | titolo/artista, controlli ◀◀ ▶/❚❚ ▶▶, ora |

## Transizioni
```
Idle      --media playing-->   Activity
Activity  --media stopped-->   Idle
Idle|Activity|Alert --pointer enter--> (attesa scelta in Aspetto, di serie 290 ms: la pillola si gonfia; con "Subito" si apre senza) --> Expanded
attesa --pointer leave--> torna com'era (un avviso resta ancora 1,5 s)
Expanded  --pointer leave (dopo la chiusura scelta, di serie subito)--> Activity se media attivo, altrimenti Idle
```
La macchina a stati è in `Core/Notch/NotchStateMachine` ed è deterministica: riceve eventi
e un orologio iniettato (per il ritardo di uscita), restituisce lo stato corrente.

## Schermo
`Core/Notch/NotchPlacement` sceglie lo schermo (Aspetto › Schermo) e posa la finestra centrata contro il suo bordo alto, in
pixel fisici di quello schermo. "Dove sta il mouse" controlla ogni 400 ms e sposta il notch solo da chiuso (mai aperto, in
attesa d'apertura o mentre si scrive); un avviso o una scheda lo portano subito sullo schermo del mouse. "Uno scelto" usa il
nome di Windows dello schermo (`\\.\DISPLAY2`): scollegato, si torna al principale. Il notch si nasconde a schermo intero
solo se l'app a schermo intero sta sul suo schermo.

## Rendering
- La finestra ha la dimensione massima (`Expanded`) ed è trasparente; la pillola visibile è
  animata dentro di essa con una molla su larghezza/altezza. Le aree trasparenti non intercettano i click.
- Testi in `DotMatrixView`; titoli lunghi scorrono orizzontalmente (marquee a punti).

## Criteri di accettazione
- Avvio: il notch compare in < 1 s, centrato, sopra ogni finestra non fullscreen.
- Con Spotify/browser in riproduzione appare lo stato `Activity`; i controlli funzionano.
- Nessun furto di focus: cliccando il notch la finestra attiva resta attiva.
