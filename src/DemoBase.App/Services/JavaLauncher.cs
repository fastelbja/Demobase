using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Java ───────────────────────────────────────────────────

public static class JavaSettings
{
    /// <summary>
    /// Arguments JVM additionnels passés avant -jar (ex. "-Xmx512m -Xms128m").
    /// </summary>
    public const string JvmArgs = "jvmargs";
}

// ─── Lanceur Java ─────────────────────────────────────────────────────────────
// La plateforme "Java" du catalogue est un vrai fourre-tout (constaté 2026-07-24) :
// selon la release, on y trouve de vrais .jar exécutables, mais aussi des .exe
// natifs Windows mal classés, des démos web (.html/.htm), des sketches Processing
// (.pde — nécessitent l'IDE Processing, PAS lançables par javaw malgré le lien de
// parenté avec Java), voire de simples .class nus sans jar (fréquent sur les
// micro-intros 4k/64k qui évitent l'overhead d'un jar). Avant ce correctif, le
// launcher se rabattait aveuglément sur "le premier fichier non ignoré" de
// l'archive quand aucun .jar n'était trouvé — ce qui pouvait sélectionner un
// .dll ou un .pde puis tenter de le lancer comme jarfile ("Invalid or corrupt
// jarfile"). Le launcher détecte maintenant le type du fichier résolu et choisit
// le mécanisme de lancement adapté (ou échoue proprement si rien n'est reconnu),
// plutôt que de deviner.
//
// Commande (cas .jar) : javaw.exe [JvmArgs] -jar <fichier.jar>
//
// Résolution de javaw.exe, par ordre de priorité :
//   1. ExecutablePath du profil, si explicitement configuré et présent sur disque
//      (permet à un utilisateur de forcer un JDK/JRE système précis si besoin).
//   2. BundledJavaPath — le JRE dédié DemoBase (Eclipse Temurin 21, téléchargé
//      via l'écran "Outils externes" comme ZXTune/UADE/RECOIL), extrait dans
//      Externals/JRE/ et résolu au démarrage par App.xaml.cs/ConfigureExternalPaths.
//      Totalement indépendant du Java installé sur le poste — ne nécessite ni
//      installation ni mise à jour du système. C'est le mode recommandé.
//   3. "javaw" nu (PATH système) — dernier recours pour compat ascendante avec
//      les profils créés avant l'ajout du JRE dédié.
//
// JVM args utiles pour les démos Java :
//   -Xmx512m         — 512 Mo de heap max (défaut JVM souvent trop petit)
//   -Xms128m         — 128 Mo de heap initial
//   -Dsun.java2d.opengl=true  — accélération OpenGL pour les démos 2D
//   -Dsun.java2d.d3d=true     — accélération D3D (Windows)

public class JavaLauncher
{
    /// <summary>
    /// Chemin vers le javaw.exe du JRE dédié DemoBase (Externals/JRE/bin/javaw.exe),
    /// renseigné par App.xaml.cs/ConfigureExternalPaths() si installé — même
    /// mécanisme que UadePlayer.UadecoreExePath (2026-08-06 : ZXTunePlayer n'a
    /// plus d'équivalent — zxtune.dll est résolu par la recherche DllImport
    /// standard, pas par un chemin explicite assigné au démarrage). Null si le
    /// JRE dédié n'a pas (encore) été téléchargé.
    /// </summary>
    public static string? BundledJavaPath { get; set; }

    private readonly PreferencesService _prefs;

    public JavaLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public async Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[JAVA] LaunchAsync : exe={emulator.ExecutablePath} romPath={romPath}");

        if (!File.Exists(romPath))
            return new(false, $"Fichier introuvable : {romPath}");

        var actualFile = romPath;
        var ext = Path.GetExtension(romPath).ToLowerInvariant();

        if (ext == ".zip")
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            var resolved  = await ExtractUsableFileAsync(romPath, configDir, release.Id);
            if (resolved == null)
            {
                return new(false,
                    "Aucun fichier lançable reconnu dans cette release Java (.jar, .exe ou .html). " +
                    "Certains formats comme les sketches Processing (.pde) nécessitent l'IDE " +
                    "Processing et ne sont pas pris en charge par DemoBase.");
            }
            actualFile = resolved;
            System.Diagnostics.Debug.WriteLine($"[JAVA] ZIP extrait → {actualFile}");
        }

        var fileExt = Path.GetExtension(actualFile).ToLowerInvariant();

        switch (fileExt)
        {
            case ".jar":
            {
                // Résoudre javaw.exe : profil > JRE dédié DemoBase > "javaw" du PATH (repli)
                var javaExe = !string.IsNullOrWhiteSpace(emulator.ExecutablePath)
                              && File.Exists(emulator.ExecutablePath)
                    ? emulator.ExecutablePath
                    : !string.IsNullOrWhiteSpace(BundledJavaPath) && File.Exists(BundledJavaPath)
                        ? BundledJavaPath
                        : "javaw";

                var args = BuildArguments(settings, actualFile);
                return await ProcessLaunchHelper.StartAndMonitorAsync(
                    javaExe, args, tag: "JAVA", friendlyName: "Java",
                    workingDir: Path.GetDirectoryName(actualFile)!);
            }

            case ".exe":
                // Certaines releases classées "Java" sont en fait des .exe natifs
                // (démos LibGDX/JavaFX packagées avec un launcher natif, ou platform
                // mal renseignée côté Demozoo) — les lancer directement plutôt que
                // de les faire échouer via javaw -jar.
                return await ProcessLaunchHelper.StartAndMonitorAsync(
                    actualFile, "", tag: "JAVA", friendlyName: "Java (exe)",
                    workingDir: Path.GetDirectoryName(actualFile)!);

            case ".html":
            case ".htm":
                // Ouvrir dans le navigateur par défaut du système (même mécanisme
                // que BrowserLauncher en mode "default" — pas de réutilisation
                // directe de BrowserLauncher ici car son EmulatorConfig/Emulator
                // de contexte serait celui de Java, pas du navigateur : mode
                // "custom" se déclencherait à tort avec javaw.exe comme "navigateur").
                try
                {
                    var uri = new Uri(actualFile).AbsoluteUri;
                    var psi = new ProcessStartInfo { FileName = uri, UseShellExecute = true };
                    Process.Start(psi);
                    return new(true);
                }
                catch (Exception ex)
                {
                    return new(false, $"Erreur ouverture navigateur : {ex.Message}");
                }

            case ".pde":
                // Sketch Processing (langage basé sur Java, mais un .pde est du code
                // SOURCE qui doit être compilé/exécuté par l'IDE Processing — ce
                // n'est jamais directement lançable par javaw, contrairement à ce
                // que suggère la parenté avec Java.
                return new(false,
                    $"« {Path.GetFileName(actualFile)} » est un sketch Processing (.pde) — " +
                    "nécessite l'IDE Processing pour être exécuté, non pris en charge par DemoBase.");

            default:
                return new(false,
                    $"Type de fichier non pris en charge pour la plateforme Java : " +
                    $"{Path.GetFileName(actualFile)}");
        }
    }

    private static string BuildArguments(Dictionary<string, string?> settings, string jarFile)
    {
        var sb = new StringBuilder();

        // Args JVM additionnels (avant -jar)
        var jvmArgs = settings.GetValueOrDefault(JavaSettings.JvmArgs, string.Empty)?.Trim();
        if (!string.IsNullOrWhiteSpace(jvmArgs))
            sb.Append(jvmArgs).Append(' ');

        sb.Append($"-jar \"{jarFile}\"");
        return sb.ToString();
    }

    private static Task<string?> ExtractUsableFileAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractUsableFileSync(zipPath, outDir, releaseId));

    private static string? ExtractUsableFileSync(string zipPath, string outDir, int releaseId)
    {
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("java", releaseId, zipPath));
        var files = WorkingPaths.ExtractZipCached(zipPath, extractDir);

        // .jar : s'il y en a plusieurs, préférer celui qui n'est PAS dans un
        // dossier "lib"/"libs" (presque toujours une dépendance, pas le jar
        // exécutable principal — cause fréquente de "aucun attribut manifest
        // principal" quand on tombe sur le mauvais jar), puis le moins profond.
        var jars = files
            .Where(f => Path.GetExtension(f).Equals(".jar", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (jars.Count > 0)
        {
            return jars
                .OrderBy(f => IsInLibFolder(f, extractDir) ? 1 : 0)
                .ThenBy(f => RelativeDepth(f, extractDir))
                .First();
        }

        var exe = files.FirstOrDefault(
            f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase));
        if (exe != null) return exe;

        var html = files.FirstOrDefault(f =>
            Path.GetExtension(f).Equals(".html", StringComparison.OrdinalIgnoreCase) ||
            Path.GetExtension(f).Equals(".htm",  StringComparison.OrdinalIgnoreCase));
        if (html != null) return html;

        var pde = files.FirstOrDefault(
            f => Path.GetExtension(f).Equals(".pde", StringComparison.OrdinalIgnoreCase));
        if (pde != null) return pde; // remonté tel quel → message clair dans LaunchAsync

        // Aucun format reconnu : mieux vaut échouer proprement (message explicite
        // dans LaunchAsync) que de tenter un fichier au hasard (.dll, .class,
        // .txt…) comme s'il s'agissait d'un jar.
        return null;
    }

    private static bool IsInLibFolder(string filePath, string extractRoot)
    {
        var rel  = Path.GetRelativePath(extractRoot, filePath);
        var dirs = Path.GetDirectoryName(rel)?
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            ?? [];
        return dirs.Any(d => d.Equals("lib", StringComparison.OrdinalIgnoreCase)
                           || d.Equals("libs", StringComparison.OrdinalIgnoreCase));
    }

    private static int RelativeDepth(string filePath, string extractRoot)
    {
        var rel = Path.GetRelativePath(extractRoot, filePath);
        return rel.Count(c => c == Path.DirectorySeparatorChar || c == Path.AltDirectorySeparatorChar);
    }
}
