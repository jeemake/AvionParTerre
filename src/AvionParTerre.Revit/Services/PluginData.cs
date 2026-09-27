using System;
using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using AvionParTerre.Core.Catalogue;
using AvionParTerre.Core.Profiles;

namespace AvionParTerre.Revit.Services;

/// <summary>
/// Accès au référentiel (profile.json, catalogue.json) et aux dossiers de sortie.
/// Un projet peut surcharger le référentiel en plaçant ses propres fichiers dans « &lt;dossier du .rvt&gt;\AvionParTerre\ ».
/// </summary>
internal sealed class PluginData
{
    public PluginProfile Profile { get; }
    public FinishCatalogue Catalogue { get; }
    public string ProfilePath { get; }
    public string CataloguePath { get; }
    public string OutputDir { get; }
    public ProjectChoices Choices { get; }
    public string ChoicesPath => Path.Combine(OutputDir, "choix-projet.json");
    public UserSettings Settings { get; }

    private PluginData(PluginProfile profile, FinishCatalogue catalogue, string profilePath, string cataloguePath, string outputDir)
    {
        Profile = profile;
        Catalogue = catalogue;
        ProfilePath = profilePath;
        CataloguePath = cataloguePath;
        OutputDir = outputDir;
        Choices = ProjectChoices.Load(ChoicesPath);
        Settings = UserSettings.Load();
    }

    public void SaveChoices() => Core.Json.Save(ChoicesPath, Choices);

    public static string DefaultDataDir =>
        Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!, "data");

    public static string ProjectDataDir(Document doc)
    {
        if (!string.IsNullOrEmpty(doc.PathName) && !doc.IsModelInCloud)
            return Path.Combine(Path.GetDirectoryName(doc.PathName)!, "AvionParTerre");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AvionParTerre");
    }

    public static PluginData Load(Document doc)
    {
        var projectDir = ProjectDataDir(doc);
        string Pick(string file)
        {
            var p = Path.Combine(projectDir, file);
            return File.Exists(p) ? p : Path.Combine(DefaultDataDir, file);
        }
        var profilePath = Pick("profile.json");
        var cataloguePath = Pick("catalogue.json");
        var output = Path.Combine(projectDir, Sanitize(doc.Title));
        return new PluginData(PluginProfile.Load(profilePath), FinishCatalogue.Load(cataloguePath), profilePath, cataloguePath, output);
    }

    public static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Trim();
    }
}
