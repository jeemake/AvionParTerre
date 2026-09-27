using Autodesk.Revit.DB;

namespace AvionParTerre.Revit.Services;

internal static class Units
{
    public static double Mm(double mm) => UnitUtils.ConvertToInternalUnits(mm, UnitTypeId.Millimeters);

    public static double ToMm(double feet) => UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Millimeters);

    public static double ToM2(double sqFeet) => UnitUtils.ConvertFromInternalUnits(sqFeet, UnitTypeId.SquareMeters);
}
