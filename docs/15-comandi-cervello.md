# 15 — Terminale e cervello dei comandi

Nothing Shell esegue tutto ciò che esegue PowerShell, e capisce anche le frasi. Un piccolo modello locale decide
cosa fare quando serve; con l'uso le decisioni confermate diventano scorciatoie e il modello serve sempre meno.

## L'assistente nel notch
Il pulsante del terminale in basso a destra nel notch espanso, su ogni pagina (o *Chiedi o scrivi un comando…* nel menù
col tasto destro), apre una casella: si scrive cosa serve e sotto compaiono i comandi eseguiti, la risposta e le scelte.
*TERMINALE* apre la console vera, *NUOVA* dimentica la conversazione, Esc interrompe o chiude.

```
quanto spazio libero ho sul disco C e quanta ram?
  ✓ Get-PSDrive -PSProvider FileSystem | Select-Object Name, @{n='LiberoGB';e={$_.Free/1GB}}…
  ✓ Get-CimInstance Win32_OperatingSystem | Select-Object @{n='LiberaGB';e={$_.FreePhysicalMemory/1MB}}…
  Sul disco C hai 109,75 GB liberi. La RAM totale è 15,7 GB e ne hai 2,16 GB liberi.
```

Il modello lavora a turni con tre forme: `COMANDO:` (si esegue e il risultato torna al modello), `RISPOSTA:` e `SCELTE:`
(pulsanti che mandano la scelta come nuova domanda). Al massimo 6 comandi per domanda. Le frasi già note leggono subito,
senza aspettare il modello, che poi scrive la risposta. *RICORDA COME SCORCIATOIA* insegna una frase risolta da un solo
comando: quello che il modello esegue da solo non si impara se non lo si chiede. `>` davanti rende il testo sempre un
comando, `?` sempre una frase.

Una risposta senza alcun comando riuscito è segnata *Non verificata*: nelle prove un modello, dopo due comandi rifiutati,
ha risposto "ci sono 0 file". Il modello riceve i risultati come dati, tra delimitatori, e con l'istruzione di non obbedire
a ciò che c'è scritto; i segreti riconosciuti sono mascherati prima di arrivare allo schermo e al modello.

Tre aiuti al modello, nati da errori visti nelle prove:

- **Un comando riuscito senza valori non è una lettura**: vuoto o con la sola intestazione, come quando si chiede una
  proprietà che non esiste. Il modello legge *NESSUN DATO*. Prima, da una tabella vuota, ha risposto "ha 8 processori logici".
  Non vale per i comandi confermati dall'utente, che spesso finiscono senza scrivere niente.
- **Dopo ogni risultato si ripete la domanda**: i modelli piccoli la perdono e continuano a leggere.
- **Lo stesso comando non si esegue due volte per la stessa domanda**: il modello viene rimandato a ciò che ha già, o a un
  altro comando se il primo non ha dato niente. Una `RISPOSTA` seguita da un `COMANDO` vale come comando: ammette che manca un dato.

### Chi risponde
Il primo disponibile fra quello scelto in Impostazioni › Sviluppo, **Qwen3.5 4B** incorporato se è scaricato, il primo modello
di Ollama. Nulla esce dal PC. Si scarica con *SCARICA IL MODELLO (2,6 GB)* e gira in un processo a sé, come quello piccolo.

Su un i7-1255U, 16 GB, senza GPU, con 40 domande vere (dati del PC, file, git, un file con istruzioni ostili, un file che non
esiste, richieste ambigue, modifiche che devono chiedere conferma) e giudizio automatico contro i valori letti a parte:

| Modello | Prime 28 | 12 mai usate per ritoccare nulla | s/domanda | RAM |
|---|---|---|---|---|
| **Qwen3.5 4B** Q4_K_M | 26 | 12 | 22–30 | 3,1 GB |
| Qwen3.5 9B Q4_K_M | 27 | 11 | 43–53 | 5,9 GB |
| `qwen3:8b` con Ollama | 17 | 9 | 30–45 | 5,5 GB |
| Gemma 4 E2B Q4_K_M | 20 | 4 | 15–18 | 3,2 GB |
| Ternary Bonsai 2 4B Q2_0 | 13 (senza gli ultimi esempi) | non provato | 35 | 1,4 GB |
| Qwen3.5 2B Q4_K_M | 11 (senza gli ultimi esempi) | non provato | 29 | 1,3 GB |

Sulle prime 28 il prompt e il ciclo sono stati ritoccati guardando gli errori di Qwen3.5 4B, quindi lo favoriscono: conta
di più l'ultima serie. È una sola esecuzione a temperatura 0, e piccole modifiche al prompt cambiano l'esito di risposte
che non c'entrano: differenze di uno o due punti non vogliono dire niente. Le prime due righe sono il processo vero; per
entrambe una risposta giusta sulla RAM libera è stata contata a mano, perché il riferimento automatico era letto prima che il
modello occupasse la memoria. Un modello più grande non ha dato risposte migliori, quindi si usa lo stesso su ogni PC.

Sbaglia ancora, e con sicurezza: "388 GB in totale" dallo spazio usato, "Windows 10" da `10.0.26200`. Per questo si vedono
i comandi eseguiti.

Il modello piccolo non scrive mai risposte, perché si inventa ("il sistema è stato configurato correttamente"): senza modelli
propone un solo comando, che passa dallo stesso cancello e di cui si vede il risultato, e quando non sa decidere offre le
scorciatoie più vicine. Ollama si cerca su `127.0.0.1:11434`.

Il primo turno legge tutto il prompt, circa 27 s su questa CPU. Poi si riparte da un'istantanea della memoria del modello
(80 MB, 0,02 s) e un turno ne costa 1–5: sui modelli Qwen3.5 togliere solo la coda della memoria non ha effetto, quindi le
istantanee sono l'unico modo. L'ora sta nell'ultima riga del prompt, fuori dall'istantanea: all'inizio costringerebbe a
rileggere tutto ogni minuto. Il processo si chiude dopo 5 minuti che non serve e la memoria torna a Windows.

### Il cancello
Ogni comando dell'assistente, da chiunque arrivi, passa da qui nell'ordine:

1. **Critico** (elenco qui sotto): due clic distinti su *ESEGUI*, entro 4 s. Non si conferma con Invio, e un doppio clic
   vale uno solo. Vale anche per le scorciatoie e per i comandi scritti a mano nel pannello.
2. **Scritto a mano nel pannello**: parte com'è.
3. **Sola lettura verificata** dall'esecutore limitato: parte da solo.
4. **Tutto il resto**: un clic su *ESEGUI*. L'elenco critico non può essere completo, quindi ciò che non è riconosciuto
   come lettura non parte da solo anche se non è nell'elenco.

*NEL TERMINALE* passa il comando alla console vera, per i programmi interattivi; se è critico lì si deve scrivere SI.

### L'esecutore limitato
`tools/NothingShell.Agent.ps1` è ciò che rende possibile far partire da solo un comando del modello. Tre barriere:

- **Albero sintattico**: solo comandi di un elenco chiuso e con il nome scritto per esteso, niente metodi, assegnazioni,
  cicli, reindirizzamenti, stringhe composte, percorsi di rete, parametri come `-ComputerName` (anche abbreviati).
- **Sessione a parte**: tutto il resto è nascosto e non ci sono programmi fuori elenco.
- **Linguaggio limitato di PowerShell**: niente .NET, nemmeno con una conversione come `[System.IO.StreamWriter]'a'`.

La prima è indispensabile: provato, un blocco dato a `Where-Object` vede anche i comandi nascosti. Il rifiuto di una forma
riscrivibile torna al modello, al massimo 2 volte; poi decide l'utente. Limite di 20 s, 2 minuti per quelli confermati;
nessuno standard input, così chi chiede qualcosa fallisce invece di restare appeso.

Non c'è nessun comando di rete, quindi ciò che si legge non può uscire dal PC. La lettura invece non ha confini: i file di
credenziali noti (`.ssh`, `.env`…) sono rifiutati, ma con caratteri jolly si può aggirare il controllo del nome. Git legge
la configurazione del repository: `core.fsmonitor` è spento, ma un repository ostile resta un rischio.

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
   in un dizionario. Da 35 a 260 µs secondo il comando, di cui si ricalcola il rischio; 1 ms dal terminale.
3. **Modello con un valore**: "processo firefox" → `Get-Process firefox`, vedi sotto.
4. **Frase simile** (parole in comune almeno al 75%): solo proposta.
5. **Modello locale**, con le scorciatoie più simili come esempi. Circa 1–1,5 s; una parola sola non lo disturba.
6. **Nessuna decisione**: la riga va a PowerShell, che dà il suo errore di sempre.

`? frase` chiede sempre al cervello, anche se la prima parola è un comando. Il comando proposto compare nella riga, si
può correggere, e una nota dice da dove viene:
```
Get-Service | Where-Object Status -eq Running  # nothing: modello 1540 ms, Invio per eseguire
```

Nel launcher: `> frase` mostra la decisione. Invio esegue quello che si vede, Ctrl+Invio il testo così com'è scritto; se
è critico, il terminale che si apre chiede di scrivere SI.

## Chi può far partire cosa
| Origine, nel terminale | Sola lettura | Modifica | Critico |
|---|---|---|---|
| Modello | Invio | Invio | scrivere SI |
| Frase simile | Invio | Invio | non si propone |
| Scorciatoia con meno di 3 riuscite, o appena fallita | Invio | Invio | scrivere SI |
| Scorciatoia con almeno 3 riuscite | **parte da sola** | Invio | scrivere SI |
| Comando scritto a mano | parte com'è | parte com'è | parte com'è |

Il modello non è affidabile e il modello di rischio lo presume: nelle prove, a "elenca i file" ha risposto una volta con
`Get-ChildItem … | Remove-Item -Force`. Per questo una sua proposta non parte mai da sola.

`CommandSafety` (Core, testato) lavora sul testo. La sola lettura è un elenco chiuso: tutto ciò che non riconosce conta
come modifica. Non è una difesa contro chi vuole ingannarla: decide solo cosa può partire senza Invio, e lì arrivano solo
comandi già confermati in passato. Ciò che il modello fa partire da solo passa dall'esecutore limitato.

## I comandi critici
`CriticalCommands` (Core, testato) è l'elenco di ciò che cancella dati, azzera una memoria, resetta il sistema o può
rendere inutilizzabile la macchina; si legge anche in Impostazioni › Sviluppo, con il motivo che compare nella conferma:

| Motivo | Per esempio |
|---|---|
| Cancella file, cartelle o altri dati | `Remove-*`, `rm`, `del`, `Clear-RecycleBin`, `robocopy /mir` |
| Cancella, formatta o ripartiziona un disco | `Format-Volume`, `diskpart`, `Clear-Disk`, `fsutil`, `chkdsk /f` |
| Reimposta o ripristina il sistema, o azzera una memoria | `systemreset`, `Reset-*`, `Restore-*`, `bcdedit`, `sfc`, `dism`, `Clear-Tpm`, `Clear-History` |
| Spegne, riavvia o chiude la sessione | `Stop-Computer`, `shutdown`, `logoff` |
| Chiude processi o cambia i servizi di Windows | `Stop-Process`, `taskkill`, `sc delete`, `net stop` |
| Cambia utenti, password o permessi | `icacls`, `net user`, `Set-ExecutionPolicy` |
| Abbassa le protezioni del sistema | `Disable-*`, `Set-MpPreference`, `netsh advfirewall` |
| Modifica il registro di sistema | `reg add`, `reg delete`, `regedit` |
| Disinstalla programmi o cancella ambienti | `Uninstall-*`, `winget uninstall`, `wsl --unregister`, `docker system prune` |
| Avvia codice che non si può controllare prima | `Invoke-Expression`, `Start-Process`, `curl … \| sh`, Base64 |
| Può far perdere lavoro su git | `git reset --hard`, `git push --force`, `git clean`, `git branch -D` |

Contano anche i metodi .NET (`$_.Delete()`, `[IO.File]::Delete`), le scritture travestite (`r''m`, un apice inverso dentro il nome, `cmd /c
"del x"`, un nome calcolato con `& (...)`), e qualsiasi modifica a cartelle di sistema, `HKLM:` o `.nothing-shell`. Le parole
corte contano solo dove comincia un comando: `Select-String 'del' a.txt` non è critico.

**Una scorciatoia non aggira nulla.** Un comando critico si può imparare ("svuota cestino"), ma il rischio si ricalcola a ogni
decisione, quindi non diventa mai fidato, nemmeno se è riuscito cento volte o se è entrato nell'elenco dopo essere stato
imparato. Non genera modelli con un valore ("cancella {0}"), non vale come frase simile e non finisce negli esempi del modello.

Nel terminale un comando scritto a mano parte com'è: l'ha scritto l'utente, e Invio è già la conferma.

*AGGIUNGI I TUOI* apre `%USERPROFILE%\.nothing-shell\brain\comandi-critici.txt`: una voce per riga, scritta come al prompt
(`kubectl delete`, `terraform *`), valida dal prossimo avvio. Si aggiungono a quelle di serie, che non si possono togliere.
Dal dettaglio di una scorciatoia imparata (sotto) ci finisce il suo comando esatto, con effetto immediato.

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

Non si impara ciò che contiene chiavi, token o password: il file è in chiaro. Si tengono al massimo 2000 scorciatoie,
togliendo le meno recenti.

`ricorda "frase" "comando"` ne insegna una a mano, `dimentica "frase"` la toglie; *Dimentica le scorciatoie* nelle
impostazioni azzera tutto.

Le scorciatoie di serie sono 79 comandi con circa 300 frasi, in italiano e in inglese: data e ora, nome e modello del PC, utenti, CPU, GPU,
RAM, dischi, risoluzione, USB, stampanti, aggiornamenti installati, programmi, avvio automatico, servizi, errori di Windows,
IP, MAC, DNS, gateway, Wi-Fi, porte, connessioni, firewall, antivirus, file e git. `--brain-check` le prova dall'esecutore
limitato e dalla cartella dell'utente, come nel notch: lì i comandi git risultano `NO` se quella cartella non è un
repository, e per lo stesso motivo le scorciatoie git servono nel terminale. Regole per aggiungerne:

- **Solo letture locali**: niente `ping` né IP pubblico, perché l'esecutore limitato non ha comandi di rete.
- **Frasi non ambigue**: "aggiornamenti windows" non c'è, perché può voler dire quelli da installare e il comando legge
  quelli installati. "wifi" legge lo stato della scheda, non le reti salvate.
- **Frasi che non somigliano a un'azione**: "servizi fermi" avrebbe la stessa chiave di "ferma i servizi", che partirebbe da
  sola leggendo invece di fermare. Lo stesso per "connessioni attive" e "attiva la connessione". C'è un test con i verbi comuni.
- **Sotto i 20 s dalla cartella dell'utente**: "dimensione cartella" con `-Recurse` lì ha impiegato 26 s, quindi "dimensione
  file" somma solo i file della cartella.

Frasi che differiscono per la vocale finale sono la stessa: "ultimo commit" è "ultimi commit", "cartelle" è "cartella". Un
test controlla che nessuna frase di serie ne nasconda un'altra.

Si imparano le decisioni, non i risultati: "ram libera" rilegge sempre il valore attuale.

### Scorciatoie imparate
Nelle impostazioni, con un clic su una riga si apre il dettaglio:

- **Descrizione**: `CommandExplainer` (Core, testato) la scrive con regole sui passaggi del comando, non col modello, che
  in questa pagina potrebbe rassicurare su un comando che non ha capito. Quello che non riconosce lo dice: "avvia `vmctl`,
  che non so descrivere".
- **Quando parte**: ricalcolato ogni volta, come le decisioni. Una scorciatoia non riconosciuta come sola lettura può comunque
  esserlo: `@{n=…}` basta a farla considerare una modifica.
- **Creata**: quelle imparate prima che si salvasse la data non ce l'hanno, e non si indovina.
- **AGGIUNGI AI COMANDI CRITICI**: scrive il comando esatto nell'elenco dell'utente e vale da subito; per un modello
  ("Get-Process {0}") vale `Get-Process *`. *TOGLI DALL'ELENCO* lo riporta com'era; quelli di serie non si possono togliere.
  Un comando critico si può ancora eseguire, ma non parte mai da solo e non finisce più negli esempi del modello.

## Il modello del terminale
**Qwen2.5 0.5B Instruct** (GGUF Q4_K_M, 469 MB, licenza Apache 2.0) con LLamaSharp su CPU, in
`%USERPROFILE%\.nothing-shell\brain\`. Si scarica solo premendo *Scarica il modello*; il download riprende da dove si è
fermato, si può annullare, e il file si controlla con l'hash SHA-256 pubblicato. Senza modello tutto il resto funziona.
Nulla esce dal PC: dopo il download non c'è rete. Qwen3.5 4B (2,6 GB, Apache 2.0) sta nella stessa cartella e si scarica allo
stesso modo, ma serve solo all'assistente nel notch: per proporre un comando al prompt conta rispondere in un secondo.

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
- `NothingShell.exe --assistant-check <file> <domanda>…`: l'assistente vero, senza interfaccia e con le sole scorciatoie
  di serie. Risponde no a ogni conferma e non impara nulla.
- `NothingShell.exe --brain-install`: scarica il modello senza interfaccia. `--assistant-install` scarica quello dell'assistente.
- `NothingShell.exe --brain-serve <file> <pipe> <secondi>`: solo il cervello, con scorciatoie e pipe proprie, accanto alla
  shell aperta; il terminale di prova la usa con `NOTHINGSHELL_BRAIN_PIPE`.

## Limiti
- Una frase che comincia con un comando esistente è quel comando ("sort i file" lancia `sort`): si usa `?`.
- Durante l'attesa del modello il terminale non accetta tasti, fino a 30 s al primo avvio.
- Nel pannello ogni comando parte in un PowerShell nuovo, dalla cartella dell'utente e senza profilo: `cd` non resta, e gli
  alias del profilo non ci sono.
- Una riga completamente riscritta dopo una proposta conta come correzione di quella frase.
- La scelta del comando non dipende dalla cartella: una scorciatoia vale ovunque.
