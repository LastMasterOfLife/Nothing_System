using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;

namespace NothingShell.App.Settings;

/// <summary>
/// Vestiti del notch (docs/22-vestiti-notch.md): il guardaroba nella pagina Aspetto e l'editor per farne di propri. Mentre lo si
/// modifica il notch vero lo indossa già, senza salvarlo; uscendo dall'editor torna quello di prima.
/// </summary>
public sealed partial class SettingsWindow
{
    private const string OutfitFillKey = "outfit-fill";
    private const string OutfitFill2Key = "outfit-fill2";
    private const string OutfitPatternKey = "outfit-pattern";
    private const string OutfitRimKey = "outfit-rim";
    private const string DefaultOutfitName = "Il mio vestito";

    /// <summary>Il vestito nell'editor; null quando l'editor è chiuso.</summary>
    private NotchOutfit? _outfitDraft;
    private bool _outfitIsNew;
    private OutfitPreview? _outfitPreview;

    /// <summary>Dov'era la pagina Aspetto quando si è aperto l'editor.</summary>
    private double? _wardrobeOffset;

    /// <summary>Apre l'editor su un vestito (anche per le istantanee).</summary>
    public void EditOutfit(NotchOutfit outfit, bool isNew)
    {
        ArgumentNullException.ThrowIfNull(outfit);
        if (_selected != SettingsPage.Appearance)
            Show(SettingsPage.Appearance);
        _wardrobeOffset ??= _scroll.VerticalOffset;
        _outfitDraft = outfit;
        _outfitIsNew = isNew;
        _openPicker = null;
        _pickerHue = null;
        TryOnDraft();
        Rebuild();
        _scroll.ScrollToTop();
    }

    /// <summary>Apre un selettore colore come se lo si fosse cliccato (per le istantanee).</summary>
    public void OpenPickerForSnapshot(string key)
    {
        _openPicker = key;
        Rebuild();
    }

    /// <summary>Chiude l'editor senza salvare: il notch torna a indossare quello di prima.</summary>
    private void CloseOutfitEditor()
    {
        if (_outfitDraft is null)
            return;
        _outfitDraft = null;
        _outfitPreview = null;
        _openPicker = null;
        Theme.Apply(_shell.Appearance);
    }

    /// <summary>Il guardaroba: un clic e il notch indossa il vestito; sotto, cosa si può fare con quello indossato.</summary>
    private void WardrobeRows(AppearanceSettings look)
    {
        var block = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        block.Children.Add(Label("Guardaroba", 14, Theme.InkBrush));
        var hint = Label("Un clic e il notch lo indossa. Quelli di serie si personalizzano, i tuoi si modificano. I colori si adattano al tema perché il testo resti leggibile",
            12, Theme.MutedBrush);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Margin = new Thickness(0, 3, 0, 10);
        block.Children.Add(hint);
        var tiles = new WrapPanel();
        tiles.Children.Add(OutfitTile(null, "Nessuno", look.NotchOutfit is null));
        foreach (var outfit in OutfitCatalog.Wardrobe(_shell.Wardrobe))
            tiles.Children.Add(OutfitTile(outfit, outfit.Name, look.NotchOutfit?.Id == outfit.Id));
        tiles.Children.Add(NewOutfitTile());
        block.Children.Add(tiles);
        AddRow(block);

        if (look.NotchOutfit is not { } worn)
            return;
        var mine = _shell.Wardrobe.FirstOrDefault(o => o.Id == worn.Id);
        ActionRow($"Indossa: {worn.Name}",
            mine is not null ? "Fatto da te" : worn.IsCustom ? "Non è più nel guardaroba" : "Di serie: personalizzandolo ne fai una copia tua",
            mine is not null
                ? [("Modifica", () => EditOutfit(mine, isNew: false)), ("Duplica", () => EditOutfit(OutfitCatalog.Copy(mine), isNew: true))]
                : [("Personalizza", () => EditOutfit(OutfitCatalog.Copy(worn), isNew: true))]);
    }

    private Border OutfitTile(NotchOutfit? outfit, string name, bool selected)
    {
        var stack = new StackPanel();
        stack.Children.Add(new OutfitPreview(outfit, large: false));
        var label = Label(name, 12, Theme.InkBrush);
        label.Margin = new Thickness(2, 6, 0, 0);
        label.MaxWidth = 112;
        label.TextTrimming = TextTrimming.CharacterEllipsis;
        stack.Children.Add(label);
        var tile = WardrobeTile(stack, name, selected);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SetAppearance(a => a with { NotchOutfit = outfit });
        };
        // doppio clic su uno dei tuoi: dritto all'editor
        if (outfit is { IsCustom: true })
            tile.MouseLeftButtonDown += (_, e) =>
            {
                if (e.ClickCount != 2)
                    return;
                e.Handled = true;
                if (_shell.Wardrobe.FirstOrDefault(o => o.Id == outfit.Id) is { } mine)
                    EditOutfit(mine, isNew: false);
            };
        return tile;
    }

    private Border NewOutfitTile()
    {
        var shape = new Grid { Width = 112, Height = 30, HorizontalAlignment = HorizontalAlignment.Left };
        shape.Children.Add(new Rectangle
        {
            RadiusX = 15,
            RadiusY = 15,
            Stroke = Theme.MutedBrush,
            StrokeThickness = 1,
            StrokeDashArray = [3, 3],
        });
        shape.Children.Add(Glyph("", Theme.MutedBrush, 12));
        var stack = new StackPanel();
        stack.Children.Add(shape);
        var label = Label("Nuovo", 12, Theme.MutedBrush);
        label.Margin = new Thickness(2, 6, 0, 0);
        stack.Children.Add(label);
        var tile = WardrobeTile(stack, "Crea un vestito tuo", selected: false);
        tile.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            EditOutfit(new NotchOutfit
            {
                Id = NotchOutfit.NewId(),
                Name = DefaultOutfitName,
                Fill = Theme.Current.Accent.ToHex(),
                Gradient = OutfitGradient.Vertical,
                Pattern = OutfitPattern.Dots,
            }, isNew: true);
        };
        return tile;
    }

    private static Border WardrobeTile(UIElement content, string tooltip, bool selected)
    {
        var tile = new Border
        {
            CornerRadius = new CornerRadius(Theme.RadiusSmall + 2),
            BorderThickness = new Thickness(selected ? 2 : 1),
            BorderBrush = selected ? Theme.AccentBrush : Brushes.Transparent,
            Background = Brushes.Transparent,
            Padding = new Thickness(selected ? 7 : 8),
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            Child = content,
        };
        if (!selected)
        {
            tile.MouseEnter += (_, _) => tile.Background = Theme.SurfaceBrush;
            tile.MouseLeave += (_, _) => tile.Background = Brushes.Transparent;
        }
        return tile;
    }

    /// <summary>L'editor: anteprima grande in cima, poi colore, motivo e materiale; ogni ritocco si vede subito, anche sul notch.</summary>
    private void OutfitEditor()
    {
        var draft = _outfitDraft!;
        Heading(_outfitIsNew ? "Nuovo vestito" : "Modifica vestito");
        Card(() =>
        {
            _outfitPreview = new OutfitPreview(draft, large: true) { Margin = new Thickness(0, 10, 0, 6) };
            _current.Children.Add(_outfitPreview);
            Note("Il notch lo sta già provando: passaci sopra per vederlo aperto. Se un colore non lascerebbe leggere il testo si scurisce o si schiarisce da solo quanto basta.",
                Theme.MutedBrush);
            var name = new TextBox { Text = draft.Name };
            name.TextChanged += (_, _) => _outfitDraft = _outfitDraft is null ? null : _outfitDraft with { Name = name.Text };
            AddField("Nome", name);
        });

        Section("Colore");
        OutfitColorRow(OutfitFillKey, "Colore", "Il fondo del vestito", draft.Fill, (o, hex) => o with { Fill = hex });
        ChoiceRow("Sfumatura", "Dal primo colore al secondo, nella direzione scelta",
            [("Nessuna", OutfitGradient.Solid), ("Orizzontale", OutfitGradient.Horizontal), ("Verticale", OutfitGradient.Vertical), ("Diagonale", OutfitGradient.Diagonal)],
            draft.Gradient, v => UpdateDraft(o => o with
            {
                Gradient = v,
                Fill2 = v == OutfitGradient.Solid ? o.Fill2 : o.Fill2 ?? o.Resolve(Argb.FromRgb(0xFFFFFF)).Fill2.ToHex(),
            }));
        if (draft.Gradient != OutfitGradient.Solid)
            OutfitColorRow(OutfitFill2Key, "Secondo colore", "Dove arriva la sfumatura", draft.Fill2 ?? draft.Fill, (o, hex) => o with { Fill2 = hex });

        Section("Motivo");
        WrapChoiceRow("Motivo", "Ripetuto sopra il colore, sotto il testo",
            [("Nessuno", OutfitPattern.None), ("Righe", OutfitPattern.Stripes), ("Pois", OutfitPattern.Dots), ("Quadretti", OutfitPattern.Checks),
             ("Griglia", OutfitPattern.Grid), ("Onde", OutfitPattern.Waves), ("Stelle", OutfitPattern.Stars), ("Cuori", OutfitPattern.Hearts)],
            draft.Pattern, v => UpdateDraft(o => o with { Pattern = v }));
        if (draft.Pattern != OutfitPattern.None)
        {
            OutfitColorRow(OutfitPatternKey, "Colore del motivo", "Resta sempre un passo più tenue del testo", draft.PatternColor, (o, hex) => o with { PatternColor = hex });
            SliderRow("Intensità", "Quanto si vede il motivo: tutta a sinistra sparisce",
                draft.PatternStrength / NotchOutfit.MaxPatternOpacity,
                v => UpdateDraft(o => o with { PatternOpacity = v * NotchOutfit.MaxPatternOpacity }, live: true),
                v => UpdateDraft(o => o with { PatternOpacity = v * NotchOutfit.MaxPatternOpacity }));
        }

        Section("Materiale");
        SliderRow("Copertura", "Quanto il vestito copre il vetro: tutta a destra è pieno, verso sinistra il vetro si intravede",
            (draft.CoverageAmount - NotchOutfit.MinCoverage) / (1 - NotchOutfit.MinCoverage),
            v => UpdateDraft(o => o with { Coverage = Coverage(v) }, live: true),
            v => UpdateDraft(o => o with { Coverage = Coverage(v) }));
        ToggleRow("Bordo colorato", "Un filo di colore tutt'intorno al notch, al posto di quello del tema",
            draft.Rim is not null, v => UpdateDraft(o => o with { Rim = v ? o.Rim ?? Theme.Current.Accent.ToHex() : null }));
        if (draft.Rim is not null)
            OutfitColorRow(OutfitRimKey, "Colore del bordo", "Il bordo non si regola: è un filo sottile", draft.Rim, (o, hex) => o with { Rim = hex });

        Card(() =>
        {
            ButtonRow(true, ("Salva e indossa", () => SaveOutfit(wear: true)), ("Salva", () => SaveOutfit(wear: false)), ("Annulla", () =>
            {
                CloseOutfitEditor();
                Rebuild();
            }));
            if (!_outfitIsNew)
                ConfirmButtonRow("Elimina vestito", "Clic di nuovo per eliminarlo", DeleteOutfit);
        });

        static double Coverage(double v) => NotchOutfit.MinCoverage + (v * (1 - NotchOutfit.MinCoverage));
    }

    private void OutfitColorRow(string key, string title, string description, string hex, Func<NotchOutfit, string, NotchOutfit> set) =>
        ColorRow(key, title, description, Argb.TryParse(hex) ?? Argb.FromRgb(0x202020),
            c => UpdateDraft(o => set(o, c.ToHex()), live: true),
            c => UpdateDraft(o => set(o, c.ToHex())));

    /// <summary>
    /// Cambia la bozza e la fa provare al notch. Con <paramref name="live"/> (trascinando) ridipinge solo l'anteprima; altrimenti
    /// rifà l'editor, che può mostrare righe nuove (secondo colore, colore del motivo, bordo).
    /// </summary>
    private void UpdateDraft(Func<NotchOutfit, NotchOutfit> change, bool live = false)
    {
        if (_outfitDraft is null)
            return;
        _outfitDraft = change(_outfitDraft);
        _outfitPreview?.Show(_outfitDraft);
        if (live)
        {
            TryOnDraft();
            return;
        }
        _holdRebuild = true;
        TryOnDraft();
        _holdRebuild = false;
        Rebuild();
    }

    /// <summary>Il notch indossa la bozza senza salvarla: le impostazioni restano quelle di prima finché non si salva.</summary>
    private void TryOnDraft()
    {
        if (_outfitDraft is not null)
            Theme.Apply(_shell.Appearance with { NotchOutfit = _outfitDraft });
    }

    private void SaveOutfit(bool wear)
    {
        if (_outfitDraft is not { } draft)
            return;
        var outfit = draft with { Name = string.IsNullOrWhiteSpace(draft.Name) ? DefaultOutfitName : draft.Name.Trim() };
        _shell.Wardrobe = OutfitCatalog.Save(_shell.Wardrobe, outfit);
        var worn = _shell.Appearance.NotchOutfit?.Id == outfit.Id;
        _outfitDraft = null;
        _outfitPreview = null;
        _openPicker = null;
        if (wear || worn) // quello indossato si aggiorna anche solo salvando
            _shell.Appearance = _shell.Appearance with { NotchOutfit = outfit };
        else
            Theme.Apply(_shell.Appearance);
        Rebuild();
    }

    private void DeleteOutfit()
    {
        if (_outfitDraft is not { } draft)
            return;
        _shell.Wardrobe = OutfitCatalog.Remove(_shell.Wardrobe, draft.Id);
        _outfitDraft = null;
        _outfitPreview = null;
        _openPicker = null;
        if (_shell.Appearance.NotchOutfit?.Id == draft.Id)
            _shell.Appearance = _shell.Appearance with { NotchOutfit = null };
        else
            Theme.Apply(_shell.Appearance);
        Rebuild();
    }

    /// <summary>Come <see cref="ChoiceRow{T}"/> ma con le scelte che vanno a capo sotto il titolo: per quando sono tante.</summary>
    private void WrapChoiceRow<T>(string title, string description, (string Label, T Value)[] options, T selected, Action<T> pick)
        where T : notnull
    {
        var block = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        block.Children.Add(Label(title, 14, Theme.InkBrush));
        var hint = Label(description, 12, Theme.MutedBrush);
        hint.Margin = new Thickness(0, 3, 0, 10);
        hint.TextWrapping = TextWrapping.Wrap;
        block.Children.Add(hint);
        var chips = new WrapPanel();
        foreach (var (label, value) in options)
        {
            var chip = Chip(label, EqualityComparer<T>.Default.Equals(value, selected));
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                pick(value);
            };
            chips.Children.Add(chip);
        }
        block.Children.Add(chips);
        AddRow(block);
    }
}
