"""I connettori del portale: controlli che la shell legge ogni 15 s (di serie e scritti da te con @check)."""

from __future__ import annotations

import time

_checks: list[tuple[str, object]] = []


def check(name: str):
    """Registra un controllo. La funzione restituisce un valore, oppure (valore, stato) o (valore, stato, dettaglio),
    oppure un dict con value/state/detail. Stato: "ok", "warn", "error". Un'eccezione vale "error".

        @nothing_debug.check("Coda email")
        def coda():
            n = Email.objects.filter(inviata=False).count()
            return n, "warn" if n > 50 else "ok"
    """

    def register(fn):
        _checks[:] = [(n, f) for n, f in _checks if n != name]
        _checks.append((name, fn))
        return fn

    return register


def _normalize(name: str, result) -> dict:
    if isinstance(result, dict):
        return {"name": name, "value": result.get("value", ""), "state": result.get("state", "ok"), "detail": result.get("detail")}
    if isinstance(result, tuple):
        value = result[0] if result else ""
        state = result[1] if len(result) > 1 else "ok"
        detail = result[2] if len(result) > 2 else None
        return {"name": name, "value": value, "state": state or "ok", "detail": detail}
    if isinstance(result, bool):
        return {"name": name, "value": "sì" if result else "no", "state": "ok" if result else "error"}
    return {"name": name, "value": result, "state": "ok"}


def run() -> list[dict]:
    from . import conf

    found = []
    entries = list(_checks)
    if conf.get("BUILTIN_CHECKS"):
        entries = [*_builtins(), *entries]
    for name, fn in entries:
        start = time.perf_counter()
        try:
            item = _normalize(name, fn())
        except Exception as exc:  # noqa: BLE001
            item = {"name": name, "value": type(exc).__name__, "state": "error", "detail": str(exc)[:300]}
        item["ms"] = round((time.perf_counter() - start) * 1000, 1)
        found.append(item)
    return found


def _builtins():
    from django.db import connections

    def database(alias):
        def probe():
            start = time.perf_counter()
            with connections[alias].cursor() as cursor:
                cursor.execute("SELECT 1")
                cursor.fetchone()
            ms = (time.perf_counter() - start) * 1000
            vendor = connections[alias].vendor
            return f"{ms:.0f} ms", "warn" if ms > 500 else "ok", vendor

        return probe

    def migrations():
        from django.db.migrations.executor import MigrationExecutor

        executor = MigrationExecutor(connections["default"])
        plan = executor.migration_plan(executor.loader.graph.leaf_nodes())
        if not plan:
            return "nessuna da applicare", "ok"
        names = ", ".join(f"{m.app_label}.{m.name}" for m, _ in plan[:3])
        return f"{len(plan)} da applicare", "warn", names

    def cache():
        from django.core.cache import cache as default

        key = "nothing_debug:probe"
        start = time.perf_counter()
        default.set(key, "1", 5)
        ok = default.get(key) == "1"
        ms = (time.perf_counter() - start) * 1000
        return (f"{ms:.0f} ms", "ok") if ok else ("non risponde", "error")

    for alias in connections:
        yield (f"Database {alias}", database(alias))
    yield ("Migrazioni", migrations)
    yield ("Cache", cache)
