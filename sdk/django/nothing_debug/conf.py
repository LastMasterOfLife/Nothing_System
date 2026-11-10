"""Impostazioni di nothing_debug lette da settings.NOTHING_DEBUG (tutte facoltative)."""

from __future__ import annotations

from pathlib import Path

DEFAULTS = {
    # Acceso di serie solo con DEBUG = True: in produzione non c'è niente da vedere né da collegare.
    "ENABLED": None,
    # Nome del portale nella shell (di serie la cartella del progetto).
    "NAME": None,
    # Eventi tenuti in memoria dal portale: la shell li chiede ogni secondo, quindi bastano pochi minuti.
    "BUFFER": 2000,
    # Query: "slow" manda solo quelle oltre SQL_SLOW_MS, "all" tutte, False nessuna (il conto per richiesta resta).
    "SQL": "slow",
    "SQL_SLOW_MS": 100,
    # Livello minimo dei log mandati alla shell.
    "LOG_LEVEL": "INFO",
    # Logger da non mandare: django.server e django.request ripetono richieste ed errori (già nella dashboard),
    # django.db.backends ogni query.
    "IGNORE_LOGGERS": ["django.server", "django.request", "django.db.backends", "django.utils.autoreload", "nothing_debug"],
    # Corpo delle richieste POST/PUT (troncato a 4 KB, campi sensibili nascosti). Spento di serie.
    "BODY": False,
    # Intestazioni e parametri con questi nomi (anche in parte) diventano [nascosto].
    "REDACT": ["authorization", "cookie", "password", "passwd", "secret", "token", "api-key", "apikey", "csrf", "session", "key"],
    # Controlli di serie (database, migrazioni, cache): False li spegne.
    "BUILTIN_CHECKS": True,
    # Accetta il collegamento anche da indirizzi non locali (portale su un server di sviluppo in rete).
    "ALLOW_REMOTE": True,
    # Cartella dei token (hash) e del codice di collegamento: fuori dal progetto, così non finisce in git.
    "STATE_DIR": None,
}


def get(name: str):
    from django.conf import settings

    user = getattr(settings, "NOTHING_DEBUG", {}) or {}
    if name in user:
        return user[name]
    value = DEFAULTS[name]
    if name == "ENABLED" and value is None:
        return bool(getattr(settings, "DEBUG", False))
    if name == "NAME" and value is None:
        return project_root().name
    if name == "STATE_DIR" and value is None:
        return Path.home() / ".nothing_debug"
    return value


def project_root() -> Path:
    from django.conf import settings

    base = getattr(settings, "BASE_DIR", None)
    if base:
        return Path(base).resolve()
    return Path.cwd().resolve()
