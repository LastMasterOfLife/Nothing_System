# 02 — Design system

## Colori
| Token | Valore | Uso |
|-------|--------|-----|
| `Black` | `#000000` | sfondo notch/widget |
| `Surface` | `#111111` | superfici secondarie |
| `White` | `#FFFFFF` | punti accesi, testo |
| `DotOff` | `#FFFFFF` @ 12% | punti spenti della griglia |
| `Grey` | `#8A8A8A` | testo secondario |
| `Red` | `#D71921` | accento (registrazione, play, alert) |
| `Outline` | `#262626` | bordo dei riquadri widget |

## Matrice a punti
- Font proprietario NDot **non** usato: glifi 5x7 disegnati in `DotFont` (cifre, A–Z, punteggiatura base).
- Spaziatura tra glifi: 1 colonna. Spaziatura tra righe: 1 riga.
- Un punto ha `pitch` (distanza fra centri) e `radius` = `pitch * 0.38`.
- I punti spenti si disegnano con `DotOff` solo nelle superfici "griglia" (widget); nel notch si disegnano solo i punti accesi.
- Ogni punto ha un'intensità 0..1, per transizioni morbide (fade a matrice).

## Forme
- Angoli: notch a pillola (raggio = altezza/2), widget raggio 24 px.
- Nessuna ombra, nessun gradiente. Il contrasto viene solo da bianco/nero/rosso.

## Movimento
- Espansione notch: molla (stiffness 280, damping 26), nessun rimbalzo eccessivo.
- Cambio contenuto: dissolvenza dei punti 120 ms.
- Controlli (App `Rendering/Motion`, idea presa dal file manager Ski): lo stato si legge dal **riempimento che sfuma**
  (trasparente → `#111111` al passaggio, 140 ms; ritorno 260 ms), non da bordi che compaiono. Premuti si stringono
  al 96% e al rilascio tornano con un piccolo rimbalzo (BackEase 420 ms). Curva comune **OutQuint**.
- Contenuti nuovi (righe di un elenco, una mail aperta, uno stato vuoto) entrano sfumando 10 DIP dal basso; gli elenchi
  in sequenza, 28 ms tra una riga e l'altra (al massimo 10 righe di ritardo).
- Controllo a segmenti: la pillola bianca **scorre** sulla scelta e ne prende la larghezza (260 ms).
- Caricamento: tre punti che respirano in sequenza.
- Con gli effetti di animazione di Windows spenti (`SystemParameters.ClientAreaAnimation`) tutto salta allo stato finale;
  anche le immagini di `--snapshot` mostrano lo stato finale.
