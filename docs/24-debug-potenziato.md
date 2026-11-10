# 24 — Debug potenziato

Stato: **giro 1 fatto** (portali Django). Completa [12](12-sviluppo.md) (sviluppo) e [18](18-connettori-diagnostica.md)
(diagnostica): quelli guardano il PC e i servizi da fuori, questo guarda **dentro** un portale in sviluppo.

Obiettivo: si collega un portale con un codice e la shell diventa uno strumento di attenzione a sé. Una dashboard con
richieste, log, errori, query e controlli dal vivo; il notch avvisa da solo quando qualcosa si rompe, anche con la
dashboard chiusa.

## Come si collega
```
Portale (runserver)                              Nothing Shell
  nothing_debug stampa il codice K7Q2-9XMP  ──▶  Impostazioni › Debug potenziato: indirizzo + codice
  POST /_nothing/pair {code, client}         ◀──  COLLEGA
  {token, portal}                            ──▶  token nel Gestore credenziali (NothingShell:debug:<id>)
  GET /_nothing/events?after=N  (ogni 1 s)   ◀──  registro, dashboard, attenzione
  GET /_nothing/status          (ogni 15 s)  ◀──  controlli (i "connettori" del portale)
```
- **La shell chiede, il portale risponde.** Niente porte aperte sul PC: funziona anche con un portale su un altro PC, in
  una VM o in WSL.
- **Codice**: 8 caratteri senza quelli che si confondono (`PairingCode`, alfabeto senza 0/O/1/I/L), 10 minuti, una volta
  sola, 5 tentativi sbagliati e non vale più. Lo stampa il runserver all'avvio; anche su `/_nothing/` (solo dal PC del
  portale) e con `python manage.py nothing_code`. Si può incollare tutto in un campo: `http://127.0.0.1:8000 K7Q2-9XMP`.
- **Token**: 32 byte casuali. Nella shell nel Gestore credenziali; nel portale solo lo SHA-256, in `~/.nothing_debug/`
  (fuori dal progetto, resta fra un riavvio e l'altro del runserver). Scollega lo cancella da entrambe le parti.
- **Riavvii**: il portale manda un `boot` diverso a ogni avvio; la shell riparte col cursore e conta i riavvii, senza avvisi.

## Lato portale: `sdk/django/nothing_debug`
App Django, si installa con `pip install -e` (il percorso è nelle impostazioni, che lo copiano). Due righe in `settings.py`:
`INSTALLED_APPS` e il middleware **per primo** (misura tutti gli altri e risponde alla shell prima di CSRF e sessioni).
Accesa solo con `DEBUG = True`. Dettagli e opzioni in [sdk/django/README.md](../sdk/django/README.md).

| Connettore | Da dove |
|---|---|
| richieste | middleware: metodo, percorso, codice, tempo, dimensione, vista, utente, query (numero e tempo), intestazioni |
| log | handler sulla radice di `logging` (da `LOG_LEVEL`), legati alla richiesta con un `contextvar` |
| errori | segnale `got_request_exception` (anche dai middleware; non 404 e permessi), stack con il codice del portale distinto |
| query | `execute_wrapper` su tutte le connessioni: lente (≥ 100 ms) o tutte |
| controlli | di serie database, migrazioni da applicare, cache; i propri con `@nothing_debug.check("Nome")` |
| eventi | `nothing_debug.emit("nome", chiave=valore)` |

Nascosti prima di partire: cookie, `Authorization`, CSRF, parametri e campi con password/token/secret/key. Il corpo solo
con `BODY: True`. In memoria al massimo 2000 eventi.

## Protocollo (versione 1)
Tutto JSON, `Authorization: Bearer <token>` tranne `pair` e la pagina del codice.

| Indirizzo | Risposta |
|---|---|
| `POST /_nothing/pair` `{code, client, protocol}` | 200 `{token, portal}` · 403 codice sbagliato o scaduto |
| `GET /_nothing/events?after=N&limit=500` | `{boot, seq, events, more}`; `after` oltre l'ultimo = da capo |
| `GET /_nothing/status` | `{portal: {name, framework, pid, uptime, debug, root}, checks: [{name, value, state, detail}]}` |
| `POST /_nothing/unpair`, `POST /_nothing/clear` | `{}` |
| `GET /_nothing/` | pagina col codice, solo da 127.0.0.1 |

Eventi: `{"seq", "kind", "t" (secondi Unix), "rid", …}` con `kind` = `request` · `log` · `error` · `sql` · `custom`.
Lettura tollerante (Core `DebugProtocol`): un campo che manca o di un altro tipo non fa perdere l'evento, un `kind`
sconosciuto si salta. Un altro framework (Flask, FastAPI, Express, ASP.NET) può parlare lo stesso protocollo.

## Pagina Debug del notch
Fra Server e Dispositivi, alta, scorre; c'è solo con il Debug potenziato acceso (si sposta o si nasconde in
Impostazioni › Notch). Per ogni portale: pallino dello stato (verde collegato, rosso giù o rifiutato, grigio in pausa),
nome, numeri degli ultimi 5 minuti (richieste/min, p95, errori) e **DETTAGLIO ›**, che apre la dashboard qui sotto
(anche il clic sul nome). Sotto, al massimo 4 cose da guardare degli ultimi 30 minuti (Core `DebugHighlights`, testato):

| Segno | Cosa |
|---|---|
| ERR | un errore per impronta, il più recente prima (al massimo 2, poi "altri N errori diversi"), con file:riga |
| 5XX | l'ultima 5xx senza il suo errore, con quante sono |
| MS | la richiesta più lenta oltre la soglia, con le query |
| CHK | i controlli che non sono a posto, col valore |

Con poco spazio restano prima le cose rosse. Un clic su una riga apre la dashboard su quell'evento; senza niente da
guardare c'è "Tutto a posto". Senza portali: COLLEGA UN PORTALE apre le impostazioni.

## Dashboard
Una finestra per portale (dalle impostazioni, dalla scheda del notch o subito dopo il collegamento), ridimensionabile,
doppio clic sul titolo per ingrandire.
- **Numeri degli ultimi 5 minuti**: richieste/min, 5xx con percentuale, eccezioni, 4xx, p95 e mediana, query per
  richiesta, warning, controlli da guardare. In rosso quando c'è da guardare.
- **Schede**: Tutto · Richieste · Errori (uno per impronta, con quante volte) · Log · Query · Eventi · Percorsi (per schema,
  `/ordini/{id}`, ordinati per p95) · Controlli. Ricerca (Ctrl+F; più parole = tutte; cerca anche nei file dello stack),
  *Solo problemi*.
- **Dettaglio**: richiesta (tempi, vista, utente, intestazioni, avviso N+1 da 30 query in su, *Copia cURL*, *Apri*),
  errore (stack con le righe del portale in evidenza; con il portale su questo PC un clic apre il file alla riga
  nell'editor), log, query. Sotto, **nella stessa richiesta**: tutto ciò che è successo con lo stesso `rid`.
- Scorrendo l'elenco si ferma (*NUOVI EVENTI ↑*), così la riga che si legge non scappa. *Pausa*, *Aggiorna*, *Svuota*.

## Attenzione autonoma (Core `DebugWatch`)
| Avviso | Quando |
|---|---|
| ERRORE NUOVO | un'impronta mai vista (tipo + file + funzione del codice del portale): `KeyError 'id'` e `'nome'` sulla stessa riga sono uno |
| RISPOSTA 5xx | una 5xx senza il suo errore nuovo (502 del proxy, 503 voluto) |
| RICHIESTA LENTA | oltre la soglia (3 s di serie) |
| PORTALE GIÙ / DI NUOVO SU | dopo 3 richieste senza risposta; "giù" solo se era stato visto su |
| COLLEGAMENTO RIFIUTATO | il portale non riconosce più il token |
| CONTROLLO DA GUARDARE / ROTTO | un controllo che peggiora (non quando migliora) |
| LOG ERROR | spento di serie |

Al primo giro si imparano gli errori già presenti senza avvisare. Ogni tipo si ripete al massimo ogni 2 minuti per
portale (errori nuovi esclusi). Niente schede con un'app a schermo intero o se quella dashboard è già davanti. La scheda ha
*Dashboard* (apre sull'evento) e, per gli errori, *Copia*. Avvisi spegnibili per portale.

## Codice
- Core `PortalDebug/`: `DebugEvents` (record), `DebugProtocol` (+ `PairingCode`, `PortalAddress`), `DebugLog`
  (registro a capienza fissa, statistiche, percorsi, gruppi di errori, tracce), `DebugWatch` (+ `DebugSettings`,
  `DebugAlertRules`). Test in `PortalDebugTests`.
- App: `Services/PortalDebugService` (un `PortalLink` per portale, tutto sul thread della UI tranne la rete),
  `Notch/DebugDashboardWindow`, `Notch/DebugNotchPage`, `Settings/SettingsWindow.Debug`, `ShellController.Debug`, `NotchCards.DebugAlert`.
- `settings.json` → `debug: {enabled, portals: [{id, name, address, watch}], rules}`. Mai il token.
- `NothingShell.exe --debug-check <indirizzo> <codice> [file]`: collega, legge eventi e controlli, scrive il rapporto e
  scollega. `--debug-snapshot <cartella>`: dashboard, scheda e pagina delle impostazioni con un portale finto.

## Prossimi giri (proposta)
- Altri framework con lo stesso protocollo: Flask/FastAPI (middleware ASGI/WSGI), Node/Express, ASP.NET Core.
- Profilo di una richiesta lenta (query duplicate raggruppate, tempo template vs. database).
- Chiedere all'assistente del notch "perché /ordini/12 dà 500?" con la traccia della richiesta (in locale).
- Eventi dal browser del portale (errori JavaScript, `fetch` falliti) con uno script incluso dal template di base.
