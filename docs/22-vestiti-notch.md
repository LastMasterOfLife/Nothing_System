# 22 — Vestiti del notch e selettore colore

## Selettore colore
Dove la pagina Aspetto chiede un colore non serve scrivere il codice: si apre un selettore come quello di Figma
(`Settings/ColorPicker`).

- **Quadrato**: verso destra più saturo, verso l'alto più luminoso, sulla tinta della banda.
- **Banda**: la tinta, dal rosso al rosso passando per tutto l'arcobaleno.
- Sotto, il colore scelto e il suo codice `#RRGGBB`: chi lo conosce già può scriverlo e confermarlo con Invio; un codice
  sbagliato torna quello di prima.
- Mentre si trascina il colore si vede subito su tutta la shell ma non si salva; si salva al rilascio. La pagina non si rifà
  durante il trascinamento (`_holdRebuild`), e la tinta resta anche scegliendo un grigio, che di tinta non ne ha.
- I conti stanno in Core: `Hsv.From` e `Hsv.ToArgb`.

Si usa per:
- **Colore d'accento**: l'ultimo pallino, con l'arcobaleno, apre e chiude il selettore; con un accento su misura ne prende il colore.
- **Sfondo › Su misura** (`SurfaceTone.Custom`, `AppearanceSettings.Surface`): il fondo di notch, widget, barra e pannelli.
  `Palette.Create` lo scurisce (tema scuro) o lo schiarisce (tema chiaro) quanto basta perché il testo abbia contrasto 7:1 e
  quello secondario 4,5:1.
- I colori dei vestiti.

## Vestiti
Un vestito (`NotchOutfit`) si stende sopra il fondo del notch, con la stessa forma (sospeso o unito al bordo):

| Voce | Scelte |
|------|--------|
| Colore | il fondo |
| Sfumatura | Nessuna · Orizzontale · Verticale · Diagonale, verso un secondo colore |
| Motivo | Nessuno · Righe · Pois · Quadretti · Griglia · Onde · Stelle · Cuori, col suo colore e un'intensità fino al 60% |
| Copertura | da pieno a lasciare intravedere il vetro (minimo 20%) |
| Bordo colorato | un filo di colore al posto del bordo del tema |

Di serie (`OutfitCatalog.BuiltIn`): Notte stellata, Natale, Halloween, Mare, Tramonto, Bosco, San Valentino, Carbonio, Nothing.

**Leggibilità.** I colori si regolano sul testo del tema come l'accento (`NotchOutfit.Resolve`): fondo almeno 4,5:1 col testo
e 3:1 con quello secondario, motivo almeno 3:1 col testo. Per questo nel tema chiaro un vestito scuro diventa la sua versione
chiara (Carbonio e Nothing diventano grigi). Lo verificano i test anche con colori scelti a caso.

**Dove si salvano.** Il vestito indossato è una copia dentro `AppearanceSettings.NotchOutfit`: solo valori semplici, così le
impostazioni si confrontano per valore come sempre, e gli stili rapidi non lo tolgono (`WithNotchOf`). I vestiti fatti a mano
stanno in `ShellSettings.Wardrobe`; hanno l'id che comincia con `mio-`.

## Pagina Aspetto › Vestiti del notch
- **Guardaroba**: Nessuno, quelli di serie, i tuoi e "Nuovo". Un clic e il notch lo indossa; doppio clic su uno tuo apre l'editor.
- Sotto, quello indossato: di serie si **personalizza** (se ne fa una copia "(mio)"), i tuoi si **modificano** o **duplicano**.
- **Editor**: anteprima del notch aperto in cima, poi colore, motivo e materiale. Il notch vero intanto **prova** il vestito
  (`Theme.Apply` senza salvare); Annulla, Esc, cambiare pagina o chiudere la finestra lo riportano a quello di prima.
  Salva e indossa, Salva (se era già indossato si aggiorna anche lì), Elimina vestito (con conferma).

## Notch
`NotchWindow` ha due `Path` sopra il fondo, `_dress` e `_dressPattern`, con la geometria della pillola a ogni fotogramma
(dentro il bordo da 1 quando è sospesa). I pennelli (`Rendering/OutfitPaint`) si rifanno solo se cambiano il vestito o la
tavolozza. Le stesse funzioni disegnano le anteprime delle impostazioni (`Settings/OutfitPreview`).

## Immagini di prova
`NothingShell.exe --outfit-sheet <cartella>`: ogni vestito di serie a riposo e aperto, in scuro e in chiaro, sospeso e unito
al bordo; poi la pagina Aspetto col guardaroba e il selettore aperto, e l'editor.
