# 15 — Terminale e cervello dei comandi

Nothing Shell esegue tutto ciò che esegue PowerShell, e capisce anche le frasi. Un piccolo modello locale decide
cosa fare quando serve; con l'uso le decisioni confermate diventano scorciatoie e il modello serve sempre meno.

## Il terminale
*Terminale Nothing* nel launcher, *Apri terminale* in Impostazioni › Sviluppo, oppure `>` nel launcher.

È una console vera: Windows Terminal se c'è, con `pwsh` se c'è, altrimenti `powershell.exe`, e il profilo dell'utente.
Non è disegnata da Nothing Shell, perché un emulatore in WPF sarebbe meno fedele e qui conta che funzioni ogni comando,
compresi `vim`, `ssh` e i programmi a tutto schermo. Parte con `-ExecutionPolicy RemoteSigned` per caricare il modulo,
solo per quella sessione.

In un PowerShell qualsiasi, oppure nel `$PROFILE`:
```powershell
Import-Module '<cartella di NothingShell.exe>\tools\NothingShell.psm1'; Enable-NothingShellBrain
```
Serve PSReadLine, che è di serie. `Disable-NothingShellBrain` rimette l'Invio normale.

## Cosa succede premendo Invio
Il modulo sostituisce il tasto Invio di PSReadLine. Nell'ordine:

1. **Comando vero: parte com'è, senza passare da nessun'altra parte.** Lo è tutto ciò che contiene simboli della shell
   (`| ; & $ < > { } =`), che è su più righe, che comincia con una parola chiave o con un comando esistente
   (`Get-Command`).
2. **Scorciatoia esatta**: la frase si riduce a parole chiave ("mi mostri quanta RAM è libera?" = "ram libera") e si cerca
   in un dizionario. Circa 55 µs, 1 ms dal terminale.
3. **Modello con un valore**: "processo firefox" → `Get-Process firefox`, vedi sotto.
4. **Frase simile** (parole in comune almeno al 75%): solo proposta.
5. **Modello locale**, con le scorciatoie più simili come esempi. Circa 1–1,5 s; una parola sola non lo disturba.
6. **Nessuna decisione**: la riga va a PowerShell, che dà il suo errore di sempre.

`? frase` chiede sempre al cervello, anche se la prima parola è un comando. Il comando proposto compare nella riga, si
può correggere, e una nota dice da dove viene:
```
Get-Service | Where-Object Status -eq Running  # nothing: modello 1540 ms, Invio per eseguire
```

Nel launcher: `> frase` mostra la decisione. Invio esegue quello che si vede (due volte se è pericoloso), Ctrl+Invio
il testo così com'è scritto.

## Chi può far partire cosa
| Origine | Sola lettura | Modifica | Pericoloso |
|---|---|---|---|
| Modello, frase simile | Invio | Invio | Invio, segnalato |
| Scorciatoia con meno di 3 riuscite, o appena fallita | Invio | Invio | non si impara |
| Scorciatoia con almeno 3 riuscite | **parte da sola** | Invio | non si impara |

Il modello non è affidabile e il modello di rischio lo presume: nelle prove, a "elenca i file" ha risposto una volta con
`Get-ChildItem … | Remove-Item -Force`. Per questo una sua proposta non parte mai da sola.

`CommandSafety` (Core, testato) lavora sul testo. La sola lettura è un elenco chiuso: tutto ciò che non riconosce conta
come modifica. Non è una difesa contro chi vuole ingannarla: decide solo cosa può partire senza Invio, e lì arrivano solo
comandi già confermati in passato.

## Come impara
Si impara **solo** ciò che è stato eseguito dopo Invio, in `%USERPROFILE%\.nothing-shell\brain\shortcuts.json`:
- **Riuscita**: +1. Dopo 3 riuscite un comando di sola lettura parte da solo.
- **Correzione**: se la riga è stata modificata prima di Invio, vale il comando eseguito e la fiducia riparte da 1.
- **Fallimento** (exit code diverso da 0 o errore): la scorciatoia chiede di nuovo Invio; dopo 3 di fila si dimentica.
  Quelle di serie non si dimenticano da sole, perché falliscono per il contesto (`git` fuori da un repository).
- **Generalizzazione**: "processo chrome → Get-Process chrome" e "processo edge → Get-Process edge" fanno nascere
  "processo {0} → Get-Process {0}". Servono due esempi. Il valore non può essere il nome del comando e accetta solo
  lettere, cifre, `. _ -` e nessun trattino iniziale, così una frase non può iniettare comandi né parametri.
- **Esempi per il modello**: più scorciatoie ci sono, migliori sono gli esempi che riceve.

Non si impara ciò che è pericoloso, né ciò che contiene chiavi, token o password: il file è in chiaro. Si tengono al
massimo 2000 scorciatoie, togliendo le meno recenti.

`ricorda "frase" "comando"` ne insegna una a mano, `dimentica "frase"` la toglie; *Dimentica le scorciatoie* nelle
impostazioni azzera tutto. Le scorciatoie di serie coprono RAM, disco, IP, processi, servizi, porte, versione di
Windows, batteria, file e git, e sono provate da `--brain-check`.

Si imparano le decisioni, non i risultati: "ram libera" rilegge sempre il valore attuale.

## Il modello
**Qwen2.5 0.5B Instruct** (GGUF Q4_K_M, 469 MB, licenza Apache 2.0) con LLamaSharp su CPU, in
`%USERPROFILE%\.nothing-shell\brain\`. Si scarica solo premendo *Scarica il modello*; il download riprende da dove si è
fermato, si può annullare, e il file si controlla con l'hash SHA-256 pubblicato. Senza modello tutto il resto funziona.
Nulla esce dal PC: dopo il download non c'è rete.

Provato su un i7-1255U senza GPU:

| Modello | Caricamento | Risposta | RAM | Esito |
|---|---|---|---|---|
| SmolLM2 135M | 0,15 s | 0,3–0,6 s | 190 MB | scartato: frasi, nessun comando |
| Qwen2.5 0.5B | 0,5 s | 0,8–1,5 s | 600 MB | comandi veri, ma circa la metà giusti |

È un modello piccolo: spesso sbaglia, o non decide. La sua forza è il ciclo: basta confermarlo o correggerlo una
volta e la risposta giusta resta.

Gira in un processo a sé (`NothingShell.exe --brain <file>`), avviato alla prima domanda e chiuso dopo 5 minuti che non
serve: un errore nel codice nativo non può chiudere la shell lasciando la taskbar nascosta, e la memoria torna a Windows.
Limite di attesa: 30 s a freddo, 10 s a caldo. Se scade, si chiude il processo e la riga va a PowerShell.

## Pipe
`\\.\pipe\NothingShell.Brain`, solo l'utente corrente: una riga JSON per connessione, una di risposta.

| `op` | Campi | Risposta |
|---|---|---|
| `decide` | `input`, `forced`, `cwd` | `command`, `source`, `risk`, `runNow`, `ms`, `note` |
| `outcome` | `input`, `command`, `ok` | vuota |
| `remember` | `input`, `command` | `note` se rifiutata |
| `forget` | `input` | `note` se non c'era |

Se Nothing Shell non è aperta, PowerShell funziona come sempre: i comandi veri non toccano mai la pipe.

## Diagnostica
- `NothingShell.exe --brain-check <file>`: tempi, ogni scorciatoia di serie provata, risposte del modello. Non impara nulla.
- `NothingShell.exe --brain-install`: scarica il modello senza interfaccia.
- `NothingShell.exe --brain-serve <file> <pipe> <secondi>`: solo il cervello, con scorciatoie e pipe proprie, accanto alla
  shell aperta; il terminale di prova la usa con `NOTHINGSHELL_BRAIN_PIPE`.

## Limiti
- Una frase che comincia con un comando esistente è quel comando ("sort i file" lancia `sort`): si usa `?`.
- Durante l'attesa del modello il terminale non accetta tasti, fino a 30 s al primo avvio.
- Una riga completamente riscritta dopo una proposta conta come correzione di quella frase.
- La scelta del comando non dipende dalla cartella: una scorciatoia vale ovunque.
