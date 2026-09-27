using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Revit.Services;

namespace AvionParTerre.Revit.UI;

internal sealed class ProfileOption
{
    public ReferenceProfile? Profile { get; init; }
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

internal sealed class FinishRow : SelectableRow
{
    private readonly FinishCatalogue _cat;
    private ProfileOption? _option;
    private List<ProfileOption> _options = new();
    private readonly Dictionary<string, string> _values = new();
    private string _famille = "";
    private string _decision = "";

    public FinishRow(Room room, FinishCatalogue cat)
    {
        Room = room;
        _cat = cat;
        foreach (var s in FinishSummary.RoomSupports)
            OptionsBySupport[s] = cat.Finitions.Where(f => f.Support == s).Select(f => f.Designation).Distinct()
                .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    [Browsable(false)] public Room Room { get; }
    [DisplayName("Niveau")] public string Niveau { get; init; } = "";
    [DisplayName("N°")] public string Numero { get; init; } = "";
    [DisplayName("Local")] public string Nom { get; init; } = "";
    [DisplayName("Famille"), ReadOnly(true)] public string Famille { get => _famille; set => Set(ref _famille, value); }
    [Browsable(false)] public string? FamilyCode { get; set; }
    [Browsable(false)] public List<ProfileOption> Options { get => _options; set { _options = value; Raise(nameof(Options)); } }
    [Browsable(false)] public Dictionary<string, List<string>> OptionsBySupport { get; } = new();

    [Browsable(false)]
    public ProfileOption? Option
    {
        get => _option;
        set
        {
            if (!Set(ref _option, value)) return;
            // Le profil propose un triplet ; chaque support reste modifiable (liste ou saisie)
            foreach (var s in FinishSummary.RoomSupports)
                SetValue(s, value?.Profile == null ? "" : FinishSummary.For(_cat, value.Profile.Applications, s) ?? "", fromProfile: true);
            Raise(nameof(Remarque));
            if (value?.Profile == null && FinishSummary.RoomSupports.All(s => string.IsNullOrEmpty(_values.GetValueOrDefault(s)))) Selected = false;
        }
    }

    public string Get(string support) => _values.TryGetValue(support, out var v) ? v : "";

    public void SetValue(string support, string value, bool fromProfile = false)
    {
        value ??= "";
        if (_values.TryGetValue(support, out var old) && old == value) return;
        _values[support] = value;
        if (fromProfile && value.Length > 0 && !OptionsBySupport[support].Contains(value))
        {
            OptionsBySupport[support] = new[] { value }.Concat(OptionsBySupport[support]).ToList();
            Raise(support + "Options");
        }
        if (!fromProfile) { Edited = true; if (value.Length > 0) Selected = true; }
        Raise(support);
    }

    [Browsable(false)] public string Sol { get => Get(FinishSummary.Sol); set => SetValue(FinishSummary.Sol, value); }
    [Browsable(false)] public string Mur { get => Get(FinishSummary.Mur); set => SetValue(FinishSummary.Mur, value); }
    [Browsable(false)] public string Plafond { get => Get(FinishSummary.Plafond); set => SetValue(FinishSummary.Plafond, value); }
    [Browsable(false)] public List<string> SolOptions => OptionsBySupport[FinishSummary.Sol];
    [Browsable(false)] public List<string> MurOptions => OptionsBySupport[FinishSummary.Mur];
    [Browsable(false)] public List<string> PlafondOptions => OptionsBySupport[FinishSummary.Plafond];
    [Browsable(false)] public bool Edited { get; private set; }

    [DisplayName("Valeurs actuelles")] public string Actuel { get; init; } = "";
    [DisplayName("État")] public string Etat { get; init; } = "";
    [Browsable(false)] public string Decision { get => _decision; set { Set(ref _decision, value); Raise(nameof(Remarque)); } }
    [DisplayName("Remarque / observations proches")] public string Remarque =>
        string.Join(" · ", new[] { Decision, _option?.Profile?.Remarque, Similaires }.Where(s => !string.IsNullOrEmpty(s)));
    [Browsable(false)] public string Similaires { get; set; } = "";

    // Décision de l'IA appliquée à la ligne
    [Browsable(false)] public DecisionSource Source { get; set; } = DecisionSource.Utilisateur;
    [Browsable(false)] public string? Model { get; set; }
    [Browsable(false)] public string? Justification { get; set; }
    [Browsable(false)] public List<string> Sources { get; set; } = new();

    public void ApplyAdvice(RoomAdvice a, FinishCatalogue cat, string model)
    {
        if (a.FamilyCode != null) { FamilyCode = a.FamilyCode; Famille = (cat.Family(a.FamilyCode)?.Libelle ?? a.FamilyCode) + " (IA)"; }
        var opt = Options.FirstOrDefault(o => o.Profile?.Id == a.ProfileId);
        // Choosing metadata must not copy profile values over preserved/manual fields.
        _option = opt;
        Raise(nameof(Option));
        Raise(nameof(Remarque));
        foreach (var (s, f) in a.Finishes) SetValue(s, f.Designation, fromProfile: true);
        Source = DecisionSource.Ia;
        Model = model;
        Justification = a.Justification;
        Sources = a.Sources;
        Decision = $"IA ({model}) : {a.Justification}" + (a.Finishes.Values.Any(f => f.HorsReferentiel) ? " [hors référentiel]" : "");
        Selected = FinishSummary.RoomSupports.Any(s => Get(s).Length > 0);
    }
}

/// <summary>Choix et validation des finitions par pièce : profil de référence, puis liste déroulante modifiable par support.</summary>
internal sealed class FinishesWindow
{
    private readonly FinishCatalogue _cat;
    private readonly FinishMatcher _matcher;
    private readonly List<FinishRow> _rows;
    private readonly ListWindow<FinishRow> _lw;
    private readonly ComboBox _library;
    private readonly CheckBox _overwrite;
    private readonly TextBox _author;

    public Action<string>? ExportGrid { get; set; }
    /// <summary>Demande à l'IA de décider les lignes cochées (fourni par la commande).</summary>
    public Action<IReadOnlyList<FinishRow>, string?>? AskAi { get; set; }

    public FinishesWindow(FinishCatalogue cat, IEnumerable<FinishRow> rows, string? defaultLibrary, string author)
    {
        _cat = cat;
        _matcher = new FinishMatcher(cat);
        _rows = rows.ToList();
        _lw = new ListWindow<FinishRow>("Finitions des pièces",
            "Profils du référentiel K&D (VLG, APROMAC, ROPAN, Planétarium) ; chaque finition se choisit dans la liste ou se tape. Rien n'est écrit sans case cochée.",
            _rows, "Valider et écrire les lignes cochées", Preview);

        var libs = new List<Library?> { null };
        libs.AddRange(cat.Bibliotheques);
        _library = Controls.Combo(libs, l => l == null ? "Toutes les bibliothèques" : l.Display, Math.Max(0, libs.FindIndex(l => l?.Code == defaultLibrary)), 400);
        _library.SelectionChanged += (_, _) => Recompute();
        _lw.Options.Children.Add(Controls.Labeled("Bibliothèque", _library));
        _author = new TextBox { Width = 130, Text = author };
        _lw.Options.Children.Add(Controls.Labeled("Validé par", _author));
        _overwrite = new CheckBox { Content = "Écraser les saisies manuelles", VerticalAlignment = VerticalAlignment.Center };
        _lw.Options.Children.Add(_overwrite);

        var grid = _lw.GridControl;
        grid.AutoGeneratedColumns += (_, _) =>
        {
            var col = new DataGridComboBoxColumn
            {
                Header = "Profil de référence",
                SelectedItemBinding = new Binding(nameof(FinishRow.Option)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged },
                Width = 230,
            };
            var style = new Style(typeof(ComboBox));
            style.Setters.Add(new Setter(ItemsControl.ItemsSourceProperty, new Binding(nameof(FinishRow.Options))));
            col.ElementStyle = style;
            col.EditingElementStyle = style;
            var at = grid.Columns.IndexOf(grid.Columns.First(c => Equals(c.Header, "Famille"))) + 1;
            grid.Columns.Insert(at, col);
            grid.Columns.Insert(at + 1, Theme.EditableComboColumn("Sol", nameof(FinishRow.Sol), nameof(FinishRow.SolOptions), 210));
            grid.Columns.Insert(at + 2, Theme.EditableComboColumn("Mur", nameof(FinishRow.Mur), nameof(FinishRow.MurOptions), 210));
            grid.Columns.Insert(at + 3, Theme.EditableComboColumn("Plafond", nameof(FinishRow.Plafond), nameof(FinishRow.PlafondOptions), 210));
        };

        var ai = Theme.Button("Décider par l'IA (lignes cochées)");
        ai.ToolTip = "Classe les locaux, choisit profils et finitions manquantes avec le modèle choisi dans Paramètres (recherche internet pour les locaux inconnus).";
        ai.Click += (_, _) =>
        {
            var sel = _lw.Selection;
            if (sel.Count == 0) { MessageBox.Show(_lw.Window, "Cocher les lignes à confier à l'IA.", "Avion par terre"); return; }
            AskAi?.Invoke(sel, LibraryCode);
        };
        var exp = Theme.Button("Exporter la grille (CSV)");
        exp.Click += (_, _) => ExportGrid?.Invoke("csv");
        _lw.ExtraButtons.Children.Add(ai);
        _lw.ExtraButtons.Children.Add(exp);
        Recompute();
    }

    public string? LibraryCode => Controls.Value<Library>(_library)?.Code;

    private void Recompute()
    {
        var lib = LibraryCode;
        foreach (var r in _rows)
        {
            if (r.Source == DecisionSource.Ia || r.Edited) continue; // ne pas effacer une décision ou une saisie
            var sugg = _matcher.Suggest(r.Nom, r.Niveau, lib, 6);
            var opts = new List<ProfileOption> { new() { Label = "(aucun — à définir)" } };
            opts.AddRange(sugg.Select(s => new ProfileOption { Profile = s.Profile, Label = $"{s.Profile.Display} — {s.Raison}" }));
            opts.AddRange(_cat.Profils.Where(p => (lib == null || p.Bibliotheque == lib) && sugg.All(s => s.Profile.Id != p.Id))
                .Select(p => new ProfileOption { Profile = p, Label = p.Display }));
            r.Options = opts;
            r.Option = sugg.Count > 0 ? opts[1] : opts[0];
            r.Similaires = string.Join(" | ", _matcher.SimilarObservations(r.Nom, lib, 2)
                .Select(m => $"{m.Observation.LibelleSource} ({_cat.LibraryOf(m.Observation.Bibliotheque)?.ProjetSource} {m.Observation.Reference})"));
        }
        _lw.GridControl.Items.Refresh();
    }

    private string Preview(IReadOnlyList<FinishRow> sel)
    {
        var lines = sel.Where(Writable).Select(r =>
            $"{r.Niveau} {r.Numero} {r.Nom} ← {r.Option?.Profile?.Id ?? (r.Source == DecisionSource.Ia ? "IA" : "saisie")} : Sol « {r.Sol} » / Mur « {r.Mur} » / Plafond « {r.Plafond} »" +
            (r.Etat.StartsWith("saisie") || r.Etat.StartsWith("retouch") ? (_overwrite.IsChecked == true ? "  [ÉCRASE la saisie manuelle]" : "  [saisie manuelle conservée]") : ""));
        var skipped = sel.Count(r => !Writable(r));
        return string.Join(Environment.NewLine, lines) + (skipped > 0 ? $"{Environment.NewLine}{skipped} ligne(s) sans finition ignorée(s)." : "");
    }

    private static bool Writable(FinishRow r) => FinishSummary.RoomSupports.Any(s => r.Get(s).Trim().Length > 0);

    public bool ShowDialog() => _lw.ShowDialog();

    internal IReadOnlyList<FinishRow> Rows => _rows;
    internal void RefreshPreview() => _lw.RefreshPreview();
    internal void Refresh() => _lw.GridControl.Items.Refresh();

    public IReadOnlyList<FinishAssignment> Selection =>
        _lw.Selection.Where(Writable).Select(r => new FinishAssignment
        {
            Room = r.Room,
            Profile = r.Option?.Profile,
            Values = FinishSummary.RoomSupports.ToDictionary(s => s, s => (string?)(r.Get(s).Trim().Length > 0 ? r.Get(s).Trim() : null)),
            FamilyCode = r.FamilyCode,
            Source = r.Source == DecisionSource.Ia && !r.Edited ? DecisionSource.Ia : DecisionSource.Utilisateur,
            Model = r.Model,
            Justification = r.Justification,
            Sources = r.Sources,
        }).ToList();

    public bool Overwrite => _overwrite.IsChecked == true;
    public string Author => string.IsNullOrWhiteSpace(_author.Text) ? Environment.UserName : _author.Text.Trim();
    public Window Window => _lw.Window;
}
