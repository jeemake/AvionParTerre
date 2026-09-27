using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using AvionParTerre.Core;
using AvionParTerre.Core.Documents;
using AvionParTerre.Core.Issues;

namespace AvionParTerre.Revit.Services;

/// <summary>Registre documentaire (document-register.json / .csv) à partir des feuilles du modèle et des anomalies d'audit.</summary>
internal sealed class RegisterService
{
    private readonly Document _doc;
    private readonly PluginData _data;

    public RegisterService(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
    }

    public static string FamilyOf(string role) => role switch
    {
        "plan_general" => "Plans généraux",
        "coupes_facades" => "Coupes et façades",
        "carnet_page" => "Carnets de pièces",
        "fiche" => "Fiches menuiseries",
        _ => "Autres (hors plugin)",
    };

    public DocumentRegister Build(IReadOnlyList<Issue> audit)
    {
        var managed = Identity.All(_doc).Where(x => x.Element is ViewSheet).ToDictionary(x => x.Element.Id, x => x.Data);
        var byElement = audit.Where(i => i.ElementId.HasValue).ToLookup(i => i.ElementId!.Value);
        var reg = new DocumentRegister
        {
            Projet = _doc.ProjectInformation.get_Parameter(BuiltInParameter.PROJECT_NAME)?.AsString() ?? "",
            DocumentRevit = _doc.Title,
            Date = DateTime.Now,
        };
        foreach (var s in new FilteredElementCollector(_doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Where(s => !s.IsPlaceholder))
        {
            managed.TryGetValue(s.Id, out var d);
            var views = s.GetAllPlacedViews().Select(id => _doc.GetElement(id)).OfType<View>().ToList();
            var tb = RevitUtil.TitleBlockOf(_doc, s);
            var entry = new RegisterEntry
            {
                Cle = d?.Key ?? $"REVIT|{s.UniqueId}",
                Famille = FamilyOf(d?.Role ?? ""),
                Numero = s.SheetNumber,
                Titre = s.Name,
                Format = tb?.Name,
                Echelles = string.Join(", ", views.Where(v => v.ViewType != ViewType.ThreeD && v.ViewType != ViewType.Schedule && v.Scale > 0)
                    .Select(v => "1:" + v.Scale).Distinct()),
                Vues = views.Select(v => v.Name).ToList(),
                RevitUniqueId = s.UniqueId,
            };
            // Anomalies rattachées à la feuille ou à ses sources (pièces, types) via l'identité
            var related = byElement[s.Id.Value].ToList();
            if (d?.Role == "carnet_page" && d.Key.Split('|') is { Length: > 1 } parts && _doc.GetElement(parts[1]) is Element room)
            {
                related.AddRange(byElement[room.Id.Value]);
                entry.Sources.Add($"Pièce {(room as Autodesk.Revit.DB.Architecture.Room)?.Number} {room.Name}");
            }
            if (d?.Role == "fiche" && d.Key.Split('|') is { Length: > 1 } fp && _doc.GetElement(fp[1]) is FamilySymbol fs)
            {
                related.AddRange(byElement[fs.Id.Value]);
                entry.Sources.Add($"Type {fs.FamilyName} : {fs.Name}");
            }
            entry.Anomalies = related.Select(i => $"[{i.GraviteLibelle}] {i.Message}").Distinct().ToList();
            entry.Etat = related.Any(i => i.Gravite is Severity.Bloquant or Severity.EmissionBloquee)
                ? PreparationState.ACompleter
                : related.Any(i => i.Gravite == Severity.ARevoir) ? PreparationState.Prepare : PreparationState.PretPourRevue;
            reg.Documents.Add(entry);
        }
        return reg;
    }

    public (string Json, string Csv) Save(DocumentRegister reg)
    {
        Directory.CreateDirectory(_data.OutputDir);
        var json = Path.Combine(_data.OutputDir, "document-register.json");
        var csv = Path.Combine(_data.OutputDir, "document-register.csv");
        Json.Save(json, reg);
        File.WriteAllText(csv, reg.ToCsv(), new UTF8Encoding(true));
        return (json, csv);
    }
}

internal sealed class IssuedFile
{
    public string Numero { get; set; } = "";
    public string Titre { get; set; } = "";
    public string Fichier { get; set; } = "";
    public long Octets { get; set; }
    public string Sha256 { get; set; } = "";
    public string? Format { get; set; }
}

internal sealed class IssueManifest
{
    public string Schema { get; set; } = "avion-par-terre/issue-manifest";
    public int SchemaVersion { get; set; } = 1;
    public string DocumentRevit { get; set; } = "";
    public string Statut { get; set; } = "brouillon";
    public string? ValidePar { get; set; }
    public DateTime Date { get; set; } = DateTime.Now;
    public string ProfilVersion { get; set; } = "";
    public List<IssuedFile> Fichiers { get; set; } = new();
    public List<string> AnomaliesAcceptees { get; set; } = new();
}

/// <summary>Export PDF feuille par feuille (format réel du cartouche) et manifeste avec empreintes.</summary>
internal sealed class PdfExporter
{
    private readonly Document _doc;
    private readonly PluginData _data;

    public PdfExporter(Document doc, PluginData data)
    {
        _doc = doc;
        _data = data;
    }

    public IssueManifest Export(IReadOnlyList<ViewSheet> sheets, string folder, bool validated, string author, Report report)
    {
        Directory.CreateDirectory(folder);
        var manifest = new IssueManifest
        {
            DocumentRevit = _doc.Title,
            Statut = validated ? "emission_validee" : "brouillon",
            ValidePar = validated ? author : null,
            ProfilVersion = _data.Profile.Version,
        };
        foreach (var s in sheets.OrderBy(s => s.SheetNumber, StringComparer.Ordinal))
        {
            var name = PluginData.Sanitize($"{s.SheetNumber}_{s.Name}" + (validated ? "" : "_BROUILLON"));
            var opts = new PDFExportOptions
            {
                FileName = name,
                Combine = true,
                PaperFormat = ExportPaperFormat.Default,
                ZoomType = ZoomType.Zoom,
                ZoomPercentage = 100,
                HideCropBoundaries = true,
                HideScopeBoxes = true,
                HideReferencePlane = true,
                HideUnreferencedViewTags = true,
                ColorDepth = ColorDepthType.Color,
                RasterQuality = RasterQualityType.High,
            };
            try
            {
                if (!_doc.Export(folder, new List<ElementId> { s.Id }, opts))
                {
                    report.Issue(Severity.Bloquant, "export", s.SheetNumber, "Export PDF refusé par Revit.", "Vérifier la feuille.", s.Id.Value);
                    continue;
                }
                var path = Path.Combine(folder, name + ".pdf");
                if (!File.Exists(path))
                {
                    report.Issue(Severity.Bloquant, "export", s.SheetNumber, "Fichier PDF absent après export.", "Relancer l'export.", s.Id.Value);
                    continue;
                }
                var bytes = File.ReadAllBytes(path);
                manifest.Fichiers.Add(new IssuedFile
                {
                    Numero = s.SheetNumber,
                    Titre = s.Name,
                    Fichier = Path.GetFileName(path),
                    Octets = bytes.LongLength,
                    Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    Format = RevitUtil.TitleBlockOf(_doc, s)?.Name,
                });
                report.Created.Add($"{Path.GetFileName(path)}");
            }
            catch (Exception ex)
            {
                report.Issue(Severity.Bloquant, "export", s.SheetNumber, ex.Message, "Relancer l'export de cette feuille.", s.Id.Value);
            }
        }
        var mpath = Path.Combine(folder, "issue-manifest.json");
        Json.Save(mpath, manifest);
        report.Created.Add("issue-manifest.json");
        report.OutputFolder = folder;
        return manifest;
    }
}
