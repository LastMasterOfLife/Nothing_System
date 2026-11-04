# 21 — Mascotte: carattere e iniziativa

La mascotte sono due occhi tondi nel notch a riposo, al posto dell'ora (docs/20-stile-nusa.md). Fino a qui reagiva soltanto:
avvisi, lavori finiti, sonno. Con l'**iniziativa** fa anche cose da sé: gesti suoi e, ogni tanto, ti dice qualcosa. Qui c'è cosa
è stato fatto, le regole che la tengono simpatica invece che fastidiosa, e il catalogo di tutte le interazioni che varrebbe la
pena aggiungere, con le loro animazioni.

## Cosa è stato fatto
| Voce | Dove | Come |
|---|---|---|
| Impostazione | Aspetto › Notch › Iniziativa | `AppearanceSettings.NotchMascotInitiative`: *No* · *Ogni tanto* · *Spesso*, di serie *No*. La riga compare solo con la mascotte accesa; gli stili rapidi non la toccano (`WithNotchOf`). |
| Gesti suoi | `MascotBrain.Gesture` | Col mouse fermo e l'utente presente: ogni 2–5 min (*Ogni tanto*) o 40–100 s (*Spesso*) un gesto a caso fra occhiolino (30%), giro d'occhi (30%), saltello (20%) e sbadiglio (20%). Assonnata o stanca, solo sbadigli. Mai col mouse in movimento, mai mentre ci si passa sopra, mai appena sveglia. |
| Occhi stanchi | `MascotBrain.Tired` | Batteria sotto il 20% e non in carica (`BatteryStatus.IsLow`): occhi aperti al massimo al 70%, e i gesti diventano sbadigli. |
| Occhiolino | `MascotPose.Wink`, `MascotView` | Nuova grandezza della posa: verso −1 chiude l'occhio sinistro, verso 1 il destro, con la molla delle palpebre (1100/52). |
| Parla | `MascotNudges`, `NotchWindow.OnNudgeTick` | Ogni 2 s il notch chiede a `MascotNudges` se c'è qualcosa da dire. Se sì, la mascotte fa il suo gesto di richiamo e, finito il gesto, arriva un avviso con **lei a sinistra al posto dell'icona**: guarda il suo testo (`MascotView.Gaze`) e sobbalza mentre lo dice (`Talking`). Resta 6 s; passandoci sopra si apre la pagina giusta. |
| Bentornato | `MascotNudgeKind.WelcomeBack` | Mentre dormi (2 min senza input) si segna mail, agenti che finiscono il turno, comandi finiti e falliti. Al ritorno, almeno 1,5 s dopo il risveglio: «Mentre eri via: 2 mail, 1 agente ha finito». Apre Posta, altrimenti Agenti, altrimenti Lavoro. Se entro 2 min non è riuscita a dirlo (notch nascosto o occupato) lascia perdere. |
| Pausa | `MascotNudgeKind.Break` | Dopo 50 min di lavoro di fila: «Sei al computer da 50 minuti: una pausa?», poi ogni altri 50 («da 1 ora e 40»). Una pausa vera è 5 min senza mouse né tastiera; alzarsi per 3 min non conta. |
| Si è fatto tardi | `MascotNudgeKind.Late`, solo *Spesso* | Dalle 23:30 alle 5, una volta per notte: sbadiglia e dice «Si è fatto tardi». |
| Capogiro | `MascotNudgeKind.Busy`, solo *Spesso* | CPU al 90% o più per 20 s di fila: le gira la testa, senza dire niente. |
| Silenzio | `MascotNudges.QuietGap` | Fra due iniziative almeno 10 minuti (il riassunto del ritorno non conta: arriva dopo un'assenza). |
| Giochi col mouse | `MascotPlay`, Aspetto › Notch › Giochi col mouse | Risponde ai gesti fatti vicino al notch a riposo (sotto, *E. Gioca con il mouse*). `AppearanceSettings.NotchMascotPlay`, di serie acceso: non è iniziativa, sei tu a cominciare. Col mouse entro 450 DIP la mascotte lo guarda ogni 30 ms invece che ogni 60. |
| Animazioni spente | `MascotView.Think` | Con gli effetti di animazione di Windows spenti niente gesti suoi: sarebbero scatti. Le parole arrivano lo stesso, senza il gesto prima. |
| Immagini | `NothingShell.exe --mascot-sheet <cartella>` | `mascotte-espressioni.png` (ogni espressione in sei momenti, sulle pose a cui puntano le molle) e `mascotte-parla.png` (l'avviso in cui parla). Disegna e basta: niente rete, niente shell. |

I test sono in `MascotTests` (gesti, occhiolino, stanchezza, limiti di ogni espressione), `MascotNudgesTests` (pausa, ritorno,
notte, CPU, silenzio, *No*) e `MascotPlayTests` (ogni gioco su una traccia di posizioni, e i movimenti normali che non devono
farne scattare nessuno).

## Regole
1. **Prima mostra, poi dice.** Un'espressione basta quasi sempre; un gesto quando serve attenzione; le parole solo quando
   c'è qualcosa che non si capirebbe dagli occhi.
2. **Parla solo a riposo.** Mai sopra un avviso o una scheda, mai a notch aperto, mai mentre ci passi sopra, mai a schermo
   intero (il notch è nascosto), mai mentre il notch è in Attività (musica, chiamata, timer, comando, download).
3. **Poco e breve.** Una riga, in seconda persona, senza prediche e senza punti esclamativi. Dieci minuti di silenzio dopo
   ogni iniziativa.
4. **Tutto si spegne.** *No* la riporta a come era prima. Ogni nuova famiglia di iniziative avrà il suo interruttore (sotto).
5. **Il movimento segue Windows.** Molle che avanzano con `Motion.Speed`; con le animazioni spente, niente gesti spontanei.
6. **Il cervello resta in Core.** Pose e decisioni deterministiche (tempo e caso da fuori) e testate; l'App fa solo molle e
   disegno.

## Il corpo
Una posa (`MascotPose`) dice dove vogliono andare gli occhi; `MascotView` ci arriva con una molla per grandezza (rigidità /
smorzamento), ridisegnando solo mentre qualcosa si muove.

| Grandezza | Valori | Resa (scala 1, notch Piccolo) | Molla |
|---|---|---|---|
| `Open` | 0 chiusi · 1 aperti · fino a 1,4 sgranati | altezza 10 × Open, mai sotto 1,6 (resta una lineetta) | 1100 / 52 |
| `LookX`, `LookY` | −1..1 | spostamento di 4 DIP in orizzontale e 2 in verticale | 140 / 19 |
| `Smile` | 0..1 | occhi più larghi del 15%, più bassi della metà, alzati di 1,5 DIP | 220 / 26 |
| `Lift` | −1..0 | tutto il viso sale di 3 DIP | 520 / 20 (rimbalza) |
| `Wink` | −1..1 | l'occhio di quel lato si chiude fino alla lineetta | 1100 / 52 |

### Grandezze proposte
Per le espressioni del catalogo che oggi non si possono disegnare.

| Grandezza | Valori | Resa | Animazione |
|---|---|---|---|
| `Stretch` | 0,6–1,4 | allunga gli occhi in verticale e li stringe in orizzontale (e viceversa sotto 1): *squash & stretch* | molla 300 / 14, rimbalza |
| `Tilt` | −15°..15° | ruota la coppia di occhi intorno al centro (testa inclinata) | molla 180 / 20 |
| `Shape` | tondo · cuore · stella · X · spirale · `^` · `u` | forma di ogni occhio; i glifi sono matrici a punti come `DotIcons` | dissolvenza incrociata 120 ms, non a molla |
| `Blush` | 0..1 | due ellissi 6 × 3 rosa sotto gli occhi | opacità, 200 ms |
| `Brow` | −1..1 | due trattini 8 × 1,6 sopra gli occhi, inclinati di ±20°: curiosa (uno solo su), preoccupata, imbronciata | molla 220 / 26 |
| `Particle` | Z · ! · ? · ♪ · ♥ · ✦ | un glifo a punti che esce di lato dalla pillola, sale di 8 DIP e svanisce in 1,2 s | `DoubleAnimation` OutQuint |
| `Lean` | −1..1 | gli occhi scivolano verso un lato e la pillola si gonfia di 8 DIP da quel lato, come se allungasse il collo | molla del gonfiore (170 / 26) |

## Catalogo
**Livello**: *Sempre* (anche senza iniziativa), *Ogni tanto*, *Spesso*. **Stato**: ✅ fatta, ○ proposta. Le durate sono a
velocità 1×; le pose a cui puntano le molle sono scritte come `Open/LookX/LookY/Smile/Lift`.

### A. Vita a riposo
| Nome | Quando | Animazione | Livello | Stato |
|---|---|---|---|---|
| Battito | ogni 2,2–5,5 s; una volta su cinque due di fila a 140 ms | `Open` 0,06 per 110 ms | Sempre | ✅ |
| Segue il mouse | il mouse si è mosso negli ultimi 4 s | `LookX/Y` = posizione rispetto al notch (÷500, ÷300 DIP) | Sempre | ✅ |
| Si guarda intorno | mouse fermo | ogni 2,5–7 s uno sguardo a caso per 0,7–1,8 s | Sempre | ✅ |
| Sorride | mouse sulla pillola, in attesa d'apertura | `Smile` 1 | Sempre | ✅ |
| Sonnolenza e sonno | 1,5 min e 2 min senza input | `Open` 0,55, poi 0,08 guardando in basso | Sempre | ✅ |
| Risveglio | primo input dopo il sonno | `Open` 1,25 per 600 ms, poi un battito | Sempre | ✅ |
| Respiro nel sonno | dorme | `Lift` −0,12 ↔ 0 con un seno di 4 s: il viso sale e scende piano | Sempre | ○ |
| Zzz | dorme da 30 s | `Particle` Z ogni 3 s, alternando lato e misura (2 e 2,5 DIP per punto) | Sempre | ○ |
| Sogna | dorme, ogni 30–60 s | occhi chiusi che tremano: `LookX` ±0,3 a 6 Hz per 0,8 s (sonno REM) | Spesso | ○ |
| Battito di sera | dopo le 22 | battiti più lenti (160 ms) e più radi (3–7 s) | Sempre | ○ |

### B. Reazioni a ciò che succede
| Nome | Quando | Animazione | Livello | Stato |
|---|---|---|---|---|
| Sorpresa | avviso o scheda in arrivo; l'avviso aspetta 350 ms | 1,4/0/−0,7/0/0 per 900 ms | Sempre | ✅ |
| Contenta | un agente finisce il turno, un comando riesce | `Smile` 1 e due saltelli in 800 ms (`Lift` −\|sin\|), 1,6 s | Sempre | ✅ |
| Triste | un comando fallisce | 0,5/0/0,8/0/0 per 2 s | Sempre | ✅ |
| Flash | screenshot | occhi strizzati (`Open` 0,1, `Smile` 1) per 120 ms, poi sgranati 1,3 per 300 ms: il lampo | Sempre | ○ |
| Troppo forte | volume portato al 100% | `Shape` `>` `<`, `Stretch` 0,8, tremolio `LookX` ±0,1 a 12 Hz per 600 ms | Spesso | ○ |
| Shh | audio in muto | occhi chiusi (`Open` 0,08) e `Tilt` 8° per 1 s | Spesso | ○ |
| Annuisce | testo copiato negli appunti | `LookY` 0,6 → −0,2 → 0,6 → 0 in 500 ms: un «sì» | Spesso | ○ |
| Ascolta | cuffie collegate | `Tilt` 12° verso destra e `Smile` 0,5 per 1,2 s | Spesso | ○ |
| Stelle | la CI torna verde dopo un fallimento | `Shape` stella, tre saltelli in 900 ms, `Particle` ✦ | Ogni tanto | ○ |
| Cerca la rete | connessione persa | sguardo lento sinistra ↔ destra (1,5 s a passata), `Brow` preoccupata, `Particle` ? | Ogni tanto | ○ |
| Rete tornata | connessione di nuovo su | un saltello e `Smile` 0,7 | Ogni tanto | ○ |
| KO | un'app si chiude per un errore (Rapporto errori di Windows) | `Shape` X per 1 s, poi `Open` 0,6 | Spesso | ○ |
| Gnam | un file trascinato sopra il notch (lo Scaffale di Nusa, se lo facciamo) | mentre passa: `Open` 1,4, guarda il file; lasciato: `Lift` −0,6 e `Smile` 1 | Sempre | ○ |

### C. Gesti suoi
| Nome | Animazione | Livello | Stato |
|---|---|---|---|
| Occhiolino | 650 ms: per 450 ms un occhio chiuso (`Wink` ±1), `Smile` 0,6, sguardo verso quel lato; poi `Smile` 0,3 | Ogni tanto | ✅ |
| Giro d'occhi | 1,4 s: un cerchio intero in 1,2 s partendo dall'alto, in senso orario, raggio 0,9, `Open` 1,05 | Ogni tanto | ✅ |
| Saltello | 600 ms: un salto in 400 ms (`Lift` −1), `Smile` 0,7, sguardo appena su | Ogni tanto | ✅ |
| Sbadiglio | 1,8 s: sgranata e in su (250 ms); si chiude piano fino a 0,12 alzandosi (400 ms); resta chiusa (550 ms); si riapre a 0,75 (400 ms) | Ogni tanto | ✅ |
| Si stiracchia | `Stretch` 1,3 per 300 ms, poi 0,75 per 300 ms, poi 1 con rimbalzo; occhi chiusi a metà | Ogni tanto | ○ |
| Curiosa | cambia la finestra in primo piano: lo sguardo va verso il centro della nuova finestra per 1 s, con `Brow` curiosa | Spesso | ○ |
| Legge con te | mentre scrivi: piccoli scatti di `LookX` verso destra ogni 400 ms, come chi segue le parole | Spesso | ○ |
| Fischietta | `LookY` −0,6, `LookX` 0,5, `Particle` ♪ due volte in 2 s | Spesso | ○ |
| Starnuto | raro (1 gesto su 80): `Open` 0,1 e `Stretch` 0,7 per 300 ms, poi `Lift` −1 di colpo e `Open` 1,3 | Spesso | ○ |
| Nascondino | gli occhi scivolano fuori dalla pillola a sinistra (tagliati dal bordo) e rientrano da destra in 1,6 s | Spesso | ○ |

### D. Ti dice qualcosa
Tutte passano dal gesto di richiamo e dall'avviso in cui parla lei (sotto, *Come parla*).

| Nome | Quando | Gesto e testo | Livello | Stato |
|---|---|---|---|---|
| Bentornato | ritorno dopo il sonno, con qualcosa successo nel frattempo | Ti chiama · «Mentre eri via: 2 mail, 1 agente ha finito» → Posta | Ogni tanto | ✅ |
| Pausa | 50 min di lavoro di fila | Ti chiama · «Sei al computer da 50 minuti: una pausa?» | Ogni tanto | ✅ |
| Si è fatto tardi | dalle 23:30, una volta per notte | Sbadiglio · «Si è fatto tardi» | Spesso | ✅ |
| Capogiro | CPU ≥ 90% per 20 s | 2 s: due giri d'occhi veloci in 1,2 s, poi barcolla (`LookX` ±0,4 a 3 Hz che si spegne) con `Open` 0,6; niente testo | Spesso | ✅ |
| L'agente ti aspetta | un agente chiede un permesso o fa una domanda | picchietta: saltelli piccoli (`Lift` −0,3) a 2 Hz per 1,5 s · «Claude Code ti aspetta» → Agenti. Serve uno stato nuovo in `AgentState` (oggi: Idle, Working, Done, Interrupted), letto dal log della sessione | Ogni tanto | ○ |
| Riunione fra poco | 2 min all'inizio e la finestra in primo piano non è quella della riunione | impaziente: sguardo che va da te a destra e torna, due volte · «Fra 2 minuti: Standup» → Agenda. Non deve doppiare le schede a 10 min e all'inizio | Ogni tanto | ○ |
| Batteria critica | sotto il 10% | occhi stanchi e un lungo sbadiglio · «Sto per addormentarmi: attacca il caricatore» | Ogni tanto | ○ |
| Si rianima | caricatore attaccato con la batteria bassa | `Open` da 0,7 a 1,2 in 600 ms, un saltello; niente testo | Ogni tanto | ○ |
| Guarda lontano | ogni 20 min di schermo (regola 20-20-20) | senza testo: gli occhi vanno piano a sinistra in fondo (`LookX` −1, `Open` 0,9) e ci restano 20 s | Spesso, con interruttore Benessere | ○ |
| Acqua | ogni 90 min di lavoro | beve: `Tilt` −10°, occhi chiusi 800 ms · «Un bicchiere d'acqua?» | Spesso, con interruttore Benessere | ○ |
| Fine giornata | finita l'ultima ora di lavoro (docs/19) | contenta · «Oggi 7 ore e 30, quasi tutte su printer» → Ore | Ogni tanto | ○ |
| Buongiorno | primo accesso del giorno, prima della scheda del mattino | si stiracchia · «Buongiorno» (la scheda arriva subito dopo) | Ogni tanto | ○ |
| Buon fine settimana | venerdì, uscendo dopo le 17 | due saltelli · «Buon fine settimana» | Spesso | ○ |
| Build finita altrove | un comando lungo (> 1 min) finisce mentre il terminale non è in primo piano | contenta o triste · «La build è finita in 2 min 14 s» → Lavoro | Ogni tanto | ○ |
| Disco pieno | meno del 5% libero su C: | preoccupata (`Brow`) · «Il disco C: è quasi pieno» | Ogni tanto | ○ |
| Riavvio in attesa | aggiornamento di Windows che aspetta il riavvio da 3 giorni | sbadiglio · «Windows aspetta un riavvio da 3 giorni» | Spesso | ○ |

### E. Gioca con il mouse
Senza aprire il notch: il gioco avviene **sotto** la pillola, dove il mouse non la apre. `MascotPlay` riceve il mouse in DIP
rispetto al centro degli occhi (y verso il basso) e tiene gli ultimi 2,5 s. Regole comuni:
- Conta solo entro 450 DIP dagli occhi, in orizzontale e in basso.
- Fermo per più di 300 ms (un clic, una lettura) si ricomincia da capo, e così dopo un buco di più di 250 ms fra due
  posizioni (notch aperto o mascotte nascosta).
- Le inversioni di marcia sotto i 3 DIP sono tremolio della mano.
- Riconosciuto un gesto, la traccia si svuota: lo stesso movimento non risponde due volte, ma continuando risponde di nuovo
  (le fusa durano finché accarezzi).

Le pose sono `Open/LookX/LookY/Smile/Lift`, più `Wink` dove serve.

| Nome | Gesto dell'utente | Animazione | Livello | Stato |
|---|---|---|---|---|
| Fusa | **carezza**: avanti e indietro in orizzontale appena sotto la pillola (fino a 60 DIP, larghezza della pillola più 40 per lato), 4 inversioni in 1,8 s con tratti da 20 DIP alla larghezza della pillola più 80; in verticale al massimo metà del tratto | 1,8 s: 0,6/±0,3/0,1/1 dondolando (0,9 s a passata) con un fremito (`Lift` −0,15, 4 al secondo); dagli 1,5 s riapre gli occhi | Giochi col mouse | ✅ |
| Ridacchia | **solletico**: scatti piccoli (3–20 DIP) e svelti, 6 inversioni in 1 s, in orizzontale o in verticale, appena sotto la pillola | 1 s: tre saltelli in 750 ms (`Lift` −0,55), `Smile` 0,6, strizza un occhio e poi l'altro ogni 250 ms con lo sguardo che segue; poi 0,85/0/0/0,6 | Giochi col mouse | ✅ |
| Saluta | **saluto**: avanti e indietro largo (tratti da 50 DIP) più in basso (in media oltre 60 DIP sotto la pillola), 4 inversioni in 2 s | 1,5 s: sguardo di qua e di là (`LookX` ±0,8, 0,6 s a passata) verso di te (`LookY` 0,3) con `Smile` 0,7; poi occhiolino e un saltello | Giochi col mouse | ✅ |
| Annuisce | **sì**: su e giù sotto la pillola, 3 inversioni in 1,6 s con tratti da 40 DIP; in orizzontale al massimo metà del tratto | 1,15 s: due cenni in 1 s, `LookY` da 0,9 a −0,5 e guardando in su il viso si alza (`Lift` −0,7) | Giochi col mouse | ✅ |
| Capogiro | **giro**: un giro intero (almeno 331°) intorno a un centro sotto la pillola, raggio medio da 18 DIP e mai sotto il 35% del medio (avanti e indietro passa dal centro e non vale), in 0,4–2,5 s, nei due sensi | il capogiro della CPU (*D*): due giri d'occhi veloci, poi barcolla | Giochi col mouse | ✅ |
| Colpetto | **colpetto**: dentro la pillola dal basso e di nuovo fuori in basso entro 450 ms, prima che si apra (uscire di lato non vale) | 0,9 s: strizza gli occhi saltando (0,1/0/0/0,4/−0,4, 150 ms), poi sgranati verso di te (1,3/0/0,6/0) | Giochi col mouse | ✅ |
| Offesa | **terzo colpetto** in 6 s | 2 s: occhi a fessura (0,4), sbuffa con un saltello e guarda dall'altra parte (0,5/∓0,9/−0,3/0), poi torna piano a sbirciarti | Giochi col mouse | ✅ |
| Spavento | il mouse arriva verso il notch a più di 3000 DIP/s | si ritrae: `Stretch` 0,8, `Open` 0,8, `Lift` −0,5 per 300 ms, poi torna e ti fissa. Da pensare: chi va ad aprire il notch arriva veloce, e lo spavento a ogni apertura stancherebbe | Sempre | ○ |
| L'ha perso | il mouse esce dallo schermo dal bordo più vicino | guarda verso quel bordo 1 s, poi due battiti e `LookY` 0,4 (si arrende) | Sempre | ○ |
| Zitta per un'ora | voce del menù col tasto destro | `Open` 0,5 e `Tilt` −6° (offesa per 2 s), poi niente iniziative per un'ora | — | ○ |

### F. In Attività
Oggi con musica, timer, comandi o download il notch è in Attività e la mascotte non c'è. Proposta: rimpicciolita al 70% al posto
del punto rosso a sinistra.

| Nome | Quando | Animazione | Stato |
|---|---|---|---|
| Balla | musica in riproduzione | `Lift` a tempo: con i picchi del livello audio (`IAudioMeterInformation`) sopra la media degli ultimi 2 s; senza segnale, 2 Hz | ○ |
| Conta | timer in corso | guarda i secondi; negli ultimi 10 s `Open` 1,3 e un battito per secondo | ○ |
| Aspetta | comando in corso da più di un minuto | sbadigli ogni 30 s | ○ |
| Segue il download | download in corso | `LookY` 0,6, un battito a ogni 10% | ○ |
| Zitta | microfono o webcam accesi | non c'è: in chiamata solo le icone rosse | ○ |

### G. Ore e stagioni
| Nome | Quando | Animazione | Livello | Stato |
|---|---|---|---|---|
| Mattina | dalle 7 alle 10 | `Open` di base 1,05 | Spesso | ○ |
| Natale | 8 dicembre – 6 gennaio | un cappellino a punti 8 × 5 sopra l'occhio destro | Spesso | ○ |
| Halloween | 31 ottobre | occhi color zucca (`#F7630C`) | Spesso | ○ |
| Capodanno | mezzanotte del 1° gennaio | tre saltelli e `Particle` ✦ da entrambi i lati per 3 s | Spesso | ○ |
| Compleanno | un evento «compleanno» in agenda per oggi | al primo accesso: «Auguri a Marco!» con ✦ | Ogni tanto | ○ |

## Come parla
Fatto:
1. **Ti chiama** (`Calling`, 700 ms): occhi a 1,2 che guardano in basso verso di te (`LookY` 0,35), due saltelli in 500 ms
   (`Lift` −0,6), `Smile` 0,2. Per l'ora tarda il richiamo è lo sbadiglio (1,8 s).
2. Finito il gesto, **l'avviso**: al posto dell'icona c'è una seconda mascotte (`NotchWindow._speaker`) a 16 DIP dal bordo,
   il testo comincia 10 DIP dopo di lei. Guarda il testo (`Gaze` 0,8/0,1) senza seguire il mouse.
3. **Parla** (`Talking`, 1,6 s): 1/0,8/0,1/0,35 e quattro sobbalzi (`Lift` −0,5) mentre il testo comincia a scorrere; poi resta
   a guardarlo battendo le palpebre. L'avviso dura 6 s; passandoci sopra si apre la pagina del messaggio.
4. Se nel frattempo arriva un avviso vero, vince quello e lei rinuncia (`Lead`: in attesa vale l'ultimo).

Proposte:
- **Fumetto**: per le cose da una parola («Ciao!», «?», «!») la pillola a riposo si allunga di 40 DIP a destra e la parola
  compare accanto agli occhi, senza passare all'avviso. Esce in 200 ms, resta 2 s.
- **Sobbalzi sulle parole**: un sobbalzo per ogni parola del testo mentre la parola entra nella finestra dell'avviso, invece
  dei quattro fissi.
- **Risposta**: clic sull'avviso = «ok, grazie» → annuisce e il messaggio vale come letto. Mouse mai passato sopra =
  ignorato.
- **Si adatta**: tre messaggi dello stesso tipo ignorati di fila → raddoppia l'intervallo di quel tipo (pausa da 50 a 100
  min), fino a quando ne leggi uno.

## Impostazioni proposte
| Voce | Scelte | Di serie |
|---|---|---|
| Iniziativa ✅ | No · Ogni tanto · Spesso | No |
| Giochi col mouse ✅ | acceso/spento | acceso |
| Iniziativa › Gesti | acceso/spento | acceso |
| Iniziativa › Pausa | spenta · 30 · 50 · 90 min | 50 min |
| Iniziativa › Al ritorno | acceso/spento | acceso |
| Iniziativa › Benessere | 20-20-20 e acqua | spento |
| Iniziativa › Silenzio | nessuno o un orario (es. 9–12) senza iniziative | nessuno |
| Carattere | Calma (gesti lenti, niente starnuti) · Allegra · Dispettosa (più occhiolini, nascondino) | Allegra |
| Colore degli occhi | Inchiostro · Accento | Inchiostro |

## Ordine di lavoro proposto
1. **L'agente ti aspetta**: è la cosa più utile. Serve lo stato «in attesa» nel lettore delle sessioni (docs/14).
2. **Zitta per un'ora** nel menù e gli interruttori sotto Iniziativa.
3. **Sonno vivo**: respiro, Zzz, sogno. Solo vista, poco codice; richiede `Particle`.
4. **Mascotte in Attività** con la musica che balla.
5. **Forme e sopracciglia** (`Shape`, `Brow`, `Blush`): sbloccano stelle, KO, fusa, troppo forte.
6. ~~**Giochi col mouse**~~: fatti (carezza, solletico, saluto, sì, giro, colpetto). Restano spavento e «l'ha perso»; con
   `Blush` e `Particle` le fusa avranno guance e ♥.
7. **Benessere** e **stagioni**.

## Limiti noti
- In Attività (musica, timer, comando, download, microfono, **un agente al lavoro**) la mascotte non si vede: non fa gesti,
  non parla e non gioca. Mentre Claude Code lavora, quindi, niente giochi col mouse. La pausa aspetta che il notch torni a
  riposo e poi dice il tempo vero.
- Mentre parla (avviso) o mentre il notch è aperto non gioca: i gesti fatti in quel momento si perdono.
- I giochi stanno nella fascia sotto il notch, dove ci sono le schede del browser e le barre delle finestre: un avanti e
  indietro svelto senza mai fermarsi può far scattare le fusa anche senza volerlo. Le soglie (4 inversioni, mai fermi più di
  300 ms) lo rendono raro; se dà fastidio, Giochi col mouse si spegne.
- Il cenno, il saluto e le fusa muovono gli occhi di pochi DIP: nella misura Piccola si vedono meno.
- Il riassunto del ritorno conta solo posta, agenti e comandi; le notifiche del telefono no. Più comandi finiti insieme
  contano come uno.
- Un avviso che arriva mentre ti sta chiamando vince: quello che voleva dire si perde.
- L'ora tarda segue l'orologio del PC, non le ore di lavoro.

## Criteri di accettazione
- Con *No* la mascotte fa esattamente quello che faceva prima (`WithoutInitiativeItOnlyReacts`, `OffSaysNothing`).
- Nessun gesto col mouse in movimento, nessuna parola fuori dal riposo.
- Fra due iniziative almeno 10 minuti (`LeavesSilenceBetweenTwoThings`).
- Nessuna posa esce dai limiti di `MascotPose`, e ogni espressione finisce (`EveryExpressionStaysInRangeAndEnds`).
- Con gli effetti di animazione di Windows spenti niente gesti spontanei; le parole arrivano senza gesto.
- `--mascot-sheet` disegna tutte le espressioni senza toccare la rete né avviare la shell.
- Ogni gioco risponde alla sua traccia e a nessun'altra; attraversare lo schermo, andare verso il notch e fermarsi, passare
  fra le schede del browser fermandosi a cliccare, mezzo giro o una curva larga non fanno scattare niente
  (`OrdinaryMovesAreNotGestures`); lontano non gioca (`FarAwayItDoesNotPlay`).
