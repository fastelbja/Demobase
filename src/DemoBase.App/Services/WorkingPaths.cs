using System;
using System.IO;
using System.IO.Compression;

namespace DemoBase.App.Services;

// ─── Dossier de travail de l'application ──────────────────────────────────────
//
// Centralise les emplacements de fichiers temporaires (extraction de ZIP pour
// la lecture tracker/recoil, scripts de pré-lancement générés...) sous un seul
// sous-dossier "Working" du dossier de l'exe, plutôt que dans %TEMP% (chemin
// utilisateur partagé par tout le système, hors contrôle de l'app, qui peut
// accumuler indéfiniment des fichiers orphelins sans rapport visible avec
// DemoBase pour quelqu'un qui ferait le ménage de son %TEMP%).
public static class WorkingPaths
{
    public static string Root => Path.Combine(AppContext.BaseDirectory, "Working");

    /// <summary>Sous-dossier du dossier de travail (ex. "Tracker", "Recoil", "Scripts"),
    /// créé s'il n'existe pas encore.</summary>
    public static string GetSubdir(string name)
    {
        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Racine du dossier "NotCurated" (2026-07-25) : contenu téléchargé à la demande pour des
    /// releases pas encore couvertes par un DAT/Mega ("Palier 1" — cf. RESUME_PROJET.md), aussi
    /// bien pour le lancement émulateur générique que, depuis peu, pour Music/Graphics
    /// (ResolveAdHocFileAsync). Volontairement à la racine du dossier de l'exe — comme Releases/
    /// DATS/Configs/Emus/Images — plutôt que sous <see cref="Root"/> ("Working") : ce dernier est
    /// intégralement vidé à CHAQUE démarrage de l'app par DbInitializer.CleanExtractedCache
    /// (App.xaml.cs), ce qui aurait forcé un re-téléchargement à chaque relance. "NotCurated"
    /// n'est jamais nettoyé automatiquement — le fichier reste en cache tant qu'il n'est pas
    /// supprimé manuellement (même logique que Releases : contenu qui grossit avec l'usage, pas
    /// de purge auto).
    /// </summary>
    public static string NotCuratedRoot
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "NotCurated");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Racine du cache Modland (2026-07-30, demande utilisateur : onglet "Musique
    /// (modland)", cache local persistant choisi plutôt que temporaire) : chaque piste
    /// téléchargée depuis http://ftp.modland.com/ est conservée ici sous la MÊME
    /// arborescence relative que le site (&lt;Format&gt;/&lt;Auteur&gt;/&lt;fichier&gt;),
    /// pour ne pas la retélécharger à chaque écoute. Comme <see cref="NotCuratedRoot"/> —
    /// volontairement à la racine du dossier de l'exe, jamais purgé automatiquement
    /// (contrairement à <see cref="Root"/> ("Working"), vidé à chaque démarrage).
    /// </summary>
    public static string ModlandRoot
    {
        get
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "Modland");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// <summary>
    /// Nom de sous-dossier d'extraction stable pour un ZIP donné, invalidé
    /// automatiquement si le ZIP change de contenu — contrairement à une clé
    /// basée uniquement sur releaseId (ancien pattern présent dans PPSSPP/
    /// DuckStation/Xenia/CxbxReloaded Launcher), qui réutilisait silencieusement
    /// un dossier d'extraction laissé par un ZIP complètement différent dès
    /// qu'une release avait plusieurs DatEntry (versions alternatives) : si
    /// l'utilisateur changeait sa sélection ("Use" dans l'onglet Files) ou si
    /// ReleaseBuilderService reconstruisait le ZIP avec un contenu différent
    /// au même chemin, l'ancien contenu extrait (parfois sans rapport, ex. un
    /// tout autre jeu/toolchain) restait utilisé indéfiniment tant que le
    /// dossier n'était pas vide — jamais réextrait.
    ///
    /// La signature intègre le chemin, la taille et la date de dernière
    /// modification du ZIP (pas de hash de contenu complet, pour rester rapide)
    /// : un ZIP différent, ou le même chemin réécrit avec un contenu différent,
    /// produit systématiquement un dossier distinct.
    /// </summary>
    public static string GetZipSignature(string prefix, int releaseId, string zipPath)
    {
        string sig;
        try
        {
            var fi = new FileInfo(zipPath);
            sig = $"{fi.Length}_{fi.LastWriteTimeUtc.Ticks}";
        }
        catch
        {
            // Fichier introuvable/inaccessible à cet instant précis (rare, cas
            // limite) — on retombe sur l'ancien comportement plutôt que planter.
            sig = "unknown";
        }
        // Hash court (8 caractères hex) pour garder un nom de dossier lisible,
        // plutôt que d'y coller la signature brute en clair.
        var hash = System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes(sig));
        var shortHash = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return $"{prefix}_{releaseId}_{shortHash}";
    }

    /// <summary>
    /// Extrait un ZIP dans <paramref name="extractDir"/> avec vérification de
    /// complétude du cache : si le dossier existe mais que le nombre de fichiers
    /// qu'il contient ne correspond pas au nombre d'entrées non-dossier du ZIP,
    /// l'extraction précédente était incomplète (interruption, corruption, fichier
    /// supprimé manuellement…) — on purge et réextrait entièrement.
    ///
    /// Remplace le pattern <c>if (!Directory.GetFiles(dir).Any())</c> répété
    /// dans chaque launcher, qui ne détectait pas les extractions partielles et
    /// gardait silencieusement un dossier incomplet/périmé.
    /// </summary>
    public static string[] ExtractZipCached(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);

        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            int expectedCount = zip.Entries.Count(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'));
            int actualCount   = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories).Length;

            if (actualCount != expectedCount)
            {
                // Cache absent ou incomplet → purge + réextraction complète.
                if (Directory.Exists(extractDir))
                {
                    try { Directory.Delete(extractDir, recursive: true); } catch { }
                    Directory.CreateDirectory(extractDir);
                }
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            }
        }
        catch
        {
            // En cas d'erreur de lecture du ZIP ou de purge, on tente quand même
            // une extraction (ZipFile.ExtractToDirectory lèvera une exception
            // explicite si le fichier est réellement corrompu).
            if (!Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories).Any())
                ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        }

        return Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
    }
}

