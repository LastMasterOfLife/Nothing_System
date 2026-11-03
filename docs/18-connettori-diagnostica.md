# 18 — Connettori di diagnostica (specifica)

Stato: **giri 1 e 2 fatti**. Giro 1: W3, W5, D2, D3, P1, P2, P4, P5, P6, P9 (modelli della libreria, `SqlGuard`). Giro 2: W1, W2, W6,
D1, D4, D6, P3 (pagina Diagnosi, driver PostgreSQL e MySQL; PostgreSQL provato su un server vero, MySQL no). Il resto è
proposta. Completa [14](14-agenti-connettori.md) (connettori) e
[17](17-dns-notch.md) (pagina Diagnosi). Obiettivo: quando un portale web, un database o un programma "non va", il notch dice
**dove** si rompe (rete, DNS, certificato, applicazione, dati, processo) senza aprire una console.

## Cosa c'è già
Ping, Sito o servizio web, Certificato SSL, Intestazioni di sicurezza, Porte esposte, Statuspage, Servizio di Windows,
App in esecuzione, File di log, Docker, SSH, Prometheus, Server FTP, Database (SQL Server completo; PostgreSQL e MySQL solo
porta), pagina DNS. Le proposte sotto **non li ripetono**: li estendono o riempiono i buchi.

## Principi
1. **Sola lettura.** Nessun connettore di diagnostica scrive, cancella o riavvia. Le azioni (Riavvia, Svuota) restano
   connettori a parte, con `confirm`.
2. **Due forme.** *Connettore* = controllo ripetuto con soglie e avvisi (come oggi). *Strumento nel notch* = si scrive
   qualcosa e si guarda la risposta una volta, come la pagina DNS. Per ogni voce sotto è indicata la forma.
3. **Segreti** solo in `NOTHING_SECRET` (Gestore credenziali), mai in argomenti, file o log.
4. **Limiti sempre**: timeout (15 s), numero di righe e byte letti, una query alla volta per database.
5. **Niente driver nuovi se si può evitare**: client scritti nel Core (come `DnsLookup`, `Mqtt`) o programmi già presenti
   (`psql`, `mysql`, `redis-cli`) se installati, con messaggio chiaro quando mancano.
6. **Dove compaiono**: pagine di sezione come Database/FTP (Core `ConnectorSections`). Nuove sezioni proposte: **Web**,
   **Programmi**; i connettori di database nuovi vanno in *Database*.

Priorità: **P1** alto valore e poco lavoro (usa quello che c'è), **P2** utile, **P3** quando serve.

---

## A. Portali web

| # | Nome | Forma | Cosa dice | Priorità |
|---|---|---|---|---|
| W1 | **Tempi di una richiesta** | strumento + connettore | DNS, connessione TCP, TLS, primo byte (TTFB), totale, dimensione, versione HTTP; soglie per fase. Dice *quale* fase è lenta | P1 |
| W2 | **Catena di redirect** | strumento | ogni salto (codice, indirizzo), URL finale, passaggi da https a http, cicli, troppi salti | P1 |
| W3 | **Contenuto atteso** | connettore | la pagina contiene (o non contiene) un testo/regex, un percorso JSON vale X, l'impronta del corpo è cambiata (pagina sostituita) | P1 |
| W4 | **Percorso di accesso (sintetico)** | connettore | POST di login con cookie, poi una pagina protetta con testo atteso; tempi di ogni passo. Credenziali nel Gestore | P2 |
| W5 | **Salute API** | connettore | `/health`, Spring `/actuator/health`, ASP.NET `/healthz`: stato complessivo e dei singoli componenti (db, cache, disco) come valori | P1 |
| W6 | **Propagazione DNS** | strumento (pagina DNS) | stessa domanda a più resolver (8.8.8.8, 1.1.1.1, 9.9.9.9 e i server autorevoli `NS`): risposte uguali o no, serial SOA diversi. Riusa `DnsLookup` | P1 |
| W7 | **Posta del dominio** | strumento + connettore | MX raggiungibili, SPF (conta le ricerche, max 10), DKIM (selector a scelta), DMARC (policy), MTA-STS. Riusa `DnsLookup` | P2 |
| W8 | **Scadenza del dominio** | connettore | giorni alla scadenza e registrar da RDAP (`rdap.org`, JSON, niente chiave) | P2 |
| W9 | **Certificato, catena completa** | strumento | estende "Certificato SSL": catena, SAN, algoritmo, scadenza di ogni anello, certificato intermedio mancante, versioni TLS accettate | P2 |
| W10 | **Prestazioni pagina** | connettore | PageSpeed Insights API (punteggio, LCP, CLS, TBT); limitato a 1 giro al giorno per quota | P3 |
| W11 | **Link e mappa del sito** | connettore | legge `sitemap.xml`, controlla a campione N pagine (codice ≠ 200, tempo); `robots.txt` raggiungibile | P3 |
| W12 | **Rete fino al server** | strumento | traceroute con latenza per salto, perdita su N pacchetti, MTU. Pagina notch con input come DNS | P2 |
| W13 | **Cache e CDN** | strumento | intestazioni `cache-control`, `age`, `x-cache`, `cf-cache-status`, WAF/CDN riconosciuto | P3 |

Campi comuni: `url`, `metodo`, `timeout`, `seguiRedirect`, `intestazioni` (come `http`), `secret` per token.
Letture di esempio per W1: `DNS ms`, `Connessione ms`, `TLS ms`, `TTFB ms`, `Totale ms`, `Dimensione`, `Codice`.

## B. Database e dati

| # | Nome | Forma | Cosa dice | Priorità |
|---|---|---|---|---|
| D1 | **PostgreSQL e MySQL veri** | connettore | oggi solo la porta: apre la connessione, versione, tempi, query di controllo (come SQL Server). Con `psql`/`mysql` se presenti, altrimenti protocollo nel Core | P1 |
| D2 | **Freschezza dei dati** | connettore | una query che restituisce una data (`MAX(aggiornato_il)`): età oltre soglia = avviso. Prende i job fermi e le sincronizzazioni rotte | P1 |
| D3 | **Conteggio con soglia** | connettore | una query `COUNT(*)` (ordini bloccati, errori nuovi, code) con `above`/`below`. Generalizza la query di controllo | P1 |
| D4 | **Connessioni e blocchi** | connettore | sessioni attive su massimo, sessioni in attesa, catena di blocco (chi blocca chi, da quanti secondi), transazioni aperte da più di N min. SQL Server `sys.dm_exec_requests`, PostgreSQL `pg_stat_activity` | P1 |
| D5 | **Query lente** | strumento | le N peggiori per durata o CPU (Query Store, `pg_stat_statements`, `performance_schema`), testo troncato | P2 |
| D6 | **Backup** | connettore | età dell'ultimo backup completo e del log (`msdb.dbo.backupset`, per PostgreSQL l'ultimo WAL/dump), esito | P1 |
| D7 | **Spazio e crescita** | connettore | dimensione dei dati e del log, spazio libero nei file, crescita al giorno (differenza da un giro all'altro) | P2 |
| D8 | **Replica e disponibilità** | connettore | ritardo di replica, stato Always On / streaming replication, repliche scollegate | P2 |
| D9 | **Lavori pianificati** | connettore | job di SQL Server Agent falliti nelle ultime 24 h, nome e ora dell'ultimo errore | P2 |
| D10 | **Deadlock ed errori** | connettore | deadlock e errori gravi (severità ≥ 17) dal log in N ore | P3 |
| D11 | **Redis** | connettore | `PING`, memoria usata/massima, chiavi scadute ed espulse, client, ruolo (replica). Protocollo RESP nel Core, come `Mqtt` | P2 |
| D12 | **MongoDB** | connettore | `serverStatus`: connessioni, opcounters, replica set, ritardo. Via `mongosh` | P3 |
| D13 | **Elasticsearch / OpenSearch** | connettore | `_cluster/health` (verde/giallo/rosso), shard non assegnati, spazio sui nodi. È http: basta un modello | P2 |
| D14 | **SQLite e file dati** | connettore | dimensione, ultima modifica, `PRAGMA integrity_check`, file `-wal` troppo grande | P3 |
| D15 | **Coda messaggi** | connettore | RabbitMQ (API di gestione: messaggi in coda, consumatori, non confermati), Kafka (ritardo del consumer via CLI) | P3 |

Regole per le query dei connettori di database (D1–D10):
- si accettano solo `SELECT`/`WITH` (un'istruzione sola, niente `;`, `INTO`, `EXEC`): controllo nel Core, **testato**;
- si consiglia un utente con permessi di sola lettura (`db_datareader`, `pg_monitor`) e la scheda lo scrive;
- risultato: una riga, prima colonna come valore (o più colonne come più letture), massimo 20 righe;
- timeout di comando 10 s, `ApplicationIntent=ReadOnly` dove esiste.

## C. Programmi e server

| # | Nome | Forma | Cosa dice | Priorità |
|---|---|---|---|---|
| P1 | **Registro eventi di Windows** | connettore | errori e crash di un'applicazione/sorgente nelle ultime N ore (`Application Error` 1000, `.NET Runtime` 1026, `Windows Error Reporting`): conteggio e ultimo messaggio | P1 |
| P2 | **Processo in dettaglio** | connettore | estende "App in esecuzione": CPU %, memoria, handle, thread, uptime, **riavvii** (PID cambiato dall'ultimo giro) | P1 |
| P3 | **Chi tiene la porta** | strumento | scrivi una porta: processo in ascolto, utente, connessioni stabilite, in attesa; porta in uso da un'altra app | P1 |
| P4 | **Cartella di coda** | connettore | numero di file e **età del più vecchio** in una cartella (spool, import, posta in uscita): coda ferma = avviso. Anche su cartelle di rete | P1 |
| P5 | **Attività pianificata** | connettore | stato, ultima esecuzione, ultimo esito (`LastTaskResult`), ritardo sull'orario atteso | P1 |
| P6 | **Log con frequenza** | connettore | estende "File di log": righe che corrispondono a un'espressione negli ultimi N minuti, righe/min, ultima riga corrispondente, log che non cresce più (programma fermo), rotazione | P1 |
| P7 | **IIS** | connettore | pool di applicazioni (avviato/fermato, riavvii), siti, richieste 5xx dai log in N min, `HTTPERR` | P2 |
| P8 | **Contatori di Windows** | connettore | qualunque contatore di `Get-Counter` (coda disco, pagine/s, connessioni TCP) con soglie | P2 |
| P9 | **File recente** | connettore | età del file più recente in una cartella (backup, esportazioni): "l'ultimo backup ha 3 giorni" | P1 |
| P10 | **Orario del PC** | connettore | scarto da un server NTP (`w32tm /stripchart` o SNTP nel Core); scarti oltre 1–2 min rompono Kerberos e certificati | P2 |
| P11 | **Posta: SMTP/IMAP** | strumento | banner, STARTTLS, certificato, accettazione di `AUTH` senza inviare nulla, tempi | P2 |
| P12 | **Directory: LDAP/AD** | connettore | connessione e bind, tempo, controller di dominio raggiungibili, ruolo FSMO | P3 |
| P13 | **Cartelle condivise e RDP** | connettore | raggiungibilità di `\\server\share` (lettura di un file di prova), porta 3389, tempo | P3 |
| P14 | **Certificati sul PC** | connettore | certificati in scadenza negli archivi `LocalMachine\My` e `CurrentUser\My` | P2 |
| P15 | **Java e .NET** | connettore | contatori via endpoint (`/actuator/metrics`, `dotnet-counters` se presente): heap, GC, thread, code | P3 |

---

## Come si inseriscono nel codice
- **Modelli della libreria** (`ConnectorCatalog`): quelli http (W3, W5, D13) e command (D*, P*) sono solo nuovi
  `ConnectorTemplate`, con test in `ConnectorCatalogTests` (ogni modello produce un connettore valido).
- **Core nuovo e testato**: `Network/` per W1/W2/W12/P10/P11 (client scritti a mano, risposte campione nei test, come
  `DnsLookupTests`), `Data/SqlGuard` per le regole sulle query, `Data/Resp` per Redis (D11).
- **Pagine con input** (W1, W2, W6, W12, P3, P11): stesso schema di `DnsPage` (campo scrivibile con `BeginAsk`,
  risultati cliccabili per copiare). Per non moltiplicare le pagine, un'unica pagina **Diagnosi** con una riga di
  comando: `http miosito.it`, `redirect miosito.it`, `dns miosito.it MX`, `porta 5432`, `traccia miosito.it`.
- **Sezioni**: `ConnectorSections` passa da due a quattro voci (Database, FTP, Web, Programmi); il mapping resta per
  `manifest.Template`, quindi un connettore scritto a mano senza modello resta in Dispositivi.
- **Permessi**: i connettori `command` chiedono il consenso come ora; gli strumenti nel notch non eseguono PowerShell
  quando basta il Core (nessun consenso).

## Ordine di lavoro consigliato
1. **Giro 1 (P1 senza codice nuovo, solo modelli):** W3, W5, D2, D3, P4, P5, P6, P9, P1 (registro eventi), P2.
2. **Giro 2 (P1 con Core nuovo):** W1 e W2 (pagina *Diagnosi*), W6, D1 con `SqlGuard`, D4, D6, P3.
3. **Giro 3 (P2):** W7, W8, W9, W12, D5, D7, D8, D9, D11, D13, P7, P8, P10, P11, P14.
4. **Dopo** (P3): solo se serve davvero.

## Cosa non si fa
- Niente scritture nei database né riavvii di servizi da questi connettori.
- Niente scansioni di porte o vulnerabilità di host che non sono dell'utente (le *Porte esposte* esistenti restano
  limitate all'indirizzo scritto dall'utente).
- Niente invio di messaggi reali (posta, MQTT) nei controlli.

## Da decidere
- PostgreSQL/MySQL: usare i programmi a riga di comando (zero dipendenze, ma devono essere installati) o aggiungere i
  driver (`Npgsql`, `MySqlConnector`) all'App (funziona sempre, più peso)? Proposta: **driver**, perché i controlli
  devono andare anche su PC senza `psql`.
- Una pagina **Diagnosi** sola con riga di comando, o una pagina per strumento? Proposta: una sola.
