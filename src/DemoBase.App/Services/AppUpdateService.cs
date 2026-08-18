using DemoBase.Core.Diagnostics;
using DemoBase.Data;
using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.Services;

/// <summary>
/// 2026-08-01, demande utilisateur : "peux-tu imaginer un système de mise à jour
/// automatique de l'application en utilisant mon compte mega et en allant regarder
/// dans un répertoire 'Updates' ?" — même principe et même dépôt que
/// <see cref="ConfigsUpdateService"/>/<see cref="DatsUpdateService"/>, avec un
/// sous-dossier dédié "Updates".
///
/// 2026-08-17, demande utilisateur : "le logiciel a maintenant son site internet. plus
/// besoin de downloader depuis Mega.nz. [...] j'ai tout remis dans
/// http://demobase.free.fr/DBSetup/ avec la même arborescence et les mêmes noms de
/// fichiers" — migration Mega.nz → HTTP direct (cf. DbSetupDownloadService). Le nom du
/// zip n'est plus une sous-chaîne à rechercher dans un nom versionné mais un nom EXACT
/// et fixe ("DemoBase_Update.zip"), réécrasé à chaque publication (le fichier
/// app_version.txt à côté suffit déjà à détecter qu'une nouvelle version existe).
///
/// Protocole de versionnage (identique à Configs/DATs) :
///   .../Updates/app_version.txt        → contient la version de la dernière build
///   .../Updates/DemoBase_Update.zip     → binaires seuls (exe/dll/pdb...), PAS les
///                                         dossiers de données utilisateur
///   Préférence locale app.version       → dernière version appliquée
///   Si les deux diffèrent → propose la mise à jour (confirmation utilisateur, choix
///   explicite — pas de mise à jour silencieuse automatique).
///
/// Problème technique résolu ici : Windows verrouille un .exe/.dll en cours
/// d'exécution — impossible de les remplacer depuis l'intérieur du processus lui-même.
/// Solution standard sans dépendance externe (pas de ClickOnce/Squirrel/MSIX) : un
/// script PowerShell généré à la volée (aucune compilation requise) qui attend la
/// fermeture du processus principal (par PID), copie les fichiers mis à jour par-dessus
/// l'installation existante (robocopy, jamais en mode /MIR : on ne supprime rien qui ne
/// soit pas dans le paquet — Database/Working/Configs/Releases/BIOS/DATS ne sont jamais
/// touchés), relance l'application, puis se nettoie lui-même. Le script écrit un
/// marqueur "update_applied.txt" dans le dossier d'installation juste après une copie
/// réussie ; <see cref="FinalizePendingUpdateAsync"/>, appelé au tout début du prochain
/// démarrage, lit ce marqueur pour enregistrer la nouvelle version en préférence — la
/// version locale n'est donc jamais marquée "à jour" tant que la copie n'a pas
/// réellement réussi.
///
/// À publier côté site par l'utilisateur (comme pour Configs/DATs) : dans le sous-dossier
/// "Updates" de http://demobase.free.fr/DBSetup, un fichier "app_version.txt" (une
/// chaîne quelconque — date, numéro de version...) et un fichier "DemoBase_Update.zip"
/// (nom EXACT, fixe), contenant directement (ou dans un premier sous-dossier) les
/// fichiers binaires de la nouvelle build — PAS les dossiers Database/Working/Configs/
/// Releases/BIOS/DATS de données utilisateur.
/// </summary>
public class AppUpdateService(DbSetupDownloadService megaService, PreferencesService prefs)
{
    private const string VersionFile       = "app_version.txt";
    private const string MegaFolderUrl     = EmulatorConfigExportService.DbSetupBaseUrl;
    private const string MegaSubFolder     = "Updates";
    private const string MegaFileNameMatch = "DemoBase_Update.zip";

    /// <summary>Nom du fichier marqueur écrit par le script de mise à jour dans le
    /// dossier d'installation juste après une copie réussie — lu et supprimé par
    /// <see cref="FinalizePendingUpdateAsync"/> au démarrage suivant.</summary>
    public const string UpdateAppliedMarkerFile = "update_applied.txt";

    /// <summary>
    /// Vérifie s'il existe une version distante différente de la version locale
    /// enregistrée. Ne télécharge PAS le paquet — juste le petit fichier texte de
    /// version. Ne lève jamais d'exception (mêmes garanties que
    /// ConfigsUpdateService/DatsUpdateService.CheckAndUpdateAsync).
    /// </summary>
    public async Task<AppUpdateInfo?> CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var tmpFile = Path.Combine(Path.GetTempPath(), "demobase_app_version.txt");
            var result  = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, VersionFile, tmpFile, subFolder: MegaSubFolder, ct: ct);
            if (!result.Success)
            {
                PerfLogger.Mark($"APPUPDATE: version distante introuvable (app_version.txt non lu) — {result.Error}");
                return null;
            }

            var remoteVersion = (await File.ReadAllTextAsync(tmpFile, ct)).Trim();
            try { File.Delete(tmpFile); } catch { /* non bloquant */ }

            var localVersion = await prefs.GetAsync(PrefKeys.AppVersion) ?? "";
            PerfLogger.Mark($"APPUPDATE: version locale='{localVersion}' distante='{remoteVersion}'");

            if (string.IsNullOrEmpty(remoteVersion) || remoteVersion == localVersion)
                return null;

            return new AppUpdateInfo(remoteVersion, localVersion);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            PerfLogger.Mark($"APPUPDATE: erreur CheckForUpdateAsync — {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Télécharge le paquet de mise à jour, l'extrait dans un dossier temporaire, puis
    /// prépare et lance le script PowerShell qui effectuera la copie réelle une fois
    /// l'application fermée (cf. commentaire de classe). N'appelle PAS
    /// Application.Shutdown() lui-même — c'est à l'appelant (App.xaml.cs, sur le thread
    /// UI) de fermer l'application après un retour Success=true.
    /// </summary>
    public async Task<(bool Success, string? Error)> DownloadAndApplyAsync(
        string remoteVersion, CancellationToken ct = default)
    {
        try
        {
            var stagingRoot = Path.Combine(Path.GetTempPath(), "DemoBase_Update_staging");
            try { if (Directory.Exists(stagingRoot)) Directory.Delete(stagingRoot, recursive: true); }
            catch { /* meilleur effort — un résidu d'une tentative précédente n'empêche pas ExtractToDirectory(overwrite:true) */ }
            Directory.CreateDirectory(stagingRoot);

            var tmpZip = Path.Combine(Path.GetTempPath(), $"demobase_update_{remoteVersion}.zip");
            PerfLogger.Mark($"APPUPDATE: téléchargement du paquet de mise à jour (version {remoteVersion})...");
            var dl = await megaService.DownloadFirstMatchingFileAsync(
                MegaFolderUrl, MegaFileNameMatch, tmpZip, subFolder: MegaSubFolder, ct: ct);
            if (!dl.Success)
                return (false, $"Téléchargement du paquet échoué : {dl.Error}");

            PerfLogger.Mark("APPUPDATE: extraction du paquet...");
            ZipFile.ExtractToDirectory(tmpZip, stagingRoot, overwriteFiles: true);
            try { File.Delete(tmpZip); } catch { /* non bloquant */ }

            // Garde-fou : un paquet sans .exe n'est pas un paquet de binaires valide —
            // mieux vaut échouer proprement ici que de lancer un script qui copierait
            // n'importe quoi par-dessus l'installation existante.
            var exeName = Path.GetFileName(
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DemoBase.App.exe"));
            var stagedExe = Directory.GetFiles(stagingRoot, exeName, SearchOption.AllDirectories).FirstOrDefault()
                         ?? Directory.GetFiles(stagingRoot, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (stagedExe == null)
            {
                try { Directory.Delete(stagingRoot, recursive: true); } catch { }
                return (false, "Le paquet téléchargé ne contient aucun .exe — mise à jour annulée par sécurité.");
            }

            // Si le zip contient un sous-dossier racine (ex: "DemoBase_App/xxx.exe" au
            // lieu des fichiers directement à la racine du zip), on part de CE
            // sous-dossier comme source de la copie, pas de stagingRoot lui-même.
            var sourceDir = Path.GetDirectoryName(stagedExe)!;
            var installDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
            var exePath     = Environment.ProcessPath ?? Path.Combine(installDir, exeName);
            var scriptPath  = Path.Combine(Path.GetTempPath(), $"DemoBase_apply_update_{Guid.NewGuid():N}.ps1");

            var script = BuildUpdateScript(
                Environment.ProcessId, sourceDir, installDir, exePath, remoteVersion, stagingRoot);
            await File.WriteAllTextAsync(scriptPath, script, ct);

            PerfLogger.Mark($"APPUPDATE: lancement du script de mise à jour ({scriptPath}) → version {remoteVersion}");

            var psi = new ProcessStartInfo
            {
                FileName        = "powershell.exe",
                Arguments       = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = true,   // détaché du processus courant — doit survivre à sa fermeture
                CreateNoWindow  = true,
            };
            Process.Start(psi);

            return (true, null);
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"APPUPDATE: erreur DownloadAndApplyAsync — {ex.GetType().Name}: {ex.Message}");
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Génère le script PowerShell de mise à jour différée. Aucune compilation requise
    /// (contrairement à un vrai "Updater.exe" séparé) — le script est un simple fichier
    /// texte écrit à la volée, exécuté par powershell.exe déjà présent sur toute machine
    /// Windows moderne.
    /// </summary>
    private static string BuildUpdateScript(
        int pid, string sourceDir, string destDir, string exePath, string version, string stagingRoot)
    {
        // Échappement PowerShell minimal : doubler les guillemets simples à l'intérieur
        // des chaînes entre guillemets simples (les chemins Windows ne contiennent pas
        // de guillemets, donc pas de risque d'injection ici — chemins générés par nous,
        // jamais saisis par l'utilisateur).
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";

        return $$"""
            # Script de mise à jour DemoBase — généré automatiquement, usage unique.
            # 1. Attend la fermeture du processus principal (PID {{pid}}, max 60s).
            # 2. Copie les binaires mis à jour par-dessus l'installation existante
            #    (jamais en mode miroir — ne supprime rien qui ne soit pas dans le
            #    paquet, les dossiers de données utilisateur sont donc préservés).
            # 3. Écrit le marqueur de version, relance l'application.
            # 4. Se nettoie (dossier temporaire de staging + lui-même).

            $ErrorActionPreference = 'SilentlyContinue'

            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Process -Id {{pid}} -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
                Start-Sleep -Milliseconds 300
            }
            # Petite marge après la fin du process pour la libération effective des
            # handles de fichiers (DLL natives type LibVLC/SndhPlayer notamment).
            Start-Sleep -Milliseconds 500

            $source = {{Q(sourceDir)}}
            $dest   = {{Q(destDir)}}

            # /E : copie récursive y compris sous-dossiers vides. PAS de /MIR — on ne
            # supprime jamais un fichier présent dans $dest mais absent de $source.
            # /XD : garde-fous supplémentaires même si le paquet ne devrait jamais
            # contenir ces dossiers.
            robocopy $source $dest /E /R:5 /W:1 /NFL /NDL /NJH /NJS `
                /XD "$dest\Database" "$dest\Working" "$dest\Configs" "$dest\Releases" "$dest\BIOS" "$dest\DATS" `
                | Out-Null
            # Codes robocopy 0-7 = succès (0 = rien à copier, 1 = fichiers copiés,
            # 2/3/4 = extras/mismatch détectés mais copie effectuée) ; 8+ = échec réel.
            # IMPORTANT : ne PAS valider avec "Test-Path $dest\<exe>" — ce fichier existe
            # déjà avant même la copie (c'est l'exécutable qui vient de se fermer), donc
            # ce test serait vrai même si robocopy avait entièrement échoué.
            $robocopyExit = $LASTEXITCODE

            if ($robocopyExit -lt 8) {
                Set-Content -Path (Join-Path $dest {{Q(UpdateAppliedMarkerFile)}}) -Value {{Q(version)}} -Encoding UTF8
                Start-Process -FilePath {{Q(exePath)}} -WorkingDirectory $dest
            }

            Start-Sleep -Seconds 2
            Remove-Item -Path {{Q(stagingRoot)}} -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
    }

    /// <summary>
    /// À appeler tout au début du démarrage suivant une mise à jour (avant même la
    /// vérification WizardCompleted — un marqueur en attente doit toujours être
    /// finalisé). Lit le marqueur "update_applied.txt" laissé par le script PowerShell
    /// juste après une copie réussie, enregistre la version en préférence, puis
    /// supprime le marqueur. No-op silencieux si aucun marqueur n'est présent (cas
    /// normal — immense majorité des démarrages).
    /// </summary>
    public async Task FinalizePendingUpdateAsync()
    {
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, UpdateAppliedMarkerFile);
            if (!File.Exists(marker)) return;

            var version = (await File.ReadAllTextAsync(marker)).Trim();
            if (!string.IsNullOrEmpty(version))
            {
                await prefs.SetAsync(PrefKeys.AppVersion, version);
                PerfLogger.Mark($"APPUPDATE: mise à jour finalisée au démarrage → version locale = '{version}'");
            }
            try { File.Delete(marker); } catch { /* non bloquant — sera juste relu/ré-écrit au prochain démarrage si suppression échoue */ }
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"APPUPDATE: erreur FinalizePendingUpdateAsync — {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Résultat de <see cref="AppUpdateService.CheckForUpdateAsync"/> — non-null
/// uniquement quand une mise à jour est réellement disponible (version distante lue
/// avec succès ET différente de la version locale).</summary>
public sealed record AppUpdateInfo(string RemoteVersion, string LocalVersion);
