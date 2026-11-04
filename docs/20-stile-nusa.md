# 20 — Movimento e aspetto del notch come Nusa

Nusa (Nusa Labs, Microsoft Store, v1.2.0) è un notch "Dynamic Island" per Windows. Vogliamo che il nostro notch si muova e
appaia in modo molto simile. Qui c'è cosa fa Nusa, misurato dal vivo, e cosa cambia rispetto a noi. Prendiamo **curve e
forma**; i colori restano i nostri (docs/02-design-system.md). La mascotte di Nusa (due trattini bianchi) è loro: la nostra è
un'altra, con occhi tondi e un comportamento suo.

## Cosa è stato fatto
| Voce | Dove | Come |
|---|---|---|
| Molle | `Core/Notch/NotchMotion` | apertura 250/20, chiusura 290/22, gonfiore 170/26; `Spring.Tune` cambia molla a metà corsa senza perdere velocità. I test confrontano rimbalzo e tempi con le curve misurate. |
| Entrata e chiusura | `NotchStateMachine` | `OnPointerHover`: attesa 290 ms (`EnterDelay`) con la pillola gonfia del 7% (`Peeking`); uscire prima annulla. `LeaveDelay` è zero. Aperto da un pulsante con il mouse lontano, il notch aspetta 1,5 s. |
| Forma unita al bordo | Aspetto › Notch › Forma | `NotchShape.Attached`: orecchie concave (`NotchLayout.Ear`, 6–20 DIP) e angoli bassi fino a 26. Il vetro sale oltre il bordo dello schermo, così i suoi angoli alti restano fuori. |
| Misura | Aspetto › Notch › Misura | Piccola = le misure di prima; Grande = larghezze di Nusa (riposo 174 × 33, aperto 480). Le altezze da aperto restano le nostre, perché a 160 le pagine si taglierebbero. Cambiandola la shell si riavvia (`--wake --settings`). |
| Ombra | Aspetto › Notch › Ombra | Sfocatura disegnata da noi sotto la forma, larga fino a 48 DIP, solo fuori dalla forma (non scurisce il vetro) e solo da aperto, così a riposo non prende i clic sotto il bordo dello schermo. |
| Mascotte | Aspetto › Notch › Mascotte | `MascotBrain` (Core, testato) decide le pose e `MascotView` le rende fluide con una molla per grandezza. Sbatte le palpebre ogni 2–5 s (a volte due volte di fila), segue il mouse, si guarda intorno col mouse fermo, sorride durante l'attesa d'apertura. Reagisce (`MascotMood`): a un avviso o una scheda sgrana gli occhi verso l'alto e l'avviso arriva 350 ms dopo, così la si vede; quando un agente finisce il turno o un comando riesce sorride e fa due saltelli; se un comando fallisce socchiude gli occhi verso il basso. Una reazione la sveglia. Ha sonno dopo 1,5 minuti senza input, dorme dopo 2 e al risveglio sgrana gli occhi. Si ridisegna solo mentre si muove. Gesti suoi e cose da dirti con l'iniziativa: docs/21-mascotte.md. |

Non ancora fatto: contenuto tagliato dalla forma ed elementi condivisi in apertura, intestazione comune, cambio pagina con le due
pagine insieme, indicatori a bruco, frecce. Le righe della tabella sotto restano la proposta.

## Come è stato misurato
- Il pacchetto contiene un solo `nusa.exe` in Rust (crate `windows` 0.58) che disegna da sé con D3D11 + DirectComposition +
  Direct2D/DirectWrite. Il vetro è la host backdrop di Windows (`use host backdrop`); niente XAML né WPF.
- La finestra `NotchWindow` è fissa, 1276 × 366 px (851 × 244 DIP), `WS_POPUP`, `WS_EX_TOPMOST | TOOLWINDOW | NOACTIVATE |
  NOREDIRECTIONBITMAP`. Come da noi, la forma si anima *dentro* una finestra che non cambia misura.
- Fotogrammi catturati dallo schermo ogni ~17 ms (monitor 4K al 150%: le misure in px fisici sono divise per 1,5), larghezza
  della forma misurata a ogni fotogramma e molla stimata ai minimi quadrati sulla curva. Errore della stima: circa 7 px.

## Cosa fa Nusa

### Stati e misure (DIP)
| Stato | Misura | Contenuto |
|---|---|---|
| Riposo | ~173 × 33 | mascotte; bagliore colorato sul bordo basso |
| Passaggio del mouse (prima di aprire) | si allarga a ~194 (+7%) in 280 ms | gli occhi lasciano il posto a un anello di stato a sinistra e all'ora a destra |
| Aperto | ~481 × 160 (pagina Acqua e Scaffale) | intestazione fissa (titolo · anello · data · ora), pagina, frecce ‹ ›, indicatori |

### Forma
- **Unita al bordo alto, con le "orecchie"**: gli angoli in alto non sono arrotondati verso l'interno. Si allargano verso
  l'esterno con un raccordo concavo che si fonde col bordo dello schermo. Aperta, l'orecchia è larga ~20 DIP e alta ~23 DIP;
  a riposo ~8 DIP. Cresce insieme alla forma.
- Angoli in basso molto tondi: ~26 DIP da aperta, metà altezza a riposo.
- Esiste anche lo stile *Floating* (staccata dal bordo, senza orecchie). Quello di serie è *Classic*.
- **Ombra ampia e morbida** sotto la forma aperta: ~25% di nero appena sotto il bordo, sparisce in ~40 DIP.

### Colori e testo
- A riposo il fondo è nero puro con un **bagliore interno sul bordo basso**: in verticale dal nero a `#113973`, in orizzontale
  da `#0C496C` (petrolio, a sinistra) a `#20296C` (indaco, a destra). Da aperta il fondo è `#000000` pieno. C'è un'opzione per
  il vetro smerigliato con tinta, spenta di serie.
- L'accento è un azzurro: `#4DBDFF` per il testo in evidenza ("Reminder"), `#0D457F` per il fondo del pulsante principale.
  I pulsanti secondari sono a pillola `#303030`, le frecce sono cerchi grigi.
- Segoe UI con gerarchia forte: titolo pagina in bold bicolore (bianco + accento), numeri grandi in bold, testo secondario grigio.

### Movimento
| Momento | Nusa |
|---|---|
| Entrata del mouse | **Aspetta ~290 ms** prima di aprire, intanto la forma si gonfia del 7% (si legge come "ti ho visto"). Uscire prima annulla. |
| Apertura | Molla **k ≈ 250, c ≈ 20 (ζ ≈ 0,63)**. Supera la misura del **~7%** a ~260 ms dall'avvio e si assesta in ~470 ms. Larghezza e altezza insieme. |
| Contenuto in apertura | Compare presto (a ~1/3 della corsa), a grandezza finale, **tagliato dalla forma che cresce**, non scalato, con una breve dissolvenza. Anello e ora della pillola **scivolano al loro posto nell'intestazione** (elemento condiviso), invece di sparire e ricomparire. |
| Uscita del mouse | **Nessun ritardo**: si chiude subito. Molla **k ≈ 290, c ≈ 22 (ζ ≈ 0,65)**. Scende **sotto** la misura di riposo del ~12% ("schiacciata") a ~270 ms, poi rimbalza fino a riposo in ~480 ms. |
| Contenuto in chiusura | Il corpo della pagina sfuma in ~100 ms mentre la forma lo taglia. Anello e ora tornano nella pillola. Dopo qualche secondo ricompare la mascotte. |
| Cambio pagina | L'intestazione resta ferma. Le due pagine si muovono **insieme**: la vecchia esce a sinistra (~−30 DIP) sfumando in ~200 ms, la nuova entra da destra (~+70 DIP) e si ferma senza rimbalzo visibile in ~350–400 ms. La misura della forma non cambia, se le due pagine sono grandi uguali. |
| Indicatori | Pagina corrente: una pillola bianca ~16 × 4 DIP; le altre sono punti grigi di 4 DIP. Al cambio la pillola **si allunga fino al punto nuovo e poi si ritira** (effetto "bruco"). |
| Opzioni | Velocità *Normale/Veloce*; apertura *al passaggio* o *al clic*; misura Auto/100–150%. |

## Cosa c'è di diverso da noi
| Aspetto | Noi oggi | Nusa | Cosa fare |
|---|---|---|---|
| Molla | `Spring(280, 26)`, ζ 0,78, ~2% di rimbalzo, uguale in apertura e chiusura (`Core/Animation/Spring.cs`) | ζ ~0,63–0,65, ~7% di rimbalzo, due molle | `Spring` con parametri per direzione: apertura (250, 20), chiusura (290, 22) |
| Entrata | si apre subito al `MouseEnter` | 290 ms di attesa + gonfiore del 7% | nuovo stato `Hover` in `NotchStateMachine` con `EnterDelay = 290 ms`; durante l'attesa target = riposo × 1,07 |
| Uscita | `LeaveDelay` 400 ms, poi chiusura | immediata, con schiacciata sotto la misura | `LeaveDelay` → 0 (resta 1500 ms per le schede); la schiacciata viene dalla molla di chiusura |
| Forma | `Border` a pillola, raggio `min(h/2, 14)` sui 4 angoli, staccata di 4 DIP dal bordo (`NotchLayout.TopMargin`) | unita al bordo, orecchie concave in alto, ~26 DIP in basso | `NotchShape.Geometry(size)` in Core (path con raccordi concavi, raggio basso scalato con l'altezza) usato da `_pill.Clip` e dallo sfondo. Lo stile *Sospeso* tiene la forma di oggi. |
| Vetro | `GlassBackdrop.SetShape(rect, radius)` sa fare solo rettangoli arrotondati | nero pieno di serie | fatto: il vetro resta in entrambe le forme; unito al bordo il rettangolo del vetro sale oltre lo schermo e solo le orecchie restano senza sfocatura |
| Ombra | ombra nativa di Windows 11 della compagna (stretta) | ombra ampia ~40 DIP, ~25% | ombra disegnata da noi sotto la forma (`DropShadowEffect` sul solo sfondo o un livello sfocato) solo da aperta, con opacità legata all'altezza |
| Colori | `Theme.Panel`: `Base #202020` o vetro colorato | nero puro + bagliore in basso a riposo | **non si prende**: restano i nostri colori |
| Misure | riposo 144 × 28, aperto 420 × 172 | riposo ~173 × 33, aperto ~481 × 160 | `NotchLayout.For`: riposo 172 × 32, Attività 256 × 32, aperto 480 × 172; pagine alte invariate (340) |
| Contenuto apertura/chiusura | livelli compatto/espanso in dissolvenza 120 ms (`FadeTo`) | taglio dalla forma + elementi condivisi | `_expanded` subito a opacità 1 sotto il taglio (dissolvenza 80 ms solo sul corpo pagina); ora e icona di stato traslate fra posizione compatta e intestazione con la stessa molla |
| Intestazione | ogni pagina ha la sua; nessuna riga comune | riga fissa con data e ora su tutte le pagine | riga comune in `_expanded` sopra `_pageHost` (titolo pagina · ora), che il cambio pagina non muove |
| Cambio pagina | solo la nuova: +40 DIP, 150 ms `CubicEase`; la vecchia sparisce subito (`Visibility.Collapsed`) | entrambe insieme, 70/30 DIP, ~400 ms | la vecchia resta visibile finché esce (−30 DIP, opacità a 0 in 200 ms); la nuova entra +70 DIP con molla (k 300, ζ 1) e opacità in 250 ms |
| Indicatori | `Ellipse` 5/6 DIP, accento sulla corrente | pillola bianca 16 × 4, "bruco" | `_indicators` diventa un `Canvas` con punti 4 DIP e una pillola che anima `Left` e `Width` su due molle sfasate (bordo in testa più veloce) |
| Frecce | nessuna, solo swipe e rotella | cerchi ‹ › ai lati | due pulsanti tondi ai lati del corpo pagina, visibili solo da aperto col mouse sopra |
| Motore | WPF `AllowsTransparency` (finestra a strati, rendering software) | GPU, DirectComposition | si tiene WPF. Misurare i fotogrammi persi a 4K (vedi Criteri); se non basta, il notch si sposta su composizione in un secondo tempo |
| Opzioni | Notch discreto | Classic/Floating, velocità, apertura al clic | Aspetto › Notch: *Unito al bordo* / *Sospeso*, *Movimento* Normale / Veloce (rigidità × 1,6), *Apri* Al passaggio / Al clic |

## Ordine di lavoro
1. **Molle e tempi** (`Spring`, `NotchStateMachine`): due molle, attesa d'entrata col gonfiore, uscita immediata. È la parte
   che si nota di più e tocca poco codice; i test della macchina a stati vanno aggiornati (`LeaveDelay`, nuovo `Hover`).
2. **Forma unita al bordo** (`NotchShape`, `NotchLayout`), fondo nero, ombra ampia, bagliore a riposo.
3. **Contenuto**: taglio invece della dissolvenza, ora e icona come elementi condivisi, riga d'intestazione comune.
4. **Cambio pagina e indicatori a bruco**, frecce.
5. **Opzioni** nella pagina Aspetto e aggiornamento di docs/02 (Movimento, Forme) e docs/03.

## Rischi
- **Vetro con le orecchie**: la compagna di `GlassBackdrop` ritaglia solo rettangoli arrotondati. Una forma concava richiede di
  passare la geometria alla composizione (`CompositionPathGeometry` come maschera) oppure di rinunciare al vetro nello stile
  *Unito*. Per ora rinunciamo: Nusa stesso lo tiene spento di serie.
- **Fotogrammi a 4K**: la finestra a strati di WPF ridisegna in software tutta l'area sporca. Con taglio + ombra + due pagine
  in movimento può scendere sotto i 60 fps; va misurato prima del passo 3.
- **Attesa d'entrata**: 290 ms fanno sembrare il notch più lento a chi ci va apposta. Con *Apri al clic* il clic apre subito.

## Criteri di accettazione
- Catturando lo schermo come per Nusa (fotogrammi ogni ~17 ms, larghezza della forma), le curve di apertura e chiusura
  hanno lo stesso rimbalzo (7 ± 2%) e gli stessi tempi di picco (260 ± 30 ms) e di assestamento (≤ 500 ms).
- Un passaggio del mouse sotto i 290 ms gonfia la pillola e la fa tornare, senza aprire.
- Uscendo col mouse la chiusura parte entro un fotogramma.
- Ora e icona di stato non spariscono mai durante apertura e chiusura: scivolano.
- Nel cambio pagina le due pagine sono visibili insieme per almeno 150 ms; l'intestazione non si muove.
- Nessun fotogramma oltre 25 ms durante apertura e cambio pagina su un monitor 4K al 150%.
- Con gli effetti di animazione di Windows spenti tutto salta allo stato finale, come oggi.
