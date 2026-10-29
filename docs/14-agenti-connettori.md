# 14 — Agenti e connettori

Due funzioni del notch per ciò che lavora fuori dalla shell: gli **agenti** (Claude Code e qualsiasi altro agente
che sappia mandare un messaggio) e i **connettori** verso dispositivi e sistemi di ogni genere. Si accendono e
spengono in Impostazioni › AGENTI e › CONNETTORI.

## Agenti

### Cosa si vede
- **Pillola**, con un agente al lavoro: scintilla a punti e **costo del turno** a destra (dopo il comando in corso, prima
  di download e musica), aggiornato a ogni risposta dell'API. Il punto rosso pulsa come per le altre attività.
- **Pagina Agenti** (alta, scorre): in alto il costo di **oggi** e quanti agenti sono al lavoro; una riga per sessione
  delle ultime 3 ore con progetto e modello, stato (punto rosso = al lavoro, ✓ = finito, ✕ = interrotto), tempo del
  turno, **token in entrata e in uscita** del turno e della sessione, costo. Il tooltip dice dove lavora e quanta cache
  ha letto e scritto.
- **Scheda HA FINITO** quando l'agente chiude il lavoro richiesto: progetto, modello, token e durata del turno, costo;
  *Agenti* apre la pagina, *Cartella* il progetto. Solo per i turni di almeno 10 s (Core `AgentRules`): una risposta
  breve non merita una scheda.

### Claude Code: i transcript
Claude Code scrive ogni risposta dell'API nel transcript della sessione, `~/.claude/projects/<progetto>/<sessione>.jsonl`
(`CLAUDE_CONFIG_DIR` sposta la cartella), i sotto-agenti in `<sessione>/subagents/`. Nothing Shell li segue con un
`FileSystemWatcher` (più un controllo ogni 10 s) e legge **solo le righe nuove**, in background; delle righe tiene token,
modello, ora e stato, **mai il testo** dei messaggi. Nessuna configurazione, nessun hook.

Regole (Core `ClaudeTranscript`, `AgentSession`, testate):
| Riga | Effetto |
|---|---|
| `user` con testo | inizia un turno: al lavoro, token del turno da zero |
| `user` con `tool_result` | ancora al lavoro |
| `user` "[Request interrupted…" | interrotto (nessuna scheda) |
| `assistant` | si contano i token di `usage`; la stessa risposta su più righe (una per blocco) conta una volta, vale l'ultima |
| `assistant` con `stop_reason: end_turn` | **lavoro finito**: scheda |
| righe `isMeta`, comandi locali (`/model`…), modello `<synthetic>` | ignorate |
| righe dei sotto-agenti | contano nei token e nel costo, non cambiano lo stato |

All'avvio i transcript di oggi si leggono per intero senza schede. Una sessione "al lavoro" senza righe nuove da 20
minuti (terminale chiuso, crash) non conta più come attiva.

I token arrivano quando una risposta dell'API è completa, non durante lo streaming: la pillola si aggiorna a ogni
passo dell'agente.

### Costo
Listino dell'API di Anthropic (dollari per milione di token, ottobre 2026), Core `AgentPricing`:
| Modello | Input | Output | Lettura cache |
|---|---|---|---|
| Fable 5.1 / Mythos 5.1 | 10 | 50 | 0,25 |
| Fable 5 / Mythos 5 | 10 | 50 | 1 |
| Opus 5.5 | 4 | 20 | 0,20 |
| Opus 5, 4.8, 4.7, 4.6, 4.5 | 5 | 25 | 0,50 |
| Opus 4.1, 4 | 15 | 75 | 1,50 |
| Sonnet 5.5, 5 | 2 | 10 | 0,20 |
| Sonnet 4.x | 3 | 15 | 0,30 |
| Haiku 5.5 | 0,10 (0,50 oltre 100K di prompt) | 0,50 (2,50) | 0,01 (0,05) |
| Haiku 4.5 | 1 | 5 | 0,10 |

La scrittura in cache costa 1,25× l'input (5 minuti) o 2× (1 ora, quella di Claude Code); la modalità veloce il doppio.
Un modello che non è in tabella ha costo `$--` invece di un numero sbagliato. **Con un abbonamento (Pro, Max) il costo è
una stima** di quanto costerebbe a consumo, non quanto si paga.

### Altri agenti
Qualsiasi agente o script manda i suoi totali dalla pipe dei comandi (la stessa di docs/12-sviluppo.md), una riga JSON:
```
NothingShell.exe --task '{"event":"agent","id":"report-notturno","title":"Report","model":"claude-sonnet-5-5","input":12000,"output":800,"state":"working"}'
```
| Campo | |
|---|---|
| `id` | obbligatorio: la sessione |
| `title` | nome nella pagina (di serie l'id) |
| `model` | per il nome breve e il costo, se manca `cost` |
| `input`, `output`, `cacheRead`, `cacheWrite` | token **totali** della sessione fino a quel momento |
| `cost` | dollari totali, se l'agente li conosce (altrimenti dal listino) |
| `state` | `working`, poi `done` (scheda HA FINITO) o `interrupted` |
| `cwd` | cartella del lavoro, per *Cartella* |
Il turno è la differenza dai totali di quando lo stato è passato a `working`.

### Diagnostica
`NothingShell.exe --agents-check <file>`: per ogni transcript di oggi progetto, modello, stato, token, costo della
sessione e di oggi, turni finiti. Senza testo dei messaggi.

## Connettori

Un connettore è un file `~/.nothing-shell/connectors/<id>.json` (id: minuscole, cifre e trattini) che dice da dove
leggere dei valori e, se serve, quali azioni offrire. La pagina **Dispositivi** del notch (alta, scorre) mostra per ogni
connettore nome, stato, da quanto è aggiornato e i valori; un valore oltre soglia è rosso e dà un avviso nel notch
(solo quando entra in allarme, non a ogni giro: Core `ConnectorAlerts`). Esempi pronti in [connectors/](../connectors/):
*Copia gli esempi* nelle impostazioni li mette in `connectors\esempi`, dove non partono; si spostano su quando sono pronti.

### Tipi
| `type` | Legge | Campi |
|---|---|---|
| `http` | GET di una URL (JSON o testo): NAS, router, stampanti 3D, API qualsiasi | `url`, `headers`, `readings[].value` |
| `homeassistant` | stato delle entità (`/api/states/<entità>`), unità e nome da Home Assistant | `url`, `readings[].entity`; token |
| `prometheus` | query istantanee (`/api/v1/query`) | `url`, `readings[].query` |
| `mqtt` | ultimo messaggio dei topic (Zigbee2MQTT, Tasmota, ESPHome…), filtri `+` e `#` | `host`, `port`, `tls`, `username`, `readings[].topic` |
| `ping` | raggiungibilità e latenza di qualsiasi cosa in rete | `host` |
| `file` | file JSON o di testo, anche su una cartella di rete | `path` |
| `command` | qualsiasi programma (PowerShell, ssh, python, curl…) che stampa JSON o testo | `command`, `args` |

Campi comuni: `name`, `refreshSeconds` (di serie 60, almeno 5), `secret` (token o password nel **Gestore credenziali**,
`NothingShell:connector:<id>`, mai nel file; per http `secretHeader` e `secretFormat`, di serie `Authorization: Bearer
{secret}`).

### Valori
`readings` è un elenco; per ognuno:
| Campo | |
|---|---|
| `label` | nome (di serie quello di Home Assistant, la query o il topic) |
| `value` | segnaposto `{percorso}` nel JSON, come i plugin: `{volumes[0].used}`. Un'uscita che non è JSON diventa `{"text": …, "lines": […]}`, quindi `{lines[0]}` è la prima riga |
| `unit`, `scale`, `decimals` | `"scale": 0.001` per i millesimi (temperature dei Raspberry), `"decimals": 1` |
| `above`, `below` | soglie: oltre diventa avviso |
| `alertOn` | testo che fa scattare l'avviso (`"APERTO"`, `"errore"`) |
Gli stati più comuni di Home Assistant si leggono in italiano (on → ACCESO, open → APERTO…). Un errore passeggero lascia i
valori di prima con il motivo accanto.

### Azioni e permessi
La shell può fare quello che fa una shell, ma **solo con il permesso dell'utente**:
- leggere (`http`, `homeassistant`, `prometheus`, `mqtt`, `ping`, `file`) non cambia nulla e parte da sé;
- un connettore `command` esegue un programma: parte solo dopo *consenti comandi e azioni* nelle impostazioni;
- `actions` aggiunge pulsanti nella pagina Dispositivi, anche loro solo con il permesso:

| Azione | Cosa fa | Campi |
|---|---|---|
| `command` | esegue un programma | `command`, `args` |
| `http` | POST/PUT/PATCH/DELETE (anche relativo all'`url` del connettore), col token | `url`, `method`, `body` |
| `homeassistant` | chiama un servizio (`/api/services/<dominio>/<servizio>`) | `service` (`light.turn_off`), `data` |
| `mqtt` | pubblica sul broker già collegato | `topic`, `payload` |
Con `"confirm": true` serve un secondo clic entro 3 s (riavvia, apri il cancello). L'esito arriva come avviso nel notch e
il connettore si rilegge subito.

Il permesso è salvato in `settings.json` (`connectorGrants`) insieme all'**hash del file**: se il file cambia (un comando
diverso, un'azione in più) il permesso cade e va dato di nuovo (Core `ShellSettings.IsConnectorGranted`, testato).

### Limiti
MQTT 3.1.1 con QoS 0 e sessione pulita (Core `Mqtt`, testato), TLS con i certificati riconosciuti da Windows. I comandi
hanno 15 s per finire, le richieste http 15 s di timeout.

### Diagnostica
`NothingShell.exe --connectors-check <file> [cartella] [--esegui]`: un giro di ogni connettore della cartella (di serie
quella dei connettori) con i valori o l'errore. I comandi partono solo con `--esegui`; MQTT resta fuori.

## Impostazioni e file
- `settings.json`: `disabledActivities` (`agents`, `connectors`), `connectorGrants`.
- `connectors/*.json`: i connettori; i segreti nel Gestore credenziali.

## Logica nel Core (testata)
`ClaudeTranscript`, `AgentSession`, `AgentPricing`, `AgentProtocol`, `AgentFormat`, `ConnectorParser`, `ConnectorReadings`,
`ConnectorAlerts`, `Mqtt`, `ShellSettings.WithConnectorGrant`.
