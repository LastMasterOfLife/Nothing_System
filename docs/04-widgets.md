# 04 — Widget sul desktop (M2)

Riquadri in stile Nothing appoggiati sul desktop: sopra lo sfondo, **sotto tutte le finestre**,
visibili anche con "Mostra desktop" (Win+D). Si spostano trascinandoli; la posizione è salvata.

## Catalogo
| Widget | Taglia (DIP) | Contenuto | Aggiornamento |
|--------|--------------|-----------|---------------|
| `Clock` | 352 × 168 | HH:MM grande a punti, giorno della settimana in rosso, data in grigio | ogni minuto |
| `Battery` | 168 × 168 | percentuale a punti + barra a punti; rossa sotto il 20%; "IN CARICA" | ogni 20 s |
| `System` | 168 × 168 | CPU e RAM: percentuale + barra a punti | ogni 2 s |
| `Calendar` | 168 × 168 | mese corrente, settimana da lunedì, oggi cerchiato in rosso | ogni minuto (ridisegna al cambio giorno) |

Senza batteria (desktop fisso) il widget batteria mostra "AC" e la barra piena.

### Widget dei connettori
Ogni connettore (docs/14-agenti-connettori.md) può avere il suo widget: si accende dalla scheda del connettore
(*Widget sul desktop*) o da Impostazioni › Widget, ed è **spento di serie** (un connettore non finisce sul desktop da
solo). Id `connector:<id>`, salvato in `settings.json` (`connectorWidgets`).

| Valori | Taglia (DIP) | Griglia |
|--------|--------------|---------|
| 1 | 168 × 168 | un valore grande (passo fino a 7) |
| 2–4 | 352 × 168 | 2 colonne |
| 5–8 | 352 × 352 | 2 colonne, fino a 4 righe |

In alto il nome e da quanto è aggiornato (*errore*, *permesso* in rosso); per ogni valore l'etichetta grigia, il valore a
punti (rosso oltre soglia) e l'unità. Un testo che a punti non entra si scrive normale. La taglia si decide sul numero di
valori dichiarati o, se sono di più, arrivati (Core `ConnectorWidgetLayout`), così non salta coi primi dati. La posizione
è salvata con quelle dei plugin; un widget acceso dopo non finisce sopra un altro.

## Aspetto
- Riquadro `Surface` con bordo 1 px `Outline` (`#262626`), raggio 24, padding 16.
- Intestazione: etichetta piccola maiuscola grigia (es. `BATTERIA`).
- Le barre mostrano anche i punti spenti (`DotOff`): è il segno grafico della griglia Nothing.

## Livello desktop
Il trucco classico `WorkerW` (finestra figlia dello sfondo) non funziona per widget interattivi:
la vista icone di Explorer sta sopra e riceve tutti i click. Si usa invece:
1. finestra top-level `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`;
2. **proprietario = `Progman`** (la finestra del desktop): le finestre di proprietà del desktop restano visibili con Win+D;
3. `WM_WINDOWPOSCHANGING` forza sempre `HWND_BOTTOM`: nessuna finestra finisce sotto un widget;
4. al messaggio `TaskbarCreated` (riavvio di Explorer) si riaggancia il proprietario.

## Interazione
- Trascinamento con il tasto sinistro; al rilascio la posizione si aggancia a una griglia di 8 DIP,
  viene tenuta dentro lo schermo virtuale e salvata.
- Menu tasto destro: *Blocca posizione* (spunta), *Ripristina disposizione*, *Esci da Nothing Shell*.

## Persistenza
`%USERPROFILE%\.nothing-shell\widgets.json` (indipendente dalla cartella di lavoro):
```json
{ "locked": false, "widgets": [ { "kind": "Clock", "x": 1144, "y": 24 }, ... ] }
```
File mancante o non valido → disposizione predefinita (colonna in alto a destra dell'area di lavoro),
senza errori. Posizioni fuori dallo schermo (monitor scollegato) → riportate dentro.

## Logica nel Core (testata)
`WidgetSizes`, `WidgetLayout` (predefinita, snap, clamp), `WidgetSettingsStore` (JSON),
`DotBar`, `CpuUsage` (da GetSystemTimes), `BatteryStatus`, `MonthCalendar`.

## Strumento di verifica
`NothingShell.exe --snapshot <cartella>` disegna ogni widget in PNG (2×) ed esce: serve per
controllare la grafica senza toccare il desktop.

## Criteri di accettazione
- All'avvio i 4 widget compaiono sul desktop, sotto le finestre aperte.
- Win+D: i widget restano visibili.
- Trascinati e riavviata l'app, ricompaiono dove lasciati.
- CPU totale dell'app a riposo < 1%.
