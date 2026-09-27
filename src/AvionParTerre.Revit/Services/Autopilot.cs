using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core;
using AvionParTerre.Core.Ai;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;
using AvionParTerre.Core.Profiles;
using AvionParTerre.Core.Text;

namespace AvionParTerre.Revit.Services;

/// <summary>
/// Mode « avion par terre » : enchaîne toute la production DCE en décidant ce qui ne l'a pas été.
/// Priorité des décisions : utilisateur (Paramètres, saisies) → projet (maquette, normes relevées) → référentiel K&amp;D
/// → règles automatiques → IA (modèle OpenRouter choisi). Trois temps : analyse (lecture seule), consultation de l'IA
/// (hors Revit), exécution dans un seul groupe de transactions (annulable en une fois).
/// </summary>
internal sealed class Autopilot
{
    private readonly Document _doc;
    private readonly PluginData _data;
    private readonly UserSettings _settings;
    public AutopilotSteps Steps { get; }
    public DecisionLog Log { get; }
    public ProjectNorms Norms { get; }
    public ProjectContext Context { get; }
    public List<string> Notes { get; } = new();

    // Analyse
    private readonly List<(Room Room, RoomInput Input)> _toAsk = new();
    private readonly List<FinishAssignment> _finishes = new();
    private readonly Dictionary<ElementId, string?> _family = new();
    private List<JoineryType> _joinery = new();
    private List<RoomDataProposal> _roomData = new();
    public string? Library { get; private set; }
    private DecisionSource _librarySource = DecisionSource.NonDecide;

    // Consultation
    private List<RoomAdvice> _roomAdvice = new();
    private List<JoineryAdvice> _joineryAdvice = new();
    public List<string> AiErrors { get; } = new();

    public Autopilot(Document doc, PluginData data, AutopilotSteps? steps = null)
    {
        _doc = doc;
        _data = data;
        _settings = data.Settings;
        Steps = steps ?? data.Settings.Etapes;
        Log = new DecisionLog { Document = doc.Title, Modele = _settings.HasKey ? _settings.Modele : null };
        Norms = ProjectNorms.Detect(doc, data);
        var pi = doc.ProjectInformation;
        Context = new ProjectContext
        {
            Document = doc.Title,
            ProjectName = pi?.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString(),
            Client = pi?.get_Parameter(BuiltInParameter.CLIENT_NAME)?.AsString(),
            Address = pi?.get_Parameter(BuiltInParameter.PROJECT_ADDRESS)?.AsString(),
            Number = pi?.get_Parameter(BuiltInParameter.PROJECT_NUMBER)?.AsString(),
            Levels = RevitUtil.Levels(doc).Where(l => RevitUtil.Rooms(doc).Any(r => r.LevelId == l.Id)).Select(l => l.Name).ToList(),
            RoomNames = RevitUtil.Rooms(doc).Where(RevitUtil.IsEnclosed).Select(RevitUtil.RoomName).Distinct().ToList(),
        };
    }

    public bool UsesAi => _settings.HasKey || ForceAi;

    /// <summary>Tests : IA simulée, limites de volume.</summary>
    internal bool ForceAi { get; set; }
    internal int? MaxCarnets { get; set; }
    internal int? MaxFiches { get; set; }

    // =========================================================================================
    // 1. Analyse (lecture seule, fil Revit)
    // =========================================================================================

    public void Analyze()
    {
        foreach (var line in Norms.Describe(_doc)) Log.Add("Normes du projet", "Maquette", line, DecisionSource.Projet);

        Library = _data.Choices.Bibliotheque;
        if (Library != null)
        {
            _librarySource = DecisionSource.Utilisateur;
            Log.Add("Finitions", "Bibliothèque de référence", _data.Catalogue.LibraryOf(Library)?.Display ?? Library, DecisionSource.Utilisateur, "choisie dans Paramètres");
        }

        if (Steps.DonneesPieces) _roomData = new RoomDataService(_doc, _data).Propose();
        if (Steps.Finitions || Steps.Carnets) AnalyzeRooms();
        if (Steps.Fiches)
        {
            _joinery = new FicheGenerator(_doc, _data, Norms).Collect();
            foreach (var j in _joinery.Where(j => j.Lot != null))
                Log.Add("Menuiseries", j.Mark, $"lot {j.Lot}", DecisionSource.Referentiel, $"classement par {j.LotSource}", id: j.Symbol.Id.Value);
        }
    }

    private void AnalyzeRooms()
    {
        var matcher = new FinishMatcher(_data.Catalogue);
        int complete = 0;
        foreach (var r in RevitUtil.Rooms(_doc).Where(RevitUtil.IsEnclosed)
                     .OrderBy(r => (_doc.GetElement(r.LevelId) as Level)?.Elevation ?? 0).ThenBy(r => r.Number))
        {
            var name = RevitUtil.RoomName(r);
            var level = (_doc.GetElement(r.LevelId) as Level)?.Name ?? "";
            var cls = matcher.Classify(name);
            _family[r.Id] = FinishService.Record(r)?.FamilleLocal ?? cls?.FamilyCode;
            var known = FinishSummary.RoomSupports.Select(s => (s, v: FinishService.Current(r, s))).Where(x => x.v.Length > 0).ToDictionary(x => x.s, x => x.v);
            if (!Steps.Finitions) continue;
            if (known.Count == 3) { complete++; continue; }
            var missing = FinishSummary.RoomSupports.Where(s => !known.ContainsKey(s)).ToList();
            var sugg = matcher.Suggest(name, level, Library, 3);
            var top = sugg.FirstOrDefault();
            var clear = top != null && cls != null && top.Profile.FamilleLocal == cls.FamilyCode &&
                        (sugg.Count < 2 || top.Score - sugg[1].Score >= 0.1 || SameSummary(top.Profile, sugg[1].Profile));
            var summary = top == null ? null : FinishSummary.ForRoom(_data.Catalogue, top.Profile.Applications);
            if (clear && missing.All(s => summary![s] != null))
            {
                _finishes.Add(new FinishAssignment
                {
                    Room = r, Profile = top!.Profile, FamilyCode = cls!.FamilyCode, Source = DecisionSource.Referentiel,
                    Values = FinishSummary.RoomSupports.ToDictionary(s => s, s => missing.Contains(s) ? summary![s] : null),
                    Justification = $"profil {top.Profile.Id} ({top.Raison})",
                });
                continue;
            }
            _toAsk.Add((r, new RoomInput
            {
                Key = r.UniqueId, Name = name, Level = level, AreaM2 = Units.ToM2(r.Area), FamilyCode = cls?.FamilyCode,
                CandidateProfiles = sugg.Select(s => s.Profile.Id).ToList(), Known = known,
            }));
        }
        if (complete > 0)
            Log.Add("Finitions", $"{complete} pièce(s)", "Sol, mur et plafond déjà renseignés — conservés", DecisionSource.Projet);
    }

    private bool SameSummary(ReferenceProfile a, ReferenceProfile b)
    {
        var x = FinishSummary.ForRoom(_data.Catalogue, a.Applications);
        var y = FinishSummary.ForRoom(_data.Catalogue, b.Applications);
        return FinishSummary.RoomSupports.All(s => x[s] == y[s]);
    }

    public string AnalysisSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Maquette : {_doc.Title}");
        sb.AppendLine($"IA : {(UsesAi ? $"{_settings.Modele} via OpenRouter" + (_settings.RechercheWeb ? ", recherche internet activée" : "") : "aucune clé OpenRouter — référentiel et règles uniquement")}");
        sb.AppendLine();
        foreach (var n in Norms.Describe(_doc)) sb.AppendLine("• " + n);
        sb.AppendLine();
        if (Steps.DonneesPieces) sb.AppendLine($"Données de pièces : {_roomData.Count} valeur(s) Niv/HSP/HSD déductibles du projet.");
        if (Steps.Finitions)
            sb.AppendLine($"Finitions : {_finishes.Count} pièce(s) décidées par le référentiel, {_toAsk.Count} à confier à l'IA " +
                          $"({_toAsk.Select(x => TextNorm.Normalize(x.Input.Name)).Distinct().Count()} libellés distincts, {_toAsk.Count(x => x.Input.FamilyCode == null)} locaux non classés).");
        if (Steps.Fiches)
        {
            var todo = _joinery.Where(j => FicheGenerator.ExistingLabel(j) == null).ToList();
            sb.AppendLine($"Menuiseries : {_joinery.Count} type(s), {_joinery.Count - todo.Count} déjà avec fiche, {todo.Count} fiche(s) à produire, " +
                          $"{_joinery.Count(j => j.Lot == null)} lot(s) à décider.");
        }
        return sb.ToString();
    }

    // =========================================================================================
    // 2. Consultation de l'IA (hors API Revit, fil de travail)
    // =========================================================================================

    public async Task ConsultAsync(ILlmClient llm, IProgress<string> progress, CancellationToken ct)
    {
        var advisor = new AutopilotAdvisor(llm, _data.Catalogue, _data.Profile.Menuiseries, _settings.AiOptions(), Log);
        if (Library == null && (Steps.Finitions && _toAsk.Count > 0))
        {
            progress.Report("Choix de la bibliothèque de finitions du projet…");
            Library = await advisor.ChooseLibraryAsync(Context, ct).ConfigureAwait(false);
            if (Library != null) _librarySource = DecisionSource.Ia;
        }
        if (Steps.Finitions && _toAsk.Count > 0)
        {
            progress.Report($"Finitions : {_toAsk.Count} pièce(s) à décider…");
            _roomAdvice = await advisor.AdviseRoomsAsync(_toAsk.Select(x => x.Input).ToList(), Library, Context, progress, ct).ConfigureAwait(false);
        }
        if (Steps.Fiches)
        {
            var items = _joinery.Where(j => FicheGenerator.ExistingLabel(j) == null || j.Fiche != null).Select(FicheGenerator.Input).ToList();
            if (items.Count > 0)
            {
                progress.Report($"Menuiseries : lots et prescriptions de {items.Count} type(s)…");
                _joineryAdvice = await advisor.AdviseJoineryAsync(items, Context, progress, ct).ConfigureAwait(false);
            }
        }
        AiErrors.AddRange(advisor.Errors);
        progress.Report($"Consultation terminée — {Log.JetonsEntree + Log.JetonsSortie} jetons, coût {AutopilotAdvisor.Money(Log.CoutUsd)}.");
    }

    // =========================================================================================
    // 3. Décisions et exécution (fil Revit)
    // =========================================================================================

    /// <summary>Intègre les réponses de l'IA et journalise les décisions (avant écriture).</summary>
    public void Decide()
    {
        var byKey = _roomAdvice.GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var (room, input) in _toAsk)
        {
            if (!byKey.TryGetValue(input.Key, out var a))
            {
                Log.Add("Finitions", RevitUtil.RoomLabel(room), "à définir", DecisionSource.NonDecide,
                    UsesAi ? "pas de réponse exploitable de l'IA" : "aucune clé OpenRouter : pas de décision IA", id: room.Id.Value);
                continue;
            }
            if (a.FamilyCode != null)
            {
                if (_family.GetValueOrDefault(room.Id) != a.FamilyCode)
                    Log.Add("Classement des locaux", RevitUtil.RoomLabel(room), _data.Catalogue.Family(a.FamilyCode)?.Libelle ?? a.FamilyCode,
                        DecisionSource.Ia, a.Justification, a.Sources, id: room.Id.Value);
                _family[room.Id] = a.FamilyCode;
            }
            _finishes.Add(new FinishAssignment
            {
                Room = room,
                Profile = _data.Catalogue.Profile(a.ProfileId),
                FamilyCode = a.FamilyCode,
                Source = DecisionSource.Ia,
                Model = _settings.Modele,
                Justification = a.Justification,
                Sources = a.Sources,
                Values = FinishSummary.RoomSupports.ToDictionary(s => s, s => a.Finishes.TryGetValue(s, out var f) ? f.Designation : null),
            });
        }
        foreach (var a in _joineryAdvice)
        {
            var j = _joinery.FirstOrDefault(x => string.Equals(x.Mark, a.Mark, StringComparison.OrdinalIgnoreCase));
            if (j == null) continue;
            if (j.Lot == null && a.Lot != null)
            {
                j.Lot = a.Lot;
                j.LotSource = "IA";
                Log.Add("Menuiseries", j.Mark, $"lot {a.Lot}", DecisionSource.Ia, a.Justification, id: j.Symbol.Id.Value);
            }
            if (!string.IsNullOrWhiteSpace(a.Prescriptions))
            {
                j.Prescriptions = a.Prescriptions;
                Log.Add("Menuiseries", $"{j.Mark} / prescriptions", a.Prescriptions!.Replace("\n", " "), DecisionSource.Ia, a.Justification, id: j.Symbol.Id.Value);
            }
        }
        foreach (var j in _joinery.Where(j => j.Lot == null))
            Log.Add("Menuiseries", j.Mark, "lot à décider", DecisionSource.NonDecide, "préfixe hors convention", id: j.Symbol.Id.Value);
        foreach (var f in _finishes.Where(f => f.Source == DecisionSource.Referentiel))
            Log.Add("Finitions", RevitUtil.RoomLabel(f.Room), Describe(f), DecisionSource.Referentiel, f.Justification, id: f.Room.Id.Value);
        foreach (var f in _finishes.Where(f => f.Source == DecisionSource.Ia))
            Log.Add("Finitions", RevitUtil.RoomLabel(f.Room), Describe(f), DecisionSource.Ia, f.Justification, f.Sources, id: f.Room.Id.Value);
        foreach (var p in _roomData)
            Log.Add("Données de pièces", $"{RevitUtil.RoomLabel(p.Room)} / {p.Label}", p.Value, p.Source, p.Justification, id: p.Room.Id.Value);
        if (Library != null && _librarySource == DecisionSource.NonDecide) _librarySource = DecisionSource.Regle;

        // Plans et carnets : décidés ici pour être revus avant écriture
        if (Steps.Plans || Steps.Carnets) PlanProfile(new KdResources(_doc, _data));
        if (Steps.Plans)
        {
            var levels = RevitUtil.Levels(_doc).ToList();
            _planLevels = levels.Where(l => RevitUtil.Rooms(_doc).Any(r => r.LevelId == l.Id && RevitUtil.IsEnclosed(r))).ToList();
            foreach (var l in _planLevels)
                Log.Add("Plans généraux", l.Name,
                    Norms.PlanSheetByLevel.TryGetValue(l.Id, out var sh) ? $"couvert par la feuille {sh.SheetNumber} {sh.Name} — conservée" : $"plan « {PlanGenerator.SheetTitle(l)} »",
                    Norms.PlanSheetByLevel.ContainsKey(l.Id) ? DecisionSource.Projet : DecisionSource.Regle, "niveau comportant des pièces", id: l.Id.Value);
        }
        if (Steps.Plans)
        {
            _coupes = Norms.SectionSheet == null;
            _facades = Norms.ElevationSheet == null;
            Log.Add("Plans généraux", "Coupes", _coupes ? "coupes A-A et B-B par le centre du bâtiment" : $"feuille de l'agence {Norms.SectionSheet!.SheetNumber} {Norms.SectionSheet.Name} — conservée",
                _coupes ? DecisionSource.Regle : DecisionSource.Projet, "axes principaux des murs");
            Log.Add("Plans généraux", "Façades", _facades ? "quatre façades selon les axes du bâtiment" : $"feuille de l'agence {Norms.ElevationSheet!.SheetNumber} {Norms.ElevationSheet.Name} — conservée",
                _facades ? DecisionSource.Regle : DecisionSource.Projet, "axes principaux des murs, nord du projet");
        }
        if (Steps.Carnets) _carnets = CarnetRooms();
    }

    private bool _coupes, _facades;
    private List<Level> _planLevels = new();
    private List<Room> _carnets = new();

    /// <summary>Retire les décisions refusées lors de la revue : elles ne seront pas appliquées.</summary>
    public void Reject(ISet<Decision> rejected)
    {
        foreach (var d in rejected)
        {
            var id = d.ElementId;
            switch (d.Domaine)
            {
                case "Finitions":
                    _finishes.RemoveAll(f => f.Room.Id.Value == id);
                    break;
                case "Données de pièces":
                    _roomData.RemoveAll(p => p.Room.Id.Value == id && d.Objet.EndsWith("/ " + p.Label));
                    break;
                case "Classement des locaux":
                    foreach (var k in _family.Keys.Where(k => k.Value == id).ToList()) _family[k] = null;
                    _carnets.RemoveAll(r => r.Id.Value == id);
                    break;
                case "Carnets de pièces":
                    _carnets.RemoveAll(r => r.Id.Value == id);
                    break;
                case "Plans généraux":
                    if (d.Objet == "Coupes") _coupes = false;
                    else if (d.Objet == "Façades") _facades = false;
                    else _planLevels.RemoveAll(l => l.Id.Value == id);
                    break;
                case "Menuiseries":
                    foreach (var j in _joinery.Where(j => j.Symbol.Id.Value == id))
                    {
                        if (d.Objet.EndsWith("/ prescriptions")) j.Prescriptions = null;
                        else j.Lot = null;
                    }
                    break;
            }
        }
    }

    private static string Describe(FinishAssignment f) =>
        (f.Profile != null ? f.Profile.Id + " — " : "") +
        string.Join(" | ", FinishSummary.RoomSupports.Where(s => f.Values.GetValueOrDefault(s) != null).Select(s => $"{s} : {f.Values[s]}"));

    private DceProfile? _dce;

    public DceProfile PlanProfile(KdResources res) => _dce ??= ChoosePlanProfile(res);

    private DceProfile ChoosePlanProfile(KdResources res)
    {
        var chosen = _data.Choices.ProfilDce is { } code ? _data.Profile.Dce(code) : null;
        if (chosen != null)
        {
            Log.Add("Plans généraux", "Profil documentaire", chosen.Libelle, DecisionSource.Utilisateur, "choisi dans Paramètres");
            return chosen;
        }
        var fromNorms = Norms.PlanProfile(_doc, res, _data.Profile.ProfilsDce[0]);
        if (fromNorms != null)
        {
            Log.Add("Plans généraux", "Profil documentaire", fromNorms.Libelle, DecisionSource.Projet, "format, cartouche et échelle des plans existants");
            return fromNorms;
        }
        var d = _data.Profile.ProfilsDce[0];
        Log.Add("Plans généraux", "Profil documentaire", d.Libelle, DecisionSource.Regle, "profil par défaut du référentiel");
        return d;
    }

    /// <summary>Carnets : locaux des familles proposées ; un seul carnet par local répété (même nom, même surface).</summary>
    public List<Room> CarnetRooms()
    {
        var proposed = _data.Profile.Carnets.FamillesProposees.ToHashSet();
        var idx = Identity.Index(_doc);
        var rooms = RevitUtil.Rooms(_doc).Where(RevitUtil.IsEnclosed).Where(r => _family.TryGetValue(r.Id, out var f) && f != null && proposed.Contains(f))
            .OrderBy(r => (_doc.GetElement(r.LevelId) as Level)?.Elevation ?? 0).ThenBy(r => r.Number).ToList();
        var result = new List<Room>();
        foreach (var g in rooms.GroupBy(r => (TextNorm.Normalize(RevitUtil.RoomName(r)), Math.Round(Units.ToM2(r.Area) * 2) / 2)))
        {
            var withCarnet = g.FirstOrDefault(r => CarnetGenerator.ExistingGroup(idx, r).HasValue);
            var rep = withCarnet ?? g.First();
            result.Add(rep);
            var fam = _data.Catalogue.Family(_family[rep.Id])?.Libelle;
            Log.Add("Carnets de pièces", RevitUtil.RoomLabel(rep), g.Count() > 1 ? $"carnet représentatif de {g.Count()} locaux identiques" : "carnet",
                DecisionSource.Regle, $"famille « {fam} » (carnets proposés pour : {string.Join(", ", proposed)})", id: rep.Id.Value);
        }
        return result;
    }

    public void Execute(Report report, WarningCollector w, Action<string> step, Func<bool> cancelled)
    {
        var res = new KdResources(_doc, _data);
        if (Steps.DonneesPieces && _roomData.Count > 0 && !cancelled())
        {
            step($"Données de pièces : {_roomData.Count} valeur(s)…");
            new RoomDataService(_doc, _data).Apply(_roomData, report, w, null);
        }
        if (Steps.Finitions && _finishes.Count > 0 && !cancelled())
        {
            step($"Finitions : {_finishes.Count} pièce(s)…");
            new FinishService(_doc, _data.Catalogue).Apply(_finishes, overwriteManual: false, _settings.AuthorOrUser, report, w);
        }
        if (Steps.Plans && _planLevels.Count > 0 && !cancelled())
        {
            var dce = PlanProfile(res);
            var levels = RevitUtil.Levels(_doc).ToList();
            step($"Plans généraux : {_planLevels.Count} niveau(x)…");
            var reqs = _planLevels.Select(l => new PlanRequest { Level = l, Index = levels.IndexOf(l) }).ToList();
            new PlanGenerator(_doc, _data, dce, Norms).Run(reqs, report, w);
        }
        if (Steps.Plans && (_coupes || _facades) && !cancelled())
        {
            step("Plans généraux : coupes et façades…");
            new SectionElevationGenerator(_doc, _data, PlanProfile(res), Norms).Run(new SectionElevationRequest { Coupes = _coupes, Facades = _facades }, report, w);
        }
        if (Steps.Carnets && !cancelled())
        {
            var rooms = MaxCarnets is { } mc ? _carnets.Take(mc).ToList() : _carnets;
            var dce = PlanProfile(res);
            for (int i = 0; i < rooms.Count && !cancelled(); i++)
            {
                step($"Carnets de pièces : {i + 1}/{rooms.Count} — {RevitUtil.RoomLabel(rooms[i])}");
                new CarnetGenerator(_doc, _data, dce).Run(new[] { rooms[i] }, report, w);
            }
        }
        if (Steps.Fiches && !cancelled())
        {
            var gen = new FicheGenerator(_doc, _data, Norms);
            var todo = _joinery.Where(j => j.Lot != null).ToList();
            if (MaxFiches is { } mf) todo = todo.Where(j => FicheGenerator.ExistingLabel(j) == null).Take(mf).ToList();
            for (int i = 0; i < todo.Count && !cancelled(); i++)
            {
                if (FicheGenerator.ExistingLabel(todo[i]) != null && todo[i].Fiche == null) { report.Kept.Add($"{todo[i].Mark} : fiche {FicheGenerator.ExistingLabel(todo[i])} conservée"); continue; }
                step($"Fiches menuiseries : {i + 1}/{todo.Count} — {todo[i].Mark}");
                gen.Run(new[] { todo[i] }, report, w);
            }
        }
        if ((Steps.Plans || Steps.Carnets || Steps.Fiches) && !cancelled())
        {
            step("Rangement du navigateur (dossier DCE)…");
            new BrowserFolders(_doc, _data).Arrange(report, w);
        }
        foreach (var e in AiErrors) report.Issue(Severity.ARevoir, "ia", "OpenRouter", e, "Relancer, changer de modèle ou décider manuellement.");
    }

    public void SaveLog(Report report)
    {
        try
        {
            var dir = Path.Combine(_data.OutputDir, "decisions");
            Directory.CreateDirectory(dir);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            Json.Save(Path.Combine(dir, $"{stamp}_decisions.json"), Log);
            File.WriteAllText(Path.Combine(dir, $"{stamp}_decisions.csv"), Log.ToCsv(), new UTF8Encoding(true));
            report.OutputFolder = _data.OutputDir;
        }
        catch (IOException)
        {
            // journal facultatif
        }
    }
}
