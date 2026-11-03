# 17 — Pagina Diagnosi del notch (DNS e non solo)

Una pagina del notch (**Diagnosi**, fra Dispositivi e Appunti) che fa quello che fa [Google Dig](https://toolbox.googleapps.com/apps/dig/):
si scrive un indirizzo nel campo e, con **Invio**, compaiono i record DNS. Non è un connettore: l'indirizzo si scrive
lì, al momento, senza toccare le impostazioni, e non serve nessun permesso.

## Come si usa
Clic sul campo (il notch lo tiene aperto finché si scrive), poi:

| Scrivi | Succede |
|---|---|
| `miosito.it` | A, AAAA, CNAME, MX, NS, TXT, SOA, CAA e DS |
| `https://utente@miosito.it:8443/pagina?x=1` | come sopra: dall'URL si prende solo il nome (`miosito.it`) |
| `miosito.it MX` | solo quel tipo (anche più: `miosito.it MX TXT`); `tutti` per i principali |
| `miosito.it TXT @8.8.8.8` | interroga quel server DNS invece di quello del PC |
| `8.8.4.4` | nome inverso (PTR) |

### Altri comandi
Con una parola davanti la stessa riga fa altro (Core `Diagnosi`, testato; specifica in [18](18-connettori-diagnostica.md)):

| Scrivi | Succede |
|---|---|
| `http miosito.it` | una richiesta GET misurata fase per fase: **DNS, connessione, TLS** (versione), **primo byte**, totale e dimensione; dice quale fase è la più lenta; mostra `server`, `content-type`, `cache-control`… (HTTP/1.1, `https` di serie, nessun cookie) |
| `redirect miosito.it` | ogni salto (codice, indirizzo, tempo) fino alla pagina finale; segnala cicli, più di 10 salti e il passaggio da https a http |
| `propaga miosito.it [TIPO]` | la stessa domanda a questo PC, Google, Cloudflare, Quad9 e ai **server autorevoli** del dominio (gli NS): dice se i valori sono uguali ovunque e se i numeri di serie SOA coincidono |
| `porta 5432` | chi ha la porta sul PC: processo in ascolto (con PID) e connessioni aperte, dalla tabella TCP di Windows senza PowerShell |

Tipi: A, AAAA, CNAME, MX, NS, TXT, SOA, PTR, SRV, CAA, DS, DNSKEY. In alto a destra nome cercato, tempo e server che
ha risposto. Per ogni record il valore e il TTL; un clic sul record lo **copia**. Un tipo senza record dice «nessuno»,
un nome che non esiste lo dice in rosso (NXDOMAIN) come gli errori del server. **Esc** esce dal campo.

## Come funziona
Core `DnsLookup` è un client DNS minimo (RFC 1035): una domanda per tipo su UDP, di nuovo su TCP se la risposta è
troncata, con i puntatori di compressione dei nomi. Senza PowerShell. Di serie usa i server DNS del PC nell'ordine in
cui sono configurati: la prima domanda li prova uno alla volta (2 s ciascuno; quello di una VPN spesso non risponde ai
nomi pubblici), quello che risponde serve alle altre domande e alla ricerca dopo. Core `DnsRequest.Parse` legge il testo
del campo (testato, come `DnsLookup`).

La pagina (prima *DNS*) è alta come Posta e Dispositivi e c'è sempre: non ha un interruttore nelle impostazioni.
