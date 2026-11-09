# 23 — Cose da fare, con un aiuto (specifica)

Stato: **proposta**, niente codice. Usa il campo del notch ([16](16-assistente-notch.md)), l'assistente e i comandi
critici ([15](15-comandi-cervello.md)), le ore per progetto ([19](19-ore-di-lavoro.md)), la pagina Agenti
([14](14-agenti-connettori.md)) e la mascotte ([21](21-mascotte.md)).

Obiettivo: nel notch c'è la lista di cose da fare della giornata. Mentre lavori, la shell guarda quanto tempo passi sulla cosa in
corso; se ci stai mettendo troppo e nella lista ci sono cose che potrebbe fare da sola, **te lo chiede e sei tu a scegliere quale
lasciarle**. La fa in autonomia, senza toccare il tuo lavoro, e ti avvisa quando ha finito, con il risultato da guardare.

**Tutto resta in casa.** Il lavoro lo fa un modello che gira sulla **Spark** (NVIDIA DGX Spark nella rete locale), guidato da
un esecutore della shell sul PC. Niente servizi in cloud: né la lista, né il codice, né i comandi escono dalla rete di casa.

## Principi
1. **Decidi sempre tu.** Non prende mai una cosa da sola: propone, e parte solo dopo un clic su quella cosa. Puoi anche
   delegare quando vuoi, senza aspettare la proposta.
2. **Non tocca il tuo lavoro.** Lavora in una copia a parte (un worktree git su un ramo suo). Non fa push, non fa merge, non
   scrive fuori da quella cartella. Il risultato arriva *da rivedere*: lo accetti tu.
3. **I comandi critici restano critici.** Ogni comando passa da `CommandSafety` e `CriticalCommands` prima di partire, perché
   l'esecutore è nostro: un comando critico non parte, la cosa si ferma e te lo chiede.
4. **Solo in locale.** Il modello si chiama solo all'indirizzo della Spark, in rete locale. Se la Spark non risponde non si
   delega e lo si dice: **non c'è mai un ripiego su un servizio in cloud**.
5. **Limiti sempre**: tempo massimo, passi massimi, una sola cosa delegata alla volta (la Spark serve un lavoro per volta).
6. **Non disturba.** Al massimo una proposta ogni 30 minuti, mai a schermo intero, in riunione (evento in agenda) o con
   *Non disturbare* di Windows.

## La lista nel notch
Nuova pagina del notch **Da fare** (fra Lavoro e Server), più una riga nella Home con la cosa in corso.

```
DA FARE                                    3 di 12 · oggi
● Sistemare il login del portale           in corso · 1 h 10 (stimata 40 min)
○ Aggiornare le dipendenze di sinapsi_web  ✦ può farla lei
○ Scrivere i test di WorkCalendar          ✦ può farla lei
○ Chiamare Marco per il preventivo
◐ Rinominare i file delle foto             la Spark ci sta lavorando · 4 min
✓ Rispondere alla mail di Anna
```

- **Aggiungere**: dal campo del notch, "da fare: aggiorna le dipendenze di sinapsi_web" o "aggiungi alla lista …"; più
  righe incollate diventano più cose. Nella pagina, *+* in fondo alla lista.
- **Stati**: Da fare ○ · In corso ● · Fatta ✓ · Delegata e in lavorazione ◐ · Da rivedere ◆ · Ferma ✕ (con il motivo).
- **In corso** è una sola: clic su una cosa e *Inizio questa*, oppure lo deduce la shell dal progetto in primo piano (sotto).
- **Riordinare** trascinando; *Fatta* con un clic sul pallino; il tasto destro per Modifica, Elimina, Stima, Progetto, Delega.
- **Stima** facoltativa ("40 min"), scritta anche nella frase: "da fare: test di WorkCalendar, 30 min".
- **Progetto**: si riconosce dalle parole chiave dei progetti delle Ore ("sinapsi_web" nella frase) o si sceglie; serve a sapere
  in quale cartella lavorare e a contare il tempo.
- Le cose fatte restano visibili fino a mezzanotte, poi vanno nello storico; quelle non fatte passano al giorno dopo.

## Cosa può fare da sola
Ogni cosa riceve un giudizio, ricalcolato quando cambia il testo o il progetto: **può farla lei** (✦), **forse** o **no**.

| Può farla | Esempi | Perché |
|---|---|---|
| Sì | aggiornare dipendenze, scrivere test, correggere un errore di build, rinominare o riordinare file in una cartella, scrivere una bozza (README, mail da rivedere), cercare e riassumere nei file | lavoro in una cartella, verificabile, si può buttare |
| Forse | "sistemare il login" (vago), cose su un progetto senza cartella | serve precisare: la proposta chiede prima una riga in più |
| No | telefonate, riunioni, pagamenti, inviare mail, pubblicare, tutto ciò che va su un server o tocca soldi e persone; cose che hanno bisogno di internet | fuori dal PC, non si torna indietro, o l'esecutore non ha rete |

Il giudizio lo dà il modello della Spark (se non risponde, il Qwen3.5 4B locale dell'assistente) con regole fisse sopra: le
parole "chiama, paga, invia, pubblica, deploy, firma" sono sempre **no**, una cosa senza progetto con cartella non è mai **sì**.
Si può sempre cambiare a mano (tasto destro › *Può farla lei* / *La faccio io*), e la scelta resta.

## Quando ci stai mettendo troppo
La cosa **in corso** ha un tempo: quello vero, contato come le Ore (`WorkClock`, solo con mouse o tastiera attivi), dal momento
in cui è iniziata. Se non l'hai iniziata a mano, la shell la deduce quando il progetto in primo piano è quello di una sola cosa
della lista, e lo mostra ("in corso, dedotto") così puoi correggerla.

"Troppo" vuol dire, la prima che arriva:
- con una stima: tempo passato oltre **1,5 volte** la stima (e almeno 15 minuti oltre);
- senza stima: oltre **45 minuti** (si cambia nelle impostazioni).

Serve anche che nella lista ci sia almeno una cosa **può farla lei** e che la Spark risponda e sia libera. Allora il notch si
apre da solo su una scheda (la mascotte, se accesa, guarda verso la scheda):

```
Sei su "Sistemare il login" da 1 h 10 (stimata 40 min).
Intanto la Spark può fare una di queste:
  ✦ Aggiornare le dipendenze di sinapsi_web    ~10 min
  ✦ Scrivere i test di WorkCalendar            ~20 min
Lavora in una copia a parte, tutto resta in casa. Ti avviso quando ha finito.
[ Falla tu: dipendenze ]  [ Falla tu: test ]  [ Non ora ]  [ Non chiedere oggi ]
```

- Al massimo 3 proposte, prima quelle dello stesso progetto (stessa testa) e poi le più brevi.
- Le durate sono stime (dal tipo di lavoro e dalle volte precedenti), dette come tali.
- *Non ora* rimanda di 30 minuti; *Non chiedere oggi* spegne le proposte fino a domani.
- La scheda si chiude da sola dopo 20 s senza clic e resta come pallino nella pagina Da fare.
- Se la Spark è spenta o occupata, niente scheda: nella pagina compare "la Spark non risponde" accanto alle ✦.

## Come la fa
Due parti: **il modello sulla Spark**, che decide il prossimo passo, e **l'esecutore sul PC** (`Services/TodoRunner`), che fa il
passo nella copia del progetto e rimanda il risultato. Il codice resta sul PC; alla Spark arrivano solo le parti di file e i
risultati che l'esecutore le manda, in rete locale.

### La Spark
- Un server compatibile con l'API OpenAI (`/v1/chat/completions` con strumenti): vLLM, llama.cpp `llama-server` oppure
  Ollama, già installato sulla Spark. La shell non installa niente lì: chiede solo l'indirizzo.
- Il modello è quello scelto sulla Spark: uno da codice che stia nei suoi 128 GB e sappia chiamare strumenti (per esempio
  Qwen3-Coder 30B-A3B o gpt-oss-120b). Quale sia il migliore lo dirà la prova sotto, non questa spec.
- Indirizzo e modello in Impostazioni (sotto). Un'eventuale chiave del server va nel Gestore credenziali, come per i connettori.
- **Solo indirizzi locali**: si accettano solo IP privati (10.x, 172.16–31.x, 192.168.x), `localhost` o nomi `.local`/`.lan`
  che si risolvono in IP privati, ricontrollati a ogni collegamento. Un indirizzo pubblico si rifiuta con un messaggio chiaro.
- Si controlla ogni 60 s, solo con la funzione accesa: *pronta*, *occupata* (un lavoro in corso, da noi o da altri) o *non risponde*.

### L'esecutore
1. **Copia a parte**: `git worktree add` in `%USERPROFILE%\.nothing-shell\todo\<id>` su un ramo `nothing/todo-<id>`, dalla
   testa attuale del progetto. Un progetto senza git: copia della cartella (solo se sotto 500 MB), altrimenti la cosa è **forse**.
2. **Istruzioni** al modello: la descrizione della cosa, il progetto, l'elenco dei file principali, cosa non fare (push, merge,
   uscire dalla cartella, installare cose globali) e la richiesta di finire con un riassunto: cosa ha fatto, file cambiati,
   cosa controllare.
3. **Strumenti** che il modello può chiedere, tutti eseguiti dalla shell e limitati alla cartella della copia:

   | Strumento | Cosa fa | Limiti |
   |---|---|---|
   | `elenca` | file e cartelle | al massimo 500 voci |
   | `leggi` | un file, o un pezzo | 2.000 righe per volta |
   | `cerca` | testo o regex nei file | 200 risultati |
   | `scrivi` | crea o sostituisce un file | solo dentro la copia, niente percorsi fuori (`..`, collegamenti) |
   | `modifica` | sostituisce un pezzo esatto di un file | come sopra |
   | `esegui` | un comando PowerShell nella copia | passa da `CommandSafety`; 10 minuti; output tagliato a 20.000 caratteri |
   | `fatto` | chiude con il riassunto | |

4. **Comandi**: ogni `esegui` passa dallo stesso controllo dell'assistente. Sola lettura e modifiche dentro la copia (build,
   test, `git add`/`commit` sul suo ramo, gestori di pacchetti locali come `dotnet restore` o `npm install`) partono; un comando
   critico non parte: la cosa diventa **Ferma** e il notch chiede "Vuole eseguire `…`: lo permetto?" (due clic, come oggi).
   `git push`, `git merge` e i comandi fuori dalla copia non partono mai, nemmeno con il permesso.
5. **Rete**: i comandi non hanno rete verso l'esterno, tranne i gestori di pacchetti per scaricare le dipendenze (da decidere,
   sotto). Il modello non ha uno strumento per navigare.
6. **Limiti**: 30 minuti, 80 passi e 400.000 token di contesto in tutto, di serie (si cambiano); superati, si ferma e tiene il
   lavoro fatto. Lo stesso passo ripetuto 3 volte di fila (un ciclo) la ferma.
7. **Mentre lavora**: nella pagina Da fare lo stato ◐ col tempo e l'ultimo passo ("esegue dotnet test"); nella pagina Agenti
   compare come un agente, con passi e token al posto del costo. *Ferma* in ogni momento.

Tutto quello che succede (passi, comandi, esiti) si scrive in `todo\<id>\diario.jsonl`, così si può rileggere cosa ha fatto.

## Quando ha finito
Una scheda nel notch, come quella di fine build (e la mascotte che festeggia, se accesa):

```
✓ La Spark ha finito "Aggiornare le dipendenze di sinapsi_web"   12 min · 23 passi
Aggiornati 7 pacchetti, build e test passano. Da controllare: Newtonsoft 13 → 14.
4 file cambiati sul ramo nothing/todo-a1b2
[ Guarda le modifiche ]  [ Accetta ]  [ Scarta ]
```

- **Guarda le modifiche** apre il diff nell'editor (lo stesso che apre gli errori, docs/12), il riassunto completo o il diario.
- **Accetta** porta il ramo nel tuo (merge senza fast-forward, mai push) solo se il tuo albero è pulito; altrimenti dice di
  salvare prima e lascia il ramo dov'è. La cosa diventa **Fatta**.
- **Scarta** cancella worktree e ramo (con conferma: è una cancellazione).
- Se è fallita: ✕ **Ferma**, col motivo e l'ultimo passo; *Riprova*, *Guarda*, *La faccio io*. Se la Spark si spegne a metà,
  la cosa si ferma con "la Spark non risponde" e *Riprendi* riparte dal diario.
- Il riassunto lo scrive il modello, quindi non vale come prova: la scheda dice anche cosa ha verificato l'esecutore (build e
  test eseguiti e il loro esito), come le risposte *Non verificate* dell'assistente.
- Se in quel momento sei in riunione o a schermo intero, la scheda aspetta e resta il pallino ◆ nella pagina.

## Impostazioni
Impostazioni › Sviluppo › **Cose da fare** (`LiveActivity.Todo`, spenta di serie):
- **Spark**: indirizzo (`http://192.168.1.40:8000`), modello (elenco letto da `/v1/models`), *Prova collegamento* con stato e
  tempo di risposta.
- Proposte quando ci metto troppo: sì/no; soglia senza stima (30, 45, 60, 90 min).
- Limiti per cosa: tempo (15, 30, 60 min) e passi (40, 80, 150).
- Cartella delle copie, con *Pulisci copie vecchie* (quelle scartate o accettate da oltre 7 giorni).

## Dove si salva
- `%USERPROFILE%\.nothing-shell\todo.json`: cose, stato, stima, progetto, tempi, deleghe (id, ramo, passi, esito, riassunto).
  In chiaro: una cosa che contiene una password o una chiave non si delega.
- I worktree e i diari sotto `.nothing-shell\todo\`. Sulla Spark non resta niente, salvo i log del suo server.

## Nel codice (proposta)
- **Core** (testato): `Todo/TodoList` (cose, stati e passaggi permessi, riporto a domani), `Todo/TodoPhrase` (frase → testo,
  stima, progetto), `Todo/Delegability` (le regole fisse sopra il giudizio del modello), `Todo/OvertimeWatch` (tempo in corso,
  soglie, pause fra le proposte, scelta delle 3 proposte), `Todo/AgentLoop` (turni, strumenti, limiti, cicli, esito),
  `Todo/WorkspaceGuard` (percorsi dentro la copia, comandi mai permessi), `Todo/LocalEndpoint` (solo indirizzi privati).
- **App**: `Notch/TodoPage`, schede di proposta e di fine nel notch; `Services/SparkClient` (API compatibile OpenAI, stato);
  `Services/TodoRunner` (worktree, strumenti, diario, pulizia); una sezione nelle impostazioni.
- **Diagnostica**: `--todo-check <cartella>`: delega una cosa di prova su un repository finto (un test da far passare, un
  comando critico che deve fermarsi, un percorso fuori dalla copia che deve essere rifiutato) e scrive `report.txt` con passi,
  tempi e token. Serve anche a confrontare i modelli della Spark.

## Ordine di lavoro consigliato
1. Lista nel notch, frasi dal campo, stati, riporto a domani (utile già da sola).
2. Collegamento alla Spark nelle impostazioni (indirizzo, modelli, prova) e `--todo-check` con l'esecutore: si vede su cose di
   prova cosa sa fare il modello prima di dargli le tue.
3. Delega vera in worktree, scheda di fine, Accetta/Scarta.
4. Tempo della cosa in corso e proposte quando ci metti troppo.
5. Giudizio "può farla lei" e stime di durata dallo storico.

## Da decidere
- **Cosa gira sulla Spark**: quale server (vLLM, llama.cpp, Ollama) e quale modello. Lo diranno le prove con `--todo-check`.
- **Rete per le dipendenze**: "aggiornare le dipendenze" ha bisogno di NuGet o npm. Permetterla ai soli gestori di pacchetti, o
  tenere l'esecutore del tutto senza rete (e quella cosa diventa **no**)?
- **Cose senza cartella** (una bozza di mail, un riassunto): si fanno in una cartella vuota e il risultato è un file da aprire?
- **Accetta**: merge nel ramo corrente o lasciare sempre il ramo e basta (più prudente)?
- **Limiti di serie**: 30 minuti e 80 passi vanno bene per la velocità della Spark?
- **Più PC**: se la Spark la usano anche altri, aspettare che sia libera o mettersi in coda?
