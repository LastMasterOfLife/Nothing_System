"""Codice di collegamento e token dei PC collegati, in un file fuori dal progetto.

Il file resta fra un riavvio e l'altro del runserver (che ricarica il codice a ogni modifica), così la shell resta
collegata. Dei token si tiene solo l'hash: chi legge il file non può fingersi la shell.
"""

from __future__ import annotations

import hashlib
import hmac
import json
import os
import secrets
import tempfile
import threading
import time
from pathlib import Path

from . import conf

ALPHABET = "ABCDEFGHJKMNPQRSTUVWXYZ23456789"  # come PairingCode della shell: niente 0/O, 1/I/L
CODE_LIFETIME = 10 * 60
MAX_TRIES = 5

_lock = threading.Lock()


def _path() -> Path:
    root = str(conf.project_root())
    key = hashlib.sha256(root.lower().encode("utf-8")).hexdigest()[:16]
    folder = Path(conf.get("STATE_DIR"))
    folder.mkdir(parents=True, exist_ok=True)
    return folder / f"{conf.project_root().name}-{key}.json"


def _load() -> dict:
    try:
        return json.loads(_path().read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _save(state: dict) -> None:
    path = _path()
    fd, temp = tempfile.mkstemp(dir=path.parent, prefix=".tmp-")
    with os.fdopen(fd, "w", encoding="utf-8") as f:
        json.dump(state, f, indent=2)
    os.replace(temp, path)


def _hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


def normalize(code: str) -> str:
    return "".join(c for c in str(code).upper() if c not in "- _·")


def format_code(code: str) -> str:
    return f"{code[:4]}-{code[4:]}" if len(code) == 8 else code


def current_code(fresh: bool = False) -> tuple[str, int]:
    """Il codice valido (lo crea se manca o è scaduto) e i secondi che gli restano."""
    with _lock:
        state = _load()
        code = state.get("code") or {}
        now = time.time()
        if fresh or not code.get("value") or code.get("expires", 0) <= now or code.get("tries", 0) >= MAX_TRIES:
            code = {"value": "".join(secrets.choice(ALPHABET) for _ in range(8)), "expires": now + CODE_LIFETIME, "tries": 0}
            state["code"] = code
            _save(state)
        return code["value"], int(code["expires"] - now)


def pair(code: str, client: str) -> str | None:
    """Scambia il codice con un token (una volta sola); None se il codice è sbagliato o scaduto."""
    with _lock:
        state = _load()
        current = state.get("code") or {}
        if not current.get("value") or current.get("expires", 0) <= time.time():
            return None
        if not hmac.compare_digest(normalize(code), current["value"]):
            current["tries"] = current.get("tries", 0) + 1
            if current["tries"] >= MAX_TRIES:  # troppi tentativi: il codice non vale più
                state.pop("code", None)
            else:
                state["code"] = current
            _save(state)
            return None
        token = secrets.token_urlsafe(32)
        state.pop("code", None)  # il codice vale una volta sola
        tokens = state.setdefault("tokens", [])
        tokens.append({"hash": _hash(token), "client": str(client)[:80], "since": int(time.time())})
        _save(state)
        return token


def check(token: str | None) -> bool:
    if not token:
        return False
    digest = _hash(token)
    return any(hmac.compare_digest(digest, t.get("hash", "")) for t in _load().get("tokens", []))


def unpair(token: str) -> None:
    with _lock:
        state = _load()
        digest = _hash(token)
        state["tokens"] = [t for t in state.get("tokens", []) if not hmac.compare_digest(digest, t.get("hash", ""))]
        _save(state)


def clients() -> list[dict]:
    return [{"client": t.get("client"), "since": t.get("since")} for t in _load().get("tokens", [])]


def forget_all() -> None:
    with _lock:
        state = _load()
        state["tokens"] = []
        _save(state)
