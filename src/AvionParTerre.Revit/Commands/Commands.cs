using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
using AvionParTerre.Core;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Revit.Services;
using AvionParTerre.Revit.UI;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Units = AvionParTerre.Revit.Services.Units;

namespace AvionParTerre.Revit.Commands;

// ---------------------------------------------------------------------------------------------
// Audit
// ---------------------------------------------------------------------------------------------

internal sealed class IssueRow : SelectableRow
{
    public IssueRow(Issue i) => Issue = i;
    [Browsable(false)] public Issue Issue { get; }
    [DisplayName("Gravité")] public string Gravite => Issue.GraviteLibelle;
    [DisplayName("Règle")] public string Regle => Issue.Regle;
    [DisplayName("Objet")] public string Objet => Issue.Objet;
    [DisplayName("Constat")] public string Message => Issue.Message;
    [DisplayName("Action attendue")] public string Action => Issue.Action;
}

[Transaction(TransactionMode.Manual)]
public sealed class AuditCommand : CommandBase
{
    protected override string Title => "Audit";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var issues = new AuditService(doc, data).Run();
        Directory.CreateDirectory(data.OutputDir);
        Json.Save(Path.Combine(data.OutputDir, "audit-report.json"), new { schema = "avion-par-terre/audit-report", schema_version = 1, document = doc.Title, date = DateTime.Now, anomalies = issues });

        var counts = string.Join(" · ", issues.GroupBy(i => i.GraviteLibelle).Select(g => $"{g.Key} : {g.Count()}"));
        var rows = issues.Select(i => new IssueRow(i)).ToList();
        var lw = new ListWindow<IssueRow>("Audit de la maquette", $"{issues.Count} anomalie(s) — {counts}. Lecture seule : aucune écriture dans la maquette.", rows, "", null);
        lw.GridControl.IsReadOnly = true;
        List<ElementId>? toShow = null;
        var show = Theme.Button("Afficher dans Revit (lignes cochées)");
        show.Click += (_, _) =>
        {
            var ids = lw.Selection.Select(r => r.Issue.ElementId).Where(i => i.HasValue).Select(i => new ElementId(i!.Value)).Distinct().ToList();
            if (ids.Count == 0 && lw.GridControl.SelectedItem is IssueRow r && r.Issue.ElementId.HasValue) ids.Add(new ElementId(r.Issue.ElementId.Value));
            if (ids.Count == 0) { MessageBox.Show(lw.Window, "Cocher une ou plusieurs anomalies liées à un élément.", "Avion par terre"); return; }
            toShow = ids;
            lw.Window.Close();
        };
        var csv = Theme.Button("Exporter CSV");
        csv.Click += (_, _) =>
        {
            var path = Path.Combine(data.OutputDir, "audit-report.csv");
            var sb = new StringBuilder("Gravité;Règle;Objet;Constat;Action;Id\r\n");
            foreach (var i in issues)
                sb.AppendLine(string.Join(";", new[] { i.GraviteLibelle, i.Regle, i.Objet, i.Message, i.Action, i.ElementId?.ToString() ?? "" }.Select(DocumentRegister.Csv)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            MessageBox.Show(lw.Window, $"Exporté : {path}", "Avion par terre");
        };
        lw.ExtraButtons.Children.Add(show);
        lw.ExtraButtons.Children.Add(csv);
        lw.ShowDialog();

        var valid = toShow?.Where(id => doc.GetElement(id) != null).ToList();
        if (valid is { Count: > 0 })
        {
            uidoc.Selection.SetElementIds(valid);
            try { uidoc.ShowElements(valid); } catch (Autodesk.Revit.Exceptions.ApplicationException) { }
        }
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Finitions
// ---------------------------------------------------------------------------------------------

[Transaction(TransactionMode.Manual)]
public sealed class FinitionsCommand : CommandBase
{
    protected override string Title => "Finitions";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var rows = BuildRows(doc, data);
        if (rows.Count == 0)
        {
            TaskDialog.Show("Avion par terre", "Aucune pièce placée dans la maquette.");
            return Result.Cancelled;
        }

        var service = new FinishService(doc, data.Catalogue);
        var win = new FinishesWindow(data.Catalogue, rows, data.Choices.Bibliotheque, data.Settings.AuthorOrUser);
        win.AskAi = (sel, lib) => AskAi(doc, data, win, sel, lib);
        win.ExportGrid = _ =>
        {
            var p = service.ExportGrid(Path.Combine(data.OutputDir, "grille-finitions.csv"));
            MessageBox.Show(win.Window, $"Grille exportée (pièces validées uniquement) :\n{p}", "Avion par terre");
        };
        if (!win.ShowDialog()) return Result.Cancelled;

        var report = new Report("Finitions — affectations validées");
        var w = new WarningCollector();
        service.Apply(win.Selection, win.Overwrite, win.Author, report, w);
        report.RevitWarnings.AddRange(w.Warnings);
        SaveLog(data, report);
        Show(report);
        return Result.Succeeded;
    }

    /// <summary>Décision IA des lignes cochées (classement, profil, finitions manquantes), sans écriture : l'utilisateur valide ensuite.</summary>
    private static void AskAi(Document doc, PluginData data, FinishesWindow win, IReadOnlyList<FinishRow> rows, string? library)
    {
        var s = data.Settings;
        if (!s.HasKey)
        {
            MessageBox.Show(win.Window, "Aucune clé OpenRouter : la renseigner dans Avion par terre > Paramètres.", "Avion par terre", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var inputs = rows.Select(r => new Core.Ai.RoomInput
        {
            Key = r.Room.UniqueId, Name = r.Nom, Level = r.Niveau, AreaM2 = Units.ToM2(r.Room.Area), FamilyCode = r.FamilyCode,
            CandidateProfiles = r.Options.Where(o => o.Profile != null).Take(3).Select(o => o.Profile!.Id).ToList(),
            Known = FinishSummary.RoomSupports.Select(x => (x, v: FinishService.Current(r.Room, x))).Where(x => x.v.Length > 0 && FinishService.ManualValues(r.Room).ContainsKey(x.x))
                .ToDictionary(x => x.x, x => x.v),
        }).ToList();
        var log = new Core.Ai.DecisionLog { Document = doc.Title, Modele = s.Modele };
        var ctx = new Core.Ai.ProjectContext { Document = doc.Title, RoomNames = rows.Select(r => r.Nom).Distinct().ToList() };
        try
        {
            var (advice, errors) = new ProgressWindow("Finitions — décision par l'IA").RunAsync(async (p, ct) =>
            {
                using var client = new Core.Ai.OpenRouterClient(s.ApiKey);
                var advisor = new Core.Ai.AutopilotAdvisor(client, data.Catalogue, data.Profile.Menuiseries, s.AiOptions(), log);
                var a = await advisor.AdviseRoomsAsync(inputs, library, ctx, p, ct);
                return (a, advisor.Errors);
            });
            var byKey = advice.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var r in rows)
                if (byKey.TryGetValue(r.Room.UniqueId, out var a)) r.ApplyAdvice(a, data.Catalogue, s.Modele);
            win.Refresh();
            win.RefreshPreview();
            var msg = $"{byKey.Count} pièce(s) décidée(s) par {s.Modele} — {Core.Ai.AutopilotAdvisor.Money(log.CoutUsd)}. Vérifier puis « Valider et écrire »." +
                      (errors.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, errors.Take(8)) : "");
            MessageBox.Show(win.Window, msg, "Avion par terre");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            MessageBox.Show(win.Window, ex.Message, "Avion par terre — IA", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal static List<FinishRow> BuildRows(Document doc, PluginData data)
    {
        var cls = new RoomClassifier(data.Catalogue.FamillesLocaux);
        return RevitUtil.Rooms(doc).Where(RevitUtil.IsPlaced)
            .OrderBy(r => (doc.GetElement(r.LevelId) as Level)?.Elevation ?? 0).ThenBy(r => r.Number)
            .Select(r =>
        {
            var c = cls.Classify(RevitUtil.RoomName(r));
            var rec = FinishService.Record(r);
            var fam = rec?.FamilleLocal ?? c?.FamilyCode;
            return new FinishRow(r, data.Catalogue)
            {
                Niveau = (doc.GetElement(r.LevelId) as Level)?.Name ?? "",
                Numero = r.Number,
                Nom = RevitUtil.RoomName(r),
                FamilyCode = fam,
                Famille = fam == null ? "à classer" : data.Catalogue.Family(fam)?.Libelle ?? fam,
                Actuel = string.Join(" / ", FinishSummary.RoomSupports.Select(s => FinishService.Current(r, s)).Select(v => string.IsNullOrEmpty(v) ? "—" : v)),
                Etat = FinishService.State(r),
            };
        }).ToList();
    }
}

// ---------------------------------------------------------------------------------------------
// Plans généraux
// ---------------------------------------------------------------------------------------------

internal sealed class LevelRow : SelectableRow
{
    [Browsable(false)] public Level Level { get; init; } = null!;
    [DisplayName("Niveau")] public string Nom => Level.Name;
    [DisplayName("Altitude (m)")] public string Altitude => (Units.ToMm(Level.Elevation) / 1000).ToString("0.00");
    [DisplayName("Pièces placées")] public int Pieces { get; init; }
    [DisplayName("Plan DCE")] public string Existant { get; init; } = "";
}

[Transaction(TransactionMode.Manual)]
public sealed class PlansCommand : CommandBase
{
    protected override string Title => "Plans généraux";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var idx = Identity.Index(doc);
        var norms = ProjectNorms.Detect(doc, data);
        var rooms = RevitUtil.Rooms(doc).Where(RevitUtil.IsPlaced).ToList();
        var levels = RevitUtil.Levels(doc).ToList();
        var rows = levels.Select(l => new LevelRow
        {
            Level = l,
            Pieces = rooms.Count(r => r.LevelId == l.Id),
            Existant = idx.TryGetValue(PlanGenerator.SheetKey(l), out var s) ? $"{((ViewSheet)s).SheetNumber} {((ViewSheet)s).Name}"
                : norms.PlanSheetByLevel.TryGetValue(l.Id, out var a) ? $"{a.SheetNumber} {a.Name} (agence)" : "—",
            Selected = rooms.Any(r => r.LevelId == l.Id) && (idx.ContainsKey(PlanGenerator.SheetKey(l)) || !norms.PlanSheetByLevel.ContainsKey(l.Id)),
        }).ToList();

        // Profils : normes du projet (plans existants) en tête, puis profils du référentiel
        var profiles = new List<DceProfile>();
        var fromNorms = norms.PlanProfile(doc, new KdResources(doc, data), data.Profile.ProfilsDce[0]);
        if (fromNorms != null) profiles.Add(fromNorms);
        profiles.AddRange(data.Profile.ProfilsDce);
        var preferred = data.Choices.ProfilDce is { } code ? profiles.FindIndex(p => p.Code == code) : -1;
        ComboBox? profileCombo = null;
        DceProfile Dce() => Controls.Value<DceProfile>(profileCombo!) ?? profiles[0];
        List<PlanRequest> Requests(IEnumerable<LevelRow> sel) =>
            sel.OrderBy(r => r.Level.Elevation).Select(r => new PlanRequest { Level = r.Level, Index = levels.IndexOf(r.Level), Force = true }).ToList();

        var lw = new ListWindow<LevelRow>("Plans généraux DCE",
            "Une vue par niveau avec gabarit APD-DCE et étiquettes K&D, sur la grande feuille des normes du projet. Les niveaux déjà couverts par un plan de l'agence sont décochés.",
            rows, "Générer / mettre à jour", sel => new PlanGenerator(doc, data, Dce(), norms).Preview(Requests(sel)));
        profileCombo = Controls.Combo(profiles, p => p.Libelle, Math.Max(0, preferred), 460);
        lw.Options.Children.Add(Controls.Labeled("Profil documentaire", profileCombo));
        if (!lw.ShowDialog()) return Result.Cancelled;

        var report = new Report($"Plans généraux — {Dce().Code}");
        var w = new WarningCollector();
        using (var tg = new TransactionGroup(doc, "Avion par terre — plans généraux"))
        {
            tg.Start();
            new PlanGenerator(doc, data, Dce(), norms).Run(Requests(lw.Selection), report, w);
            tg.Assimilate();
        }
        report.RevitWarnings.AddRange(w.Warnings);
        SaveLog(data, report);
        Show(report);
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Carnets de pièces
// ---------------------------------------------------------------------------------------------

internal sealed class RoomRow : SelectableRow
{
    [Browsable(false)] public Room Room { get; init; } = null!;
    [DisplayName("Niveau")] public string Niveau { get; init; } = "";
    [DisplayName("N°")] public string Numero => Room.Number;
    [DisplayName("Local")] public string Nom => RevitUtil.RoomName(Room);
    [DisplayName("Famille")] public string Famille { get; init; } = "";
    [DisplayName("Surface (m²)")] public string Surface => Units.ToM2(Room.Area).ToString("0.00");
    [DisplayName("Carnet")] public string Carnet { get; init; } = "";
    [DisplayName("État")] public string Etat { get; init; } = "";
}

[Transaction(TransactionMode.Manual)]
public sealed class CarnetsCommand : CommandBase
{
    protected override string Title => "Carnets de pièces";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var idx = Identity.Index(doc);
        var cls = new RoomClassifier(data.Catalogue.FamillesLocaux);
        var proposed = data.Profile.Carnets.FamillesProposees.ToHashSet();
        var preselected = uidoc.Selection.GetElementIds().Select(doc.GetElement).OfType<Room>().Select(r => r.Id).ToHashSet();
        var rows = RevitUtil.Rooms(doc).Where(RevitUtil.IsPlaced)
            .OrderBy(r => (doc.GetElement(r.LevelId) as Level)?.Elevation ?? 0).ThenBy(r => r.Number)
            .Select(r =>
            {
                var c = cls.Classify(RevitUtil.RoomName(r));
                var g = CarnetGenerator.ExistingGroup(idx, r);
                return new RoomRow
                {
                    Room = r,
                    Niveau = (doc.GetElement(r.LevelId) as Level)?.Name ?? "",
                    Famille = c == null ? "à classer" : data.Catalogue.Family(c.FamilyCode)?.Libelle ?? c.FamilyCode,
                    Carnet = g.HasValue ? $"D{g}" : "—",
                    Etat = RevitUtil.IsEnclosed(r) ? FinishService.State(r) : "non fermée",
                    Selected = RevitUtil.IsEnclosed(r) && !g.HasValue &&
                               (preselected.Count > 0 ? preselected.Contains(r.Id) : c != null && proposed.Contains(c.FamilyCode)),
                };
            }).ToList();

        ComboBox? profileCombo = null;
        DceProfile Dce() => Controls.Value<DceProfile>(profileCombo!) ?? data.Profile.ProfilsDce[0];
        var lw = new ListWindow<RoomRow>("Carnets de pièces",
            "Plan agrandi, vue 3D découpée, tableau de pièce et élévations a–d sur A3 (Dn.00, Dn.01…). Proposition : sanitaires, cuisines, habitation, accueil — ou la sélection Revit.",
            rows, "Générer les carnets", sel => new CarnetGenerator(doc, data, Dce()).Preview(sel.Select(r => r.Room)));
        profileCombo = Controls.Combo(data.Profile.ProfilsDce, p => p.Libelle, 0, 420);
        lw.Options.Children.Add(Controls.Labeled("Profil documentaire", profileCombo));
        if (!lw.ShowDialog()) return Result.Cancelled;

        var report = new Report("Carnets de pièces");
        var w = new WarningCollector();
        using (var tg = new TransactionGroup(doc, "Avion par terre — carnets de pièces"))
        {
            tg.Start();
            new CarnetGenerator(doc, data, Dce()).Run(lw.Selection.Select(r => r.Room).ToList(), report, w);
            tg.Assimilate();
        }
        report.RevitWarnings.AddRange(w.Warnings);
        SaveLog(data, report);
        Show(report);
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Fiches menuiseries
// ---------------------------------------------------------------------------------------------

internal sealed class JoineryRow : SelectableRow
{
    [Browsable(false)] public JoineryType Type { get; init; } = null!;
    [DisplayName("Repère")] public string Repere => Type.Mark;
    [DisplayName("Famille")] public string Famille => Type.Symbol.FamilyName;
    [DisplayName("Catégorie")] public string Categorie => Type.Symbol.Category?.Name ?? "";
    [DisplayName("Lot")] public string Lot => Type.Lot == null ? "à classer" : Type.LotSource.StartsWith("préfixe approché") ? $"{Type.Lot} (probable)" : Type.Lot;
    [DisplayName("Qté")] public int Quantite => Type.Instances.Count;
    [DisplayName("Vue de dessin K&D")] public string Dessin => Type.Drafting == null ? "—" : Type.DraftingSheet != null ? $"sur {Type.DraftingSheet}" : "disponible";
    [DisplayName("Fiche")] public string Fiche => FicheGenerator.ExistingLabel(Type) ?? "—";
    [DisplayName("Alerte")] public string Alerte => Type.Ambiguous ? "repère porté par plusieurs familles" : "";
}

[Transaction(TransactionMode.Manual)]
public sealed class FichesCommand : CommandBase
{
    protected override string Title => "Fiches menuiseries";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var gen = new FicheGenerator(doc, data);
        var rows = gen.Collect().Select(t => new JoineryRow
        {
            Type = t,
            Selected = t.Lot != null && FicheGenerator.ExistingLabel(t) == null,
        }).ToList();
        if (rows.Count == 0)
        {
            TaskDialog.Show("Avion par terre", "Aucune porte ni fenêtre placée dans la maquette.");
            return Result.Cancelled;
        }
        var lw = new ListWindow<JoineryRow>("Fiches menuiseries",
            "Une fiche A3 par type : vue de dessin K&D du même nom si elle existe, sinon élévation + plan (gabarits Calepin Baies), localisations et quantités.",
            rows, "Générer / mettre à jour", sel => string.Join(Environment.NewLine, sel.Select(r =>
                r.Type.Lot == null ? $"{r.Repere} : lot inconnu — ignoré"
                : r.Type.Fiche != null ? $"{r.Repere} : fiche {r.Fiche} existante — bloc descriptif mis à jour si non retouché"
                : r.Type.AgencySheet != null ? $"{r.Repere} : fiche de l'agence {r.Fiche} — conservée, non dupliquée"
                : r.Type.DraftingSheet != null ? $"{r.Repere} : vue de dessin déjà sur {r.Type.DraftingSheet} — non dupliquée"
                : $"{r.Repere} : nouvelle fiche {r.Type.Lot} ({(r.Type.Drafting != null ? "vue de dessin K&D" : "vues générées")}), {r.Quantite} u")));
        if (!lw.ShowDialog()) return Result.Cancelled;

        var report = new Report("Fiches menuiseries");
        var w = new WarningCollector();
        using (var tg = new TransactionGroup(doc, "Avion par terre — fiches menuiseries"))
        {
            tg.Start();
            gen.Run(lw.Selection.Select(r => r.Type).ToList(), report, w);
            tg.Assimilate();
        }
        report.RevitWarnings.AddRange(w.Warnings);
        SaveLog(data, report);
        Show(report);
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Registre
// ---------------------------------------------------------------------------------------------

internal sealed class RegisterRow : SelectableRow
{
    [Browsable(false)] public RegisterEntry Entry { get; init; } = null!;
    [DisplayName("Famille")] public string Famille => Entry.Famille;
    [DisplayName("N°")] public string Numero => Entry.Numero;
    [DisplayName("Titre")] public string Titre => Entry.Titre;
    [DisplayName("Format")] public string Format => Entry.Format ?? "";
    [DisplayName("Échelles")] public string Echelles => Entry.Echelles ?? "";
    [DisplayName("État")] public string Etat => DocumentRegister.EtatLabel(Entry.Etat);
    [DisplayName("Anomalies")] public string Anomalies => string.Join(" ; ", Entry.Anomalies);
}

[Transaction(TransactionMode.Manual)]
public sealed class RegistreCommand : CommandBase
{
    protected override string Title => "Registre";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var audit = new AuditService(doc, data).Run();
        var svc = new RegisterService(doc, data);
        var reg = svc.Build(audit);
        var (json, csv) = svc.Save(reg);
        var rows = reg.Documents.OrderBy(d => d.Famille).ThenBy(d => d.Numero, StringComparer.Ordinal).Select(d => new RegisterRow { Entry = d }).ToList();
        var byState = string.Join(" · ", reg.Documents.GroupBy(d => d.Etat).Select(g => $"{DocumentRegister.EtatLabel(g.Key)} : {g.Count()}"));
        var lw = new ListWindow<RegisterRow>("Registre documentaire", $"{reg.Documents.Count} feuille(s) — {byState}. Enregistré : {json}", rows, "", null);
        lw.GridControl.IsReadOnly = true;
        var open = Theme.Button("Ouvrir le dossier");
        open.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{data.OutputDir}\"") { UseShellExecute = true });
        lw.ExtraButtons.Children.Add(open);
        lw.ShowDialog();
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Export PDF
// ---------------------------------------------------------------------------------------------

internal sealed class SheetRow : SelectableRow
{
    [Browsable(false)] public ViewSheet Sheet { get; init; } = null!;
    [DisplayName("N°")] public string Numero => Sheet.SheetNumber;
    [DisplayName("Titre")] public string Titre => Sheet.Name;
    [DisplayName("Famille")] public string Famille { get; init; } = "";
    [DisplayName("Cartouche")] public string Cartouche { get; init; } = "";
}

[Transaction(TransactionMode.Manual)]
public sealed class ExportCommand : CommandBase
{
    protected override string Title => "Export PDF";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var roles = Identity.All(doc).Where(x => x.Element is ViewSheet).ToDictionary(x => x.Element.Id, x => x.Data.Role);
        var rows = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.Ordinal)
            .Select(s => new SheetRow
            {
                Sheet = s,
                Famille = RegisterService.FamilyOf(roles.TryGetValue(s.Id, out var r) ? r : ""),
                Cartouche = RevitUtil.TitleBlockOf(doc, s)?.Name ?? "—",
                Selected = roles.ContainsKey(s.Id),
            }).ToList();

        var validated = new CheckBox { Content = "Émission validée par le responsable projet (sinon : brouillon)", VerticalAlignment = VerticalAlignment.Center };
        var author = new TextBox { Width = 140, Text = Environment.UserName };
        var lw = new ListWindow<SheetRow>("Export PDF",
            "Une feuille = un PDF au format réel du cartouche, manifeste avec empreintes SHA-256. Aucune diffusion externe n'est effectuée.",
            rows, "Exporter", sel => $"{sel.Count} feuille(s) → {Path.Combine(data.OutputDir, "PDF")}" + (validated.IsChecked == true ? " — ÉMISSION VALIDÉE" : " — BROUILLON"));
        lw.Options.Children.Add(validated);
        lw.Options.Children.Add(Controls.Labeled("   Responsable", author));
        if (!lw.ShowDialog()) return Result.Cancelled;

        var isValidated = validated.IsChecked == true;
        if (isValidated)
        {
            var audit = new AuditService(doc, data).Run();
            var blocking = audit.Count(i => i.Gravite is Severity.Bloquant or Severity.EmissionBloquee);
            if (blocking > 0)
            {
                var td = new TaskDialog("Avion par terre")
                {
                    MainInstruction = $"{blocking} anomalie(s) bloquent l'émission",
                    MainContent = "Lancer l'audit pour les consulter. Exporter quand même comme BROUILLON ?",
                    CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No,
                };
                if (td.Show() != TaskDialogResult.Yes) return Result.Cancelled;
                isValidated = false;
            }
        }
        var folder = Path.Combine(data.OutputDir, "PDF", DateTime.Now.ToString("yyyyMMdd-HHmm") + (isValidated ? "_EMISSION" : "_BROUILLON"));
        var report = new Report("Export PDF");
        new PdfExporter(doc, data).Export(lw.Selection.Select(r => r.Sheet).ToList(), folder, isValidated, author.Text.Trim(), report);
        SaveLog(data, report);
        report.OutputFolder = folder;
        Show(report);
        return Result.Succeeded;
    }
}

// ---------------------------------------------------------------------------------------------
// Référentiel
// ---------------------------------------------------------------------------------------------

[Transaction(TransactionMode.Manual)]
public sealed class ReferentielCommand : CommandBase
{
    protected override string Title => "Référentiel";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var c = data.Catalogue;
        var sb = new StringBuilder();
        sb.AppendLine($"Profil documentaire : {data.ProfilePath} (v{data.Profile.Version})");
        sb.AppendLine($"Catalogue de finitions : {data.CataloguePath} (v{c.Version}, {c.Date})");
        sb.AppendLine($"Sorties du projet : {data.OutputDir}");
        sb.AppendLine();
        sb.AppendLine(c.Avertissement);
        sb.AppendLine();
        sb.AppendLine($"{c.Finitions.Count} finitions normalisées · {c.Profils.Count} profils de référence · {c.Observations.Count} lignes observées");
        foreach (var b in c.Bibliotheques)
        {
            sb.AppendLine();
            sb.AppendLine($"{b.Code} — {b.Display}");
            sb.AppendLine($"  Source : {b.Fichier}" + (b.Sha256 != null ? $" (sha256 {b.Sha256[..12]}…)" : ""));
            if (b.Alerte != null) sb.AppendLine($"  Alerte : {b.Alerte}");
            foreach (var p in c.Profils.Where(p => p.Bibliotheque == b.Code))
            {
                var s = FinishSummary.ForRoom(c, p.Applications);
                sb.AppendLine($"  {p.Id,-8} {p.Libelle} [{c.Family(p.FamilleLocal)?.Libelle}] — Sol : {s["Sol"] ?? "ND"} | Mur : {s["Mur"] ?? "ND"} | Plafond : {s["Plafond"] ?? "ND"}");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Pour adapter le référentiel à un projet : copier profile.json et/ou catalogue.json dans");
        sb.AppendLine($"  {PluginData.ProjectDataDir(uidoc.Document)}");
        Directory.CreateDirectory(data.OutputDir);
        ReportWindow.Show("Référentiel K&D", sb.ToString(), Path.GetDirectoryName(data.CataloguePath));
        return Result.Succeeded;
    }
}
