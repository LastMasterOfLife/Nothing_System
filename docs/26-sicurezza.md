# 26 — Modalità Sicurezza (specifica)

Stato: **motore fatto e testato** (Core + servizio App + due comandi CLI, 18 test). **Da fare**: pagina del notch con lo
stato grafico dal vivo e la pagina Impostazioni › SICUREZZA (vedi *Da fare* in fondo). Completa la categoria **Sicurezza**
dei connettori (docs/14, docs/18): quelli sono controlli ripetuti e di sola lettura; questa è una **sessione** autorizzata
che prova attivamente un sito e ne scrive un resoconto da sistemare.

## Obiettivo
Provare in modo autorizzato la sicurezza di **un sito proprio** (o per cui si ha il permesso scritto): mandare le richieste
che servono per trovare le debolezze — intestazioni deboli, TLS vecchio, cookie senza protezioni, vulnerabilità note — e
alla fine dare un **resoconto azione-per-azione** con dove sta il problema e **come si risolve**. Il notch mostra l'attività
dal vivo e cambia aspetto mentre la sessione è in corso; alla fine avvisa, spegne la modalità e lascia il report.

## Il cancello di autorizzazione (regola assoluta)
Una sessione parte **solo** contro un host che l'utente dichiara suo o autorizzato, e **ogni** richiesta resta dentro
quell'host. Così lo stesso strumento non si può puntare, per sbaglio o di proposito, su un sito di terzi. È la versione
«autorizzata ed esplicita» della regola già scritta in docs/18 (*«Niente scansioni di vulnerabilità di host che non sono
dell'utente»*).

- Il bersaglio si scrive una volta; la conferma («questo host è mio / sono autorizzato») è un passo a sé.
- `ScopeGuard` (Core, testato) decide cosa è «dentro»: stesso host; i sottodomini solo se attivati. Schemi diversi da
  http/https sono sempre fuori. Serve a tenere in riga anche un redirect o un link che porterebbe lo scanner altrove.
- Da riga di comando il cancello resta esplicito: senza `--confirm` non parte nulla (e non si manda **nessuna** richiesta al
  bersaglio).

## Cosa NON fa
- Non è uno strumento «punta un sito qualsiasi ed entra». Niente bersagli non autorizzati, nessun uso senza il cancello.
- **Non distruttiva**: niente DoS, niente brute-force di massa, niente fuzzing aggressivo. nuclei gira con
  `-exclude-tags dos,intrusive,fuzz` e un limite di ritmo; ZAP in modalità *baseline* (passiva + attiva sicura).
- **Non esfiltra**: le debolezze dimostrano che il buco **esiste** (es. «qui c'è una SQLi», «manca HSTS»), non tirano fuori
  il database, le password o i dati personali. Il report non contiene bottino.
- Limiti sempre: tempo massimo per sessione e per singolo strumento, letture limitate.

## Le fasi
Il piano dipende da cosa è installato; la **ricognizione passiva** c'è sempre.

| Fase | Strumento | Cosa fa | Serve installare? |
|---|---|---|---|
| Ricognizione | *interna* (`ReconChecks`, Core) | Intestazioni HTTP, versione TLS, cookie, software esposto — **senza payload** | No |
| nuclei | `nuclei` | Template di vulnerabilità note (CVE, misconfig), in sola rilevazione | Sì |
| OWASP ZAP | `zap.bat`/`zap.sh` | Scansione *baseline* dell'app web, report JSON | Sì |
| Nikto | `nikto` | File e configurazioni rischiose note del server web (uscita grezza salvata) | Sì |
| testssl.sh | `testssl.sh` | Protocolli e cifrari TLS deboli (uscita grezza salvata) | Sì |

Scelta di partenza: **ricognizione + nuclei + ZAP** se presenti; il resto si aggiunge dalle impostazioni. Gli strumenti non
installati vengono **saltati** (scritto nel report), non fanno fallire la sessione.

### Ricognizione passiva (sempre)
Guarda **solo** la risposta a una normale richiesta e ne ricava le debolezze, mappate a CWE:
HTTP in chiaro (CWE-319), TLS obsoleto (CWE-326), manca HSTS / CSP / X-Frame-Options / nosniff / Referrer-Policy,
cookie senza HttpOnly/Secure/SameSite (CWE-1004/614/352), versione del software nel banner (CWE-200), CORS permissivo con
credenziali (CWE-942). È la fase che dà un risultato anche senza nessuno scanner installato.

### Strumenti esterni
L'App li avvia come processi con flag prudenti e **ne trasmette l'uscita dal vivo** al notch; `nuclei` (JSONL) e `ZAP`
(report JSON) vengono interpretati in `Finding` dai parser del Core (testati con campioni). Le debolezze fuori scope
vengono scartate. Nikto/testssl, per ora senza parser dedicato, salvano l'uscita grezza accanto al report, citata
nell'azione.

## Il report
Markdown in `%USERPROFILE%\.nothing-shell\security\<host>-<data>.md` (puro, testato):
bersaglio ed esito, **riepilogo per gravità**, **strumenti** usati, **azione per azione** (ogni fase con tempo, esito e
quante debolezze), e ogni **debolezza** con *dove*, *prova*, *come si risolve*, *riferimento* (CWE/OWASP/template) e *fonte*.
In fondo la nota che la scansione è stata autorizzata e non distruttiva.

## Comandi (per provare senza aprire la shell)
```
NothingShell.exe --security-tools <report.txt>
NothingShell.exe --security-scan <url> <report.md> --confirm
```
- `--security-tools` elenca quali scanner sono installati.
- `--security-scan` esegue una sessione e scrive il resoconto. Senza `--confirm` rifiuta (e non tocca il bersaglio).
  Codici d'uscita: 0 ok, 1 errore della sessione, 2 URL non valido, 3 autorizzazione mancante.

## Come si inserisce nel codice
- **Core nuovo e testato** (`src/NothingShell.Core/Security/`): `ScopeGuard`+`SecurityTarget` (cancello), `SecuritySession`
  (macchina a stati deterministica, registro azioni, debolezze), `ReconChecks` (recon passivo), `NucleiParser`/`ZapParser`
  (uscita → `Finding`), `SecurityReport` (Markdown), `ScanPlan`/`ScanModels`. Test in `tests/.../SecurityTests.cs`.
- **App**: `Services/SecurityScanService.cs` (rilevazione strumenti, avvio prudente e in scope, istantanea dal vivo,
  scrittura report), `Services/SecurityCli.cs` (i due comandi). Riusa `Network/HttpProbe` del Core per il recon.
- **Principi condivisi col resto**: il Core non conosce Windows; i servizi danno **istantanee immutabili** (`record`) via
  callback; fallimenti morbidi (bersaglio irraggiungibile → la sessione fallisce con un motivo, non un'eccezione visibile).

## Da fare (prossimo giro: la parte visibile)
1. **Pagina del notch** `Sicurezza` (come `DiagnosiPage`/`DevicesPage`): campo per l'indirizzo, pulsante di conferma
   dell'autorizzazione, poi l'attività dal vivo (fase in corso, avanzamento, conteggio per gravità) e, alla fine, l'elenco
   delle debolezze con un clic per aprire il report. Aggiungere `NotchPage.Security`, `NotchLayout.IsTall`, registrazione in
   `NotchWindow` e in `ShellController`.
2. **Stato grafico dal vivo**: mentre la sessione è in corso la pillola del notch cambia aspetto (colore legato alla
  gravità più alta trovata, `SecuritySnapshot.Peak`) e a fine sessione una **scheda** *Fatto* con il riassunto e *Apri
  report*, poi la modalità si spegne da sola.
3. **Impostazioni › SICUREZZA**: attiva/disattiva la pagina, scelta degli strumenti, sottodomini sì/no, tempo massimo,
  cartella dei report, link per installare gli scanner mancanti.
4. **Connettore di comodo** (categoria Sicurezza): «Ricognizione sito» che rifà il recon passivo come controllo ripetuto con
  avviso quando compare una debolezza nuova (riusa `ReconChecks`).
