"""Cosa si raccoglie: richiesta in corso, query, stack degli errori, dati sensibili nascosti."""

from __future__ import annotations

import contextvars
import sysconfig
import time
import traceback
from pathlib import Path
from urllib.parse import parse_qsl, urlencode

from . import buffer, conf

HIDDEN = "[nascosto]"

# La richiesta in corso nel thread (o nel task async): id, conto delle query.
current = contextvars.ContextVar("nothing_debug_request", default=None)


class RequestState:
    __slots__ = ("rid", "sql_count", "sql_ms")

    def __init__(self, rid: str):
        self.rid = rid
        self.sql_count = 0
        self.sql_ms = 0.0


def rid() -> str | None:
    state = current.get()
    return state.rid if state else None


def sensitive(name: str) -> bool:
    lowered = name.lower()
    return any(word in lowered for word in conf.get("REDACT"))


def headers(items) -> dict:
    return {k: (HIDDEN if sensitive(k) else str(v)[:500]) for k, v in items}


def query_string(text: str) -> str | None:
    if not text:
        return None
    pairs = parse_qsl(text, keep_blank_values=True)
    return urlencode([(k, HIDDEN if sensitive(k) else v) for k, v in pairs])[:1000]


def body(request) -> str | None:
    if not conf.get("BODY") or request.method in ("GET", "HEAD", "OPTIONS"):
        return None
    try:
        if request.content_type == "application/x-www-form-urlencoded":
            return urlencode([(k, HIDDEN if sensitive(k) else v) for k, v in request.POST.items()])[:4096]
        raw = request.body[:4096]
        return raw.decode("utf-8", errors="replace")
    except Exception:  # noqa: BLE001 (corpo già letto come stream, multipart…)
        return None


_LIBRARIES = tuple(
    str(Path(p).resolve()).lower() for p in {sysconfig.get_paths().get("purelib"), sysconfig.get_paths().get("stdlib")} if p
)


def in_app(filename: str) -> bool:
    """Codice del portale: dentro il progetto e non in site-packages o nella libreria standard."""
    try:
        path = str(Path(filename).resolve()).lower()
    except (OSError, ValueError):
        return False
    if "site-packages" in path or path.startswith(_LIBRARIES):
        return False
    return path.startswith(str(conf.project_root()).lower())


def frames(exc: BaseException) -> list[dict]:
    found = []
    for f in traceback.extract_tb(exc.__traceback__)[-30:]:
        found.append({"file": f.filename, "line": f.lineno, "func": f.name, "code": (f.line or "")[:300], "app": in_app(f.filename)})
    return found


def record_exception(exc: BaseException, request=None) -> None:
    buffer.push(
        "error",
        rid=rid(),
        type=type(exc).__qualname__,
        msg=str(exc)[:2000],
        frames=frames(exc),
        method=getattr(request, "method", None),
        path=getattr(request, "path", None),
    )


def sql_wrapper(execute, sql, params, many, context):
    """Avvolge ogni query (connection.execute_wrapper): tempo, conto per richiesta, query lente come eventi."""
    start = time.perf_counter()
    try:
        return execute(sql, params, many, context)
    finally:
        ms = (time.perf_counter() - start) * 1000
        state = current.get()
        if state is not None:
            state.sql_count += 1
            state.sql_ms += ms
        mode = conf.get("SQL")
        if mode == "all" or (mode == "slow" and ms >= conf.get("SQL_SLOW_MS")):
            alias = getattr(context.get("connection"), "alias", None)
            buffer.push("sql", rid=state.rid if state else None, sql=str(sql)[:4000], ms=round(ms, 2), db=alias)
