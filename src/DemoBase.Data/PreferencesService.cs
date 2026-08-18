using Microsoft.Data.Sqlite;

namespace DemoBase.Data;

// ─── Clés ────────────────────────────────────────────────────────────────────

public static class PrefKeys
{
    public const string PathConfigs    = "path.configs";
    public const string PathEmulators  = "path.emulators";
    public const string PathImages     = "path.images";
    public const string PathDats       = "path.dats";
    public const string Theme          = "ui.theme";       // "Light" | "Dark"
    public const string DemoEffects    = "ui.demoeffects"; // "true" | "false"
    public const string Language       = "ui.language";     // "fr" | "en"
    public const string PathReleases   = "path.releases";
    public const string PathRecoil2Png = "path.recoil2png"; // chemin vers recoil2png.exe
    // Seuil de vues au-delà duquel une release est automatiquement ajoutée aux favoris.
    // 0 = désactivé. Stocké dans la table Preferences ("config" pour l'utilisateur).
    public const string AutoFavoriteViewThreshold = "release.autofavoriteviewthreshold";
    public const string SlideshowDuration         = "mediabrowser.slideshowduration";
    // "true" une fois l'assistant de configuration initiale entièrement terminé
    // (bouton Terminer cliqué à la dernière étape). Distinct de "la base a des
    // données" : un utilisateur peut avoir importé la base Demozoo (étape 3)
    // sans avoir fini les étapes suivantes (émulateurs, outils externes, DATS),
    // auquel cas le wizard doit pouvoir se rouvrir au prochain lancement.
    public const string WizardCompleted  = "wizard.completed";
    public const string ConfigsVersion   = "configs.version";  // version du dernier import configs Mega
    // 2026-07-27, demande utilisateur : même protocole que ConfigsVersion ci-dessus, mais
    // pour le catalogue DATs (dats_version.txt sur Mega, sous-dossier "DATS") — cf.
    // DatsUpdateService.
    public const string DatsVersion      = "dats.version";
    // "true" une fois que l'utilisateur a coché "Ne plus demander" sur la confirmation de
    // téléchargement ad-hoc (release pas encore couverte par un DAT, lancée directement
    // depuis son lien Demozoo) — cf. ExternalDownloadConfirmWindow, 2026-07-25.
    public const string SkipExternalDownloadConfirm = "release.skipexternaldownloadconfirm";
    // 2026-08-01, demande utilisateur ("système de mise à jour automatique de
    // l'application en utilisant mon compte mega, répertoire 'Updates'") : dernière
    // version de l'APPLICATION ELLE-MÊME appliquée avec succès (distinct de
    // ConfigsVersion/DatsVersion ci-dessus, qui versionnent des données, pas les
    // binaires). Écrite uniquement par AppUpdateService.FinalizePendingUpdateAsync,
    // après confirmation qu'une copie de mise à jour a réellement réussi (jamais
    // optimiste — cf. commentaire de classe AppUpdateService).
    public const string AppVersion = "app.version";
    // 2026-08-02, demande utilisateur ("il faudrait garder l'affiche de l'info dans
    // les préférences de sorte à ce que l'utilisateur puisse choisir") : mémorise si
    // le panneau "Infos" (ordre/patterns/instruments) du lecteur de musique tracker
    // doit être ouvert par défaut — cf. SoundtrackPlayerView.xaml.cs, PreferencesService
    // .SetInfoPanelOpenAsync/LastInfoPanelOpen. Clé indépendante de AppPreferences/
    // SaveAllAsync (écrite/lue directement) pour ne pas risquer d'écraser les autres
    // préférences avec un objet AppPreferences potentiellement obsolète en mémoire côté
    // vue, qui n'a pas accès à la page Réglages.
    public const string PlayerInfoPanelOpen = "player.infopanelopen";
    // 2026-08-06, demande utilisateur ("on avait mis une case à coché et un slider pour
    // la separation stereo pour uade. peux tu le rajouter dans les preferences et le
    // gérer au niveau du player ?") : "Panoramique stéréo" UADE (UC_PANNING_VALUE côté
    // libuade.dll — cf. TrackerPlayer.Core.Players.UadePlayer.PanningEnabled/
    // PanningAmount/SetPanning). Même schéma que PlayerInfoPanelOpen ci-dessus (clés
    // indépendantes de AppPreferences/SaveAllAsync, écrites/lues directement).
    public const string UadePanningEnabled = "uade.panningenabled";
    public const string UadePanningAmount  = "uade.panningamount";
    // 2026-08-07, retour utilisateur ("les musiques venant de uade sont generalement
    // moins forte en volume [...] il faudrait pouvoir le regler et garder la valeur
    // dans les preferences. mets un slider dans l'écran de préférences. l'écran de
    // lecture est déjà bien chargé") : gain UADE (UC_GAIN côté libuade.dll, cf.
    // TrackerPlayer.Core.Players.UadePlayer.GainAmount), rendu réglable après un
    // premier défaut fixe (1.8) livré la veille. Contrairement à
    // UadePanningEnabled/UadePanningAmount ci-dessus (réglés depuis le player lui-même,
    // écriture ciblée immédiate), celui-ci vit sur l'écran Préférences — donc rangé
    // avec le reste des champs dans AppPreferences/LoadAllAsync/SaveAllAsync, appliqué
    // uniquement au clic sur "Sauvegarder" (même flux que Thème/Langue/Effets démo).
    public const string UadeGainAmount = "uade.gainamount";
}

// ─── Service ─────────────────────────────────────────────────────────────────

public class PreferencesService
{
    private readonly string _connectionString;

    // Valeurs par défaut calculées à partir du répertoire de l'exe
    public static string DefaultPathConfigs   => Path.Combine(AppContext.BaseDirectory, "Configs");
    public static string DefaultPathEmulators => Path.Combine(AppContext.BaseDirectory, "Emus");
    public static string DefaultPathImages    => Path.Combine(AppContext.BaseDirectory, "Images");
    public static string DefaultPathDats      => Path.Combine(AppContext.BaseDirectory, "DATS");
    public static string DefaultPathReleases  => Path.Combine(AppContext.BaseDirectory, "Releases");
    public static string DefaultPathRecoil2Png => string.Empty;

    // 2026-08-02 : référence statique vers le singleton — permet à des vues sans
    // accès DI direct (ex. SoundtrackPlayerView, construite via `new` depuis
    // plusieurs ViewModels sans PreferencesService injecté) de persister de petites
    // préférences ponctuelles (cf. SetInfoPanelOpenAsync ci-dessous), même style
    // pragmatique que LastResolvedPathReleases ci-dessous pour les converters XAML.
    public static PreferencesService? Instance { get; private set; }

    public PreferencesService(string connectionString)
    {
        _connectionString = connectionString;
        Instance = this;
    }

    // ── Lecture ──────────────────────────────────────────────────────────────

    public async Task<string?> GetAsync(string key)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Value" FROM "Preferences" WHERE "Key"=@k LIMIT 1;""";
        cmd.Parameters.AddWithValue("@k", key);
        return await cmd.ExecuteScalarAsync() as string;
    }

    public async Task<string> GetAsync(string key, string defaultValue)
        => await GetAsync(key) ?? defaultValue;

    // ── Écriture ─────────────────────────────────────────────────────────────

    public async Task SetAsync(string key, string? value)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO "Preferences"("Key","Value") VALUES(@k,@v)
            ON CONFLICT("Key") DO UPDATE SET "Value"=excluded."Value";
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@v", (object?)value ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync();
    }

    // ── Chargement groupé ────────────────────────────────────────────────────

    /// <summary>
    /// Cache statique du dernier ResolvedPathReleases chargé — permet un accès
    /// synchrone depuis les value converters XAML (ex. coloration de la liste
    /// Files selon la présence du fichier), qui ne peuvent pas await ni
    /// recevoir ce service par injection de dépendances facilement.
    /// Mis à jour à chaque LoadAllAsync()/SaveAllAsync().
    /// </summary>
    public static string LastResolvedPathReleases { get; private set; } = DefaultPathReleases;

    /// <summary>Cache statique du choix "panneau Infos ouvert par défaut" — même
    /// besoin d'accès synchrone que LastResolvedPathReleases ci-dessus, ici pour
    /// SoundtrackPlayerView.xaml.cs qui n'a pas de PreferencesService injecté.
    /// True par défaut (comportement historique avant l'ajout de cette préférence).</summary>
    public static bool LastInfoPanelOpen { get; private set; } = true;

    /// <summary>Cache statique "panoramique stéréo UADE activé" — même besoin d'accès
    /// synchrone que ci-dessus, ici pour TrackerPlayer.Core.Players.UadePlayer
    /// (statique, ne référence pas DemoBase.Data) via SoundtrackPlayerViewModel. False
    /// par défaut (son Amiga brut, comportement historique).</summary>
    public static bool   LastUadePanningEnabled { get; private set; } = false;
    /// <summary>Cache statique de l'intensité du panoramique UADE (0.0-2.0). 0.7 par
    /// défaut — réglage historique d'UADE quand l'effet est actif.</summary>
    public static double LastUadePanningAmount  { get; private set; } = 0.7;

    /// <summary>Cache statique du gain UADE (cf. PrefKeys.UadeGainAmount) — même besoin
    /// d'accès synchrone que ci-dessus, pour TrackerPlayer.Core.Players.UadePlayer
    /// (statique, ne référence pas DemoBase.Data) via SoundtrackPlayerViewModel. 1.6 par
    /// défaut pour les nouvelles installations (2026-08-07, retour utilisateur) — les
    /// utilisateurs existants gardent la valeur déjà persistée en base.</summary>
    public static double LastUadeGainAmount { get; private set; } = 1.6;

    public async Task<AppPreferences> LoadAllAsync()
    {
        var prefs = new AppPreferences();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """SELECT "Key","Value" FROM "Preferences";""";
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key = reader.GetString(0);
            var val = reader.IsDBNull(1) ? null : reader.GetString(1);
            switch (key)
            {
                case PrefKeys.PathConfigs:   prefs.PathConfigs   = val ?? prefs.PathConfigs;   break;
                case PrefKeys.PathEmulators: prefs.PathEmulators = val ?? prefs.PathEmulators; break;
                case PrefKeys.PathImages:    prefs.PathImages     = val ?? prefs.PathImages;    break;
                case PrefKeys.PathDats:      prefs.PathDats       = val ?? prefs.PathDats;      break;
                case PrefKeys.PathReleases:  prefs.PathReleases   = val ?? prefs.PathReleases;  break;
                case PrefKeys.PathRecoil2Png: prefs.PathRecoil2Png = val ?? prefs.PathRecoil2Png; break;
                case PrefKeys.Theme:         prefs.Theme          = val ?? prefs.Theme;         break;
                case PrefKeys.DemoEffects:   prefs.DemoEffects    = val == "true"; break;
                case PrefKeys.Language:      prefs.Language       = val ?? prefs.Language;  break;
                case PrefKeys.AutoFavoriteViewThreshold:
                    prefs.AutoFavoriteViewThreshold = int.TryParse(val, out var th) ? th : 0;
                    break;
                case PrefKeys.SlideshowDuration:
                    prefs.SlideshowDurationSeconds = int.TryParse(val, out var sd) ? sd : 5;
                    break;
                case PrefKeys.WizardCompleted:
                    prefs.WizardCompleted = val == "true";
                    break;
                case PrefKeys.SkipExternalDownloadConfirm:
                    prefs.SkipExternalDownloadConfirm = val == "true";
                    break;
                case PrefKeys.PlayerInfoPanelOpen:
                    prefs.PlayerInfoPanelOpen = val == "true";
                    break;
                case PrefKeys.UadePanningEnabled:
                    prefs.UadePanningEnabled = val == "true";
                    break;
                case PrefKeys.UadePanningAmount:
                    prefs.UadePanningAmount = double.TryParse(val,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var pa) ? pa : 0.7;
                    break;
                case PrefKeys.UadeGainAmount:
                    prefs.UadeGainAmount = double.TryParse(val,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var ga) ? ga : 1.6;
                    break;
            }
        }
        LastResolvedPathReleases  = prefs.ResolvedPathReleases;
        LastInfoPanelOpen         = prefs.PlayerInfoPanelOpen;
        LastUadePanningEnabled    = prefs.UadePanningEnabled;
        LastUadePanningAmount     = prefs.UadePanningAmount;
        LastUadeGainAmount        = prefs.UadeGainAmount;
        return prefs;
    }

    /// <summary>Persiste le choix utilisateur d'afficher ou non le panneau "Infos"
    /// (ordre/patterns/instruments) du lecteur de musique tracker — cf.
    /// SoundtrackPlayerView.xaml.cs (BtnToggleInfo_Click). Écriture ciblée sur une
    /// seule clé plutôt que SaveAllAsync : la vue n'a pas accès à l'objet
    /// AppPreferences complet chargé par la page Réglages, et un SaveAllAsync ici
    /// écraserait les autres préférences avec des valeurs potentiellement obsolètes.</summary>
    public async Task SetInfoPanelOpenAsync(bool open)
    {
        LastInfoPanelOpen = open;
        await SetAsync(PrefKeys.PlayerInfoPanelOpen, open ? "true" : "false");
    }

    // 2026-08-07, retour utilisateur ("enleve le [panoramique] du player. Crée du coup
    // une section 'UADE' dans les préférences") : SetUadePanningEnabledAsync/
    // SetUadePanningAmountAsync (écritures ciblées depuis le player, cf. l'historique
    // de ce fichier) ont été retirées — le panoramique UADE vit désormais sur l'écran
    // Préférences comme le gain (PrefKeys.UadeGainAmount ci-dessus), donc persisté via
    // SaveAllAsync ci-dessous, au même rythme que le reste de cette page.

    // ── Sauvegarde groupée ───────────────────────────────────────────────────

    public async Task SaveAllAsync(AppPreferences prefs)
    {
        await SetAsync(PrefKeys.PathConfigs,   prefs.PathConfigs);
        await SetAsync(PrefKeys.PathEmulators, prefs.PathEmulators);
        await SetAsync(PrefKeys.PathImages,    prefs.PathImages);
        await SetAsync(PrefKeys.PathDats,      prefs.PathDats);
        await SetAsync(PrefKeys.PathReleases,  prefs.PathReleases);
        await SetAsync(PrefKeys.PathRecoil2Png, prefs.PathRecoil2Png);
        await SetAsync(PrefKeys.Theme,         prefs.Theme);
        await SetAsync(PrefKeys.DemoEffects,   prefs.DemoEffects ? "true" : "false");
        await SetAsync(PrefKeys.Language,      prefs.Language);
        await SetAsync(PrefKeys.AutoFavoriteViewThreshold, prefs.AutoFavoriteViewThreshold.ToString());
        await SetAsync(PrefKeys.SlideshowDuration, prefs.SlideshowDurationSeconds.ToString());
        await SetAsync(PrefKeys.SkipExternalDownloadConfirm, prefs.SkipExternalDownloadConfirm ? "true" : "false");
        await SetAsync(PrefKeys.UadeGainAmount, prefs.UadeGainAmount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await SetAsync(PrefKeys.UadePanningEnabled, prefs.UadePanningEnabled ? "true" : "false");
        await SetAsync(PrefKeys.UadePanningAmount,
            prefs.UadePanningAmount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        LastResolvedPathReleases = prefs.ResolvedPathReleases;
        LastUadeGainAmount       = prefs.UadeGainAmount;
        LastUadePanningEnabled   = prefs.UadePanningEnabled;
        LastUadePanningAmount    = prefs.UadePanningAmount;
    }

    /// <summary>Marque l'assistant de configuration initiale comme terminé —
    /// appelé une seule fois, quand l'utilisateur clique "Terminer" sur la
    /// dernière page du wizard.</summary>
    public Task MarkWizardCompletedAsync() => SetAsync(PrefKeys.WizardCompleted, "true");
}

// ─── DTO ─────────────────────────────────────────────────────────────────────

public class AppPreferences
{
    public string? PathConfigs   { get; set; }
    public string? PathEmulators { get; set; }
    public string? PathImages    { get; set; }
    public string? PathDats      { get; set; }
    public string? PathReleases  { get; set; }
    public string? PathRecoil2Png { get; set; }
    public string  Theme         { get; set; } = "Dark";
    public bool    DemoEffects   { get; set; } = true;
    public string  Language      { get; set; } = "en";
    /// <summary>Seuil de vues pour l'ajout automatique aux favoris. 0 = désactivé.</summary>
    public int     AutoFavoriteViewThreshold { get; set; } = 0;
    /// <summary>Durée d'affichage de chaque image en mode diaporama (secondes). 5 par défaut.</summary>
    public int     SlideshowDurationSeconds { get; set; } = 5;
    /// <summary>True une fois l'assistant de configuration initiale entièrement
    /// terminé (cf. PrefKeys.WizardCompleted).</summary>
    public bool    WizardCompleted { get; set; } = false;
    /// <summary>True une fois que l'utilisateur a coché "Ne plus demander" sur la
    /// confirmation de téléchargement ad-hoc (release pas encore couverte par un DAT) —
    /// cf. PrefKeys.SkipExternalDownloadConfirm, ExternalDownloadConfirmWindow.</summary>
    public bool    SkipExternalDownloadConfirm { get; set; } = false;
    /// <summary>True si le panneau "Infos" du lecteur de musique tracker doit être
    /// ouvert par défaut — cf. PrefKeys.PlayerInfoPanelOpen, SoundtrackPlayerView.xaml.cs.</summary>
    public bool    PlayerInfoPanelOpen { get; set; } = true;
    /// <summary>True si le panoramique stéréo UADE (adoucissement du hard-panning
    /// Amiga) doit être actif par défaut — cf. PrefKeys.UadePanningEnabled,
    /// TrackerPlayer.Core.Players.UadePlayer.PanningEnabled.</summary>
    public bool    UadePanningEnabled { get; set; } = false;
    /// <summary>Intensité du panoramique stéréo UADE (0.0-2.0, 0.7 = réglage
    /// historique UADE) — cf. PrefKeys.UadePanningAmount,
    /// TrackerPlayer.Core.Players.UadePlayer.PanningAmount.</summary>
    public double  UadePanningAmount { get; set; } = 0.7;
    /// <summary>Gain (amplification) appliqué à la sortie audio d'UADE (UC_GAIN côté
    /// libuade.dll, 1.0 = neutre) — cf. PrefKeys.UadeGainAmount,
    /// TrackerPlayer.Core.Players.UadePlayer.GainAmount. 1.6 par défaut (2026-08-07,
    /// retour utilisateur) : les formats Amiga exotiques joués via UADE sortent
    /// généralement plus bas en volume que ZXTune/libopenmpt.</summary>
    public double  UadeGainAmount { get; set; } = 1.6;

    // Résolution avec fallback sur les valeurs par défaut
    public string ResolvedPathConfigs   => string.IsNullOrWhiteSpace(PathConfigs)   ? PreferencesService.DefaultPathConfigs   : PathConfigs;
    public string ResolvedPathEmulators => string.IsNullOrWhiteSpace(PathEmulators) ? PreferencesService.DefaultPathEmulators : PathEmulators;
    public string ResolvedPathImages    => string.IsNullOrWhiteSpace(PathImages)    ? PreferencesService.DefaultPathImages    : PathImages;
    public string ResolvedPathDats      => string.IsNullOrWhiteSpace(PathDats)      ? PreferencesService.DefaultPathDats      : PathDats;
    public string ResolvedPathReleases  => string.IsNullOrWhiteSpace(PathReleases)  ? PreferencesService.DefaultPathReleases  : PathReleases;
}
