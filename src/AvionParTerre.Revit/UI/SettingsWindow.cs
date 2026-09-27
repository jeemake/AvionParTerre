using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Revit.Services;

namespace AvionParTerre.Revit.UI;

internal sealed class ResourceChoiceRow : Observable
{
    private string _choice = Auto;
    public const string Auto = "(automatique)";

    [Browsable(false)] public string Key { get; init; } = "";
    [DisplayName("Rôle")] public string Role { get; init; } = "";
    [DisplayName("Ressource utilisée")] public string Current { get; init; } = "";
    [Browsable(false)] public List<string> Options { get; init; } = new();
    [Browsable(false)] public string Choice { get => _choice; set => Set(ref _choice, string.IsNullOrWhiteSpace(value) ? Auto : value); }
}

/// <summary>Paramètres : mode avion par terre (OpenRouter), ressources imposées pour ce projet, normes relevées, à propos.</summary>
internal sealed class SettingsWindow
{
    private readonly Window _win;
    private readonly UserSettings _s;
    private readonly ProjectChoices _choices;
    private readonly PasswordBox _key = new() { Width = 420 };
    private readonly ComboBox _model = new() { Width = 520, IsEditable = true, IsTextSearchEnabled = false };
    private readonly TextBox _filter = new() { Width = 180, ToolTip = "Filtrer la liste des modèles" };
    private readonly TextBlock _keyState = new() { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox _web = new() { Content = "Recherche internet pour les locaux non classés (plugin « web » d'OpenRouter)" };
    private readonly TextBox _webN = new() { Width = 40 };
    private readonly TextBox _temp = new() { Width = 50 };
    private readonly CheckBox _confirm = new() { Content = "Montrer les décisions et demander confirmation avant d'écrire dans la maquette" };
    private readonly TextBox _author = new() { Width = 200 };
    private readonly Dictionary<string, CheckBox> _steps = new();
    private readonly List<ResourceChoiceRow> _rows;
    private readonly ComboBox _library;
    private readonly ComboBox _dce;
    private List<string> _allModels = new();
    public bool Saved { get; private set; }

    public static readonly string[] SuggestedModels =
    {
        "anthropic/claude-sonnet-5", "anthropic/claude-opus-5.5", "anthropic/claude-haiku-latest",
        "openai/gpt-5.5", "openai/gpt-5.4-mini", "google/gemini-2.5-pro", "mistralai/mistral-medium-3-5",
    };

    public SettingsWindow(UserSettings settings, ProjectChoices choices, List<ResourceChoiceRow> rows, IEnumerable<string> norms,
        FinishCatalogue cat, PluginProfile profile, string about)
    {
        _s = settings;
        _choices = choices;
        _rows = rows;
        _win = Theme.Window("Paramètres", 980, 720);
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = Theme.Header("Paramètres", "Mode avion par terre (IA via OpenRouter), ressources imposées et normes du projet ouvert.");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var close = Theme.Button("Fermer");
        close.Click += (_, _) => _win.Close();
        var save = Theme.Button("Enregistrer", primary: true);
        save.Click += (_, _) => Save();
        buttons.Children.Add(close);
        buttons.Children.Add(save);
        root.Children.Add(buttons);

        var tabs = new TabControl { FontFamily = Theme.Font };
        tabs.Items.Add(new TabItem { Header = "Mode avion par terre", Content = AiTab() });
        tabs.Items.Add(new TabItem { Header = "Ressources du projet", Content = ResourcesTab() });

        var libs = new List<Library?> { null };
        libs.AddRange(cat.Bibliotheques);
        _library = Controls.Combo(libs, l => l == null ? "(automatique — décidée par l'IA en mode avion par terre)" : l.Display,
            Math.Max(0, libs.FindIndex(l => l?.Code == choices.Bibliotheque)), 560);
        var dces = new List<DceProfile?> { null };
        dces.AddRange(profile.ProfilsDce);
        _dce = Controls.Combo(dces, d => d == null ? "(automatique — normes du projet si elles existent)" : d.Libelle,
            Math.Max(0, dces.FindIndex(d => d?.Code == choices.ProfilDce)), 560);
        tabs.Items.Add(new TabItem { Header = "Normes du projet", Content = NormsTab(norms) });
        tabs.Items.Add(new TabItem { Header = "À propos", Content = new TextBox { Text = about, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Consolas"), Margin = new Thickness(8), VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        root.Children.Add(tabs);
        _win.Content = root;
        Load();
    }

    private static TextBlock Label(string text, bool title = false) => new()
    {
        Text = text, Margin = new Thickness(0, title ? 14 : 8, 0, 3), FontWeight = FontWeights.Bold,
        Foreground = Theme.B(title ? Theme.Vert : Theme.Gris), FontSize = title ? 13 : 12, TextWrapping = TextWrapping.Wrap,
    };

    private static UIElement Row(params UIElement[] items)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) { if (i is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 0); sp.Children.Add(i); }
        return sp;
    }

    private UIElement AiTab()
    {
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Foreground = Theme.B(Theme.Noir),
            Text = "En mode avion par terre, le plugin enchaîne audit, données de pièces, finitions, plans, carnets, fiches et registre. " +
                   "Il ne remplace jamais ce que vous ou la maquette avez décidé ; le reste est décidé par le référentiel K&D, par des règles, " +
                   "puis par le modèle d'IA que vous choisissez sur OpenRouter. Chaque décision est tracée (journal JSON / CSV).",
        });
        sp.Children.Add(Label("Clé API OpenRouter", true));
        var link = new TextBlock();
        var h = new Hyperlink(new Run("Créer ou copier une clé sur openrouter.ai/keys")) { NavigateUri = new Uri("https://openrouter.ai/keys") };
        h.RequestNavigate += (_, e) => { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); e.Handled = true; };
        link.Inlines.Add(h);
        var test = Theme.Button("Tester la clé");
        test.Click += async (_, _) =>
        {
            var key = string.IsNullOrWhiteSpace(_key.Password) ? _s.ApiKey : _key.Password;
            _keyState.Foreground = Theme.B(Theme.Gris);
            _keyState.Text = "Vérification…";
            try
            {
                using var c = new OpenRouterClient(key, TimeSpan.FromSeconds(30));
                _keyState.Text = await c.CheckKeyAsync();
                _keyState.Foreground = Theme.B(Theme.Vert);
            }
            catch (Exception ex)
            {
                _keyState.Text = ex.Message;
                _keyState.Foreground = Theme.B(Theme.Rouge);
            }
        };
        sp.Children.Add(Row(_key, test));
        sp.Children.Add(_keyState);
        sp.Children.Add(link);

        sp.Children.Add(Label("Modèle (choisir dans la liste ou taper son identifiant OpenRouter)", true));
        var load = Theme.Button("Charger tous les modèles OpenRouter");
        load.Click += async (_, _) =>
        {
            load.IsEnabled = false;
            try
            {
                using var c = new OpenRouterClient(null, TimeSpan.FromSeconds(30));
                var list = await c.ListModelsAsync();
                _allModels = list.Select(m => m.Display).ToList();
                ApplyFilter();
                _keyState.Foreground = Theme.B(Theme.Gris);
                _keyState.Text = $"{list.Count} modèles disponibles sur OpenRouter.";
            }
            catch (Exception ex)
            {
                _keyState.Foreground = Theme.B(Theme.Rouge);
                _keyState.Text = ex.Message;
            }
            finally { load.IsEnabled = true; }
        };
        _filter.TextChanged += (_, _) => ApplyFilter();
        sp.Children.Add(Row(_model));
        sp.Children.Add(Row(new TextBlock { Text = "Filtre :", VerticalAlignment = VerticalAlignment.Center }, _filter, load));

        sp.Children.Add(Label("Décisions", true));
        sp.Children.Add(Row(_web, new TextBlock { Text = "résultats :", VerticalAlignment = VerticalAlignment.Center }, _webN));
        sp.Children.Add(Row(new TextBlock { Text = "Température :", VerticalAlignment = VerticalAlignment.Center }, _temp,
            new TextBlock { Text = "(0 = décisions stables et reproductibles)", Foreground = Theme.B(Theme.Gris), VerticalAlignment = VerticalAlignment.Center }));
        sp.Children.Add(_confirm);
        sp.Children.Add(Row(new TextBlock { Text = "Validé par :", VerticalAlignment = VerticalAlignment.Center }, _author));

        sp.Children.Add(Label("Étapes du mode avion par terre", true));
        var wrap = new WrapPanel();
        foreach (var (k, l) in new[] { ("DonneesPieces", "Données de pièces (Niv, HSP, HSD)"), ("Finitions", "Finitions"), ("Plans", "Plans généraux"),
                     ("Carnets", "Carnets de pièces"), ("Fiches", "Fiches menuiseries"), ("Registre", "Registre"), ("ExportPdf", "Export PDF (brouillon)") })
        {
            var cb = new CheckBox { Content = l, Margin = new Thickness(0, 2, 16, 2) };
            _steps[k] = cb;
            wrap.Children.Add(cb);
        }
        sp.Children.Add(wrap);
        return new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void ApplyFilter()
    {
        var f = _filter.Text.Trim();
        var src = _allModels.Count > 0 ? _allModels : _s.ModelesRecents.Concat(SuggestedModels).Distinct().ToList();
        var text = _model.Text;
        _model.ItemsSource = src.Where(m => f.Length == 0 || m.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        _model.Text = text;
    }

    private UIElement ResourcesTab()
    {
        var dp = new DockPanel { Margin = new Thickness(10) };
        var info = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
            Text = "Ressource utilisée : ce que le plugin trouve dans la maquette ouverte (nom du profil, même famille rechargée, ou ressource équivalente disponible). " +
                   "« Imposer » fixe la ressource pour ce projet uniquement (choix-projet.json dans le dossier de sorties).",
        };
        DockPanel.SetDock(info, Dock.Top);
        dp.Children.Add(info);
        var g = Theme.Grid(_rows);
        g.AutoGeneratedColumns += (_, _) => g.Columns.Add(Theme.EditableComboColumn("Imposer", nameof(ResourceChoiceRow.Choice), nameof(ResourceChoiceRow.Options), 380));
        dp.Children.Add(g);
        return dp;
    }

    private UIElement NormsTab(IEnumerable<string> norms)
    {
        var sp = new StackPanel { Margin = new Thickness(10) };
        sp.Children.Add(Label("Normes relevées dans la maquette ouverte (elles l'emportent sur les valeurs par défaut du profil)", true));
        sp.Children.Add(new TextBox
        {
            Text = string.Join(Environment.NewLine, norms), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 260,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new FontFamily("Consolas"), Background = Theme.B(Theme.GrisClair),
        });
        sp.Children.Add(Label("Bibliothèque de finitions de référence pour ce projet", true));
        sp.Children.Add(_library);
        sp.Children.Add(Label("Profil documentaire des plans généraux", true));
        sp.Children.Add(_dce);
        return sp;
    }

    private void Load()
    {
        _keyState.Text = _s.HasKey ? "Une clé est enregistrée (chiffrée pour votre compte Windows). Laisser vide pour la conserver." : "Aucune clé enregistrée : le mode avion par terre fonctionnera sans IA (référentiel et règles uniquement).";
        _keyState.Foreground = Theme.B(_s.HasKey ? Theme.Vert : Theme.Rouge);
        ApplyFilter();
        _model.Text = _s.Modele;
        _web.IsChecked = _s.RechercheWeb;
        _webN.Text = _s.ResultatsWeb.ToString(CultureInfo.InvariantCulture);
        _temp.Text = _s.Temperature.ToString("0.0", CultureInfo.InvariantCulture);
        _confirm.IsChecked = _s.ConfirmerAvantEcriture;
        _author.Text = _s.AuthorOrUser;
        var st = _s.Etapes;
        _steps["DonneesPieces"].IsChecked = st.DonneesPieces;
        _steps["Finitions"].IsChecked = st.Finitions;
        _steps["Plans"].IsChecked = st.Plans;
        _steps["Carnets"].IsChecked = st.Carnets;
        _steps["Fiches"].IsChecked = st.Fiches;
        _steps["Registre"].IsChecked = st.Registre;
        _steps["ExportPdf"].IsChecked = st.ExportPdf;
        foreach (var r in _rows) r.Choice = _choices.Ressources.TryGetValue(r.Key, out var v) ? v : ResourceChoiceRow.Auto;
    }

    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(_key.Password)) _s.ApiKey = _key.Password;
        var model = (_model.Text ?? "").Split("  —")[0].Trim();
        if (model.Length > 0) _s.Modele = model;
        _s.RechercheWeb = _web.IsChecked == true;
        if (int.TryParse(_webN.Text, out var n)) _s.ResultatsWeb = Math.Clamp(n, 1, 10);
        if (double.TryParse(_temp.Text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var t)) _s.Temperature = Math.Clamp(t, 0, 1.5);
        _s.ConfirmerAvantEcriture = _confirm.IsChecked == true;
        _s.Auteur = string.IsNullOrWhiteSpace(_author.Text) ? null : _author.Text.Trim();
        _s.Etapes = new AutopilotSteps
        {
            DonneesPieces = _steps["DonneesPieces"].IsChecked == true,
            Finitions = _steps["Finitions"].IsChecked == true,
            Plans = _steps["Plans"].IsChecked == true,
            Carnets = _steps["Carnets"].IsChecked == true,
            Fiches = _steps["Fiches"].IsChecked == true,
            Registre = _steps["Registre"].IsChecked == true,
            ExportPdf = _steps["ExportPdf"].IsChecked == true,
        };
        _choices.Ressources.Clear();
        foreach (var r in _rows.Where(r => r.Choice != ResourceChoiceRow.Auto && !string.IsNullOrWhiteSpace(r.Choice)))
            _choices.Ressources[r.Key] = r.Choice.Trim();
        _choices.Bibliotheque = Controls.Value<Library>(_library)?.Code;
        _choices.ProfilDce = Controls.Value<DceProfile>(_dce)?.Code;
        Saved = true;
        _win.Close();
    }

    public Window Window => _win;

    public bool ShowDialog()
    {
        _win.ShowDialog();
        return Saved;
    }
}
