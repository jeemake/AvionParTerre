using System.Reflection;
using Autodesk.Revit.UI;
using AvionParTerre.Revit.Commands;
using AvionParTerre.Revit.UI;

namespace AvionParTerre.Revit;

/// <summary>Plugin « Avion par terre » — production du dossier graphique DCE Architecture K&amp;D.</summary>
public sealed class App : IExternalApplication
{
    public const string Tab = "Avion par terre";

    public Result OnStartup(UIControlledApplication app)
    {
        try { app.CreateRibbonTab(Tab); } catch (Autodesk.Revit.Exceptions.ArgumentException) { }
        var asm = Assembly.GetExecutingAssembly().Location;

        var avion = app.CreateRibbonPanel(Tab, "Mode avion par terre");
        var big = Theme.Png("avion_32.png");
        var small = Theme.Png("avion_16.png");
        avion.AddItem(new PushButtonData("apt_avion", "Avion\npar terre", asm, typeof(AutopilotCommand).FullName)
        {
            ToolTip = "Production DCE complète en une commande : données de pièces, finitions, plans, carnets, fiches, registre.",
            LongDescription = "Ce que vous et la maquette avez décidé est conservé ; le reste est décidé par le référentiel K&D, des règles, " +
                              "puis par le modèle d'IA choisi sur OpenRouter (Paramètres). Chaque décision est tracée et l'ensemble s'annule en un Ctrl+Z.",
            LargeImage = big ?? Theme.Icon("AT", 32),
            Image = small ?? Theme.Icon("AT", 16),
        });
        Add(avion, "apt_parametres", "Paramètres", typeof(SettingsCommand), "PA", asm,
            "Clé et modèle OpenRouter, étapes du mode avion par terre, ressources imposées et normes du projet.");

        var prep = app.CreateRibbonPanel(Tab, "Préparer");
        Add(prep, "apt_audit", "Audit", typeof(AuditCommand), "AU", asm,
            "Contrôle la maquette : ressources K&D, pièces, menuiseries, feuilles gérées. Lecture seule.");
        Add(prep, "apt_finitions", "Finitions", typeof(FinitionsCommand), "FI", asm,
            "Propose des profils de finitions du référentiel K&D par pièce ; écrit Sol / Mur / Plafond après validation.");

        var prod = app.CreateRibbonPanel(Tab, "Produire");
        Add(prod, "apt_plans", "Plans\ngénéraux", typeof(PlansCommand), "PL", asm,
            "Vues par niveau (gabarits APD-DCE), étiquettes et grandes feuilles A0/A1.");
        Add(prod, "apt_carnets", "Carnets\nde pièces", typeof(CarnetsCommand), "CP", asm,
            "Plan agrandi, 3D découpée, tableau et élévations intérieures sur A3 (D1.00, D1.01…).");
        Add(prod, "apt_fiches", "Fiches\nmenuiseries", typeof(FichesCommand), "FM", asm,
            "Fiches A3 par type (CAL / CB / CS) avec localisations et quantités.");

        var emit = app.CreateRibbonPanel(Tab, "Émettre");
        Add(emit, "apt_registre", "Registre", typeof(RegistreCommand), "RG", asm,
            "Registre documentaire : feuilles, états de préparation, anomalies (JSON + CSV).");
        Add(emit, "apt_export", "Export\nPDF", typeof(ExportCommand), "PDF", asm,
            "Export PDF feuille par feuille et manifeste avec empreintes. Brouillon par défaut.");
        Add(emit, "apt_referentiel", "Référentiel", typeof(ReferentielCommand), "RF", asm,
            "Bibliothèques de finitions, profils et fichiers de configuration utilisés.");
        return Result.Succeeded;
    }

    public Result OnShutdown(UIControlledApplication app) => Result.Succeeded;

    private static void Add(RibbonPanel panel, string name, string text, System.Type cmd, string letters, string asm, string tip)
    {
        var data = new PushButtonData(name, text, asm, cmd.FullName)
        {
            ToolTip = tip,
            LargeImage = Theme.Icon(letters, 32),
            Image = Theme.Icon(letters, 16),
        };
        panel.AddItem(data);
    }
}
