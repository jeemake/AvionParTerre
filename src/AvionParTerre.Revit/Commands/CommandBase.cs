using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AvionParTerre.Revit.Services;

namespace AvionParTerre.Revit.Commands;

[Transaction(TransactionMode.Manual)]
[Regeneration(RegenerationOption.Manual)]
public abstract class CommandBase : IExternalCommand
{
    protected abstract string Title { get; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var uidoc = commandData.Application.ActiveUIDocument;
        if (uidoc == null || uidoc.Document.IsFamilyDocument)
        {
            TaskDialog.Show("Avion par terre", "Ouvrir une maquette de projet Revit (pas une famille).");
            return Result.Cancelled;
        }
        try
        {
            var data = PluginData.Load(uidoc.Document);
            return Run(uidoc, data);
        }
        catch (Exception ex)
        {
            var td = new TaskDialog("Avion par terre")
            {
                MainInstruction = $"{Title} : erreur",
                MainContent = ex.Message,
                ExpandedContent = ex.ToString(),
            };
            td.Show();
            return Result.Failed;
        }
    }

    internal abstract Result Run(UIDocument uidoc, PluginData data);

    internal static void Show(Report report)
    {
        UI.ReportWindow.Show(report.Title, report.ToText(), report.OutputFolder);
    }

    internal static void SaveLog(PluginData data, Report report)
    {
        try
        {
            var dir = System.IO.Path.Combine(data.OutputDir, "journal");
            System.IO.Directory.CreateDirectory(dir);
            var file = System.IO.Path.Combine(dir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{PluginData.Sanitize(report.Title)}.txt");
            System.IO.File.WriteAllText(file, report.ToText(), new System.Text.UTF8Encoding(true));
            report.OutputFolder ??= data.OutputDir;
        }
        catch
        {
            // Le journal est facultatif : un dossier non accessible ne doit pas masquer le rapport.
        }
    }
}
