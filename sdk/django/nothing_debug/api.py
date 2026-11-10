"""Gli indirizzi che la shell chiede (docs/24-debug-potenziato.md del repo Nothing Shell).

    POST /_nothing/pair      {"code": "K7Q29XMP", "client": "PC-ANDREA"}  →  {"token": …, "portal": {…}}
    GET  /_nothing/events?after=N&limit=500                                  →  {"boot", "seq", "events", "more"}
    GET  /_nothing/status                                                    →  {"portal": {…}, "checks": […]}
    POST /_nothing/unpair                                                    →  {}
    GET  /_nothing/            pagina col codice di collegamento (solo dal PC del portale)

Tutto tranne pair e la pagina vuole l'intestazione Authorization: Bearer <token>.
"""

from __future__ import annotations

import html
import json
import os
import platform
import time

import django
from django.http import HttpResponse, JsonResponse

from . import buffer, checks, conf, state

PROTOCOL = 1
CONNECTORS = ["requests", "logs", "errors", "sql", "checks", "custom"]


def handle(request, route: str):
    if route == "pair" and request.method == "POST":
        return _pair(request)
    if route == "":
        return _page(request)
    if not state.check(_token(request)):
        return JsonResponse({"error": "non collegato"}, status=401)
    if route == "events":
        after = _int(request.GET.get("after"), 0)
        limit = min(max(_int(request.GET.get("limit"), 500), 1), 1000)
        return JsonResponse(buffer.read(after, limit))
    if route == "status":
        return JsonResponse({"portal": portal_info(), "checks": checks.run()})
    if route == "unpair" and request.method == "POST":
        state.unpair(_token(request))
        return JsonResponse({})
    if route == "clear" and request.method == "POST":
        buffer.clear()
        return JsonResponse({})
    return JsonResponse({"error": "indirizzo sconosciuto"}, status=404)


def portal_info() -> dict:
    from django.conf import settings

    return {
        "name": conf.get("NAME"),
        "framework": f"Django {django.get_version()} · Python {platform.python_version()}",
        "version": str(PROTOCOL),
        "connectors": CONNECTORS,
        "debug": bool(getattr(settings, "DEBUG", False)),
        "uptime": round(time.time() - buffer.STARTED, 1),
        "pid": os.getpid(),
        "root": str(conf.project_root()),
    }


def _pair(request):
    if not conf.get("ALLOW_REMOTE") and not _local(request):
        return JsonResponse({"error": "collegamento accettato solo da questo PC"}, status=403)
    try:
        data = json.loads(request.body or b"{}")
    except ValueError:
        data = {}
    token = state.pair(str(data.get("code", "")), str(data.get("client", "?")))
    if token is None:
        return JsonResponse({"error": "codice sbagliato o scaduto"}, status=403)
    return JsonResponse({"token": token, "portal": portal_info()})


def _page(request):
    """La pagina col codice, per chi non vede la console del runserver. Solo dal PC del portale."""
    if not _local(request):
        return HttpResponse("Nothing Debug: il codice si vede solo dal PC del portale.", status=403, content_type="text/plain; charset=utf-8")
    code, left = state.current_code(fresh=request.GET.get("nuovo") == "1")
    address = request.build_absolute_uri("/").rstrip("/")
    clients = state.clients()
    paired = "".join(f"<li>{html.escape(str(c['client']))}</li>" for c in clients) or "<li>nessuno</li>"
    body = f"""<!doctype html><meta charset="utf-8"><title>Nothing Debug</title>
<style>body{{font:15px system-ui;background:#000;color:#fff;display:grid;place-items:center;height:100vh;margin:0}}
main{{text-align:center}}code{{font:600 44px ui-monospace,Consolas;letter-spacing:.12em;color:#d71921}}
small,li{{color:#999}}ul{{list-style:none;padding:0}}a{{color:#fff}}</style>
<main><p>Nothing Shell › Impostazioni › Debug potenziato › Collega un portale</p>
<p>Indirizzo <b>{html.escape(address)}</b></p><code>{state.format_code(code)}</code>
<p><small>valido ancora {left // 60} min · una volta sola · <a href="?nuovo=1">codice nuovo</a></small></p>
<p><small>PC collegati</small></p><ul>{paired}</ul></main>"""
    return HttpResponse(body)


def _token(request) -> str | None:
    header = request.headers.get("Authorization", "")
    return header[7:].strip() if header.lower().startswith("bearer ") else None


def _local(request) -> bool:
    return request.META.get("REMOTE_ADDR") in ("127.0.0.1", "::1", "localhost")


def _int(text, default: int) -> int:
    try:
        return int(text)
    except (TypeError, ValueError):
        return default
