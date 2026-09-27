using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace AvionParTerre.Revit.Services;

internal static class ScheduleUtil
{
    /// <summary>Ajoute un champ de paramètre intégré ; renvoie null si non disponible pour la catégorie.</summary>
    public static ScheduleField? AddBuiltIn(ViewSchedule vs, BuiltInParameter bip, ScheduleFieldType type = ScheduleFieldType.Instance)
    {
        var def = vs.Definition;
        var sf = def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId.Value == (long)bip && f.FieldType == type)
                 ?? def.GetSchedulableFields().FirstOrDefault(f => f.ParameterId.Value == (long)bip);
        return sf == null ? null : def.AddField(sf);
    }

    /// <summary>Ajoute un champ de paramètre partagé/projet par son nom.</summary>
    public static ScheduleField? AddNamed(Document doc, ViewSchedule vs, IEnumerable<string> names)
    {
        var def = vs.Definition;
        foreach (var n in names)
        {
            var sf = def.GetSchedulableFields().FirstOrDefault(f =>
                f.FieldType == ScheduleFieldType.Instance && f.ParameterId.Value > 0 && f.GetName(doc) == n);
            if (sf != null) return def.AddField(sf);
        }
        return null;
    }

    public static ScheduleField? AddCount(ViewSchedule vs)
    {
        var sf = vs.Definition.GetSchedulableFields().FirstOrDefault(f => f.FieldType == ScheduleFieldType.Count);
        return sf == null ? null : vs.Definition.AddField(sf);
    }

    public static ScheduleField? AddRoomField(ViewSchedule vs, ScheduleFieldType roomSide, BuiltInParameter bip)
    {
        var sf = vs.Definition.GetSchedulableFields().FirstOrDefault(f => f.FieldType == roomSide && f.ParameterId.Value == (long)bip);
        return sf == null ? null : vs.Definition.AddField(sf);
    }
}
