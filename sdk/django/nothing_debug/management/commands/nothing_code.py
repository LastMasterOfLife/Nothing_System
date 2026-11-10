"""python manage.py nothing_code: il codice per collegare Nothing Shell (anche col server già avviato)."""

from django.core.management.base import BaseCommand

from nothing_debug import state


class Command(BaseCommand):
    help = "Codice di collegamento per Nothing Shell › Debug potenziato"

    def add_arguments(self, parser):
        parser.add_argument("--nuovo", action="store_true", help="un codice nuovo (il vecchio non vale più)")
        parser.add_argument("--scollega-tutti", action="store_true", help="dimentica tutti i PC collegati")

    def handle(self, *args, **options):
        if options["scollega_tutti"]:
            state.forget_all()
            self.stdout.write("Tutti i PC scollegati.")
            return
        code, left = state.current_code(fresh=options["nuovo"])
        self.stdout.write(f"Codice {state.format_code(code)} · valido ancora {left // 60} min · una volta sola")
        clients = state.clients()
        if clients:
            self.stdout.write("Collegati: " + ", ".join(str(c["client"]) for c in clients))
