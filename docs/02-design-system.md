# 02 — Design system

Di serie la shell deve potersi scambiare per un pezzo di Windows 11: tema e colore d'accento di Windows, Segoe UI Variable,
icone Fluent, angoli e ombre del sistema, vetro discreto. La matrice a punti di Nothing resta come stile da scegliere.

## Pagina Aspetto
Impostazioni › **Aspetto** (`AppearanceSettings` in Core, salvate in `settings.json`). Ogni scelta si applica subito a tutte
le finestre, senza riavvio; con Sistema e con l'accento di Windows la shell segue anche i cambi fatti nelle impostazioni di Windows.

| Voce | Scelte | Di serie |
|------|--------|----------|
| Stile rapido | Windows 11 · Windows solido · Nothing | Windows 11 |
| Modalità | Sistema · Chiaro · Scuro | Sistema |
| Colore d'accento | quello di Windows, 10 proposti, oppure `#RRGGBB` | Windows |
| Sfondo | Neutro · Colorato (punta d'accento) · Profondo (nero/bianco puro) | Neutro |
| Liquid Glass | acceso/spento, con la trasparenza da velato a limpido | acceso, a metà |
| Testo e icone | Windows (Segoe e Fluent) · Punti | Windows |
| Notch discreto | a riposo solo una lineetta contro il bordo alto | spento |
| Notch › Forma | Sospeso · Unito al bordo (con le "orecchie", docs/20-stile-nusa.md) | Sospeso |
| Notch › Misura | Piccola · Grande (larga come Nusa); cambiandola la shell si riavvia e riapre le impostazioni | Piccola |
| Notch › Mascotte | a riposo due occhi al posto dell'ora; reagiscono ad avvisi e lavori finiti | spenta |
| Notch › Ombra | da nessuna a larga e morbida, sotto il notch aperto | a metà |
| Notch › Apertura | col mouse sopra, attesa prima di aprire: subito, 0,15, 0,3 o 0,6 s | 0,3 s (290 ms, come Nusa) |
| Notch › Chiusura | uscito il mouse, quanto resta aperto: subito, 0,3, 0,7 o 1,5 s | subito |
| Notch › Schermo | principale, dove sta il mouse (ci passa a notch chiuso, e ci porta avvisi e schede), o uno scelto | principale |
| Movimento › Molla del notch | Morbida (senza rimbalzo), Nusa, Scattante (svelta, rimbalzo vivace) | Nusa |
| Movimento › Velocità | tutte le animazioni della shell, da 0,5× a 1,5× | 1× |

Gli stili rapidi cambiano colori e materiale ma lasciano le scelte del notch (`AppearanceSettings.WithNotchOf`).

Con gli effetti di trasparenza di Windows spenti il vetro resta inattivo e la pagina lo dice.

## Colori
`Palette.Create` calcola i token da tema, accento, tono e vetro; `Theme` li espone a WPF.

| Token | Scuro / Chiaro | Uso |
|-------|----------------|-----|
| `Base`, `BaseSolid` | `#202020` / `#F3F3F3` | sfondo delle superfici; `Base` col vetro è traslucido, `BaseSolid` mai (finestre che non possono averlo) |
| `Card` | bianco 5,5% / bianco 70% | schede appoggiate su una superficie |
| `Surface`, `SurfaceHover` | bianco 7–12% / nero 5–9% | passaggio del mouse, campi, pulsanti secondari |
| `Ink` | `#FFFFFF` / `#1B1B1B` | testo, punti accesi |
| `Muted` | `#C8C8C8` / `#505050` | testo secondario |
| `Accent`, `OnAccent` | dall'accento | scelta corrente, barre, pulsante principale; testo sopra |
| `Critical` | `#FF6B6B` / `#C42B1C` | errori, pericoli, privacy, batteria scarica: **resta rosso** con qualunque accento |
| `DotOff` | inchiostro 14% | punti spenti, traccia delle barre |
| `Outline` | bianco 10% / nero 10% | bordi |

Regole, verificate dai test: `Accent` ha contrasto almeno 4,5:1 sul fondo in ogni tema e tono, anche con un colore scritto a
mano (si schiarisce o si scurisce quanto basta); `OnAccent` almeno 4,5:1 sull'accento.

I pennelli di `Theme` sono sempre gli stessi oggetti e cambiano colore: chi li usa si aggiorna da solo. Quindi: non si
congelano (negli stili XAML si passa da un `Binding`, perché uno stile congela ciò che contiene), si usano solo sul thread
dell'interfaccia e non si animano direttamente (`Motion.FadeTo` ne fa una copia).

## Liquid Glass
WPF con la trasparenza per pixel non sa sfocare ciò che sta dietro; l'acrilico di Windows sulla stessa finestra resta
rettangolare e con una regione ha i bordi seghettati. Perciò ogni superficie ha sotto una **finestra compagna**
(`GlassBackdrop`): vuota, grande come la superficie, contiene solo la sfocatura di sistema (composizione di Windows con angoli
antialias) e porta l'ombra nativa di Windows 11. Sopra, la finestra WPF disegna la tinta, il velo di luce e il bordo che
prende luce in alto (`PanelBrush`, `RimBrush`) e riceve i clic.

- La compagna è proprietaria della finestra, così Windows le tiene incollate nell'ordine; per i widget è lei a stare in fondo al
  desktop, di proprietà di Progman, e il widget le sta sopra.
- **Mai chiamare `SetWindowPos` per l'ordine in risposta a un messaggio di posizione**: Windows riordina insieme le finestre dello
  stesso proprietario e rimanda i messaggi alle altre, e due widget si rubano il fondo all'infinito (interfaccia bloccata, sfarfallio).
  L'ordine si decide solo riscrivendo `WM_WINDOWPOSCHANGING`, che non genera altri messaggi.
- Il notch le comunica la forma a ogni fotogramma dell'animazione.
- La composizione si carica solo la prima volta che serve il vetro; spento resta solo l'ombra. Il rendering WPF resta software.
- Il pannello delle anteprime resta opaco: DWM non disegna miniature su finestre a trasparenza per pixel.
- Diagnostica: `NothingShell.exe --glass-check <cartella>` apre finestre vere sopra una tavola colorata, cambia tema dal vivo e
  ne fotografa l'immagine dallo schermo (solo le proprie finestre); nel `report.txt` ordine di più widget insieme, assenza di cicli a riposo e clic.
  `--snapshot <cartella> [chiaro|scuro] [punti] [#RRGGBB]` genera le immagini delle superfici, senza vetro.

## Testo e icone
`DotMatrixView` riceve sempre una `DotGrid`, che ricorda cosa rappresenta. Nello stile Windows disegna il testo in Segoe (le
etichette tutte maiuscole diventano normali, `DotText.Friendly`; le sigle restano), l'icona Fluent corrispondente (`FluentIcons`)
o una barra piena. Occupa quasi lo stesso spazio dei punti, così le pagine non cambiano impaginazione. Segnale ed
equalizzatore restano a punti.

## Matrice a punti
- Font proprietario NDot **non** usato: glifi 5x7 disegnati in `DotFont` (cifre, A–Z, punteggiatura base).
- Spaziatura tra glifi: 1 colonna. Spaziatura tra righe: 1 riga.
- Un punto ha `pitch` (distanza fra centri) e `radius` = `pitch * 0.38`.
- I punti spenti si disegnano con `DotOff` solo nelle superfici "griglia" (widget); nel notch si disegnano solo i punti accesi.
- Ogni punto ha un'intensità 0..1, per transizioni morbide (fade a matrice).

## Forme
- Angoli come in Windows 11: superfici 8 (`Theme.RadiusLarge`), controlli 4 (`Theme.RadiusSmall`); notch a pillola, al massimo 14.
  Unito al bordo il notch ha gli angoli bassi fino a 26 e in alto due raccordi concavi da 6 a 20 (`NotchLayout.Ear`, `NotchShape`).
- Ombra solo quella di Windows, sulle superfici; il notch aperto ne ha in più una sua, ampia quanto si sceglie. Gradienti solo nel vetro.
- Superfici con `Theme.Panel`, così fondo, bordo e angoli sono uguali ovunque.

## Movimento
- Notch, come Nusa (docs/20-stile-nusa.md, `NotchMotion`): col mouse sopra aspetta 290 ms gonfiandosi del 7% (molla 170/26), poi
  si apre con la molla 250/20 (supera la misura del ~7%); uscendo si chiude subito con la molla 290/22, che scende sotto la
  misura di riposo e rimbalza. Le altre molle della shell restano 280/26.
- Molla del notch (`NotchMotion.Of`): Morbida apre 70/16, chiude 85/18 (ζ ≈ 0,95, si posa in ~0,6 s); Scattante apre 460/23,5,
  chiude 520/25 (ζ ≈ 0,55, supera la misura del ~13% e si posa in ~0,4 s).
- Velocità (`Motion.Speed`): le durate di `Motion` e del notch si dividono per la velocità, le molle del notch e della mascotte
  avanzano il tempo moltiplicato per lei (stessa forma del movimento, più svelta o più calma). Le attese di apertura e chiusura
  del notch non cambiano: hanno le loro scelte.
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
