using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Revit.Services;
using Units = AvionParTerre.Revit.Services.Units;

namespace AvionParTerre.Revit;

/// <summary>
/// Tests de fumée pour le développement : exécutent les générateurs sur la maquette active dans un groupe
/// de transactions ANNULÉ à la fin (aucune modification conservée) et exportent des images des feuilles créées.
/// </summary>
public static class Diagnostics
{
    /// <summary>IA simulée : réponses déterministes construites à partir de la question (aucun appel réseau).</summary>
    private sealed class FakeLlm : ILlmClient
    {
        public int Calls;

        public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken ct = default)
        {
            Calls++;
            var prompt = request.Messages.Last().Content;
            string json;
            if (prompt.Contains("\"bibliotheque\""))
                json = "{\"bibliotheque\":\"KD_G2_APROMAC_TERTIAIRE\",\"justification\":\"bâtiment administratif et laboratoires (test)\"}";
            else if (prompt.Contains("LOCAUX À DÉCIDER"))
            {
                var items = Regex.Matches(prompt, "\"id\":\"([^\"]+)\",\"nom\":\"([^\"]*)\"").Select(m => (Id: m.Groups[1].Value, Name: m.Groups[2].Value));
                json = "{\"pieces\":[" + string.Join(",", items.Select(i =>
                {
                    var n = i.Name.ToLowerInvariant();
                    var (fam, prof) = n.Contains("toilette") || n.Contains("sanitaire") ? ("sanitaire", "APO-03")
                        : n.Contains("stock") || n.Contains("local") || n.Contains("transfo") || n.Contains("lge") ? ("technique", "APO-12a")
                        : ("administration", "PLA-05");
                    return $"{{\"id\":\"{i.Id}\",\"famille\":\"{fam}\",\"profil\":\"{prof}\",\"sol\":\"Grès cérame 60 × 120\",\"mur\":\"Peinture Pantex 800 avec enduit repassé\"," +
                           $"\"plafond\":\"Faux plafond démontable (test hors référentiel)\",\"justification\":\"réponse simulée pour « {i.Name} »\",\"sources\":[\"https://example.org/test\"]}}";
                })) + "]}";
            }
            else
            {
                var marks = Regex.Matches(prompt, "\"repere\":\"([^\"]+)\"").Select(m => m.Groups[1].Value);
                json = "{\"menuiseries\":[" + string.Join(",", marks.Select(m =>
                    $"{{\"repere\":\"{m}\",\"lot\":\"CAL\",\"prescriptions\":\"Profilés aluminium laqués (test)\\nQuincaillerie inox\\nPose en tunnel sur précadre\",\"justification\":\"simulé\"}}")) + "]}";
            }
            return Task.FromResult(new LlmResponse { Content = "```json\n" + json + "\n```", Model = "test/fake", PromptTokens = prompt.Length / 4, CompletionTokens = json.Length / 4, Cost = 0 });
        }
    }

    /// <summary>Mode avion par terre complet avec IA simulée, annulé à la fin.</summary>
    public static string AutopilotTest(UIApplication app, string imageFolder, int maxCarnets = 2, int maxFiches = 2)
    {
        var doc = app.ActiveUIDocument.Document;
        var data = PluginData.Load(doc);
        var sb = new StringBuilder();
        var w = new WarningCollector();
        Directory.CreateDirectory(imageFolder);
        var before = RevitUtil.SheetNumbers(doc).Count;
        using var tg = new TransactionGroup(doc, "Avion par terre — test mode automatique");
        tg.Start();
        try
        {
            var steps = new AutopilotSteps { DonneesPieces = true, Finitions = true, Plans = true, Carnets = true, Fiches = true, Registre = false, ExportPdf = false };
            var ap = new Autopilot(doc, data, steps) { ForceAi = true, MaxCarnets = maxCarnets, MaxFiches = maxFiches };
            ap.Analyze();
            sb.AppendLine(ap.AnalysisSummary());
            var fake = new FakeLlm();
            ap.ConsultAsync(fake, new Progress<string>(_ => { }), CancellationToken.None).GetAwaiter().GetResult();
            sb.AppendLine($"Appels IA simulés : {fake.Calls} ; bibliothèque : {ap.Library} ; erreurs : {string.Join(" | ", ap.AiErrors)}");
            ap.Decide();
            sb.AppendLine("DÉCISIONS : " + ap.Log.Summary());
            foreach (var g in ap.Log.Decisions.GroupBy(d => d.Domaine))
            {
                sb.AppendLine($"  [{g.Key}] {g.Count()}");
                foreach (var d in g.Take(6)) sb.AppendLine($"     {d.Objet} → {d.Choix} ({d.SourceLibelle}) {d.Justification}");
            }
            var report = new Report("Test mode automatique");
            ap.Execute(report, w, _ => { }, () => false);
            sb.AppendLine(report.ToText());
            sb.AppendLine($"FEUILLES avant={before} après={RevitUtil.SheetNumbers(doc).Count}");
            DescribeManaged(doc, data, sb);
            ExportSheets(doc, imageFolder, sb);
            sb.AppendLine($"Avertissements Revit : {w.Warnings.Count}");
            foreach (var x in w.Warnings.Distinct().Take(15)) sb.AppendLine("  " + x);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
        }
        finally
        {
            tg.RollBack();
            File.WriteAllText(Path.Combine(imageFolder, "report.txt"), sb.ToString(), new UTF8Encoding(true));
        }
        return sb.ToString();
    }

    /// <summary>Commandes unitaires (finitions, plan forcé, carnets, fiches) puis relance, annulées à la fin.</summary>
    public static string SmokeTest(UIApplication app, string imageFolder, int maxRooms = 2, int maxTypes = 3)
    {
        var doc = app.ActiveUIDocument.Document;
        var data = PluginData.Load(doc);
        var dce = data.Profile.ProfilsDce[0];
        var sb = new StringBuilder();
        var w = new WarningCollector();
        Directory.CreateDirectory(imageFolder);

        using var tg = new TransactionGroup(doc, "Avion par terre — test");
        tg.Start();
        try
        {
            var norms = ProjectNorms.Detect(doc, data);
            foreach (var n in norms.Describe(doc)) sb.AppendLine("NORME " + n);
            var before = RevitUtil.SheetNumbers(doc).Count;
            var cls = new RoomClassifier(data.Catalogue.FamillesLocaux);
            var rooms = RevitUtil.Rooms(doc).Where(RevitUtil.IsEnclosed)
                .OrderBy(r => cls.Classify(RevitUtil.RoomName(r))?.FamilyCode == "sanitaire" ? 0 : 1).ThenBy(r => r.Area).Take(maxRooms).ToList();
            var level = rooms.Select(r => (Level)doc.GetElement(r.LevelId)).FirstOrDefault() ?? RevitUtil.Levels(doc).First();

            // 1. Finitions (profil + saisie libre)
            var matcher = new FinishMatcher(data.Catalogue);
            var fr = new Report("Finitions");
            var items = rooms.Select(r => (r, p: matcher.Suggest(RevitUtil.RoomName(r), level.Name, null).FirstOrDefault()?.Profile))
                .Select(x => new FinishAssignment
                {
                    Room = x.r, Profile = x.p,
                    Values = FinishSummary.RoomSupports.ToDictionary(s => s, s => x.p == null ? (s == "Sol" ? "Béton ciré (saisie test)" : null) : FinishSummary.For(data.Catalogue, x.p.Applications, s)),
                }).ToList();
            new FinishService(doc, data.Catalogue).Apply(items, false, "test", fr, w);
            sb.AppendLine(fr.ToText());

            // 2. Plans : niveau couvert par l'agence (conservé) puis création forcée
            var levels = RevitUtil.Levels(doc).ToList();
            var pr = new Report("Plans");
            var planDce = norms.PlanProfile(doc, new KdResources(doc, data), dce) ?? dce;
            var gen = new PlanGenerator(doc, data, planDce, norms);
            gen.Run(new List<PlanRequest> { new() { Level = level, Index = levels.FindIndex(l => l.Id == level.Id) } }, pr, w);
            gen.Run(new List<PlanRequest> { new() { Level = level, Index = levels.FindIndex(l => l.Id == level.Id), Force = true } }, pr, w);
            sb.AppendLine(pr.ToText());

            // 3. Carnets
            var cr = new Report("Carnets");
            new CarnetGenerator(doc, data, dce).Run(rooms, cr, w);
            sb.AppendLine(cr.ToText());

            // 4. Fiches (types sans fiche existante)
            var fgen = new FicheGenerator(doc, data, norms);
            var types = fgen.Collect().Where(t => t.Lot != null && FicheGenerator.ExistingLabel(t) == null).Take(maxTypes).ToList();
            var fir = new Report("Fiches");
            fgen.Run(types, fir, w);
            sb.AppendLine(fir.ToText());

            // 4b. Coupes et façades, puis rangement du navigateur (dossier DCE)
            var sr = new Report("Coupes et façades");
            new SectionElevationGenerator(doc, data, planDce, norms).Run(new SectionElevationRequest { Coupes = true, Facades = true, Force = true }, sr, w);
            new BrowserFolders(doc, data).Arrange(sr, w);
            sb.AppendLine(sr.ToText());
            foreach (var dim in new FilteredElementCollector(doc).OfClass(typeof(Dimension)).Cast<Dimension>()
                         .GroupBy(d => doc.GetElement(d.OwnerViewId) is View ov && Identity.Get(ov) is { } id ? id.Role : null).Where(g => g.Key != null))
                sb.AppendLine($"COTES {dim.Key} : {dim.Count()}");

            var afterFirst = RevitUtil.SheetNumbers(doc).Count;

            // 5. Relance : aucune feuille supplémentaire attendue
            var r2 = new Report("Relance");
            var norms2 = ProjectNorms.Detect(doc, data);
            new PlanGenerator(doc, data, planDce, norms2).Run(new List<PlanRequest> { new() { Level = level, Index = levels.FindIndex(l => l.Id == level.Id), Force = true } }, r2, w);
            new CarnetGenerator(doc, data, dce).Run(rooms, r2, w);
            var fgen2 = new FicheGenerator(doc, data, norms2);
            fgen2.Run(fgen2.Collect().Where(t => types.Any(x => x.Symbol.Id == t.Symbol.Id)).ToList(), r2, w);
            var afterSecond = RevitUtil.SheetNumbers(doc).Count;
            foreach (var c in r2.Created) sb.AppendLine("  relance a créé : " + c);
            sb.AppendLine($"Relance : créés={r2.Created.Count} mis à jour={r2.Updated.Count} inchangés={r2.Unchanged.Count} conservés={r2.Kept.Count}");
            sb.AppendLine($"FEUILLES avant={before} après 1re passe={afterFirst} après relance={afterSecond}");

            DescribeManaged(doc, data, sb);
            ExportSheets(doc, imageFolder, sb);
            sb.AppendLine($"Avertissements Revit : {w.Warnings.Count}");
            foreach (var x in w.Warnings.Distinct().Take(15)) sb.AppendLine("  " + x);
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION " + ex);
        }
        finally
        {
            tg.RollBack();
            File.WriteAllText(Path.Combine(imageFolder, "report.txt"), sb.ToString(), new UTF8Encoding(true));
        }
        return sb.ToString();
    }

    private static void DescribeManaged(Document doc, PluginData data, StringBuilder sb)
    {
        var composer = new SheetComposer(doc, data.Profile.MiseEnPage);
        foreach (var (e, d) in Identity.All(doc).OrderBy(x => x.Data.Key))
        {
            if (e is ViewSheet s)
            {
                var area = composer.DrawingArea(s);
                sb.AppendLine($"SHEET {s.SheetNumber} | {s.Name} | {d.Role} | {RevitUtil.TitleBlockOf(doc, s)?.Symbol.FamilyName} | zone {area.Width:0}x{area.Height:0} mm" +
                              $" | phase={RevitUtil.ParamText(s, data.Profile.Parameter("feuille_phase"))} lot={RevitUtil.ParamText(s, data.Profile.Parameter("feuille_lot"))}");
                foreach (var vp in new FilteredElementCollector(doc, s.Id).OfClass(typeof(Viewport)).Cast<Viewport>())
                {
                    var v = (View)doc.GetElement(vp.ViewId);
                    var o = vp.GetBoxOutline();
                    sb.AppendLine($"   VP {v.Name} 1:{v.Scale} type={doc.GetElement(vp.GetTypeId())?.Name} [{Units.ToMm(o.MinimumPoint.X):0},{Units.ToMm(o.MinimumPoint.Y):0} → {Units.ToMm(o.MaximumPoint.X):0},{Units.ToMm(o.MaximumPoint.Y):0}] fit={composer.FitsInArea(s, vp)}");
                }
                foreach (var si in new FilteredElementCollector(doc, s.Id).OfClass(typeof(ScheduleSheetInstance)).Cast<ScheduleSheetInstance>().Where(x => !x.IsTitleblockRevisionSchedule))
                    sb.AppendLine($"   SCHED {doc.GetElement(si.ScheduleId).Name}");
                foreach (var tn in new FilteredElementCollector(doc, s.Id).OfClass(typeof(TextNote)).Cast<TextNote>())
                    sb.AppendLine($"   TEXT [{doc.GetElement(tn.GetTypeId())?.Name}] {tn.Text.Replace("\r", " / ").Trim()}");
            }
            else if (e is Autodesk.Revit.DB.Architecture.Room room)
                sb.AppendLine($"ROOM {RevitUtil.RoomLabel(room)} : {FinishService.State(room)} | {string.Join(" / ", FinishSummary.RoomSupports.Select(x => FinishService.Current(room, x)))}");
        }
    }

    private static void ExportSheets(Document doc, string imageFolder, StringBuilder sb)
    {
        var sheetIds = Identity.All(doc).Where(x => x.Element is ViewSheet).Select(x => x.Element.Id).ToList();
        if (sheetIds.Count == 0) return;
        var opt = new ImageExportOptions
        {
            ExportRange = ExportRange.SetOfViews,
            FilePath = Path.Combine(imageFolder, "sheet"),
            HLRandWFViewsFileType = ImageFileType.PNG,
            ShadowViewsFileType = ImageFileType.PNG,
            ImageResolution = ImageResolution.DPI_150,
            ZoomType = ZoomFitType.FitToPage,
            PixelSize = 1600,
        };
        opt.SetViewsAndSheets(sheetIds);
        try { doc.ExportImage(opt); sb.AppendLine($"Images : {imageFolder}"); }
        catch (Exception ex) { sb.AppendLine("Export images impossible : " + ex.Message); }
    }

    /// <summary>Construit les fenêtres sans les afficher et en exporte un rendu PNG (contrôle visuel de l'interface).</summary>
    public static string UiSnapshot(UIApplication app, string folder)
    {
        var doc = app.ActiveUIDocument.Document;
        var data = PluginData.Load(doc);
        Directory.CreateDirectory(folder);
        var rows = Commands.FinitionsCommand.BuildRows(doc, data);
        var fw = new UI.FinishesWindow(data.Catalogue, rows, null, "test");
        foreach (var r in fw.Rows.Where(r => r.Option?.Profile != null).Take(4)) r.Selected = true;
        var first = fw.Rows.FirstOrDefault(r => r.Option?.Profile == null);
        if (first != null) first.Sol = "Béton ciré (saisie)";
        fw.RefreshPreview();
        var path = Path.Combine(folder, "ui-finitions.png");
        Render(fw.Window, path);

        var res = new KdResources(doc, data.Profile);
        var rrows = res.ResolveAll().ToList().Select(x => new UI.ResourceChoiceRow
        {
            Key = x.Role.Key, Role = x.Role.Label, Current = x.Id == ElementId.InvalidElementId ? "introuvable" : res.NameOf(x.Id),
            Options = new[] { UI.ResourceChoiceRow.Auto }.Concat(res.Candidates(x.Role)).ToList(),
        }).ToList();
        var sw = new UI.SettingsWindow(new UserSettings(), new ProjectChoices(), rrows, ProjectNorms.Detect(doc, data).Describe(doc), data.Catalogue, data.Profile, "test");
        Render(sw.Window, Path.Combine(folder, "ui-parametres.png"));
        var tabs = FindTabs(sw.Window.Content as System.Windows.DependencyObject);
        if (tabs != null)
            for (int i = 1; i < tabs.Items.Count; i++)
            {
                tabs.SelectedIndex = i;
                Render(sw.Window, Path.Combine(folder, $"ui-parametres-{i}.png"));
            }

        var pw = new UI.ProgressWindow("Mode avion par terre — consultation de l'IA");
        pw.Log("Finitions : 20/48 libellés traités (avec recherche internet)");
        Render(pw.Window, Path.Combine(folder, "ui-progression.png"));

        var ribbon = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Background = System.Windows.Media.Brushes.White };
        var plane = new System.Windows.Controls.Image { Source = UI.Theme.Png("avion_32.png"), Width = 32, Margin = new System.Windows.Thickness(4) };
        ribbon.Children.Add(plane);
        foreach (var l in new[] { "PA", "AU", "FI", "PL", "CP", "FM", "RG", "PDF", "RF" })
            ribbon.Children.Add(new System.Windows.Controls.Image { Source = UI.Theme.Icon(l, 32), Width = 32, Margin = new System.Windows.Thickness(4) });
        var w2 = new System.Windows.Window { Content = ribbon, Width = 420, Height = 60 };
        Render(w2, Path.Combine(folder, "ui-icones.png"));
        return $"{rows.Count} lignes ; sélection {fw.Selection.Count} ; ressources {rrows.Count} ; rendu : {folder}";
    }

    private static System.Windows.Controls.TabControl? FindTabs(System.Windows.DependencyObject? o)
    {
        if (o == null) return null;
        if (o is System.Windows.Controls.TabControl t) return t;
        foreach (var c in System.Windows.LogicalTreeHelper.GetChildren(o).OfType<System.Windows.DependencyObject>())
            if (FindTabs(c) is { } f) return f;
        return null;
    }

    private static void Render(System.Windows.Window w, string path)
    {
        var root = (System.Windows.FrameworkElement)w.Content;
        var size = new System.Windows.Size(w.Width, w.Height - 30);
        root.Measure(size);
        root.Arrange(new System.Windows.Rect(size));
        root.UpdateLayout();
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
