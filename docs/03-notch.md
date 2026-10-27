# 03 — Notch (M1)

Finestra a pillola nera, in alto al centro del monitor principale, sempre in primo piano,
che non ruba il focus (`WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`: non appare in Alt+Tab).

## Stati
| Stato | Dimensione (px logici) | Contenuto |
|-------|------------------------|-----------|
| `Idle` | 200 × 34 | orologio HH:MM a punti |
| `Activity` | 300 × 34 | orologio + indicatore musica (punto rosso pulsante se in riproduzione) |
| `Expanded` | 420 × 150 | titolo/artista, controlli ◀◀ ▶/❚❚ ▶▶, ora |

## Transizioni
```
Idle      --media playing-->   Activity
Activity  --media stopped-->   Idle
Idle|Activity --pointer enter--> Expanded
Expanded  --pointer leave (dopo 400 ms)--> Activity se media attivo, altrimenti Idle
```
La macchina a stati è in `Core/Notch/NotchStateMachine` ed è deterministica: riceve eventi
e un orologio iniettato (per il ritardo di uscita), restituisce lo stato corrente.

## Rendering
- La finestra ha la dimensione massima (`Expanded`) ed è trasparente; la pillola visibile è
  animata dentro di essa con una molla su larghezza/altezza. Le aree trasparenti non intercettano i click.
- Testi in `DotMatrixView`; titoli lunghi scorrono orizzontalmente (marquee a punti).

## Criteri di accettazione
- Avvio: il notch compare in < 1 s, centrato, sopra ogni finestra non fullscreen.
- Con Spotify/browser in riproduzione appare lo stato `Activity`; i controlli funzionano.
- Nessun furto di focus: cliccando il notch la finestra attiva resta attiva.
