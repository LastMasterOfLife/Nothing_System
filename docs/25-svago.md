# 25 — Svago: むちねこ nel notch

Stato: **fatto**. Una sezione delle impostazioni che, accesa, aggiunge al notch la pagina **Svago** con un gioco 2D animato:
むちねこ (*muchineko*, «gatto paffuto»), dal concept in [immagine_ispirazione.jpg](immagine_ispirazione.jpg).

> 食べたい。でも、飛べなくなる。 — Vuole mangiare. Ma poi non salta più.

Il gatto corre da solo da un tetto all'altro della città. Mangiando fa punti e ingrassa; più pesa, meno salta. Correndo
dimagrisce piano. Ogni tanto una bandiera: il traguardo della tappa.

## Dove si accende
| Voce | Dove | Come |
|---|---|---|
| Gioco nel notch | Impostazioni › Svago | `ShellSettings.Leisure` (`LeisureSettings`), **spento di serie**. Acceso, la pagina Svago entra nello swipe **in fondo**; si sposta e si nasconde come le altre in Impostazioni › Notch. |
| Gioca nel notch | Impostazioni › Svago | apre il notch sulla pagina Svago (`OpenLeisure`). |
| La tavola | Impostazioni › Svago | `MuchiShowcase`: SISTEMA (corre, salta, cade), CORPORATURA (leggero → normale → pesante), CIBO, con gli stessi asset del gioco. |
| Record | Impostazioni › Svago | punti e tappa della partita migliore, salvati alla fine di ogni partita che lo batte; *Azzera record* chiede conferma. |

## Regole
Il mondo è alto 170 unità (y verso il basso); la vista si scala sull'altezza, quindi col notch Grande si vede più città davanti.
Le costanti stanno in `RooftopRun`.

| Cosa | Valore |
|---|---|
| Velocità | 118 unità/s alla tappa 1, +10 a ogni tappa, fino a 178 |
| Tappa | una bandiera ogni 2600 unità (~22 s); passarla vale 10 punti |
| Crocchette (カリカリ) | 1 punto, +3,5% di peso |
| Scatoletta (ねこ缶) | 5 punti, +18% di peso |
| Peso | parte dal 20%; correndo sul tetto −1,5% al secondo; fra 0 e 100% |
| Salto | spinta da 345 (leggero) a 120 (100%) con gravità da 1000 a 1250: salto pieno alto ~60 unità da leggero, ~6 al 100% (飛べなくなる) |
| Saltino | lasciando presto il tasto la salita si taglia al 45% della spinta: passa sotto le crocchette in alto |
| Aiuti | 80 ms dopo il bordo si salta ancora; premuto 120 ms prima di toccare, salta all'atterraggio; arrivando fino a 5 unità sotto il bordo di un tetto ci si sale |
| Si perde | cadendo nel vuoto o sbattendo contro il muro di un tetto più alto: il gatto ruzzola giù, poi la scheda di fine |
| Corporatura | sotto il 34% leggero (軽い), sotto il 67% normale (ふつう), poi pesante (重い) |

**La città** (`RooftopRun.AddRoof`) si costruisce dal seme, davanti alla camera, e quella dietro si dimentica. Ogni vuoto e
ogni gradino si passano col salto pieno fino al **60% di peso** (`DesignWeight`), con un margine del 20%: chi mangia tutto
arriva al 100% in una ventina di secondi e da lì i vuoti larghi diventano impossibili. Le prime tappe usano vuoti più stretti.
La pista di partenza è lunga e senza cibo. Le crocchette stanno in tre modi: in **arco sopra il vuoto**, dove passa il salto
pieno di un gatto leggero (col saltino si evitano), in **fila sul tetto** (si scavalcano) e, una volta su cinque, una scatoletta.
Ogni partita nuova cambia seme.

## Comandi
| Gesto | Effetto |
|---|---|
| Clic sul gioco, fermo (titolo, pausa, fine) | parte, riprende, riprova, **al rilascio**: trascinare cambia ancora pagina |
| Clic in corsa | salta subito; tenuto, salto pieno |
| Clic sul ⏸ in alto a destra | pausa |
| Spazio, ↑, W, Invio | come il clic |
| P | pausa e riprende |
| Esc | pausa, e la tastiera torna alla finestra di prima |

Come il campo della Home (docs/16-assistente-notch.md), alla partenza il notch **prende la tastiera e resta aperto** anche
col mouse altrove (`NotchWindow.HoldForGame`). Lo lasciano: Esc, un clic su un'altra finestra (pausa), un'altra pagina
(pausa; da fermi lo swipe funziona anche tenuto, in corsa no). Riprovando dopo una partita la tastiera c'è già.

## Asset
Tutti ridisegnati a vettori dal concept (`MuchiArt`), nella sua palette: `#111111 · #333333 · #777777 · #BEB8AD · #E6E1D7
· #F6F4EE`; fuori palette solo il bianco del pelo, il marrone delle crocchette e il rosa di naso e guance. Il carattere è
Bahnschrift (il DIN di Windows, Bauhaus come il concept), il giapponese Yu Gothic UI.

| Asset | Com'è |
|---|---|
| Gatto di lato | corpo a fagiolo bianco col bordo nero, testa tonda con berretto e orecchie neri, macchia sulla schiena, coda nera, guance rosa. **Il peso lo arrotonda in modo continuo**: corpo da 26 a 36 unità, pancia che scende fino a sfiorare il tetto, zampe più corte e larghe. |
| Gatto seduto | di fronte, come in CAT STATE: corpo a pera che si allarga col peso, testa che ci affonda, coda arricciata che ondeggia, zampette, bocca a ω. Facce: normale, contento (^ ^), triste. |
| Crocchette | tre sassolini marroni con un riflesso; ondeggiano. |
| Scatoletta | coperchio con l'anello, fascia scura, etichetta col muso del gatto, scintille che si accendono a turno. |
| Bandiera | asta e drappo neri col cerchio chiaro e il numero della tappa; sventola. |
| Tetti | facciata scura con il cornicione, finestre (qualcuna accesa), un tratto di ringhiera e a volte condizionatore, serbatoio o antenna. |
| Città | due strati che scorrono più piano (parallasse 0,15 e 0,4): lontano chiaro e piatto, vicino grigio con le finestre, serbatoi tondi sulle gambe, antenne e l'insegna verticale むちねこ. Un grande sole pallido. |
| HUD | come nel concept: PESO con la barra e la percentuale (e la corporatura), crocchette × punti, ⏸, la tappa al centro. |

### Animazioni (`CatPose`, in Core)
| Posa | Quando | Come |
|---|---|---|
| Corre (走る) | sul tetto | zampe a coppie in opposizione, passo lungo 30 unità (40 da pesante, che ciondola), corpo che sobbalza a metà passo, coda che dondola, tre linee di velocità dietro |
| Stacco | 120 ms dopo il salto | allungato fino a 1,15 in verticale |
| Salta (跳ぶ) | in salita | inclinato in su di 13°, zampe tese avanti e dietro, coda distesa, linee sotto |
| Cade (落ちる) | in discesa | raccolto a palla, zampe sotto, coda su, occhi sgranati, linee sopra |
| Atterra | 140 ms dopo | schiacciato a 0,78, si rialza |
| Mastica | 300 ms dopo aver mangiato | guance piene e bocca che si apre e chiude, occhi contenti; il +1 sale e sparisce |
| Contento | 1,2 s dopo un traguardo | occhi a ^; al centro TAPPA 1 ✓ e +10 |
| Ruzzolone | ha sbattuto | gira a 540°/s scivolando lungo il muro, occhi a X |
| Seduto | titolo e fine | battito ogni 3,1 s, coda che ondeggia in 1,6 s |

## Disegno e costo
La shell disegna in software (`RenderMode.SoftwareOnly`), quindi il gioco sta attento a cosa ridisegna (`RooftopView`):
- palazzi e tetti, centinaia di finestre, diventano **immagini alla densità dello schermo** la prima volta che compaiono e
  scorrono allineati ai pixel, senza filtro;
- HUD e pannelli (titolo, pausa, fine) sono immagini rifatte solo quando cambia quello che dicono; il gatto e il cibo restano
  vettori;
- a pagina visibile un fotogramma a ogni giro di schermo in corsa, 24 al secondo da fermi; a pagina nascosta niente.

Misurato con la pagina del notch Grande a 150%: da 14 ms a fotogramma tutto vettoriale a circa 5 ms.

## Immagini
`NothingShell.exe --svago-sheet <cartella>` disegna e basta (niente rete, niente shell):
- `svago-gatto.png`: ogni posa nelle tre corporature;
- `svago-oggetti.png`: cibo, bandiera, un tetto, la città con l'insegna;
- `svago-notch-*.png`: la pagina nei suoi momenti (titolo, corsa, salto, pesante, pausa, traguardo, fine, record);
- `svago-impostazioni.png`: la pagina Svago delle impostazioni.

## Codice
| Dove | Cosa |
|---|---|
| `Core/Leisure/RooftopRun.cs` | la partita: fasi, fisica a passi fissi (1/240 s), città, cibo, traguardi, eventi; `City(seed, length)` la costruisce senza giocarla |
| `Core/Leisure/CatPose.cs` | la posa del gatto in ogni istante |
| `Core/Leisure/LeisureSettings.cs` | acceso/spento e record |
| `App/Leisure/MuchiArt.cs` | gli asset; `Skyline`, gli strati di palazzi |
| `App/Leisure/RooftopView.cs` | lo schermo del gioco |
| `App/Leisure/MuchiShowcase.cs` | la tavola delle impostazioni |
| `App/Notch/LeisurePage.cs` | la pagina del notch, clic e tastiera |
| `App/Settings/SettingsWindow.Leisure.cs`, `App/ShellController.Leisure.cs` | impostazioni e record |
| `App/App.LeisureSheet.cs` | `--svago-sheet` |

I test sono in `LeisureTests`: parte al primo clic e non salta; più pesante salta più basso; saltino contro salto pieno;
salto prenotato prima di toccare; dimagrisce correndo; punti e peso di ogni cibo; senza saltare cade e la partita finisce,
si riprova solo dopo mezzo secondo e con un'altra città; sbattendo resta contro il muro e ruzzola; la pausa ferma tutto; stesso
seme stessa città, giocata o no; **ogni vuoto e gradino si passano al 60% di peso** e ogni bandiera sta su un tetto; un
giocatore attento arriva al primo traguardo in almeno 7 partite su 10; la posa segue il salto e resta nei limiti; il record
sale soltanto; le impostazioni si salvano; la pagina sta in fondo allo swipe e ha la sua altezza.

## Limiti noti
- Niente suoni.
- Con la tastiera serve che il notch possa prendere il primo piano: se Windows lo nega (raro, un'app a schermo intero che lo
  trattiene) si gioca solo col mouse.
- Mentre si gioca la tastiera è del notch: per scrivere altrove, Esc o un clic sull'altra finestra (il gioco va in pausa).
- Il record conta i punti; tappa e peso si vedono solo nella scheda di fine.
- Nella misura Piccola le scritte dell'HUD sono piccole (unità del mondo × 1,14).
