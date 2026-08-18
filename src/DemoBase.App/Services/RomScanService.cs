using DemoBase.App.Services.ReleaseBuilder;
using DemoBase.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.Services;

/// <summary>
/// Progression d'un scan. <see cref="ArchiveMessage"/> non-nul signifie qu'une archive est
/// en cours de traitement — l'UI affiche alors une DEUXIÈME barre de progression, en plus de
/// la barre globale (2026-07-28, demande utilisateur : "si oui au sein d'une archive peut tu
/// mettre une deuxieme progress bar au cas où l'archive est importante").
/// <see cref="ArchiveIndeterminate"/> est vrai pendant la phase d'extraction proprement dite
/// (aucune progression fiable disponible — ArchiveExtractor n'expose pas de callback par
/// octet/entrée, cf. commentaire sur ProcessArchiveAsync) puis repasse à faux dès qu'on
/// entame la comparaison des fichiers extraits (là, un pourcentage réel entrée/total est
/// disponible).
/// </summary>
public record RomScanProgress(
    string  Message,
    int     Percent,
    string? ArchiveMessage       = null,
    int     ArchivePercent       = 0,
    bool    ArchiveIndeterminate = false);

/// <summary>
/// Une release dont le ZIP a été écrit ou mis à jour pendant le scan — pas forcément
/// complète (2026-07-28, demande utilisateur : "il faudrait qu'il puisse le faire aussi en
/// partiel [...] il peut y avoir des fichiers dans l'archive qui ne sont pas vital au
/// démarrage de l'application"). <see cref="IsComplete"/> indique si TOUS les DatRom
/// attendus sont désormais présents (même convention que le badge vert/rouge de l'onglet
/// Files, cf. DatEntryStatusToColorConverter) ; sinon la release reste "partielle" mais son
/// ZIP contient déjà ce qui a été trouvé — suffisant dans la pratique pour lancer l'émulateur
/// si le fichier réellement nécessaire (disque/exécutable principal) en fait partie, cf.
/// ReleaseService.LaunchAsync/EmulatorService qui n'exigent jamais un set 100% complet pour
/// lancer, juste que le ZIP existe et contienne AU MOINS un fichier reconnu par l'émulateur.
/// </summary>
public record RomScanUpdatedRelease(
    int DemozooId, string RomPath, int NewFilesAdded, int SatisfiedCount, int TotalCount, bool IsComplete);

public record RomScanResult(
    int FilesScanned, int ArchivesScanned, int FilesMatched,
    List<RomScanUpdatedRelease> UpdatedReleases);

/// <summary>
/// "Scan ROMs" (2026-07-27, demande utilisateur) : scanne un dossier choisi par
/// l'utilisateur (fichiers isolés récupérés au fil du temps — ex. plusieurs .dsk
/// Amstrad CPC) et les fait correspondre au catalogue DATs (taille puis CRC32,
/// même principe que ReleaseBuilderService.TryMatchFileToSets/BuildZipForSet).
/// Chaque fichier trouvé est ajouté au ZIP de son DatEntry ("set") dans le dossier
/// Releases — sans passer par un téléchargement.
///
/// 2026-07-28 (demande utilisateur : "je pense que tu scan juste le fichier,
/// indepedemment qu'il s'agisse d'archives ou non. il faudrait scanner l'intérieur
/// d'une archive") : les fichiers reconnus comme archive (.zip/.7z/.rar/.gz/.tgz/
/// .lzh/.lha/.lzx/.bz2/.tar/.zst) sont désormais extraits (réutilise
/// ArchiveExtractor, déjà utilisé par ReleaseBuilderService) et leur contenu comparé
/// au catalogue exactement comme un fichier isolé — récursivement, avec une limite
/// de profondeur (archive dans une archive) pour rester borné.
///
/// 2026-07-28 bis (demande utilisateur : "il faudrait qu'il puisse le faire aussi en
/// partiel [...] il peut y avoir des fichiers dans l'archive qui ne sont pas vital au
/// démarrage de l'application") : un set n'a plus besoin d'être satisfait à 100% pour
/// que son ZIP soit écrit — chaque fichier trouvé est ajouté au ZIP existant (ou un
/// nouveau ZIP est créé), en PRÉSERVANT ce qu'un scan précédent y avait déjà déposé
/// (mode Update plutôt que Create-et-écrase). Seuls les DatEntry DÉJÀ complets à 100%
/// (même convention que le badge vert de l'onglet Files, cf.
/// DatEntryStatusToColorConverter) sont ignorés d'emblée — rien à y ajouter.
/// </summary>
public class RomScanService(DatImportService datImportService, PreferencesService prefs)
{
    // Extensions reconnues comme archive — ".gz" couvre aussi bien un ".tar.gz"/".tgz" qu'un
    // simple fichier isolé compressé (ex. "disk.dsk.gz"), les deux cas sont dispatchés
    // correctement par ArchiveExtractor.ExtractAny selon le nom complet du fichier.
    // 2026-07-30, retour utilisateur ("rechercher des releases" devrait trouver plus de
    // fichiers) : ".arj" et ".adz" étaient gérés par le téléchargement
    // (ReleaseBuilderService.ProcessDownloadedFile) mais absents ici — un fichier .arj ou
    // .adz posé dans le dossier scanné n'était jamais ouvert, juste comparé tel quel (donc
    // jamais reconnu, son contenu réel n'étant jamais atteint).
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".lzh", ".lha", ".lzx", ".bz2", ".tar", ".zst",
        ".arj", ".adz",
    };

    // Protection contre une archive imbriquée dans elle-même / un enchaînement pathologique —
    // jamais rencontré en pratique sur des roms demoscene, mais coûte peu à borner.
    private const int MaxArchiveDepth = 5;

    private class ScanSet
    {
        public required int DatEntryId;
        public required int DemozooId;
        public required string RomPath;
        public required List<DatRomIndexEntry> Roms;

        /// <summary>Noms d'entrée (normalisés) déjà présents dans le ZIP existant sur disque
        /// au moment où le scan a commencé — le cas échéant (un scan précédent a pu déjà
        /// déposer une partie des fichiers). Vide si le ZIP n'existe pas encore.</summary>
        public required HashSet<string> AlreadyPresent;

        /// <summary>Nouveaux fichiers trouvés durant CE passage de scan (pas encore écrits
        /// sur disque tant que BuildOrUpdateZip n'a pas tourné).</summary>
        public readonly Dictionary<DatRomIndexEntry, string> Satisfied = new();

        public bool IsRomAlreadyPresent(DatRomIndexEntry rom) => AlreadyPresent.Contains(RomEntryName(rom));

        /// <summary>Nombre total de roms satisfaits (déjà dans le ZIP + trouvés ce passage),
        /// compté uniquement parmi les roms réellement attendus par ce DatEntry.</summary>
        public int SatisfiedTotalCount => Roms.Count(IsRomAlreadyPresent) + Satisfied.Count;

        public bool IsComplete => Roms.Count > 0 && SatisfiedTotalCount == Roms.Count;
    }

    // État partagé pendant un scan — évite de faire circuler 6-7 paramètres à travers
    // chaque appel récursif (fichier direct / entrée d'archive / archive imbriquée).
    private class ScanContext
    {
        public required Dictionary<long, List<(ScanSet Set, DatRomIndexEntry Rom)>> BySize;
        public required IProgress<RomScanProgress>? Progress;
        public required CancellationToken Ct;
        public required string WorkDir;
        public int FilesScanned;
        public int ArchivesScanned;
        public int FilesMatched;
        public double OuterPercent;
    }

    public async Task<RomScanResult> ScanFolderAsync(
        string folder, IProgress<RomScanProgress>? progress = null, CancellationToken ct = default)
    {
        progress?.Report(new("Chargement du catalogue DATs…", 0));
        var index = await datImportService.GetAllRomsIndexAsync(ct);
        var romsRoot = (await prefs.LoadAllAsync()).ResolvedPathReleases;

        // Un "set" par DatEntry (= une version possible d'une release). Si un ZIP existe déjà
        // pour ce set, on lit ses entrées pour savoir ce qu'il contient déjà — un scan
        // précédent (ou un autre dossier) a pu déjà en déposer une partie (2026-07-28,
        // demande utilisateur : le scan doit pouvoir compléter progressivement une release
        // au lieu d'exiger 100% des fichiers en un seul passage). Seuls les sets DÉJÀ
        // complets (même convention que le badge vert de l'onglet Files, cf.
        // DatEntryStatusToColorConverter.IsSetComplete) sont exclus d'emblée — rien à y
        // ajouter.
        var sets = index
            .GroupBy(r => r.DatEntryId)
            .Select(g =>
            {
                var romPath = g.First().RomPath;
                var zipPath = string.IsNullOrEmpty(romPath) ? null : Path.Combine(romsRoot, romPath);
                return new ScanSet
                {
                    DatEntryId     = g.Key,
                    DemozooId      = g.First().DemozooId,
                    RomPath        = romPath,
                    Roms           = g.ToList(),
                    AlreadyPresent = zipPath != null ? GetExistingEntryNames(zipPath)
                                                      : new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                };
            })
            .Where(s => !string.IsNullOrEmpty(s.RomPath) && !s.IsComplete)
            .ToList();

        var bySize = BuildSizeIndex(sets);

        progress?.Report(new($"Catalogue chargé — {sets.Count} release(s) incomplète(s) suivie(s).", 3));

        List<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SCANROMS] Impossible de lister {folder} : {ex.Message}");
            files = new List<string>();
        }

        var workDir = Path.Combine(AppPaths.Working, "RomScan", $"scan_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        var ctx = new ScanContext
        {
            BySize   = bySize,
            Progress = progress,
            Ct       = ct,
            WorkDir  = workDir,
        };

        try
        {
            for (int i = 0; i < files.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var file = files[i];
                ctx.OuterPercent = 3 + 96.0 * (i + 1) / Math.Max(1, files.Count);

                progress?.Report(new($"Analyse : {Path.GetFileName(file)} ({i + 1}/{files.Count})…",
                    (int)ctx.OuterPercent));

                await ProcessPathAsync(file, ctx, depth: 0);
            }

            progress?.Report(new("Écriture des releases mises à jour…", 99));
            var updated = new List<RomScanUpdatedRelease>();
            foreach (var set in sets)
            {
                // Rien de nouveau trouvé pour ce set pendant CE passage → on ne touche pas à
                // son ZIP (que ce set soit déjà partiellement rempli ou totalement vide).
                if (set.Satisfied.Count == 0) continue;

                try
                {
                    var destZip = Path.Combine(romsRoot, set.RomPath);
                    BuildOrUpdateZip(set, destZip);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SCANROMS] Échec construction/mise à jour ZIP {set.RomPath} : {ex.Message}");
                    continue;
                }

                // BuildOrUpdateZip retire de Satisfied les entrées qui n'ont finalement pas pu
                // être écrites (fichier disparu, erreur d'E/S) — 2026-07-28, retour utilisateur :
                // "la recherche de release a réussi à créer des zips vides. pas bon". Si plus
                // rien n'a survécu pour ce set, il n'y a rien à rapporter.
                if (set.Satisfied.Count == 0) continue;

                int total = set.SatisfiedTotalCount;
                updated.Add(new RomScanUpdatedRelease(
                    set.DemozooId, set.RomPath, set.Satisfied.Count, total, set.Roms.Count,
                    IsComplete: total == set.Roms.Count));
            }

            if (updated.Count > 0)
            {
                // Les couleurs vert/rouge de l'onglet Files (DatEntryStatusToColorConverter)
                // ont leur propre cache de 5s — on le vide explicitement pour refléter
                // immédiatement les ZIP tout juste modifiés, même principe que
                // ReleaseViewModels après un (re)build réussi.
                DemoBase.App.Converters.DatEntryStatusToColorConverter.ClearCache();
            }

            progress?.Report(new("Terminé.", 100));
            return new RomScanResult(ctx.FilesScanned, ctx.ArchivesScanned, ctx.FilesMatched, updated);
        }
        finally
        {
            // Les fichiers extraits ont déjà été soit consommés (satisfaction d'un rom, copié
            // dans le ZIP final via BuildOrUpdateZip ci-dessus), soit ignorés — comme
            // ReleaseBuilderService.TryBuildAsync, on ne nettoie qu'à la toute fin, une fois
            // tous les ZIP construits/mis à jour.
            try { Directory.Delete(workDir, recursive: true); } catch { /* non bloquant */ }
        }
    }

    // ── Dispatch récursif : fichier isolé, ou archive à ouvrir ─────────────────────

    private async Task ProcessPathAsync(string path, ScanContext ctx, int depth)
    {
        ctx.Ct.ThrowIfCancellationRequested();
        if (!File.Exists(path)) return;

        var ext = Path.GetExtension(path).ToLowerInvariant();

        // 2026-07-30, retour utilisateur ("rechercher des releases" devrait trouver plus de
        // fichiers) : le téléchargement (ReleaseBuilderService.ProcessDownloadedFile) convertit
        // les disquettes Amiga .dms → .adf et Atari ST .msa → .st AVANT de comparer au DAT — le
        // DAT référence toujours la version convertie, jamais le .dms/.msa brut. Cette étape
        // manquait ici : un .dms/.msa posé dans le dossier scanné avait son CRC32 comparé tel
        // quel, qui ne correspond à AUCUN rom du catalogue. Même logique, reprise à l'identique.
        if (ext == ".dms")
        {
            var converted = TryConvertDms(path, ctx.WorkDir);
            if (converted != null) { await ProcessPathAsync(converted, ctx, depth); return; }
        }
        else if (ext == ".msa")
        {
            var converted = TryConvertMsa(path, ctx.WorkDir);
            if (converted != null) { await ProcessPathAsync(converted, ctx, depth); return; }
        }

        if (depth < MaxArchiveDepth && ArchiveExtensions.Contains(ext))
        {
            ctx.ArchivesScanned++;
            await ProcessArchiveAsync(path, ext, ctx, depth);
            return;
        }

        ctx.FilesScanned++;
        if (TryMatchFile(path, ctx.BySize)) ctx.FilesMatched++;
    }

    /// <summary>Conversion .dms → .adf, même logique que
    /// ReleaseBuilderService.ProcessDownloadedFile (DemosceneDownloader.Services.DMS.DMS,
    /// inchangée) — écrit dans workDir plutôt qu'à côté du fichier source (qui peut être un
    /// dossier utilisateur en lecture seule ou partagé).</summary>
    private static string? TryConvertDms(string dmsPath, string workDir)
    {
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(dmsPath);
            var adfPath  = Path.Combine(workDir, $"dms_{Guid.NewGuid():N}_{baseName}.adf");
            ushort err = DemosceneDownloader.Services.DMS.DMS.ProcessFile(dmsPath, adfPath, 6, 0, 0, 0);
            bool ok = err == 0 || (err == 12 && File.Exists(adfPath) && new FileInfo(adfPath).Length > 0);
            return ok ? adfPath : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SCANROMS] Conversion .dms échouée ({dmsPath}) : {ex.Message}");
            return null;
        }
    }

    /// <summary>Conversion .msa → .st, même logique que
    /// ReleaseBuilderService.ProcessDownloadedFile (DemosceneDownloader.Services.MSA,
    /// inchangée).</summary>
    private static string? TryConvertMsa(string msaPath, string workDir)
    {
        try
        {
            var baseName = Path.GetFileNameWithoutExtension(msaPath);
            var stPath   = Path.Combine(workDir, $"msa_{Guid.NewGuid():N}_{baseName}.st");
            int err = DemosceneDownloader.Services.MSA.DecodeMSA(msaPath, stPath);
            return err == 0 ? stPath : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SCANROMS] Conversion .msa échouée ({msaPath}) : {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extrait une archive et traite chacun de ses fichiers récursivement (une archive peut
    /// en contenir une autre, ex. un .tar.zst, ou un .zip de plusieurs .dsk).
    ///
    /// Limitation connue : ArchiveExtractor (SharpZipLib/SevenZipExtractor sous-jacents,
    /// partagés avec ReleaseBuilderService — logique volontairement inchangée) n'expose pas
    /// de callback de progression par octet ou par entrée pendant l'EXTRACTION elle-même :
    /// la 2ème barre reste en mode indéterminé ("ArchiveIndeterminate=true") le temps de
    /// l'extraction, puis devient une vraie barre entrée/total pendant la comparaison des
    /// fichiers extraits (qui est en pratique l'étape la plus longue sur une grosse archive,
    /// à cause du calcul CRC32 par fichier).
    /// </summary>
    private async Task ProcessArchiveAsync(string archivePath, string ext, ScanContext ctx, int depth)
    {
        var archiveName = Path.GetFileName(archivePath);

        // .zst : compression mono-fichier (comme .gz), pas une archive à entrées multiples —
        // le résultat peut lui-même être une autre archive (ex. "demo.tar.zst") ou directement
        // un rom isolé (ex. "disk.dsk.zst") : on le repasse dans le dispatcher général.
        if (ext == ".zst")
        {
            ctx.Progress?.Report(new($"Analyse : {archiveName}…", (int)ctx.OuterPercent,
                $"Décompression de {archiveName}…", 0, ArchiveIndeterminate: true));

            string? decompressed = null;
            try { decompressed = DecompressZstd(archivePath, ctx.WorkDir); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SCANROMS] Décompression zstd échouée ({archivePath}) : {ex.Message}");
            }
            if (decompressed != null)
                await ProcessPathAsync(decompressed, ctx, depth + 1);
            return;
        }

        // .adz = .adf.gz (disquette Amiga compressée) — ArchiveExtractor ne reconnaît pas
        // ".adz" directement, il lui faut un nom se terminant par ".adf.gz" pour dispatcher
        // vers le chemin d'extraction .gz générique (même règle que
        // ReleaseBuilderService.ProcessDownloadedFile). On travaille sur une COPIE dans
        // workDir plutôt que de renommer le fichier original du dossier scanné.
        if (ext == ".adz")
        {
            var baseName = Path.GetFileNameWithoutExtension(archivePath);
            var renamed  = Path.Combine(ctx.WorkDir, $"adz_{Guid.NewGuid():N}_{baseName}.adf.gz");
            try { File.Copy(archivePath, renamed, overwrite: true); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SCANROMS] Copie .adz échouée ({archivePath}) : {ex.Message}");
                return;
            }
            archivePath = renamed;
            ext         = ".adf.gz";
            archiveName = Path.GetFileName(archivePath);
        }

        ctx.Progress?.Report(new($"Analyse : {archiveName}…", (int)ctx.OuterPercent,
            $"Extraction de {archiveName}…", 0, ArchiveIndeterminate: true));

        var extractDir = Path.Combine(ctx.WorkDir, $"arc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(extractDir);

        bool ok;
        try
        {
            ok = ArchiveExtractor.ExtractAny(archivePath, extractDir, ext);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SCANROMS] Extraction échouée ({archivePath}) : {ex.Message}");
            ok = false;
        }

        if (!ok) return;

        var extracted = Directory.GetFiles(extractDir, "*", SearchOption.AllDirectories);
        for (int i = 0; i < extracted.Length; i++)
        {
            ctx.Ct.ThrowIfCancellationRequested();
            int entryPct = extracted.Length > 0 ? (int)(100.0 * (i + 1) / extracted.Length) : 100;
            ctx.Progress?.Report(new($"Analyse : {archiveName}…", (int)ctx.OuterPercent,
                $"{Path.GetFileName(extracted[i])} ({i + 1}/{extracted.Length})", entryPct,
                ArchiveIndeterminate: false));

            await ProcessPathAsync(extracted[i], ctx, depth + 1);
        }
    }

    private static string DecompressZstd(string zstPath, string workDir)
    {
        // Path.GetFileNameWithoutExtension retire uniquement ".zst" — "demo.tar.zst" devient
        // "demo.tar", qui sera lui-même reconnu comme archive (.tar) au prochain passage du
        // dispatcher (ProcessPathAsync).
        var baseName = Path.GetFileNameWithoutExtension(zstPath);
        var outPath  = Path.Combine(workDir, $"zstd_{Guid.NewGuid():N}_{baseName}");

        using (var input = File.OpenRead(zstPath))
        using (var zstdStream = new ZstdSharp.DecompressionStream(input))
        using (var output = File.Create(outPath))
        {
            zstdStream.CopyTo(output);
        }
        return outPath;
    }

    // ── Correspondance fichier ↔ DatRom (taille + CRC32) ────────────────────────

    private static bool TryMatchFile(
        string filePath, Dictionary<long, List<(ScanSet Set, DatRomIndexEntry Rom)>> bySize)
    {
        long size;
        try { size = new FileInfo(filePath).Length; }
        catch { return false; }

        if (!bySize.TryGetValue(size, out var candidates)) return false;

        uint? crc = null;
        bool matched = false;

        foreach (var (set, rom) in candidates)
        {
            if (set.IsComplete) continue;
            if (set.Satisfied.ContainsKey(rom)) continue;
            if (set.IsRomAlreadyPresent(rom)) continue;
            if (string.IsNullOrEmpty(rom.Crc32)) continue;
            if (!uint.TryParse(rom.Crc32, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var expectedCrc))
                continue;

            try { crc ??= DatMaker.Crc32.GetFileCRC32(filePath); }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SCANROMS] CRC32 échoué sur {filePath} : {ex.Message}");
                break;
            }
            if (crc.Value != expectedCrc) continue;

            set.Satisfied[rom] = filePath;
            matched = true;
            // Pas de "break" ici — un même fichier physique peut satisfaire le même rom
            // dans plusieurs sets différents (versions alternatives partageant un rom
            // identique), même logique que ReleaseBuilderService.TryMatchFileToSets.
        }

        return matched;
    }

    private static Dictionary<long, List<(ScanSet Set, DatRomIndexEntry Rom)>> BuildSizeIndex(List<ScanSet> sets)
    {
        // Index par taille pour éviter de calculer un CRC32 (coûteux) sur des fichiers dont
        // la taille ne correspond à aucun rom recherché — le catalogue peut compter plusieurs
        // centaines de milliers de lignes.
        var bySize = new Dictionary<long, List<(ScanSet, DatRomIndexEntry)>>();
        foreach (var set in sets)
        {
            foreach (var rom in set.Roms)
            {
                if (!bySize.TryGetValue(rom.Size, out var list))
                    bySize[rom.Size] = list = new List<(ScanSet, DatRomIndexEntry)>();
                list.Add((set, rom));
            }
        }
        return bySize;
    }

    /// <summary>
    /// Écrit un ZIP flambant neuf (aucun ZIP existant pour ce set) ou AJOUTE les fichiers
    /// nouvellement trouvés à un ZIP déjà présent (mode Update — les entrées déjà là,
    /// trouvées lors d'un scan précédent, sont préservées) : 2026-07-28, demande utilisateur
    /// — le scan doit pouvoir compléter une release progressivement, pas uniquement en un
    /// seul passage tout-ou-rien.
    /// </summary>
    /// <summary>
    /// 2026-07-28, retour utilisateur (capture d'écran 7-Zip) : "la recherche de release a
    /// réussi à créer des zips vides. pas bon". Root cause probable : un fichier trouvé
    /// pendant le scan (notamment extrait d'une archive dans le dossier de travail temporaire)
    /// pouvait ne plus exister au moment de l'écriture finale, ou <see cref="ZipArchive"/>
    /// pouvait échouer sur UNE entrée sans que ça empêche <c>Dispose()</c> d'écrire quand même
    /// un central directory (valide mais vide) pour les autres. Durci en conséquence : chaque
    /// fichier est vérifié individuellement, un échec sur l'un d'eux n'empêche pas les autres
    /// d'être écrits, et le ZIP est supprimé plutôt que laissé sur disque s'il finit à 0 entrée
    /// (que ce soit un ZIP flambant neuf ou un ZIP existant resté vide malgré cette tentative).
    /// Les roms qui n'ont finalement pas pu être écrits sont retirés de <see
    /// cref="ScanSet.Satisfied"/> pour que le résumé affiché à l'utilisateur reflète ce qui est
    /// VRAIMENT dans le ZIP, pas seulement ce qui avait été trouvé en mémoire pendant le scan.
    /// </summary>
    private static void BuildOrUpdateZip(ScanSet set, string destZip)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);
        var mode = File.Exists(destZip) ? ZipArchiveMode.Update : ZipArchiveMode.Create;

        var toRemove = new List<DatRomIndexEntry>();
        int finalEntryCount;

        using (var archive = ZipFile.Open(destZip, mode))
        {
            foreach (var (rom, localPath) in set.Satisfied)
            {
                if (!File.Exists(localPath))
                {
                    System.Diagnostics.Debug.WriteLine($"[SCANROMS] Fichier disparu avant écriture ({localPath}) — rom ignoré.");
                    toRemove.Add(rom);
                    continue;
                }

                var entryName = RomEntryName(rom);
                try
                {
                    // Défensif : ne devrait normalement pas arriver puisque AlreadyPresent
                    // exclut déjà ces roms de la recherche (TryMatchFile), mais évite une
                    // exception si une entrée du même nom existait quand même déjà.
                    archive.GetEntry(entryName)?.Delete();
                    archive.CreateEntryFromFile(localPath, entryName, CompressionLevel.Optimal);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[SCANROMS] Échec écriture entrée '{entryName}' ({localPath}) : {ex.Message}");
                    toRemove.Add(rom);
                }
            }

            finalEntryCount = archive.Entries.Count;
        }

        foreach (var rom in toRemove) set.Satisfied.Remove(rom);

        if (finalEntryCount == 0)
        {
            try { File.Delete(destZip); } catch { }
        }
    }

    /// <summary>Noms d'entrée déjà présents dans un ZIP existant, normalisés (slashs,
    /// insensible à la casse). ZIP absent ou illisible → ensemble vide (traité comme
    /// "rien encore", sera (re)créé normalement). Auto-nettoyage (2026-07-28) : un ZIP déjà
    /// présent mais totalement vide (0 entrée — artefact possible d'un scan précédent avant
    /// ce correctif) est supprimé au passage, il n'a aucune valeur à préserver.</summary>
    private static HashSet<string> GetExistingEntryNames(string zipPath)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(zipPath)) return result;
        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                foreach (var entry in zip.Entries)
                    result.Add(NormalizeEntryName(entry.FullName));
            }

            if (result.Count == 0)
            {
                System.Diagnostics.Debug.WriteLine($"[SCANROMS] ZIP existant mais vide ({zipPath}) — suppression.");
                try { File.Delete(zipPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SCANROMS] ZIP illisible ({zipPath}), traité comme vide : {ex.Message}");
        }
        return result;
    }

    /// <summary>Nom d'entrée ZIP effectif pour un DatRom — même règle que
    /// ReleaseBuilderService.BuildZipForSet : décodage des entités HTML/XML résiduelles
    /// ("&amp;") puis normalisation des slashs, pour que ce qu'on écrit et ce qu'on
    /// recherche dans un ZIP déjà existant utilisent exactement la même clé.</summary>
    private static string RomEntryName(DatRomIndexEntry rom)
        => NormalizeEntryName(System.Net.WebUtility.HtmlDecode(rom.Name));

    /// <summary>Même normalisation que DatEntryStatusToColorConverter.NormalizeEntryName —
    /// les métadonnées DAT stockent parfois les chemins avec des antislashs, un ZIP stocke
    /// toujours ses entrées avec des slashs.</summary>
    private static string NormalizeEntryName(string name) => name.Replace('\\', '/').TrimStart('/');
}
