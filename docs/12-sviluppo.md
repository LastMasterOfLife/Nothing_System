# 12 — Lavoro nel notch, appunti intelligenti, server locali

Estende docs/03-notch.md, docs/08-notch-mail-pages.md e docs/10-live-activities.md. Tre gruppi di attività
pensate per chi programma, ognuna accesa e spenta in Impostazioni › SVILUPPO (`disabledActivities`: `tasks`,
`ci`, `clipboard`, `clipboardHints`, `clipboardSecrets`, `servers`); spenta, il suo servizio è fermo e la sua
pagina del notch sparisce dallo swipe.

Pagine del notch espanso: Home · Agenda · Posta · **Lavoro** · **Server** · **Appunti** · Timer · Sistema.
Le pagine delle funzioni spente non ci sono: lo swipe salta direttamente alla successiva.

## 1. Lavoro: comandi, build e CI

### Da dove arrivano
La shell ascolta la named pipe `\\.\pipe\NothingShell.Tasks` (solo l'utente corrente,
`PipeOptions.CurrentUserOnly`). Ogni connessione porta **un messaggio JSON su una riga**:

| `event` | Campi | Significato |
|---|---|---|
| `start` | `id`, `title`, `cwd`, `pid` | è partito un comando |
| `end` | `id`, `exitCode`, `log`? | il comando `id` è finito; `log` è il file con l'output |
| `done` | `title`, `seconds`, `exitCode`, `cwd`, `pid`, `quiet`? | comando già finito (hook del prompt), senza `start` |
| `push` | `cwd` | `git push` riuscito in `cwd`: si segue la CI del branch |

Messaggi non validi o sconosciuti si ignorano. `pid` è il processo della shell (pwsh, bash): la finestra del
terminale è il primo antenato con una finestra principale (Windows Terminal, conhost, VS Code).

Tre modi di mandarli, tutti in `tools/`:
- **`ns <comando>`** (modulo PowerShell `tools/NothingShell.psm1`): esegue il comando, copia l'output in un
  file temporaneo (`%TEMP%\nothing-shell\task-*.log`, tenuti gli ultimi 20) e manda `start` e `end`.
  Esempio: `ns dotnet build`, `ns npm test`. L'output resta nel terminale, ma alcuni programmi perdono i
  colori quando l'output è copiato.
- **Hook del prompt** (`Enable-NothingShellPrompt` nello stesso modulo, da mettere nel `$PROFILE`): dopo ogni
  comando che è durato almeno 30 s manda `done` con `quiet: true`. Dopo un `git push` riuscito manda `push`.
- **`NothingShell.exe --task '<json>'`**: per altre shell (bash, cmd, script, CI locale). Avvia il processo,
  scrive il messaggio e chiude (~0,3 s).

### Nel notch
- **Pillola compatta**: a destra l'icona del terminale e il tempo trascorso (`1:23`) del comando avviato per
  ultimo. Ha la precedenza sul download e sull'equalizzatore.
- **Scheda alla fine** (8 s, col mouse sopra resta aperta): `FATTO` in bianco o `ERRORE 1` in rosso (il numero
  è l'exit code). Nella scheda ci sono il titolo del comando, la durata e il primo errore riconosciuto
  dall'output (`Program.cs:42 · CS1002: ; previsto`). Pulsanti:
  - *Errore*: apre il file alla riga nell'editor (se c'è un errore riconosciuto);
  - *Log*: apre l'output (se c'è un log);
  - *Terminale*: porta davanti la finestra del terminale.
- **Quando si mostra la scheda**: dopo `ns` sempre. Dopo l'hook del prompt (`quiet`) solo se il terminale non
  è la finestra in primo piano: se lo stai guardando lo sai già.
- **Pagina Lavoro**: i comandi in corso con il tempo, poi gli ultimi finiti (fino a tre righe in tutto), con
  ✓/✗, titolo, durata e quanto tempo fa. Un clic apre l'errore, altrimenti il log. Sotto c'è la riga della CI.

Un comando in corso da più di 6 ore, o il cui terminale non esiste più, viene tolto (terminale chiuso a metà).
Le liste vivono solo in memoria (Core `TaskTracker`, testato).

### Primo errore e posizioni nel codice
Il Core `CodeLocations` (testato), condiviso con gli appunti, riconosce:

| Formato | Esempio |
|---|---|
| MSBuild, csc, tsc | `src\Program.cs(42,13): error CS1002: ; expected [C:\app\app.csproj]` |
| gcc, clang, Go, generico | `main.c:10:5: error: expected ';'` |
| rustc | `error[E0425]: cannot find value` … `--> src/main.rs:3:5` |
| Traceback Python | `File "C:\app\main.py", line 12, in run` (l'ultimo frame) |
| Stack .NET | `at App.Run() in C:\app\Program.cs:line 42` |
| Stack JavaScript | `at run (C:\app\index.js:10:15)` |

I percorsi relativi si risolvono rispetto a `cwd`. Nelle stack trace vale il primo frame che non sta in una
libreria (`node_modules`, `.nuget`, `site-packages`, `lib\python`, `internal/`) e il cui file esiste.

**Editor**: `editor` in `settings.json` è un modello di comando (`code --goto "{file}:{line}:{column}"`).
Se manca, si usa il primo fra VS Code, VS Code Insiders e Cursor trovato nel `PATH`. Se non c'è nessuno di
questi, il file si apre con l'app associata, senza riga.

### CI (GitHub Actions)
Dopo un messaggio `push`, la shell interroga
`gh run list --branch <branch> --limit 10 --json databaseId,status,conclusion,name,url,createdAt`, eseguito in
`cwd` (il branch arriva da `git rev-parse --abbrev-ref HEAD`). Si parte dopo 5 s e si ripete ogni 15 s, finché
le esecuzioni create dopo il push sono tutte concluse (al massimo per 60 minuti). Se non compare nessuna
esecuzione entro 2 minuti, il repository non ha CI per quel branch e si smette.
- In corso: nella pagina Lavoro `CI · build, test · in corso`, e nella pillola il punto che pulsa.
- Finita: scheda `CI OK` o `CI FALLITA` con il nome dei workflow falliti. I pulsanti sono *Apri* (pagina
  dell'esecuzione) e *Rilancia* (`gh run rerun <id> --failed`), quest'ultimo solo se è fallita.
- Senza `gh` installato o autenticato: nella pagina compare la riga grigia
  `CI: installa e autentica GitHub CLI (gh auth login)`. Nessun errore visibile.

Lettura del JSON di `gh` e stato complessivo: Core `CiRuns`, testato.

## 2. Appunti intelligenti

### Cosa si ascolta
`AddClipboardFormatListener` su una finestra di soli messaggi. Conta solo il testo (`CF_UNICODETEXT`) fino a
100.000 caratteri. **Non si guarda nulla** se negli appunti c'è uno dei formati con cui i gestori di password
chiedono di essere ignorati: `ExcludeClipboardContentFromMonitorProcessing`, `Clipboard Viewer Ignore`, oppure
`CanIncludeInClipboardHistory` = 0 (KeePass, 1Password, Bitwarden, Windows stesso).

### Cosa si riconosce
Core `ClipClassifier` (testato), in quest'ordine:

| Tipo | Riconoscimento | Azioni |
|---|---|---|
| **Segreto** | prefissi noti: GitHub (`ghp_`, `gho_`, `ghs_`, `github_pat_`), GitLab `glpat-`, Slack `xox?-`, AWS `AKIA…`, Stripe `sk_live_`/`rk_live_`, OpenAI `sk-`, Anthropic `sk-ant-`, Google `AIza…`, npm `npm_`, chiavi private PEM, connection string con `Password=`/`pwd=` | *Cancella*, *Tieni* |
| **JWT** | `xxx.yyy.zzz` con header e payload in base64url che sono JSON | *Copia payload*, *Cancella* |
| **JSON** | inizia con `{` o `[` e si legge come JSON | *Formatta*, *Minifica* |
| **Errore / stack trace** | `CodeLocations` trova almeno una posizione | *Apri* (file alla riga) |
| **Colore** | `#rgb`, `#rrggbb`, `#rrggbbaa`, `rgb()`/`rgba()` | anteprima, *Copia HEX*, *Copia RGB* |
| **Timestamp** | 10 o 13 cifre fra il 2001 e il 2100 | data locale, *Copia ISO* |
| **URL** | una riga, `http(s)://` | *Apri* |
| **Percorso** | una riga, `C:\…` o `\\server\…`, anche fra virgolette, che esiste | *Apri*, *Cartella* |
| **Testo** | il resto | — |

*Formatta* e *Minifica* sostituiscono il contenuto degli appunti; la cronologia segna la nuova versione.

### Segreti e token
Un segreto o un JWT appena copiato apre una scheda (8 s) con un titolo rosso (`CHIAVE GITHUB`, `TOKEN JWT`).
Per un JWT c'è anche la scadenza, letta da `exp` (`scade tra 2 h`, `scaduto`). Il testo nella scheda è
`Si cancella dagli appunti tra 30 s`. Dopo 30 s gli appunti vengono svuotati, ma solo se contengono ancora
quel testo (si confronta il numero di sequenza degli appunti, `GetClipboardSequenceNumber`). *Cancella* svuota subito, *Tieni* annulla
la cancellazione.

Nella cronologia un segreto è sempre mascherato (`ghp_••••3f2a`) e il testo vero non viene conservato: non si
può ricopiare da lì. La cronologia di Windows (Win+V) lo registra comunque, se è attiva, perché lo decide l'app
da cui si copia.

### Nel notch
- **Avviso al momento della copia** (`clipboardHints`, 4 s): solo per JSON, errori, colori e timestamp, per
  esempio `JSON - 2,1 KB`, `ERRORE - Program.cs:42`, `#FF3B30` (con l'icona del colore copiato). Passandoci sopra col mouse si apre la pagina
  Appunti. Per URL, percorsi e testo non compare nulla: si copiano troppo spesso.
- **Pagina Appunti**: le ultime tre copie con l'etichetta del tipo a punti (`JSON`, `ERR`, `URL`, `CLR` col pallino del colore, `TS`,
  `DIR`, `KEY`, `TXT`), l'anteprima di una riga e l'azione principale. Un clic sulla riga la rimette negli
  appunti; per i segreti non si può.

La cronologia tiene 30 voci in memoria e si svuota all'uscita: non si scrive mai su disco. Due copie uguali di
seguito contano una volta.

## 3. Porte e server locali

### Cosa si legge
`GetExtendedTcpTable` (IPv4 e IPv6, `TCP_TABLE_OWNER_PID_LISTENER`) ogni 3 s, solo mentre l'attività è accesa.
Il nome del processo si ricava dal PID e resta in cache finché il PID esiste.

Core `DevServers` (testato) decide cosa mostrare:
- si esclude il sistema (`System`, `svchost`, `lsass`, `wininit`, `services`, `spoolsv`, `smss`, `csrss`,
  Nothing Shell stessa) e le porte di Windows (135, 139, 445, 5040, 5357, 7680);
- si escludono le porte da 49152 in su, che sono di RPC e delle app in background, tranne quando le apre un
  runtime di sviluppo (`node`, `python`, `dotnet`, `java`, `ruby`, `php`, `go`, `deno`, `bun`, i processi
  generati da `dotnet run`, Docker, WSL);
- si escludono le app in background che tengono porte aperte (Steam, Spotify, Discord, OneDrive, VS Code,
  i browser, client torrent…) e i servizi di Windows (sessione 0) che non sono un runtime di sviluppo né su
  una porta nota: VPN, driver, utilità del produttore;
- la stessa porta dello stesso processo su IPv4 e IPv6 conta una volta;
- l'ordine è per numero di porta.

Le porte note hanno un nome: 5432 Postgres, 3306 MySQL, 1433 SQL Server, 6379 Redis, 27017 MongoDB,
9200 Elasticsearch, 5672 RabbitMQ, 11434 Ollama. Per queste non c'è *Apri*, perché non sono pagine web.

### Nel notch
- **Avviso** (4 s) quando una porta nuova si mette in ascolto, per esempio `LOCALHOST:5173 - NODE`.
  Passandoci sopra si apre la pagina Server. Le porte già aperte all'avvio della shell non danno avvisi.
- **Pagina Server**: fino a tre righe con la porta a punti, il processo (`node · PID 18244`) o il nome del
  servizio, e due pulsanti:
  - *Apri*: `http://localhost:<porta>` nel browser;
  - *✕*: termina il processo. Il primo clic trasforma il pulsante in un `TERMINA?` rosso, il secondo termina;
    dopo 3 s senza conferma torna com'era. Docker, WSL e i servizi di Windows non hanno ✕ (fermarli
    spegnerebbe tutto). Se il processo è di un amministratore, per 3 s al posto dei pulsanti compare in rosso
    `serve l'amministratore`.
- Con più di tre server, il titolo dice quanti sono (`5 server`) e si vedono i primi tre.

## Diagnostica
`NothingShell.exe --dev-check <file>` è in sola lettura e scrive:
- i server trovati e quelli esclusi, con il motivo;
- l'editor che verrebbe usato;
- se `gh` è disponibile;
- il tipo riconosciuto del testo che c'è ora negli appunti (senza scriverne il contenuto).

## Fuori da questa versione
- launcher `:3000` → chi occupa la porta;
- CI di GitLab e Azure DevOps;
- hook del prompt per bash e zsh: per ora con `--task`.
