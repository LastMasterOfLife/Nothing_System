"""Nothing Debug: collega un portale Django alla dashboard di debug di Nothing Shell.

    INSTALLED_APPS = [..., "nothing_debug"]
    MIDDLEWARE = ["nothing_debug.middleware.NothingDebugMiddleware", ...]   # per primo

Poi in Nothing Shell › Impostazioni › Debug potenziato si scrive l'indirizzo del portale e il codice che il runserver
stampa all'avvio (o che si legge su /_nothing/). Acceso solo con DEBUG = True, salvo NOTHING_DEBUG = {"ENABLED": …}.

Dal codice del portale:

    import nothing_debug

    nothing_debug.emit("ordine creato", id=ordine.id, totale=ordine.totale)

    @nothing_debug.check("Coda email")
    def coda_email():
        n = Email.objects.filter(inviata=False).count()
        return n, "warn" if n > 50 else "ok"
"""

from __future__ import annotations

from .checks import check

__all__ = ["check", "emit", "capture_exception"]
__version__ = "0.1.0"


def emit(name: str, **data) -> None:
    """Un evento a piacere nella dashboard (tab Eventi), legato alla richiesta in corso."""
    from . import buffer, capture

    buffer.push("custom", rid=capture.rid(), name=str(name)[:200], data={k: str(v)[:500] for k, v in data.items()})


def capture_exception(exc: BaseException) -> None:
    """Un'eccezione gestita che si vuole vedere comunque (dentro un except)."""
    from . import capture

    capture.record_exception(exc)
