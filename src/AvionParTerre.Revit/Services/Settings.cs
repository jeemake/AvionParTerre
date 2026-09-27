using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Serialization;
using AvionParTerre.Core;
using AvionParTerre.Core.Ai;

namespace AvionParTerre.Revit.Services;

/// <summary>Étapes du mode avion par terre.</summary>
internal sealed class AutopilotSteps
{
    public bool DonneesPieces { get; set; } = true;
    public bool Finitions { get; set; } = true;
    public bool Plans { get; set; } = true;
    public bool Carnets { get; set; } = true;
    public bool Fiches { get; set; } = true;
    public bool Registre { get; set; } = true;
    public bool ExportPdf { get; set; }
}

/// <summary>
/// Paramètres de l'utilisateur (%APPDATA%\AvionParTerre\settings.json). La clé OpenRouter est chiffrée avec DPAPI :
/// elle n'est lisible que par ce compte Windows sur ce poste.
/// </summary>
internal sealed class UserSettings
{
    public string Schema { get; set; } = "avion-par-terre/settings";
    public int SchemaVersion { get; set; } = 1;
    public string? CleOpenrouterChiffree { get; set; }
    public string Modele { get; set; } = "anthropic/claude-sonnet-5";
    public bool RechercheWeb { get; set; } = true;
    public int ResultatsWeb { get; set; } = 3;
    public double Temperature { get; set; } = 0.2;
    public int TaillePaquet { get; set; } = 20;
    public bool ConfirmerAvantEcriture { get; set; } = true;
    public string? Auteur { get; set; }
    public AutopilotSteps Etapes { get; set; } = new();
    /// <summary>Modèles récemment utilisés (liste déroulante).</summary>
    public List<string> ModelesRecents { get; set; } = new();

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AvionParTerre");
    public static string FilePath => Path.Combine(Folder, "settings.json");

    public static UserSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return Json.Load<UserSettings>(FilePath);
        }
        catch
        {
            // Fichier illisible : paramètres par défaut (la clé devra être ressaisie)
        }
        return new UserSettings();
    }

    public void Save()
    {
        if (!ModelesRecents.Contains(Modele)) ModelesRecents.Insert(0, Modele);
        if (ModelesRecents.Count > 10) ModelesRecents.RemoveRange(10, ModelesRecents.Count - 10);
        Json.Save(FilePath, this);
    }

    [JsonIgnore]
    public string? ApiKey
    {
        get => Dpapi.Unprotect(CleOpenrouterChiffree);
        set => CleOpenrouterChiffree = string.IsNullOrWhiteSpace(value) ? null : Dpapi.Protect(value.Trim());
    }

    [JsonIgnore]
    public bool HasKey => !string.IsNullOrEmpty(ApiKey);

    [JsonIgnore]
    public string AuthorOrUser => string.IsNullOrWhiteSpace(Auteur) ? Environment.UserName : Auteur!;

    public AiOptions AiOptions() => new()
    {
        Model = Modele,
        WebSearch = RechercheWeb,
        WebMaxResults = ResultatsWeb,
        Temperature = Temperature,
        BatchSize = TaillePaquet,
    };
}

/// <summary>Choix propres à une maquette : ressources imposées par l'utilisateur, bibliothèque et profil documentaire.</summary>
internal sealed class ProjectChoices
{
    public string Schema { get; set; } = "avion-par-terre/choix-projet";
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Rôle de ressource (clé de profile.json) → « Famille:Type » ou nom choisi dans Paramètres.</summary>
    public Dictionary<string, string> Ressources { get; set; } = new();
    public string? Bibliotheque { get; set; }
    public string? ProfilDce { get; set; }

    public static ProjectChoices Load(string path)
    {
        try
        {
            if (File.Exists(path)) return Json.Load<ProjectChoices>(path);
        }
        catch
        {
            // Choix illisibles : ignorés
        }
        return new ProjectChoices();
    }
}

internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob pDataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private const int UiForbidden = 0x1;

    public static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        return Convert.ToBase64String(Run(bytes, protect: true));
    }

    public static string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher)) return null;
        try { return Encoding.UTF8.GetString(Run(Convert.FromBase64String(cipher), protect: false)); }
        catch { return null; }
    }

    private static byte[] Run(byte[] input, bool protect)
    {
        var inBlob = new DataBlob { cbData = input.Length, pbData = Marshal.AllocHGlobal(input.Length) };
        var outBlob = new DataBlob();
        try
        {
            Marshal.Copy(input, 0, inBlob.pbData, input.Length);
            var ok = protect
                ? CryptProtectData(ref inBlob, "Avion par terre", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref outBlob);
            if (!ok) throw new InvalidOperationException("DPAPI : échec " + Marshal.GetLastWin32Error());
            var result = new byte[outBlob.cbData];
            Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inBlob.pbData);
            if (outBlob.pbData != IntPtr.Zero) LocalFree(outBlob.pbData);
        }
    }
}
