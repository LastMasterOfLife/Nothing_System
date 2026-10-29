# 07 — Impostazioni e plugin (M5)

## Impostazioni
Finestra Nothing (nera, 1000 × 720 DIP, ridotta se lo schermo è più piccolo), aperta da: menu col tasto destro di barra,
notch e widget (*Impostazioni…*) e dal launcher (voce "Impostazioni Nothing Shell").
Ogni modifica si applica **subito**, senza riavvio.

A sinistra il **menù** delle pagine (Generale, Notch, Telefono, Posta, Agenda, Sviluppo, Agenti, Connettori, Accesso,
Widget, Plugin, Info): la pagina aperta ha il fondo chiaro e il punto rosso, Connettori e Plugin mostrano quanti ce ne
sono. A destra il titolo a punti della pagina e le sue **schede** (fondo `Surface`, raggio 20), ognuna con un titolo
grigio sopra; tra due interruttori di fila una linea sottile. Ctrl+Tab e Ctrl+Maiusc+Tab cambiano pagina, Esc chiude
(o torna alla libreria dal modulo di un connettore). Il pulsante principale di un modulo (Collega, Attiva) è bianco
pieno, gli altri a contorno; Rimuovi chiede un secondo clic entro 3 s.

| Sezione | Voce | Effetto |
|---|---|---|
| Generale | Avvia con Windows | valore `NothingShell` in `HKCU\…\CurrentVersion\Run` (solo utente corrente) |
| Componenti | Notch | mostra/nasconde il notch |
| | Barra al posto della taskbar | spenta: barra nascosta e taskbar di Windows ripristinata |
| | Tasto Win apre il launcher | come l'opzione nel menu della barra |
| Widget | un interruttore per ogni widget (anche dei plugin) | mostra/nasconde il widget |
| | Blocca posizione · Ripristina disposizione | come nel menu dei widget |
| Plugin | Apri cartella · Ricarica | ricarica i manifest senza riavviare; elenca i plugin non validi e il motivo |
| Info | versione, cartella dei dati | — |

Interruttore stile Nothing: pillola con bordo; acceso = pillola bianca con punto nero a destra,
spento = punto grigio a sinistra.

`%USERPROFILE%\.nothing-shell\settings.json`:
```json
{ "notch": true, "bar": true, "disabledWidgets": ["System", "plugin:meteo"] }
```
(Tasto Win e posizioni restano in `launcher.json` e `widgets.json`.)

## Plugin
Un plugin è un widget per il desktop descritto da una cartella:
```
%USERPROFILE%\.nothing-shell\plugins\<id>\plugin.json   (+ eventuali script)
```
`<id>`: minuscole, cifre e trattini; è il nome della cartella.

```json
{
  "name": "Meteo",
  "size": "small",                // small 168×168 | wide 352×168
  "refreshSeconds": 600,          // minimo 5, predefinito 300
  "source": { "type": "http", "url": "https://api.open-meteo.com/v1/forecast?..." },
  "display": {
    "header": "METEO",
    "value": "{current.temperature_2m}°",
    "caption": "ROMA",
    "bar": "{current.relative_humidity_2m}",   // opzionale: numero 0..1 o 0..100
    "barMax": 100
  }
}
```
### Sorgenti
- **http**: richiesta GET (timeout 10 s), risposta JSON. I segnaposto `{a.b[0].c}` nei campi di
  `display` leggono la risposta; un percorso mancante diventa `--`.
- **command**: `{ "type": "command", "command": "script.ps1" | "python", "args": ["..."] }`.
  Il comando parte nella cartella del plugin (timeout 10 s, senza finestra). Lo standard output è:
  - JSON `{ "header", "value", "caption", "bar" (0..1), "alert" (bool) }` — i campi presenti
    sostituiscono quelli di `display`; oppure
  - testo semplice → diventa `value`.
  I file `.ps1` vengono eseguiti con `powershell -NoProfile -ExecutionPolicy Bypass -File`.

### Resa
Riquadro widget standard: intestazione, valore grande a punti (passo ridotto automaticamente se il
testo non entra), didascalia grigia, barra a punti opzionale (rossa se `alert`).
Errore della sorgente → valore `--` e didascalia con il motivo; si riprova al giro successivo.
Primo posizionamento: primo spazio libero della griglia dei widget (da destra a sinistra, dall'alto).

### Sicurezza
I plugin `command` eseguono programmi con i permessi dell'utente: installare solo plugin di cui ci si
fida. Nulla viene eseguito dentro il processo della shell, e un plugin che va in errore o si blocca
(timeout, processo chiuso) non tocca il resto.

### Esempi inclusi (`plugins/` nel repository)
- `meteo` (http, Open-Meteo, senza chiave): temperatura e umidità di Roma.
- `disco` (command, PowerShell): spazio libero del disco C:, rosso sotto il 10%.

## Logica nel Core (testata)
`ShellSettings` + store, `PluginManifest.Parse` (validazione con messaggi in italiano),
`JsonTemplate` (segnaposto con percorsi e indici), `WidgetData.FromCommandOutput`,
`WidgetLayout.FindFreeSlot`.

## Criteri di accettazione
- Spegnere la barra dalle impostazioni fa ricomparire subito la taskbar di Windows; riaccenderla la rinasconde.
- I due plugin d'esempio compaiono sul desktop con dati veri; un manifest rotto è elencato con il motivo.
- Le scelte sopravvivono al riavvio dell'app.
