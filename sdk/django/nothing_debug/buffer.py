"""Gli eventi in memoria: numerati, al massimo BUFFER, letti dalla shell con un cursore."""

from __future__ import annotations

import secrets
import threading
import time
from collections import deque

# Cambia a ogni avvio del processo (runserver che ricarica il codice): la shell riparte da zero col cursore.
BOOT = secrets.token_hex(6)
STARTED = time.time()

_lock = threading.Lock()
_events: deque = deque(maxlen=2000)
_seq = 0


def resize(size: int) -> None:
    global _events
    with _lock:
        _events = deque(_events, maxlen=max(100, int(size)))


def push(kind: str, **data) -> None:
    """Aggiunge un evento; non solleva mai eccezioni (il portale non deve rompersi per il debug)."""
    global _seq
    try:
        event = {"kind": kind, "t": round(time.time(), 3)}
        event.update({k: v for k, v in data.items() if v is not None})
        with _lock:
            _seq += 1
            event["seq"] = _seq
            _events.append(event)
    except Exception:  # noqa: BLE001
        pass


def read(after: int, limit: int = 500) -> dict:
    with _lock:
        last = _seq
        if after > last:  # cursore di un altro avvio: si riparte
            after = 0
        found = [e for e in _events if e["seq"] > after]
    more = len(found) > limit
    found = found[:limit]
    return {
        "boot": BOOT,
        "seq": found[-1]["seq"] if more else last,
        "events": found,
        "more": more,
    }


def clear() -> None:
    with _lock:
        _events.clear()
