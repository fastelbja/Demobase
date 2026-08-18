using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace DemoBase.App.Services;

// ─── Lanceur — Windows natif (PC, pas d'émulateur) ───────────────────────────
// Pour les releases qui tournent directement sous Windows — le système sur
// lequel DemoBase lui-même s'exécute, donc pas de programme externe à invoquer
// comme pour les autres types : il suffit d'extraire l'archive en entier et de
// lancer l'exécutable qu'elle contient comme un process normal (cf. demande
// utilisateur : "c'est le plus simple y'a qu'à l'extraire complètement et
// l'exécuter"). `Emulator.ExecutablePath` n'est donc PAS utilisé pour ce
// type — il n'y a pas d'émulateur séparé à pointer dessus. `settings` est
// accepté par cohérence de signature avec les autres launchers mais ignoré
// (aucun réglage par machine n'a de sens ici).

public class WindowsLauncher
{
    // Fragments de nom à éviter s'il existe une alternative — installeurs/
    // désinstalleurs générés automatiquement (NSIS/InstallShield/Inno Setup),
    // qui ne sont jamais le "vrai" exécutable de la demo elle-même.
    private static readonly string[] NoisyNameFragments =
        ["unins000", "uninstall", "setup", "install"];

    private static readonly string[] IgnoredExtensions =
        [".txt", ".nfo", ".diz", ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".pdf", ".doc", ".docx", ".md"];

    public async Task<LaunchResult> LaunchAsync(
        Emulator                     emulator,
        EmulatorConfig               config,
        Dictionary<string, string?>  settings,
        string                       romPath,
        Release                      release)
    {
        System.Diagnostics.Debug.WriteLine($"[WINDOWS] LaunchAsync : romPath={romPath}");

        if (!File.Exists(romPath))
        {
            System.Diagnostics.Debug.WriteLine($"[WINDOWS] Fichier introuvable sur disque : {romPath}");
            return new(false, $"Fichier introuvable : {romPath}");
        }

        var actualFile = romPath;
        if (Path.GetExtension(romPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var configDir = WorkingPaths.GetSubdir("Configs");
            actualFile = await ExtractMainExeAsync(romPath, configDir, release.Id);
            System.Diagnostics.Debug.WriteLine($"[WINDOWS] ZIP extrait → exécutable choisi : {actualFile}");
        }

        if (!Path.GetExtension(actualFile).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            return new(false, $"Aucun exécutable (.exe) trouvé dans l'archive : {Path.GetFileName(romPath)}");

        // CommandLine est ici directement des arguments pour la demo elle-même (ex.
        // "-fullscreen"), pas une ligne pointant vers un émulateur — donc pas de
        // substitution {file} nécessaire (l'exe lancé EST déjà le fichier).
        var args    = string.IsNullOrWhiteSpace(config.CommandLine) ? "" : config.CommandLine;
        var workDir = !string.IsNullOrWhiteSpace(config.WorkingDirectory)
            ? config.WorkingDirectory
            : Path.GetDirectoryName(actualFile)!;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = actualFile,
                Arguments        = args,
                WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetDirectoryName(actualFile)!,
                UseShellExecute  = false,
            };
            var process = Process.Start(psi);
            System.Diagnostics.Debug.WriteLine(
                $"[WINDOWS] Process.Start a retourné : {(process == null ? "null" : $"PID={process.Id}")}");
            return new(true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WINDOWS] Exception au lancement : {ex}");
            return new(false, $"Erreur lancement : {ex.Message}");
        }
    }

    private static Task<string> ExtractMainExeAsync(string zipPath, string outDir, int releaseId)
        => Task.Run(() => ExtractMainExeSync(zipPath, outDir, releaseId));

    private static string ExtractMainExeSync(string zipPath, string outDir, int releaseId)
    {
        // Dossier d'extraction COURT (juste l'Id de la release, pas son titre complet) —
        // testé en conditions réelles : un titre de release long (souvent le cas pour les
        // "Executable Music"/"Executable Graphics", qui embarquent la liste complète des
        // auteurs et le contexte de compo dans le nom) combiné au chemin de travail de
        // l'appli peut dépasser les 260 caractères de MAX_PATH. L'extraction ZIP elle-même
        // (System.IO.Compression) tolère les chemins longs, mais PAS Process.Start/
        // CreateProcessW, qui échoue alors avec "le fichier spécifié est introuvable" même
        // si le fichier existe bel et bien sur le disque — bug constaté avec un titre de 140+
        // caractères. D'où l'Id numérique ici plutôt que Path.GetFileNameWithoutExtension(zipPath).
        var extractDir = Path.Combine(outDir, "extracted",
            WorkingPaths.GetZipSignature("win", releaseId, zipPath));
        Directory.CreateDirectory(extractDir);

        if (!Directory.GetFiles(extractDir).Any())
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);

        var allExes = Directory.GetFiles(extractDir, "*.exe", SearchOption.AllDirectories);

        // Écarte les exécutables d'installation/désinstallation générés automatiquement,
        // sauf si c'est tout ce qu'il y a (mieux vaut lancer ça que rien du tout).
        var clean = allExes
            .Where(f => !NoisyNameFragments.Any(frag =>
                Path.GetFileName(f).Contains(frag, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var candidates = clean.Count > 0 ? clean : allExes.ToList();

        // Parmi les candidats restants, préfère le moins profond dans l'arborescence —
        // probablement le lanceur principal à la racine, pas un outil annexe glissé
        // dans un sous-dossier (ex. "Tools\", "Redist\"...).
        var best = candidates
            .OrderBy(f => f.Count(c => c == Path.DirectorySeparatorChar))
            .ThenBy(f => f)
            .FirstOrDefault();

        if (best != null) return best;

        // Aucun .exe trouvé : retombe sur le premier fichier non-"texte" pour donner
        // au moins un message d'erreur clair côté appelant plutôt qu'une exception.
        var files = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
        return files.FirstOrDefault(f => !IgnoredExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            ?? files.FirstOrDefault()
            ?? zipPath;
    }
}
