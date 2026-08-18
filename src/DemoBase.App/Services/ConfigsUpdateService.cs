using DemoBase.Core.Diagnostics;
using DemoBase.Data;

namespace DemoBase.App.Services;

/// <summary>
/// Vérifie et applique les mises à jour des configs émulateurs depuis
/// http://demobase.free.fr/DBSetup. Appelé au démarrage de l'application — silencieux
/// en cas d'erreur réseau.
///
/// 2026-08-17 : migration Mega.nz → HTTP direct (cf. DbSetupDownloadService). Les JSON
/// (emulator_configs.json, release_profile_overrides.json) ont désormais un nom EXACT
/// et fixe. Les fichiers .uae/.cfg (téléchargement en masse, nombre et noms inconnus à
/// l'avance) s'appuient maintenant sur un manifeste "configs_files.txt" que
/// l'utilisateur doit maintenir dans le sous-dossier Configs du site, à côté des
/// fichiers eux-mêmes (aucun listing de répertoire disponible sur demobase.free.fr).
///
/// Protocole de versionnage :
///   DBSetup/Configs/configs_version.txt  → contient le timestamp de la dernière version
///   Préférence locale configs.version     → dernière version importée
///   Si les deux diffèrent → mise à jour automatique
/// </summary>
public class ConfigsUpdateService(
    DbSetupDownloadService megaService,
    EmulatorConfigExportService exportService,
    PreferencesService prefs,
    DemoBase.Data.ReleaseProfileOverrideExportService? profileOverrideExportService = null)
{
    private const string VersionFile    = "configs_version.txt";
    private const string FolderUrl      = EmulatorConfigExportService.DbSetupBaseUrl;
    private const string SubFolder      = EmulatorConfigExportService.DbSetupSubFolder;
    private const string JsonMatch      = EmulatorConfigExportService.DbSetupFileName;
    private const string ProfileOverridesJsonMatch = DemoBase.Data.ReleaseProfileOverrideExportService.DbSetupFileName;

    /// <summary>
    /// Vérifie la version distante et applique la mise à jour si nécessaire.
    /// Ne lève jamais d'exception — les erreurs sont loggées silencieusement.
    ///
    /// 2026-07-25 : les traces `Debug.WriteLine` ci-dessous sont invisibles en build
    /// Release (compilées hors du binaire — [Conditional("DEBUG")] sur
    /// System.Diagnostics.Debug), donc inexploitables sur une installation utilisateur
    /// normale. Doublées avec PerfLogger.Mark (écrit dans Working/perf_log.txt, actif
    /// dans TOUTES les configurations de build) pour pouvoir diagnostiquer un souci de
    /// synchro même en Release — cf. RESUME_PROJET.md pour le contexte du bug
    /// ayant motivé cet ajout.
    /// </summary>
    public async Task CheckAndUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // 1. Lire la version distante depuis le site
            var remoteVersion = await FetchRemoteVersionAsync(ct);
            if (string.IsNullOrEmpty(remoteVersion))
            {
                System.Diagnostics.Debug.WriteLine("[CONFIGS] Version distante introuvable — pas de mise à jour");
                PerfLogger.Mark("CONFIGS: version distante introuvable (configs_version.txt non lu) — aucune mise à jour tentée");
                return;
            }

            // 2. Lire la version locale
            var localVersion = await prefs.GetAsync(PrefKeys.ConfigsVersion);
            System.Diagnostics.Debug.WriteLine($"[CONFIGS] Version locale={localVersion ?? "(aucune)"} distante={remoteVersion}");
            PerfLogger.Mark($"CONFIGS: version locale='{localVersion ?? "(aucune)"}' distante='{remoteVersion}'");

            if (localVersion == remoteVersion)
            {
                System.Diagnostics.Debug.WriteLine("[CONFIGS] Configs à jour");
                PerfLogger.Mark("CONFIGS: versions identiques — rien téléchargé (ni JSON, ni .uae/.cfg)");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[CONFIGS] Mise à jour disponible — téléchargement...");
            PerfLogger.Mark("CONFIGS: versions différentes — début du téléchargement");

            // 3. Télécharger et importer le JSON
            await UpdateJsonConfigsAsync(ct);

            // 3bis. Télécharger et importer les sélections de profil par release —
            // best-effort, réutilise le même cycle de version que les configs
            // émulateurs (bump conjoint attendu côté site).
            await UpdateProfileOverridesJsonAsync(ct);

            // 4. Télécharger les fichiers de config émulateurs (.uae, .cfg...)
            await UpdateEmulatorConfigFilesAsync(ct);

            // 5. Enregistrer la nouvelle version
            await prefs.SetAsync(PrefKeys.ConfigsVersion, remoteVersion);
            System.Diagnostics.Debug.WriteLine($"[CONFIGS] Mise à jour appliquée → version {remoteVersion}");
            PerfLogger.Mark($"CONFIGS: mise à jour appliquée → version locale = '{remoteVersion}'");
        }
        catch (OperationCanceledException) { /* app fermée */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CONFIGS] Erreur mise à jour (non bloquante) : {ex.Message}");
            PerfLogger.Mark($"CONFIGS: erreur non bloquante — {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string?> FetchRemoteVersionAsync(CancellationToken ct)
    {
        try
        {
            var tmpFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "demobase_configs_version.txt");
            var result  = await megaService.DownloadFirstMatchingFileAsync(
                FolderUrl, VersionFile, tmpFile, subFolder: SubFolder, ct: ct);
            if (!result.Success)
            {
                // 2026-07-25 : result.Error était auparavant ignoré ici (juste `return null`)
                // — impossible de savoir SI le sous-dossier "Configs" n'a pas été trouvé, si
                // le fichier n'existe pas dedans, ou si le site a rejeté la requête (réseau/HTTP).
                PerfLogger.Mark($"CONFIGS: échec DownloadFirstMatchingFileAsync({VersionFile}, subFolder={SubFolder}) — {result.Error}");
                return null;
            }

            var version = (await System.IO.File.ReadAllTextAsync(tmpFile, ct)).Trim();
            try { System.IO.File.Delete(tmpFile); } catch { }
            return version;
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"CONFIGS: exception dans FetchRemoteVersionAsync — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private async Task UpdateJsonConfigsAsync(CancellationToken ct)
    {
        var tmpJson = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "demobase_emulator_configs.json");
        var result  = await megaService.DownloadFirstMatchingFileAsync(
            FolderUrl, JsonMatch, tmpJson, subFolder: SubFolder, ct: ct);
        if (!result.Success)
        {
            System.Diagnostics.Debug.WriteLine($"[CONFIGS] JSON non trouvé : {result.Error}");
            PerfLogger.Mark($"CONFIGS: emulator_configs JSON non trouvé — {result.Error}");
            return;
        }
        var (configs, settings) = await exportService.ImportFromJsonAsync(tmpJson);
        System.Diagnostics.Debug.WriteLine($"[CONFIGS] JSON importé : {configs} configs, {settings} settings");
        PerfLogger.Mark($"CONFIGS: emulator_configs JSON importé — {configs} configs, {settings} settings");
        try { System.IO.File.Delete(tmpJson); } catch { }
    }

    /// <summary>
    /// Télécharge et importe le JSON des sélections de profil par release (cf.
    /// ReleaseProfileOverrideExportService). Fichier absent = fonctionnalité pas encore
    /// utilisée sur cette installation, ou service non injecté (ancienne
    /// construction sans profileOverrideExportService) — non bloquant dans les deux cas.
    /// </summary>
    private async Task UpdateProfileOverridesJsonAsync(CancellationToken ct)
    {
        if (profileOverrideExportService == null)
        {
            PerfLogger.Mark("CONFIGS: release_profile_overrides ignoré — service non injecté (ne devrait plus arriver depuis le fix wizard du 2026-07-25)");
            return;
        }

        var tmpJson = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "demobase_release_profile_overrides.json");
        var result  = await megaService.DownloadFirstMatchingFileAsync(
            FolderUrl, ProfileOverridesJsonMatch, tmpJson, subFolder: SubFolder, ct: ct);
        if (!result.Success)
        {
            System.Diagnostics.Debug.WriteLine($"[CONFIGS] JSON profils releases non trouvé : {result.Error}");
            PerfLogger.Mark($"CONFIGS: release_profile_overrides JSON non trouvé — {result.Error}");
            return;
        }
        var (imported, skipped) = await profileOverrideExportService.ImportFromJsonAsync(tmpJson);
        System.Diagnostics.Debug.WriteLine($"[CONFIGS] JSON profils releases importé : {imported} importé(s), {skipped} ignoré(s)");
        PerfLogger.Mark($"CONFIGS: release_profile_overrides JSON importé — {imported} importé(s), {skipped} ignoré(s)");
        try { System.IO.File.Delete(tmpJson); } catch { }
    }

    private async Task UpdateEmulatorConfigFilesAsync(CancellationToken ct)
    {
        // Les fichiers de config de base (.uae, .cfg...) référencés par
        // EmulatorConfig.ConfigFilePath vivent tous à plat dans AppPaths.Configs
        // (= dossier proposé par BrowseConfigFile) — PAS dans Emus\<Emulateur>\...,
        // qui est réservé aux exécutables/installations des émulateurs eux-mêmes.
        var configsDir = AppPaths.Configs;
        System.IO.Directory.CreateDirectory(configsDir);

        int uaeCount = await megaService.DownloadAllMatchingFilesAsync(
            FolderUrl, ".uae", configsDir, subFolder: SubFolder, ct: ct);
        System.Diagnostics.Debug.WriteLine($"[CONFIGS] {uaeCount} fichier(s) .uae mis à jour dans {configsDir}");
        PerfLogger.Mark($"CONFIGS: {uaeCount} fichier(s) .uae téléchargé(s) dans {configsDir}");

        int cfgCount = await megaService.DownloadAllMatchingFilesAsync(
            FolderUrl, ".cfg", configsDir, subFolder: SubFolder, ct: ct);
        System.Diagnostics.Debug.WriteLine($"[CONFIGS] {cfgCount} fichier(s) .cfg mis à jour dans {configsDir}");
        PerfLogger.Mark($"CONFIGS: {cfgCount} fichier(s) .cfg téléchargé(s) dans {configsDir}");
    }
}
