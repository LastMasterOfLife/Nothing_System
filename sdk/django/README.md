# nothing_debug

Collega un portale Django alla dashboard di **Debug potenziato** di Nothing Shell
(Impostazioni › Debug potenziato). Spec: `docs/24-debug-potenziato.md` nel repo di Nothing Shell.

## Installazione
Nel venv del portale:
```
pip install -e "<cartella di questo file>"
```
In `settings.py`:
```python
INSTALLED_APPS += ["nothing_debug"]
MIDDLEWARE = ["nothing_debug.middleware.NothingDebugMiddleware", *MIDDLEWARE]  # per primo
```
Niente da aggiungere a `urls.py`: il middleware risponde da solo sotto `/_nothing/`.

Si accende solo con `DEBUG = True`. In produzione non raccoglie niente e `/_nothing/` non esiste.

## Collegare la shell
1. `python manage.py runserver` stampa `Nothing Debug · codice di collegamento K7Q2-9XMP`.
   Il codice vale 10 minuti e una volta sola. Lo trovi anche su `http://127.0.0.1:8000/_nothing/`
   (solo dal PC del portale) o con `python manage.py nothing_code`.
2. In Nothing Shell › Impostazioni › Debug potenziato scrivi l'indirizzo del portale e il codice, poi premi **Collega**.

La shell resta collegata anche quando il runserver si riavvia: il token (solo il suo hash) sta in
`~/.nothing_debug/`, fuori dal progetto. `python manage.py nothing_code --scollega-tutti` scollega tutti i PC.

## Cosa arriva nella dashboard
| Connettore | Cosa |
|---|---|
| richieste | metodo, percorso, codice, tempo, dimensione, vista, utente, numero e tempo delle query, intestazioni |
| log | tutti i logger da `LOG_LEVEL` in su, legati alla richiesta in corso |
| errori | eccezioni non gestite con lo stack (il codice del portale distinto da Django e librerie) |
| query | quelle oltre `SQL_SLOW_MS` (o tutte con `"SQL": "all"`) |
| controlli | database, migrazioni da applicare, cache, più i tuoi con `@nothing_debug.check` |
| eventi | quelli che mandi tu con `nothing_debug.emit(...)` |

```python
import nothing_debug

nothing_debug.emit("ordine creato", id=ordine.id, totale=ordine.totale)

@nothing_debug.check("Coda email")
def coda_email():
    n = Email.objects.filter(inviata=False).count()
    return n, "warn" if n > 50 else "ok"     # oppure (valore, stato, dettaglio) o un dict

try:
    paga(ordine)
except GatewayError as exc:
    nothing_debug.capture_exception(exc)     # un errore gestito che vuoi vedere lo stesso
```

## Impostazioni (tutte facoltative)
```python
NOTHING_DEBUG = {
    "ENABLED": DEBUG,        # acceso anche senza DEBUG solo se lo scrivi tu
    "NAME": "sinapsi_web",   # nome nella shell (di serie la cartella del progetto)
    "SQL": "slow",           # "slow", "all" o False
    "SQL_SLOW_MS": 100,
    "LOG_LEVEL": "INFO",
    "BODY": False,           # corpo di POST/PUT (4 KB, campi sensibili nascosti)
    "BUFFER": 2000,          # eventi in memoria
    "ALLOW_REMOTE": True,    # False: collegamento solo dal PC del portale
    "BUILTIN_CHECKS": True,
}
```

## Riservatezza
Cookie, `Authorization`, CSRF e parametri o campi con nomi come password, token, secret, key arrivano come
`[nascosto]`. Il portale non chiama mai la shell: è la shell che chiede, con il suo token. Con più processi
(gunicorn con più worker) ognuno ha i suoi eventi: per lo sviluppo usa il runserver.
