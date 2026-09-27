using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace AvionParTerre.Revit.Services;

/// <summary>Identité persistante d'un objet géré par le plugin, stockée dans l'élément (Extensible Storage).</summary>
internal sealed class IdentityData
{
    /// <summary>Clé stable : ex. « PLAN|&lt;UniqueId du niveau&gt; », « CARNET|&lt;UniqueId pièce&gt;|PAGE|0 ».</summary>
    public string Key { get; set; } = "";
    /// <summary>Rôle documentaire : plan_general, carnet_page, carnet_plan, carnet_3d, carnet_elevation, fiche, finitions…</summary>
    public string Role { get; set; } = "";
    /// <summary>Empreinte des propriétés écrites lors de la dernière génération (détection des retouches).</summary>
    public string Generated { get; set; } = "";
    /// <summary>Données complémentaires (JSON).</summary>
    public string Payload { get; set; } = "";
}

internal static class Identity
{
    private static readonly Guid SchemaGuid = new("6C1B2E1A-3F5D-4E8A-9B7C-A1D2E3F40501");
    private const string Vendor = "KOFFIDIABATE";

    private static Schema GetSchema()
    {
        var s = Schema.Lookup(SchemaGuid);
        if (s != null) return s;
        var b = new SchemaBuilder(SchemaGuid);
        b.SetSchemaName("AvionParTerreIdentity");
        b.SetVendorId(Vendor);
        b.SetReadAccessLevel(AccessLevel.Public);
        // Écriture publique : le plugin, ses outils de diagnostic et les scripts agence doivent pouvoir mettre à jour les identités.
        b.SetWriteAccessLevel(AccessLevel.Public);
        b.SetDocumentation("Identité des objets gérés par le plugin Avion par terre (K&D).");
        b.AddSimpleField("Key", typeof(string));
        b.AddSimpleField("Role", typeof(string));
        b.AddSimpleField("Generated", typeof(string));
        b.AddSimpleField("Payload", typeof(string));
        return b.Finish();
    }

    public static IdentityData? Get(Element e)
    {
        var s = Schema.Lookup(SchemaGuid);
        if (s == null) return null;
        var ent = e.GetEntity(s);
        if (!ent.IsValid()) return null;
        return new IdentityData
        {
            Key = ent.Get<string>("Key") ?? "",
            Role = ent.Get<string>("Role") ?? "",
            Generated = ent.Get<string>("Generated") ?? "",
            Payload = ent.Get<string>("Payload") ?? "",
        };
    }

    /// <summary>À appeler dans une transaction ouverte.</summary>
    public static void Set(Element e, IdentityData d)
    {
        var s = GetSchema();
        var ent = new Entity(s);
        ent.Set("Key", d.Key);
        ent.Set("Role", d.Role);
        ent.Set("Generated", d.Generated);
        ent.Set("Payload", d.Payload);
        e.SetEntity(ent);
    }

    /// <summary>Tous les éléments gérés (hors types) indexés par clé.</summary>
    public static Dictionary<string, Element> Index(Document doc)
    {
        var map = new Dictionary<string, Element>(StringComparer.Ordinal);
        if (Schema.Lookup(SchemaGuid) == null) return map;
        foreach (var e in new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid)))
        {
            var d = Get(e);
            if (d != null && d.Key.Length > 0 && !map.ContainsKey(d.Key)) map[d.Key] = e;
        }
        return map;
    }

    public static IEnumerable<(Element Element, IdentityData Data)> All(Document doc)
    {
        if (Schema.Lookup(SchemaGuid) == null) return Enumerable.Empty<(Element, IdentityData)>();
        return new FilteredElementCollector(doc).WherePasses(new ExtensibleStorageFilter(SchemaGuid))
            .Select(e => (e, Get(e)!)).Where(x => x.Item2 != null).ToList();
    }
}
