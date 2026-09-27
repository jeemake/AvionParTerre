using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using TextBox = System.Windows.Controls.TextBox;
using AvionParTerre.Core.Ai;
using AvionParTerre.Revit.Services;
using AvionParTerre.Revit.UI;

namespace AvionParTerre.Revit.Commands;

internal sealed class DecisionRow : SelectableRow
{
    [Browsable(false)] public Decision D { get; init; } = null!;
    [DisplayName("Domaine")] public string Domaine => D.Domaine;
    [DisplayName("Objet")] public string Objet => D.Objet;
    [DisplayName("Décision")] public string Choix => D.Choix;
    [DisplayName("Source")] public string Source => D.SourceLibelle;
    [DisplayName("Justification")] public string Justification => D.Justification ?? "";
    [DisplayName("Sources consultées")] public string Sources => string.Join(" ", D.Sources);
}

/// <summary>
/// Mode « avion par terre » : production complète du dossier DCE en une commande. L'utilisateur et la maquette décident en premier ;
/// le reste est décidé par le référentiel K&amp;D, les règles, puis le modèle d'IA choisi sur OpenRouter. Tout est journalisé
/// et exécuté dans un seul groupe de transactions (Ctrl+Z annule l'ensemble).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class AutopilotCommand : CommandBase
{
    protected override string Title => "Mode avion par terre";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var s = data.Settings;
        if (!s.HasKey)
        {
            var td = new TaskDialog("Avion par terre")
            {
                MainInstruction = "Aucune clé OpenRouter",
                MainContent = "Sans clé, le mode avion par terre décide avec le référentiel K&D et les règles automatiques uniquement : " +
                              "les locaux non classés et les prescriptions de menuiseries resteront « à définir ».",
            };
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Ouvrir les paramètres", "Saisir la clé et choisir le modèle.");
            td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Continuer sans IA");
            td.CommonButtons = TaskDialogCommonButtons.Cancel;
            switch (td.Show())
            {
                case TaskDialogResult.CommandLink1:
                    return new SettingsCommand().Run(uidoc, data);
                case TaskDialogResult.CommandLink2:
                    break;
                default:
                    return Result.Cancelled;
            }
        }

        // 1. Analyse (lecture seule)
        var ap = new Autopilot(doc, data);
        ap.Analyze();
        var selectedSteps = AvionParTerre.Core.Json.Serialize(ap.Steps);
        if (!ConfirmLaunch(ap)) return Result.Cancelled;
        if (selectedSteps != AvionParTerre.Core.Json.Serialize(ap.Steps))
            ap.Analyze(); // Rebuild only if the launch dialog changed the requested work.

        // 2. Consultation de l'IA (hors Revit)
        if (ap.UsesAi)
        {
            try
            {
                new ProgressWindow("Mode avion par terre — consultation de l'IA").RunAsync(async (p, ct) =>
                {
                    using var client = new OpenRouterClient(s.ApiKey);
                    await ap.ConsultAsync(client, p, ct);
                    return true;
                });
            }
            catch (OperationCanceledException)
            {
                return Result.Cancelled;
            }
            catch (LlmException ex)
            {
                ap.AiErrors.Add(ex.Message);
            }
        }
        ap.Decide();

        // 3. Revue des décisions puis exécution
        if (s.ConfirmerAvantEcriture && !Review(ap)) { var r0 = new Report("Mode avion par terre — annulé"); ap.SaveLog(r0); return Result.Cancelled; }

        var report = new Report("Mode avion par terre");
        var w = new WarningCollector();
        var progress = new ProgressWindow("Mode avion par terre — production");
        progress.Show();
        try
        {
            using var tg = new TransactionGroup(doc, "Avion par terre — mode automatique");
            tg.Start();
            ap.Execute(report, w, progress.Step, () => progress.Cancelled);
            tg.Assimilate();
            if (ap.Steps.Registre && !progress.Cancelled)
            {
                progress.Step("Registre documentaire…");
                var svc = new RegisterService(doc, data);
                var (json, _) = svc.Save(svc.Build(new AuditService(doc, data).Run()));
                report.Created.Add($"Registre documentaire : {json}");
            }
            if (ap.Steps.ExportPdf && !progress.Cancelled)
            {
                progress.Step("Export PDF (brouillon)…");
                var sheets = Identity.All(doc).Select(x => x.Element).OfType<ViewSheet>().OrderBy(x => x.SheetNumber, StringComparer.Ordinal).ToList();
                var folder = Path.Combine(data.OutputDir, "PDF", DateTime.Now.ToString("yyyyMMdd-HHmm") + "_BROUILLON");
                new PdfExporter(doc, data).Export(sheets, folder, false, s.AuthorOrUser, report);
            }
        }
        finally
        {
            progress.Close();
        }
        report.Notes.Add($"Décisions : {ap.Log.Summary()}");
        if (ap.UsesAi) report.Notes.Add($"IA : {s.Modele} — {ap.Log.JetonsEntree + ap.Log.JetonsSortie} jetons, coût {AutopilotAdvisor.Money(ap.Log.CoutUsd)}");
        report.Notes.Add("Tout a été fait dans un seul groupe d'opérations : Ctrl+Z (Annuler « Avion par terre — mode automatique ») revient à l'état initial.");
        report.RevitWarnings.AddRange(w.Warnings);
        ap.SaveLog(report);
        SaveLog(data, report);
        Show(report);
        return Result.Succeeded;
    }

    private static bool ConfirmLaunch(Autopilot ap)
    {
        var win = Theme.Window("Mode avion par terre", 820, 640);
        var root = new DockPanel { Margin = new Thickness(14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var icon = new Image { Source = Theme.Png("avion_32.png"), Width = 64, Height = 64, Margin = new Thickness(0, 0, 14, 0) };
        System.Windows.Media.RenderOptions.SetBitmapScalingMode(icon, System.Windows.Media.BitmapScalingMode.NearestNeighbor);
        DockPanel.SetDock(icon, Dock.Left);
        header.Children.Add(icon);
        header.Children.Add(Theme.Header("Mode avion par terre", "Production DCE complète : ce que vous et la maquette avez décidé est conservé, le reste est décidé et tracé."));
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var steps = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        var st = ap.Steps;
        (string, Func<bool>, Action<bool>)[] defs =
        {
            ("Données de pièces", () => st.DonneesPieces, v => st.DonneesPieces = v),
            ("Finitions", () => st.Finitions, v => st.Finitions = v),
            ("Plans généraux", () => st.Plans, v => st.Plans = v),
            ("Carnets de pièces", () => st.Carnets, v => st.Carnets = v),
            ("Fiches menuiseries", () => st.Fiches, v => st.Fiches = v),
            ("Registre", () => st.Registre, v => st.Registre = v),
            ("Export PDF brouillon", () => st.ExportPdf, v => st.ExportPdf = v),
        };
        foreach (var (label, get, set) in defs)
        {
            var cb = new CheckBox { Content = label, IsChecked = get(), Margin = new Thickness(0, 0, 16, 4) };
            cb.Checked += (_, _) => set(true);
            cb.Unchecked += (_, _) => set(false);
            steps.Children.Add(cb);
        }
        DockPanel.SetDock(steps, Dock.Top);
        root.Children.Add(steps);

        bool go = false;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var cancel = Theme.Button("Annuler");
        cancel.Click += (_, _) => win.Close();
        var launch = Theme.Button(ap.UsesAi ? "Décoller (analyse IA puis production)" : "Décoller (sans IA)", primary: true);
        launch.Click += (_, _) => { go = true; win.Close(); };
        buttons.Children.Add(cancel);
        buttons.Children.Add(launch);
        root.Children.Add(buttons);
        root.Children.Add(new TextBox
        {
            Text = ap.AnalysisSummary(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"), Background = Theme.B(Theme.GrisClair),
        });
        win.Content = root;
        win.ShowDialog();
        return go;
    }

    private static bool Review(Autopilot ap)
    {
        var rows = ap.Log.Decisions.Where(d => d.Domaine != "Normes du projet").Select(d => new DecisionRow { D = d }).ToList();
        var lw = new ListWindow<DecisionRow>("Décisions du mode avion par terre",
            $"{ap.Log.Summary()}" + (ap.UsesAi ? $" — coût IA {AutopilotAdvisor.Money(ap.Log.CoutUsd)}" : "") +
            (ap.AiErrors.Count > 0 ? $" — {ap.AiErrors.Count} alerte(s) IA" : "") + ". Rien n'a encore été écrit dans la maquette.",
            rows, "Appliquer à la maquette", null);
        lw.GridControl.IsReadOnly = true;
        foreach (var r in rows) r.Selected = true;
        if (ap.AiErrors.Count > 0)
        {
            var err = Theme.Button("Alertes IA");
            err.Click += (_, _) => MessageBox.Show(lw.Window, string.Join(Environment.NewLine, ap.AiErrors.Take(30)), "Avion par terre — alertes IA");
            lw.ExtraButtons.Children.Add(err);
        }
        if (!lw.ShowDialog())
        {
            foreach (var row in rows) row.D.StatutRevue = "annulee";
            return false;
        }
        foreach (var row in rows) row.D.StatutRevue = row.Selected ? "acceptee" : "refusee";
        // Les décisions décochées ne sont pas appliquées : on les retire des listes d'exécution via le journal
        var rejected = rows.Where(r => !r.Selected).Select(r => r.D).ToHashSet();
        if (rejected.Count > 0) ap.Reject(rejected);
        return true;
    }
}

/// <summary>Paramètres : clé et modèle OpenRouter, étapes, ressources imposées, normes du projet.</summary>
[Transaction(TransactionMode.Manual)]
public sealed class SettingsCommand : CommandBase
{
    protected override string Title => "Paramètres";

    internal override Result Run(UIDocument uidoc, PluginData data)
    {
        var doc = uidoc.Document;
        var res = new KdResources(doc, data.Profile);
        var rows = res.ResolveAll().ToList().Select(x => new ResourceChoiceRow
        {
            Key = x.Role.Key,
            Role = x.Role.Label,
            Current = x.Id == ElementId.InvalidElementId ? "introuvable" : res.NameOf(x.Id)
                      + (res.Missing.Any(m => m.Objet == x.Role.Key && m.Regle == "ressource_remplacement") ? " (remplacement)" : ""),
            Options = new[] { ResourceChoiceRow.Auto }.Concat(res.Candidates(x.Role)).ToList(),
        }).ToList();
        var norms = ProjectNorms.Detect(doc, data).Describe(doc).ToList();
        var asm = Assembly.GetExecutingAssembly();
        var about = new StringBuilder()
            .AppendLine($"Avion par terre {asm.GetName().Version} — Koffi & Diabaté Architectes, Cellule IA")
            .AppendLine()
            .AppendLine($"Paramètres utilisateur : {UserSettings.FilePath}")
            .AppendLine($"Choix du projet : {data.ChoicesPath}")
            .AppendLine($"Profil documentaire : {data.ProfilePath}")
            .AppendLine($"Catalogue de finitions : {data.CataloguePath}")
            .AppendLine($"Sorties : {data.OutputDir}")
            .AppendLine()
            .AppendLine("La clé OpenRouter est chiffrée avec DPAPI (compte Windows). Les requêtes envoient à OpenRouter les noms, niveaux et surfaces des locaux,")
            .AppendLine("les repères et familles de menuiseries et le référentiel de finitions K&D ; aucune géométrie ni fichier Revit n'est transmis.")
            .ToString();
        var win = new SettingsWindow(data.Settings, data.Choices, rows, norms, data.Catalogue, data.Profile, about);
        if (!win.ShowDialog()) return Result.Cancelled;
        data.Settings.Save();
        data.SaveChoices();
        return Result.Succeeded;
    }
}
