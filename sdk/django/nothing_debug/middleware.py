"""Il middleware: misura ogni richiesta e risponde agli indirizzi /_nothing/ della shell.

Va messo per primo in MIDDLEWARE: così misura anche il tempo degli altri middleware e risponde alla shell prima di
CSRF, sessioni e login (la shell si fa riconoscere col suo token, non coi cookie).
"""

from __future__ import annotations

import time
import uuid

from django.db import connections

from . import api, buffer, capture, conf

PREFIX = "/_nothing"


class NothingDebugMiddleware:
    def __init__(self, get_response):
        self.get_response = get_response
        self.enabled = bool(conf.get("ENABLED"))

    def __call__(self, request):
        if not self.enabled:
            return self.get_response(request)
        if request.path == PREFIX or request.path.startswith(PREFIX + "/"):
            response = api.handle(request, request.path[len(PREFIX):].strip("/"))
            # la shell chiede ogni secondo: le sue 401 non devono finire nel log del runserver
            response._has_been_logged = True
            return response

        state = capture.RequestState(uuid.uuid4().hex[:12])
        token = capture.current.set(state)
        start = time.perf_counter()
        response = None
        try:
            with _wrap_all_connections():
                response = self.get_response(request)
            return response
        finally:
            try:
                _record(request, response, state, (time.perf_counter() - start) * 1000)
            finally:
                capture.current.reset(token)

    # Le eccezioni arrivano dal segnale got_request_exception (apps.py): prende anche quelle dei middleware, non solo
    # delle viste, e non le 404 o i permessi negati, che non sono errori.


class _wrap_all_connections:
    """execute_wrapper su tutte le connessioni del progetto per la durata della richiesta."""

    def __init__(self):
        self._stack = []

    def __enter__(self):
        for alias in connections:
            try:
                cm = connections[alias].execute_wrapper(capture.sql_wrapper)
                cm.__enter__()
                self._stack.append(cm)
            except Exception:  # noqa: BLE001 (connessione non configurabile: si va avanti senza)
                pass
        return self

    def __exit__(self, *exc):
        while self._stack:
            self._stack.pop().__exit__(*exc)
        return False


def _record(request, response, state, ms):
    status = getattr(response, "status_code", 500)
    size = None
    if response is not None and not getattr(response, "streaming", False):
        try:
            size = len(response.content)
        except Exception:  # noqa: BLE001
            size = None
    match = getattr(request, "resolver_match", None)
    user = getattr(request, "user", None)
    username = None
    try:
        if user is not None and user.is_authenticated:
            username = user.get_username()
    except Exception:  # noqa: BLE001
        username = None
    buffer.push(
        "request",
        rid=state.rid,
        method=request.method,
        path=request.path,
        query=capture.query_string(request.META.get("QUERY_STRING", "")),
        status=status,
        ms=round(ms, 2),
        size=size,
        view=(match.view_name or getattr(match, "_func_path", None)) if match else None,
        user=username,
        ip=request.META.get("REMOTE_ADDR"),
        sql={"count": state.sql_count, "ms": round(state.sql_ms, 2)},
        headers=capture.headers(request.headers.items()),
        response_headers=capture.headers(response.items()) if response is not None else None,
        body=capture.body(request),
    )
