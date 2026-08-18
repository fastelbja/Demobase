using DemoBase.Core.Enums;
using DemoBase.Core.Models;
using DemoBase.Data.Context;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;

namespace DemoBase.App.Services;

public record EmulatorSeedResult(int TotalSeeded, int NewlyCreated, int ExecutablesDetected, int Failed = 0);

/// <summary>
/// Crée (ou met à jour, sans écraser) les lignes <see cref="Emulator"/> pour
/// tous les types connus de DemoBase, avec un Id TOUJOURS égal à (int)EmulatorType.
///
/// Idempotent : peut être ré-exécuté sans risque (ex. retour en arrière dans le
/// wizard, ou réutilisation future depuis les Préférences) — ne touche jamais le
/// Name/Notes/DefaultArgs d'un émulateur déjà présent en base, ne fait que
/// remplir un ExecutablePath vide si un .exe est maintenant détecté dans Emus/.
///
/// Résilient par entrée : un problème isolé sur UN émulateur (dossier illisible,
/// versions.json corrompu par un téléchargement interrompu, etc.) ne doit
/// jamais empêcher les autres d'être enregistrés. Avant ce correctif, une seule
/// exception dans la boucle faisait échouer tout le lot d'un coup — puisque
/// SaveChangesAsync n'était appelé qu'une fois à la toute fin, un émulateur en
/// échec de téléchargement (donc dans un état de fichiers incomplet) suffisait
/// à empêcher TOUS les émulateurs d'être créés en base, y compris ceux qui
/// s'étaient téléchargés sans problème.
/// </summary>
public class EmulatorSeedService
{
    private readonly IDbContextFactory<DemoBaseDbContext> _dbFactory;
    private readonly EmulatorInstallerService _installerService;

    public EmulatorSeedService(
        IDbContextFactory<DemoBaseDbContext> dbFactory,
        EmulatorInstallerService installerService)
    {
        _dbFactory        = dbFactory;
        _installerService = installerService;
    }

    public async Task<EmulatorSeedResult> SeedAllAsync(CancellationToken ct = default)
    {
        using var totalScope = PerfLogger.Begin("SeedAllAsync (total)");
        await using var ctx = await _dbFactory.CreateDbContextAsync(ct);

        int created  = 0;
        int detected = 0;
        int failed   = 0;

        // ── Charge TOUTES les lignes Emulator en une seule requête ────────────
        // Ancienne approche : 1 FindAsync par entrée du catalogue → N requêtes
        // séquentielles (43 SELECT sur le thread UI à chaque navigation).
        // Nouvelle approche : 1 seul WHERE Id IN (...) puis traitement en mémoire.
        var allIds    = EmulatorSeedCatalog.All.Select(e => (int)e.Type).ToList();
        Dictionary<int, Emulator> existing;
        using (PerfLogger.Begin("SeedAllAsync.BulkLoadEmulators"))
            existing = await ctx.Set<Emulator>()
                                .Where(e => allIds.Contains(e.Id))
                                .ToDictionaryAsync(e => e.Id, ct);

        foreach (var entry in EmulatorSeedCatalog.All)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var id      = (int)entry.Type;
                var path    = entry.Type switch
                {
                    EmulatorType.Browser => FindSystemChrome(),
                    EmulatorType.Java    => FindBundledJre() ?? FindSystemJava(),
                    _                    => FindExecutable(entry),
                };
                var version = entry.FolderName != null
                    ? (await _installerService.GetInstalledVersionAsync(entry.FolderName))?.Version
                    : null;
                if (!string.IsNullOrEmpty(path)) detected++;

                if (!existing.TryGetValue(id, out var row))
                {
                    ctx.Set<Emulator>().Add(new Emulator
                    {
                        Id             = id,
                        Name           = entry.DefaultName,
                        Version        = version ?? "",
                        ExecutablePath = path ?? "",
                        Status         = EmulatorStatus.Active,
                        EmulatorType   = entry.Type,
                    });
                    created++;
                }
                else
                {
                    // Ne remplit que les champs vides — ne jamais écraser une valeur
                    // déjà configurée manuellement par l'utilisateur.
                    if (string.IsNullOrEmpty(row.ExecutablePath) && !string.IsNullOrEmpty(path))
                        row.ExecutablePath = path!;
                    if (string.IsNullOrEmpty(row.Version) && !string.IsNullOrEmpty(version))
                        row.Version = version!;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                System.Diagnostics.Debug.WriteLine(
                    $"[EmulatorSeedService] Échec seed pour {entry.DefaultName} (non bloquant) : {ex.Message}");
            }
        }

        try
        {
            await ctx.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Si SaveChangesAsync échoue malgré tout (ex. une seule entrée en
            // conflit avec une contrainte), on tente de sauver les autres une
            // par une plutôt que de tout perdre d'un coup.
            System.Diagnostics.Debug.WriteLine(
                $"[EmulatorSeedService] SaveChangesAsync global échoué, retry entrée par entrée : {ex.Message}");
            foreach (var e in ctx.ChangeTracker.Entries<Emulator>().ToList())
            {
                try
                {
                    await using var singleCtx = await _dbFactory.CreateDbContextAsync(ct);
                    if (e.State == Microsoft.EntityFrameworkCore.EntityState.Added)
                        singleCtx.Set<Emulator>().Add(e.Entity);
                    else
                        singleCtx.Set<Emulator>().Update(e.Entity);
                    await singleCtx.SaveChangesAsync(ct);
                }
                catch (Exception exSingle)
                {
                    // Conflit PK/UNIQUE = l'entrée existe déjà → non bloquant, ne pas compter comme échec
                    var msg = exSingle.InnerException?.Message ?? exSingle.Message;
                    bool isConflict = msg.Contains("UNIQUE") || msg.Contains("PRIMARY KEY")
                                   || msg.Contains("duplicate") || msg.Contains("already exists");
                    if (!isConflict)
                    {
                        failed++;
                        created = Math.Max(0, created - 1);
                    }
                    System.Diagnostics.Debug.WriteLine(
                        $"[EmulatorSeedService] Échec sauvegarde individuelle {e.Entity.Name} ({(isConflict ? "conflit ignoré" : "erreur")}) : {exSingle.Message}");
                }
            }
        }

        // ── Seed configs et settings émulateurs ──────────────────────────────
        // INSERT OR IGNORE : idempotent — ne touche pas les configs déjà créées.
        // Fait ici (après installation des émulateurs) et non dans DbInitializer,
        // pour ne pas pré-remplir EmulatorConfigs/EmulatorSettings avant le wizard.
        try
        {
            var connStr = ctx.Database.GetConnectionString()!;
            await DemoBase.Data.DbInitializer.SeedEmulatorConfigsAsync(connStr);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EmulatorSeedService] SeedEmulatorConfigsAsync échoué (non bloquant) : {ex.Message}");
        }

        return new EmulatorSeedResult(EmulatorSeedCatalog.All.Count, created, detected, failed);
    }

    /// <summary>
    /// Cherche Chrome aux emplacements d'installation standard sous Windows
    /// (Program Files 64/32-bit, puis installation utilisateur sans droits admin).
    /// </summary>
    private static string? FindSystemChrome()
    {
        var candidates = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        }
        .Where(p => !string.IsNullOrEmpty(p))
        .Select(p => Path.Combine(p, "Google", "Chrome", "Application", "chrome.exe"));

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Cherche le JRE dédié DemoBase (Externals/JRE/bin/javaw.exe — cf.
    /// EmulatorDownloadCatalog.AllExternals, téléchargé comme ZXTune/UADE/RECOIL
    /// depuis l'écran "Outils externes"). Prioritaire sur FindSystemJava() : ne
    /// nécessite ni installation ni mise à jour du Java du poste. Ne s'applique
    /// qu'aux lignes Emulator neuves ou dont l'ExecutablePath est encore vide
    /// (cf. commentaire de classe) — un profil déjà configuré manuellement (ou
    /// auto-détecté avant l'ajout du JRE dédié) n'est jamais écrasé automatiquement.
    /// </summary>
    private static string? FindBundledJre()
    {
        var candidate = Path.Combine(AppContext.BaseDirectory, "Externals", "JRE", "bin", "javaw.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Cherche java.exe : d'abord JAVA_HOME, puis le PATH système, puis les
    /// emplacements d'installation standard (Program Files\Java\*, Eclipse
    /// Adoptium, qui a remplacé le JDK Oracle par défaut sur beaucoup de postes).
    /// </summary>
    private static string? FindSystemJava()
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
        {
            var candidate = Path.Combine(javaHome, "bin", "java.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir, "java.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* segment de PATH invalide — ignorer */ }
        }

        foreach (var root in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Foundation"),
        })
        {
            if (!Directory.Exists(root)) continue;
            try
            {
                var match = Directory.GetFiles(root, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (match != null) return match;
            }
            catch { /* dossier illisible */ }
        }

        return null;
    }

    /// <summary>
    /// Recherche récursive insensible à la casse d'un des noms d'exécutable
    /// candidats dans Emus/{FolderName}. Tolère que l'archive téléchargée ait
    /// extrait dans un sous-dossier (ex. Emus/Vice/bin/x64sc.exe au lieu de
    /// Emus/Vice/x64sc.exe).
    /// </summary>
    private static string? FindExecutable(EmulatorSeedEntry entry)
    {
        if (entry.FolderName == null || entry.ExeCandidates.Length == 0)
            return null;

        var dir = Path.Combine(EmulatorInstallerService.EmusRoot, entry.FolderName);
        if (!Directory.Exists(dir)) return null;

        try
        {
            var allExes = Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories);
            foreach (var candidate in entry.ExeCandidates)
            {
                // Support wildcard : "dcmoto*.exe" matche "dcmoto-64_20260114.exe"
                if (candidate.Contains('*') || candidate.Contains('?'))
                {
                    var regex = "^" + System.Text.RegularExpressions.Regex.Escape(candidate)
                        .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    var match = allExes.FirstOrDefault(f =>
                        System.Text.RegularExpressions.Regex.IsMatch(
                            Path.GetFileName(f), regex,
                            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
                    if (match != null) return match;
                }
                else
                {
                    var match = allExes.FirstOrDefault(f =>
                        string.Equals(Path.GetFileName(f), candidate, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
            }
        }
        catch { /* dossier illisible, accès refusé… → laisser vide, configurable manuellement */ }

        return null;
    }
}
