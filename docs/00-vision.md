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
| M8 | Barra: app appuntate di Windows, recenti col clic destro, anteprime dal vivo, una barra per monitor (docs/05-bar.md) | fatto |
| M9 | Posta nel notch dalla casella collegata (IMAP) e impostazioni guidate (docs/08-notch-mail-pages.md) | fatto |
| M10 | Lettore di posta: clic su una mail del notch, rispondi, webmail, segna come letta, codici (docs/08-notch-mail-pages.md) | fatto |
| M11 | Attività dal vivo: volume, luminosità, microfono e webcam, download, screenshot, cuffie (docs/10-live-activities.md) | fatto |
| M12 | Agenda nel notch, riepilogo del mattino, pannello rapido, screenshot di Greenshot (docs/11-agenda-pannello-rapido.md) | fatto |

Versioni: 0.5 = M0–M5, 0.6 = M6–M7, 0.7 = M8–M9, 0.8 = M10, 0.9 = M11, 0.10 = M12.
