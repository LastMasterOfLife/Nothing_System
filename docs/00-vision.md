# 00 — Visione

**Nothing Shell** è uno strato grafico in stile Nothing OS che si appoggia sopra Windows 11.
Non sostituisce `explorer.exe`: lo affianca con finestre native (notch, widget, barra) e,
dove serve, nasconde gli elementi originali.

## Obiettivi
- Estetica Nothing: monocromo nero/bianco, accento rosso, griglia e tipografia a punti, animazioni a matrice.
- Notch interattivo (Dynamic Island) in alto al centro: orologio, musica, timer, notifiche.
- Widget a punti sul livello del desktop.
- Processo residente leggero: < 80 MB RAM, ~0% CPU a riposo.

## Non-obiettivi
- Sostituire la shell o il centro notifiche di sistema. (Lo Start viene affiancato dal launcher,
  non rimosso: resta raggiungibile disattivando l'opzione del tasto Win.)
- Modificare file di sistema o il registro oltre le preferenze dell'app.

## Milestone
| ID | Contenuto | Stato |
|----|-----------|-------|
| M0 | Design system + motore matrice a punti | fatto |
| M1 | Notch: orologio, media, espansione animata | fatto |
| M2 | Widget sul desktop (orologio, batteria, sistema, calendario) | fatto |
| M3 | Barra al posto della taskbar (docs/05-bar.md) | fatto |
| M4 | Launcher in stile Nothing (docs/06-launcher.md) | fatto |
| M5 | Impostazioni + plugin widget (docs/07-settings-plugins.md) | fatto |
| M6 | Notch: avvisi mail e pagine con swipe (docs/08-notch-mail-pages.md) | fatto |
| M7 | Accesso Nothing: animazioni di blocco e bentornato con card account (docs/09-access.md) | fatto |
