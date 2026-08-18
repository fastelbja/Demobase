using System.IO;

namespace DemoBase.App.Services;

// ─── Clés de préférence pour les chemins ────────────────────────────────────

public static class PathPreferenceKeys
{
    public const string Bios     = "path.bios";
    public const string Configs  = "path.configs";
    public const string Database = "path.database";
    public const string Releases = "path.releases";
    public const string Working  = "path.working";
}

// ─── Chemins de l'application (configurables par l'utilisateur) ──────────────

/// <summary>
/// Centralise les dossiers principaux de DemoBase.
/// Par défaut : sous-dossiers du répertoire de l'exécutable.
/// Les valeurs peuvent être remplacées par l'utilisateur via le wizard
/// (stockées dans Preferences/config.db).
/// </summary>
public static class AppPaths
{
    private static string _appBase = AppContext.BaseDirectory;

    // Chemins par défaut (avant toute lecture des préférences)
    public static string Bios     { get; private set; } = Path.Combine(AppContext.BaseDirectory, "BIOS");
    public static string Configs  { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Configs");
    public static string Database { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Database");
    public static string Releases { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Releases");
    public static string Working  { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Working");

    /// <summary>
    /// Charge les chemins depuis les préférences utilisateur.
    /// Appelé au démarrage après l'ouverture de config.db.
    /// Les chemins manquants (non encore configurés) gardent leur valeur par défaut.
    /// </summary>
    public static async Task LoadFromPreferencesAsync(DemoBase.Data.PreferencesService prefs)
    {
        static string Resolve(string? stored)
        {
            if (string.IsNullOrWhiteSpace(stored)) return "";
            // Chemins relatifs → absolus par rapport au dossier de l'exécutable
            return Path.IsPathRooted(stored)
                ? stored
                : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, stored));
        }

        var bios     = Resolve(await prefs.GetAsync(PathPreferenceKeys.Bios));
        var configs  = Resolve(await prefs.GetAsync(PathPreferenceKeys.Configs));
        var database = Resolve(await prefs.GetAsync(PathPreferenceKeys.Database));
        var releases = Resolve(await prefs.GetAsync(PathPreferenceKeys.Releases));
        var working  = Resolve(await prefs.GetAsync(PathPreferenceKeys.Working));

        if (!string.IsNullOrEmpty(bios))     Bios     = bios;
        if (!string.IsNullOrEmpty(configs))  Configs  = configs;
        if (!string.IsNullOrEmpty(database)) Database = database;
        if (!string.IsNullOrEmpty(releases)) Releases = releases;
        if (!string.IsNullOrEmpty(working))  Working  = working;
    }

    /// <summary>Sauvegarde les chemins dans les préférences SANS créer les dossiers.</summary>
    public static async Task SaveAsync(
        DemoBase.Data.PreferencesService prefs,
        string bios, string configs, string database, string releases, string working)
    {
        Bios     = bios;
        Configs  = configs;
        Database = database;
        Releases = releases;
        Working  = working;

        await prefs.SetAsync(PathPreferenceKeys.Bios,     bios);
        await prefs.SetAsync(PathPreferenceKeys.Configs,  configs);
        await prefs.SetAsync(PathPreferenceKeys.Database, database);
        await prefs.SetAsync(PathPreferenceKeys.Releases, releases);
        await prefs.SetAsync(PathPreferenceKeys.Working,  working);
    }

    /// <summary>
    /// Crée physiquement tous les dossiers configurés.
    /// Appelé une seule fois à la fin du wizard (page "Prêt !").
    /// </summary>
    public static void CreateDirectories()
    {
        foreach (var dir in new[] { Bios, Configs, Database, Releases, Working,
                                    Path.Combine(AppContext.BaseDirectory, "Emus") })
        {
            try { Directory.CreateDirectory(dir); }
            catch { /* chemin invalide : l'UI le signalera */ }
        }
    }

    /// <summary>
    /// Sauvegarde ET crée les dossiers — pour compatibilité avec les appels existants.
    /// À préférer uniquement depuis la page finale du wizard.
    /// </summary>
    public static async Task SaveAndApplyAsync(
        DemoBase.Data.PreferencesService prefs,
        string bios, string configs, string database, string releases, string working)
    {
        await SaveAsync(prefs, bios, configs, database, releases, working);
        CreateDirectories();
    }

    /// <summary>Valeur par défaut d'un chemin (avant configuration utilisateur).</summary>
    public static string DefaultFor(string key) => key switch
    {
        PathPreferenceKeys.Bios     => Path.Combine(AppContext.BaseDirectory, "BIOS"),
        PathPreferenceKeys.Configs  => Path.Combine(AppContext.BaseDirectory, "Configs"),
        PathPreferenceKeys.Database => Path.Combine(AppContext.BaseDirectory, "Database"),
        PathPreferenceKeys.Releases => Path.Combine(AppContext.BaseDirectory, "Releases"),
        PathPreferenceKeys.Working  => Path.Combine(AppContext.BaseDirectory, "Working"),
        _ => AppContext.BaseDirectory,
    };
}
