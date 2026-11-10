from __future__ import annotations

import logging
import os
import sys

from django.apps import AppConfig


class NothingDebugHandler(logging.Handler):
    """Manda i log del portale alla shell, legati alla richiesta in corso."""

    def emit(self, record: logging.LogRecord) -> None:
        from . import buffer, capture, conf

        try:
            if any(record.name == n or record.name.startswith(n + ".") for n in conf.get("IGNORE_LOGGERS")):
                return
            message = record.getMessage()
            if record.exc_info and record.exc_info[1] is not None:
                message += "\n" + "".join(logging.Formatter().formatException(record.exc_info))[-3000:]
            buffer.push("log", rid=capture.rid(), level=record.levelname, logger=record.name, msg=message[:4000])
        except Exception:  # noqa: BLE001
            self.handleError(record)


class NothingDebugConfig(AppConfig):
    name = "nothing_debug"
    verbose_name = "Nothing Debug"

    def ready(self):
        from django.core.signals import got_request_exception

        from . import buffer, capture, conf, state

        if not conf.get("ENABLED"):
            return
        buffer.resize(conf.get("BUFFER"))

        handler = NothingDebugHandler(level=getattr(logging, str(conf.get("LOG_LEVEL")).upper(), logging.INFO))
        root = logging.getLogger()
        if not any(isinstance(h, NothingDebugHandler) for h in root.handlers):
            root.addHandler(handler)
            if root.level > handler.level or root.level == logging.NOTSET:
                root.setLevel(handler.level)
        # i logger di Django non propagano sempre alla radice: si aggiunge anche a "django"
        django_logger = logging.getLogger("django")
        if not any(isinstance(h, NothingDebugHandler) for h in django_logger.handlers) and not django_logger.propagate:
            django_logger.addHandler(handler)

        def on_exception(sender, request=None, **kwargs):
            exc = sys.exc_info()[1]
            if exc is not None:
                capture.record_exception(exc, request)

        got_request_exception.connect(on_exception, weak=False, dispatch_uid="nothing_debug_exception")

        # Il runserver avvia due processi (quello che guarda i file e quello che serve): si scrive una volta sola.
        serving = os.environ.get("RUN_MAIN") == "true" or "--noreload" in sys.argv
        if "runserver" in sys.argv and serving:
            clients = state.clients()
            if clients:
                print(f"Nothing Debug · {len(clients)} PC collegati · /_nothing/ per collegarne un altro", flush=True)
            else:
                code, _ = state.current_code()
                print(f"Nothing Debug · codice di collegamento {state.format_code(code)} (10 min) · anche su /_nothing/", flush=True)
