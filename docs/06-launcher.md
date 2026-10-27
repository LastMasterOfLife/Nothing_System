# 06 — Launcher Nothing (M4)

Pannello per cercare e avviare le app, al posto dello Start di Windows.
Si apre dal pulsante ⁘ della barra e dal **tasto Win** premuto da solo.

## Aspetto
Pannello nero 560 × 660 DIP, raggio 28, bordo `Outline`, centrato in orizzontale e appoggiato
sopra la barra. Comparsa: dissolvenza + salita di 12 DIP in 150 ms.
```
┌──────────────────────────────────────┐
│ ⌕  Cerca app                         │
│                                      │
│ RECENTI                              │
│ [ic] [ic] [ic] [ic] [ic] [ic]        │  ← le 6 app più usate di recente
│ TUTTE LE APP                         │
│ A                                    │  ← lettera a punti, rossa
│ [ic] Audacity                        │
│ ...                                  │
│ andrea                 🔒  ☾  ↻  ⏻   │  ← blocca, sospendi, riavvia, spegni
└──────────────────────────────────────┘
```
- Icone con i loro colori originali, come nella barra; finché un'icona non è caricata si mostra l'iniziale a punti.
- Con una ricerca in corso: al massimo 12 risultati, il primo selezionato.

## Tastiera
| Tasto | Azione |
|---|---|
| lettere | scrivono nella ricerca (il campo ha il focus all'apertura) |
| ↑ ↓ | spostano la selezione |
| Invio | avvia l'app selezionata |
| Esc, clic fuori, Win | chiude |

## Catalogo app
Cartella shell `shell:AppsFolder`: contiene tutte le app dello Start, desktop e UWP, con nome e
"parsing name" (percorso o AppUserModelId). Avvio uniforme con
`explorer.exe shell:AppsFolder\<parsing name>`: l'app nasce da Explorer, non da Nothing Shell.
Il catalogo si carica su un thread STA separato all'avvio e si ricarica a ogni apertura (nuove installazioni).
Esclusi gli elementi "rumore": disinstallazioni, leggimi, guide, licenze, siti web, documenti
(`.url .txt .chm .pdf .htm .html`).

## Ricerca (Core, testata)
Normalizzazione: minuscole, accenti rimossi, spazi ai bordi tolti. Punteggio (più alto = meglio):
| Corrispondenza | Punti |
|---|---|
| il nome inizia con la ricerca | 1000 − lunghezza del nome |
| una parola inizia con la ricerca | 800 |
| le iniziali delle parole iniziano con la ricerca (≥ 2 lettere, es. `vsc` → Visual Studio Code) | 700 |
| il nome contiene la ricerca | 500 |
| le lettere compaiono in ordine (sottosequenza) partendo dall'inizio di una parola, saltando al massimo 3 lettere per lettera cercata | 300 − lettere saltate |
Più un bonus d'uso: 5 punti per avvio, massimo 20 avvii. A parità: ordine alfabetico.

## Uso e preferenze
`%USERPROFILE%\.nothing-shell\launcher.json`: numero di avvii e ultimo avvio per app
(alimentano "Recenti" e il bonus), e la preferenza `useWinKey`.

## Tasto Win
Hook di tastiera di basso livello su un thread dedicato (Windows rimuove gli hook lenti: il thread
UI non deve mai bloccarlo). Regola (Core, testata):
- Win premuto e rilasciato **senza altri tasti in mezzo** → il rilascio viene bloccato, si inviano
  un tasto "maschera" non assegnato (`0xE8`) e poi il rilascio di Win: Windows vede una combinazione
  e non apre il suo Start. Si apre il launcher.
- Qualsiasi combinazione (Win+E, Win+D, Win+Shift+S…) passa intatta.
- Gli eventi iniettati (anche i nostri) sono ignorati.
Si disattiva dal menu della barra: *Usa il tasto Win per il launcher*.

## Alimentazione
Blocca (`LockWorkStation`) subito; Sospendi, Riavvia, Spegni chiedono **conferma col secondo clic**
entro 3 s (il pulsante diventa rosso). Riavvia/Spegni: `shutdown /r|/s /t 0`.

## Criteri di accettazione
- Win da solo apre/chiude il launcher; Win+E apre ancora Esplora file.
- `vsc` trova Visual Studio Code; Invio lo avvia.
- Le app avviate di recente compaiono in "Recenti".
- Apertura < 150 ms dopo la prima; nessun consumo di CPU a launcher chiuso.

## Fuori portata
Ricerca di file e impostazioni, calcolatrice, app appuntate, ordinamento personalizzato.
