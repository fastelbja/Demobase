using DemoBase.Core.Diagnostics;
using DemoBase.Data;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

/// <summary>
/// Vérifie et applique les mises à jour du catalogue DATs depuis
/// http://demobase.free.fr/DBSetup — même principe et même protocole de versionnage par
/// fichier texte que <see cref="ConfigsUpdateService"/> (2026-07-27, demande utilisateur
/// explicite : "faut utiliser le même système de détection que pour les configs
/// émulateurs avec un fichier texte de versioning").
///
/// 2026-08-17 : migration Mega.nz → HTTP direct (cf. DbSetupDownloadService) — le zip
/// "Demobase DATs" a désormais un nom EXACT et fixe ("Demobase_DATs.zip"), réécrasé à
/// chaque publication (plus de recherche par sous-chaîne, aucun listing de répertoire
/// disponible sur le site).
///
/// Protocole de versionnage :
///   DBSetup/DATS/dats_version.txt  → contient la version de la dernière mise à jour
///   Préférence locale dats.version  → dernière version importée
///   Si les deux diffèrent → retélécharge le zip "Demobase_DATs.zip", l'extrait dans DATS/,
///   relance DatImportService.ImportAsync (qui compare déjà, PAR FICHIER XML, sa propre
///   balise &lt;version&gt; à la table DatFileVersions — supprime puis réimporte
///   uniquement les DatEntries/DatRoms des fichiers réellement modifiés, cf.
///   DatImportService.ProcessFileAsync : aucun mécanisme "supprimer toutes les tables" à
///   ajouter séparément, ce comportement existe déjà nativement, fichier par fichier).
///
/// À publier côté site par l'utilisateur : un fichier texte "dats_version.txt" dans le
/// sous-dossier "DATS" (même dossier que "Demobase_DATs.zip"), contenant une chaîne
/// quelconque (date, numéro...) à changer à chaque mise à jour du zip. Tant que ce fichier
/// n'existe pas, cette vérification ne fait rien (silencieuse, non bloquante) — aucune
/// régression pour une installation qui n'a pas encore ce fichier.
/// </summary>
public class DatsUpdateService(
    DbSetupDownloadService megaService,
    DatImportService datImportService,
    PreferencesService prefs)
{
    private const string VersionFile      = "dats_version.txt";
    private const string MegaFolderUrl    = DemoBase.App.Services.EmulatorConfigExportService.DbSetupBaseUrl;
    private const string MegaSubFolder    = "DATS";
    private const string MegaFileNameMatch = "Demobase_DATs.zip";

    /// <summary>
    /// Vérifie la version distante et applique la mise à jour si nécessaire. Ne lève jamais
    /// d'exception — les erreurs sont loggées silencieusement (PerfLogger + Debug), même
    /// principe que ConfigsUpdateService.CheckAndUpdateAsync.
    ///
    /// 2026-07-27, demande utilisateur : retourne désormais <c>true</c> si une mise à jour a
    /// réellement été appliquée (et <c>false</c> sinon — déjà à jour, version distante
    /// introuvable, ou erreur) pour que l'appelant (App.xaml.cs) puisse enchaîner sur un
    /// rafraîchissement de la BDD Demozoo UNIQUEMENT quand c'est pertinent : de nouveaux
    /// DatEntry importés peuvent référencer des DemozooId pas encore présents dans une base
    /// Demozoo locale restée sur un ancien dump.
    /// </summary>
    public async Task<bool> CheckAndUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var remoteVersion = await FetchRemoteVersionAsync(ct);
            if (string.IsNullOrEmpty(remoteVersion))
            {
                System.Diagnostics.Debug.WriteLine("[DATS] Version distante introuvable — pas de mise à jour");
                PerfLogger.Mark("DATS: version distante introuvable (dats_version.txt non lu) — aucune mise à jour tentée");
                return false;
            }

            var localVersion = await prefs.GetAsync(PrefKeys.DatsVersion);
            System.Diagnostics.Debug.WriteLine($"[DATS] Version locale={localVersion ?? "(aucune)"} distante={remoteVersion}");
            PerfLogger.Mark($"DATS: version locale='{localVersion ?? "(aucune)"}' distante='{remoteVersion}'");

            if (localVersion == remoteVersion)
            {
                System.Diagnostics.Debug.WriteLine("[DATS] DATs à jour");
                PerfLogger.Mark("DATS: versions identiques — rien téléchargé");
                return false;
            }

            System.Diagnostics.Debug.WriteLine("[DATS] Mise à jour disponible — téléchargement...");
            PerfLogger.Mark("DATS: versions différentes — début du téléchargement");
            // 2026-07-31, retour utilisateur : "peux tu me corriger afin que je vois
            // qu'il a télécharger de nouveau dats qu'il est en train d'intégrer" —
            // jusqu'ici tout le flux (téléchargement, extraction, import) était
            // totalement silencieux (uniquement logs debug/PerfLogger, invisibles en
            // usage normal). StatusScrollerControl est le même mécanisme "toast"
            // déjà utilisé ailleurs dans l'appli (téléchargements Modland, releases
            // ad-hoc...) — visible sans bloquer ni ouvrir de nouvelle fenêtre.
            DemoBase.App.Controls.StatusScrollerControl.Post(
                "Mise à jour des DATs disponible — téléchargement en cours…");

            // 2026-07-31, bug rapporté par l'utilisateur (log) : le zip "Demobase DATs"
            // était introuvable sur Mega ("Zip non trouvé : No file containing..."),
            // DownloadExtractAndImportAsync ne faisait donc RIEN, et pourtant la ligne
            // suivante persistait quand même la version distante comme "appliquée" et
            // retournait true — DemoBase pensait alors être à jour (plus aucune
            // nouvelle tentative au prochain lancement, même après correction du nom du
            // zip sur Mega, tant que dats_version.txt ne changeait pas à nouveau) ET,
            // pire, ce faux "true" déclenchait en cascade un import Demozoo complet
            // automatique (cf. App.xaml.cs, bloc Étape 2d) — surprenant l'utilisateur
            // avec une fenêtre de progression inattendue pour un import qui n'avait en
            // réalité rien à voir avec une vraie mise à jour de DATs. Fix : on ne
            // persiste la version et on ne retourne true QUE si le téléchargement/
            // import a réellement réussi.
            bool applied = await DownloadExtractAndImportAsync(ct);
            if (!applied)
            {
                System.Diagnostics.Debug.WriteLine("[DATS] Téléchargement/import échoué — version locale inchangée, nouvelle tentative au prochain lancement.");
                PerfLogger.Mark("DATS: téléchargement/import échoué — version locale NON mise à jour (retentera au prochain lancement)");
                DemoBase.App.Controls.StatusScrollerControl.Post(
                    "Mise à jour des DATs : échec du téléchargement (voir Working/perf_log.txt).",
                    isWarning: true);
                return false;
            }

            await prefs.SetAsync(PrefKeys.DatsVersion, remoteVersion);
            System.Diagnostics.Debug.WriteLine($"[DATS] Mise à jour appliquée → version {remoteVersion}");
            PerfLogger.Mark($"DATS: mise à jour appliquée → version locale = '{remoteVersion}'");
            return true;
        }
        catch (OperationCanceledException) { /* app fermée */ return false; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DATS] Erreur mise à jour (non bloquante) : {ex.Message}");
            PerfLogger.Mark($"DATS: erreur non bloquante — {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<string?> FetchRemoteVersionAsync(CancellationToken ct)
    {
        try
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), "demobase_dats_version.txt");
            var result  = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, VersionFile, tmpFile, subFolder: MegaSubFolder, ct: ct);
            if (!result.Success)
            {
                PerfLogger.Mark($"DATS: échec DownloadFirstMatchingFileAsync({VersionFile}, subFolder={MegaSubFolder}) — {result.Error}");
                return null;
            }

            var version = (await File.ReadAllTextAsync(tmpFile, ct)).Trim();
            try { File.Delete(tmpFile); } catch { }
            return version;
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"DATS: exception dans FetchRemoteVersionAsync — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Télécharge "Demobase_DATs.zip" depuis le site, l'extrait dans DATS/ (même dossier
    /// et même logique que le wizard, cf. DatsPage.xaml.cs), relance l'import, puis
    /// nettoie DATS/ — reproduit exactement le flux du wizard pour rester rejouable à
    /// volonté sans dupliquer la logique d'import elle-même (DatImportService inchangé).
    ///
    /// 2026-07-31 : retourne désormais <c>true</c> uniquement si le zip a été trouvé,
    /// téléchargé, extrait ET importé — <c>false</c> sinon (zip introuvable, par exemple
    /// à cause d'un nom différent de "Demobase_DATs.zip" sur le site). L'appelant
    /// (CheckAndUpdateAsync) s'appuie sur cette valeur pour décider s'il doit persister
    /// la nouvelle version — sans quoi un échec de téléchargement était marqué "réussi".
    /// </summary>
    private async Task<bool> DownloadExtractAndImportAsync(CancellationToken ct)
    {
        var tmpZip = Path.Combine(Path.GetTempPath(), $"DemoBaseDats_{Guid.NewGuid():N}.zip");
        var result = await megaService.DownloadFirstMatchingFileAsync(
            MegaFolderUrl, MegaFileNameMatch, tmpZip, subFolder: MegaSubFolder, ct: ct);
        if (!result.Success)
        {
            System.Diagnostics.Debug.WriteLine($"[DATS] Zip non trouvé : {result.Error}");
            PerfLogger.Mark($"DATS: zip 'Demobase_DATs.zip' non trouvé — {result.Error}");
            return false;
        }

        DemoBase.App.Controls.StatusScrollerControl.Post(
            $"DATs téléchargés ({result.FileName}) — extraction en cours…");

        var datsDir = Path.Combine(AppContext.BaseDirectory, "DATS");
        Directory.CreateDirectory(datsDir);

        await Task.Run(() =>
        {
            using var archive = ZipFile.OpenRead(tmpZip);
            var entries = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            foreach (var entry in entries)
            {
                var destPath = Path.GetFullPath(Path.Combine(datsDir, entry.FullName));
                if (!destPath.StartsWith(Path.GetFullPath(datsDir))) continue; // sécurité (zip slip)
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
        }, ct);

        try { File.Delete(tmpZip); } catch { }

        DemoBase.App.Controls.StatusScrollerControl.Post(
            "DATs extraits — intégration en base en cours…");

        // DatImportService.ImportAsync compare déjà, PAR FICHIER, la balise <version> du XML
        // à la table DatFileVersions — supprime puis réimporte uniquement les DatEntries/
        // DatRoms des fichiers dont la version a réellement changé (cf. ProcessFileAsync,
        // DatImportService.cs). Inchangé, réutilisé tel quel.
        int entriesImported = 0;
        var importProgress = new Progress<DatImportProgress>(p =>
        {
            if (p.IsComplete) entriesImported = p.EntriesImported;
        });
        await Task.Run(() => datImportService.ImportAsync(importProgress, ct));
        System.Diagnostics.Debug.WriteLine($"[DATS] Import terminé — {entriesImported} entrée(s)");
        PerfLogger.Mark($"DATS: import terminé — {entriesImported} entrée(s)");
        DemoBase.App.Controls.StatusScrollerControl.Post(
            $"DATs mis à jour — {entriesImported} entrée(s) intégrée(s).");

        // Même nettoyage que le wizard — DATS/ ne sert qu'à l'import, redondant une fois
        // les données intégrées dans demobase.db. Non bloquant en cas d'échec (fichier
        // verrouillé) : l'import en base a déjà réussi à ce stade.
        try { Directory.Delete(datsDir, recursive: true); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DATS] Échec suppression DATS/ (non bloquant) : {ex.Message}");
        }

        return true;
    }
}
