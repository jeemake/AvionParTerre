using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using AvionParTerre.Core.Issues;

namespace AvionParTerre.Revit.Services;

/// <summary>Audit d'entrée (§5) : ressources K&amp;D, pièces, menuiseries, feuilles gérées, informations projet. Aucune écriture.</summary>
internal sealed class AuditService
{
    private readonly Document _doc;
    private readonly PluginData _data;

    public AuditService(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
    }

    public List<Issue> Run()
    {
        var issues = new List<Issue>();
        Resources(issues);
        ProjectInfo(issues);
        Rooms(issues);
        Joinery(issues);
        ManagedSheets(issues);
        return issues.OrderBy(i => i.Gravite).ThenBy(i => i.Regle).ThenBy(i => i.Objet).ToList();
    }

    private void Resources(List<Issue> issues)
    {
        var res = new KdResources(_doc, _data);
        foreach (var (role, id) in res.ResolveAll().ToList())
            if (id == ElementId.InvalidElementId && role.Key.StartsWith("cartouche"))
                issues.Add(new Issue(Severity.Bloquant, "ressource_kd", role.Label,
                    $"Aucun cartouche disponible ({string.Join(" / ", _data.Profile.Resource(role.Key))}).",
                    "Charger un cartouche K&D ou en choisir un dans Paramètres > Ressources."));
        issues.AddRange(res.Missing.GroupBy(m => (m.Objet, m.Message)).Select(g => g.First()));
        foreach (var line in ProjectNorms.Detect(_doc, _data).Describe(_doc))
            issues.Add(new Issue(Severity.Information, "normes_projet", "Maquette", line, "Appliqué automatiquement par les commandes de production."));
        foreach (var key in new[] { "piece_niv", "piece_hsp", "piece_hsd" })
        {
            var names = _data.Profile.Parameter(key);
            var room = RevitUtil.Rooms(_doc).FirstOrDefault();
            if (room != null && RevitUtil.Param(room, names) == null)
                issues.Add(new Issue(Severity.EmissionBloquee, "parametre_absent", string.Join("/", names),
                    "Paramètre de pièce absent du modèle.", "Ajouter le paramètre partagé agence aux pièces (pas de doublon de libellé)."));
        }
    }

    private void ProjectInfo(List<Issue> issues)
    {
        var pi = _doc.ProjectInformation;
        foreach (var (bip, label) in new[] { (BuiltInParameter.PROJECT_NAME, "Nom du projet"), (BuiltInParameter.PROJECT_NUMBER, "Numéro de projet (affaire)"), (BuiltInParameter.CLIENT_NAME, "Nom du client") })
            if (string.IsNullOrWhiteSpace(pi.get_Parameter(bip)?.AsString()))
                issues.Add(new Issue(Severity.EmissionBloquee, "info_projet", label, "Information projet non renseignée (cartouches).",
                    "Renseigner Gérer > Informations sur le projet.", pi.Id.Value));
    }

    private void Rooms(List<Issue> issues)
    {
        var rooms = RevitUtil.Rooms(_doc).ToList();
        var p = _data.Profile;
        foreach (var r in rooms)
        {
            var label = RevitUtil.RoomLabel(r);
            if (!RevitUtil.IsPlaced(r))
            {
                issues.Add(new Issue(Severity.Information, "piece_non_placee", label, "Pièce non placée.", "Placer ou supprimer la pièce.", r.Id.Value));
                continue;
            }
            if (!RevitUtil.IsEnclosed(r))
            {
                issues.Add(new Issue(Severity.ARevoir, "piece_non_fermee", label, "Pièce non fermée ou redondante (surface nulle).",
                    "Fermer les limites de la pièce.", r.Id.Value));
                continue;
            }
            var missing = new List<string>();
            foreach (var (lbl, key) in new[] { ("Niv", "piece_niv"), ("HSP", "piece_hsp"), ("HSD", "piece_hsd") })
                if (RevitUtil.Param(r, p.Parameter(key)) is { } par && string.IsNullOrWhiteSpace(par.StorageType == StorageType.String ? par.AsString() : par.AsValueString()))
                    missing.Add(lbl);
            if (missing.Count > 0)
                issues.Add(new Issue(Severity.EmissionBloquee, "donnees_piece", label, $"{string.Join(", ", missing)} non renseigné(s).",
                    "Renseigner les hauteurs/niveaux validés (HSP distincte de HSD).", r.Id.Value));
            var noFinish = new[] { ("Sol", BuiltInParameter.ROOM_FINISH_FLOOR), ("Mur", BuiltInParameter.ROOM_FINISH_WALL), ("Plfd", BuiltInParameter.ROOM_FINISH_CEILING) }
                .Where(x => string.IsNullOrWhiteSpace(r.get_Parameter(x.Item2)?.AsString())).Select(x => x.Item1).ToList();
            if (noFinish.Count > 0)
                issues.Add(new Issue(Severity.EmissionBloquee, "finitions_piece", label, $"Finitions non renseignées : {string.Join(", ", noFinish)}.",
                    "Commande Finitions : choisir et valider un profil de référence.", r.Id.Value));
            else if (FinishService.State(r) == "retouché manuellement")
                issues.Add(new Issue(Severity.Information, "finitions_retouchees", label, "Finitions modifiées depuis la dernière validation.",
                    "Vérifier la cohérence avec la grille de finition.", r.Id.Value));
        }
        foreach (var g in rooms.Where(RevitUtil.IsPlaced).GroupBy(r => (r.LevelId, r.Number)).Where(g => g.Count() > 1))
            issues.Add(new Issue(Severity.EmissionBloquee, "numero_piece_duplique", $"N° {g.Key.Number}",
                $"{g.Count()} pièces portent le même numéro sur le niveau {(_doc.GetElement(g.Key.LevelId) as Level)?.Name}.",
                "Renuméroter : l'identité d'un local ne repose pas sur son nom.", g.First().Id.Value));
    }

    private void Joinery(List<Issue> issues)
    {
        var gen = new FicheGenerator(_doc, _data);
        foreach (var jt in gen.Collect())
        {
            if (jt.Lot == null)
                issues.Add(new Issue(Severity.ARevoir, "lot_menuiserie", $"{jt.Symbol.FamilyName} : {jt.Mark}",
                    "Repère sans lot reconnu (préfixe hors convention).", "Renommer le type, compléter les préfixes du profil ou laisser le mode avion par terre décider.", jt.Symbol.Id.Value));
            if (jt.Ambiguous)
                issues.Add(new Issue(Severity.EmissionBloquee, "repere_ambigu", jt.Mark,
                    $"Repère porté par plusieurs familles ({jt.Symbol.FamilyName}…).", "Un repère = un type d'ouvrage dans un périmètre.", jt.Symbol.Id.Value));
            if (jt.Lot != null && FicheGenerator.ExistingLabel(jt) == null)
                issues.Add(new Issue(Severity.EmissionBloquee, "ouvrage_sans_fiche", jt.Mark, $"{jt.Instances.Count} occurrence(s) sans fiche menuiserie.",
                    "Commande Fiches menuiseries.", jt.Symbol.Id.Value));
        }
    }

    private void ManagedSheets(List<Issue> issues)
    {
        var composer = new SheetComposer(_doc, _data.Profile.MiseEnPage);
        foreach (var (e, d) in Identity.All(_doc))
        {
            if (e is not ViewSheet s) continue;
            foreach (var vp in new FilteredElementCollector(_doc, s.Id).OfClass(typeof(Viewport)).Cast<Viewport>())
                if (!composer.FitsInArea(s, vp))
                    issues.Add(new Issue(Severity.ARevoir, "debordement", $"{s.SheetNumber} {s.Name}",
                        $"La vue « {_doc.GetElement(vp.ViewId).Name} » dépasse la zone de dessin.", "Recomposer la feuille.", s.Id.Value));
        }
        foreach (var (e, d) in Identity.All(_doc).Where(x => x.Data.Role.StartsWith("carnet_") && x.Element is View v && !(x.Element is ViewSheet)))
        {
            if (e is View v && !(v is ViewSchedule) && !RevitUtil.IsOnSheet(v))
                issues.Add(new Issue(Severity.ARevoir, "vue_non_placee", v.Name, "Vue de carnet générée mais non placée sur une feuille.",
                    "Relancer Carnets de pièces ou placer la vue.", v.Id.Value));
        }
    }
}
