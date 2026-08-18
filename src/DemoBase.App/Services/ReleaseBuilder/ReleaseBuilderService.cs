using DemoBase.Core.Interfaces;
using DemoBase.Core.Models;
using DemoBase.Data;
using DemoBase.Data.Context;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.Services.ReleaseBuilder;

public record BuildProgress(string Message, int Percent);

// FoundRomIds : Id (DatRom.Id) des fichiers effectivement trouvés/téléchargés lors de CETTE
// tentative — 2026-07-29, retour utilisateur : "possibile de mettre dans la liste des fichiers
// celui est correspond au dat ?" (savoir lequel des N fichiers attendus a été trouvé, pas
// seulement le compte). Paramètre optionnel pour ne pas casser les appels existants.
public record BuildResult(bool Success, string? ZipPath, string? Error, int FilesFound, int FilesNeeded,
    IReadOnlyList<int>? FoundRomIds = null);

/// <summary>
/// Reconstruit automatiquement le ZIP d'une release manquante sur le disque, en
/// téléchargeant ses fichiers depuis les download links Demozoo et en les
/// vérifiant contre les fichiers DAT (taille + CRC32).
///
/// Principe (cf. discussion utilisateur) :
///   1. Charger tous les DatEntry (= "sets" possibles, versions alternatives
///      incluses) liés au DemozooId de la release.
///   2. Scanner les ReleaseLink de la release, en excluant les link_class qui
///      ne pointent jamais vers un fichier téléchargeable.
///   3. Pour chaque lien : résoudre l'URL réelle, télécharger dans Working/,
///      extraire/convertir (zip/rar/7z/lzx/lha/tar.gz/tar.bz2, DMS→ADF,
///      MSA→ST — logique ArchiveExtractor/DMS/MSA non modifiée).
///   4. Calculer CRC32+taille de chaque fichier obtenu, le comparer aux DatRom
///      non encore satisfaits de TOUS les DatEntry candidats.
///   5. Dès qu'un set est complet → construire son ZIP et s'arrêter (inutile
///      de télécharger le reste).
/// </summary>
public class ReleaseBuilderService(
    IReleaseService releaseService,
    IDbContextFactory<DemoBaseDbContext> dbFactory,
    PreferencesService prefs,
    DemoBase.Data.DownloadAttemptService downloadAttempts)
{
    /// <summary>URL du dossier Mega.nz contenant les ROMS (structure identique au RomPath).</summary>
    // Link_class qui ne pointent jamais vers un fichier téléchargeable —
    // porté tel quel depuis DemozooDownloadService.ExcludedLinkClasses
    // (projet de référence fourni par l'utilisateur).
    private static readonly HashSet<string> ExcludedLinkClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        // Vidéos en streaming — non téléchargeables
        "YoutubeVideo",     "VimeoVideo",        "DemosceneTvVideo",
        "CappedVideo",      "DosDemosVideo",

        // Pages et profils externes — pas de fichier
        "PouetProduction",  "KestraBitworldRelease",
        "ZxdemoItem",       "StonishDisk",
        "HallOfLightGame",  "DiscogsRelease",        "DOPEdition",
        "NectarineSong",    "HearthisTrack",
        "SpotifyTrack",     "BitjamSong",
        "WikipediaPage",    "PixeljointImage",       "ShadertoyShader",
        "Pico8Cart",
        "SpectrumComputingRelease",
    };

    private static readonly HttpClient _http = BuildHttpClient();
    private static readonly UrlResolver _resolver = new(_http, BuildNoRedirectClient());

    // 2026-07-30, retour utilisateur : un lien AtariAge (forums.atariage.com/.../attachment.php)
    // marche très bien dans un navigateur (sans connexion) mais échoue systématiquement depuis
    // DemoBase ("Téléchargement vide", HTTP non-succès). Hypothèse la plus probable, non
    // vérifiable depuis cet environnement (accès réseau restreint côté sandbox de dev) :
    // l'ancien User-Agent s'identifiait explicitement comme non-navigateur
    // ("...DemoBase-ReleaseBuilder/1.0"), ce qui déclenche souvent un blocage anti-bot sur les
    // forums IPS/Invision (attachment.php en particulier). Remplacé par un User-Agent de
    // navigateur standard, sans suffixe identifiant — comportement à reconfirmer par test réel.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36";

    private static HttpClient BuildHttpClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return c;
    }

    private static HttpClient BuildNoRedirectClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var c = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        c.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return c;
    }

    // ── État interne d'un "set" en cours de complétion ─────────────────────────

    private class SetProgress
    {
        public required DatEntry Entry;
        // Rom non encore satisfait → chemin du fichier local qui le satisfait, une fois trouvé
        public readonly Dictionary<DatRom, string> Satisfied = new();

        // 2026-07-29, retour utilisateur (Atari 7800, "SN Cart Demo") : le DAT listait un
        // SNCartDemo.txt dont la taille ne correspondait pas (version différente du fichier
        // texte), ce qui empêchait le set de passer "complet" alors que SNCartDemo.BIN — seul
        // fichier réellement lu par l'émulateur (ProSystemLauncher.IgnoredExtensions ignore
        // déjà .txt à l'extraction) — était trouvé et vérifié. Mêmes extensions que
        // IgnoredTextExtensions (ReleaseViewModels.cs, PlayVideoInlineAsync) : jamais un
        // fichier qu'un émulateur charge au démarrage, donc jamais bloquant pour juger un set
        // "assez complet pour être construit/lancé". Reste inclus dans le ZIP final s'il a
        // quand même été trouvé (BuildZipForSet zippe tout ce qui est dans Satisfied).
        private static readonly HashSet<string> NonEssentialExtensions =
            new(StringComparer.OrdinalIgnoreCase)
            { ".txt", ".nfo", ".diz", ".doc", ".docx", ".pdf", ".md" };

        private IEnumerable<DatRom> EssentialRoms =>
            Entry.Roms.Where(r => !NonEssentialExtensions.Contains(Path.GetExtension(r.Name)));

        public bool IsComplete
        {
            get
            {
                var essential = EssentialRoms.ToList();
                // Set composé UNIQUEMENT de fichiers non essentiels (cas limite improbable) —
                // retombe sur la règle stricte plutôt que de déclarer un set "vide" complet.
                return essential.Count > 0
                    ? essential.All(Satisfied.ContainsKey)
                    : Satisfied.Count == Entry.Roms.Count && Entry.Roms.Count > 0;
            }
        }
    }

    /// <summary>
    /// Tente de reconstruire le ZIP de la release identifiée par son DemozooId.
    /// Retourne BuildResult.Success=true si un set complet a pu être construit
    /// (et écrit sur disque, prêt à être utilisé immédiatement).
    /// </summary>
    /// <summary>
    /// Tente de reconstruire le ZIP de la release identifiée par son DemozooId.
    /// Retourne BuildResult.Success=true si un set complet a pu être construit
    /// (et écrit sur disque, prêt à être utilisé immédiatement).
    /// </summary>
    /// <param name="preferredDatEntryId">
    /// Id du DatEntry explicitement sélectionné par l'utilisateur dans l'onglet
    /// Files (bouton "Use"/"Selected"), le cas échéant. Si fourni, ce set précis
    /// est celui qu'on cherche à compléter — les autres sets continuent d'être
    /// suivis en parallèle (un même lien peut satisfaire plusieurs sets à la
    /// fois) mais NE déclenchent PAS un arrêt anticipé : on ne bascule sur un
    /// set différent qu'en tout dernier recours, si le préféré n'a jamais pu
    /// être complété après épuisement de tous les liens. Sans préférence
    /// (Play générique, sans sélection explicite), on garde l'ancien
    /// comportement : le premier set complet gagne, pour aller au plus vite.
    /// </param>
    public async Task<BuildResult> TryBuildAsync(
        int demozooId, IProgress<BuildProgress>? progress = null,
        int? preferredDatEntryId = null, CancellationToken ct = default)
    {
        progress?.Report(new("Recherche des informations de la release…", 0));

        var datEntries = (await releaseService.GetDatEntriesAsync(demozooId)).ToList();
        if (datEntries.Count == 0)
            return new(false, null, "Aucun fichier DAT connu pour cette release.", 0, 0);

        await using var ctx = await dbFactory.CreateDbContextAsync(ct);
        var release = await ctx.Releases
            .Include(r => r.Links)
            .FirstOrDefaultAsync(r => r.DemozooId == demozooId, ct);
        if (release == null)
            return new(false, null, "Release introuvable.", 0, 0);

        var links = release.Links
            .Where(l => !string.IsNullOrEmpty(l.LinkClass) && !ExcludedLinkClasses.Contains(l.LinkClass!))
            .Where(l => !string.IsNullOrEmpty(l.LinkParameter) || !string.IsNullOrEmpty(l.Url))
            .ToList();

        if (links.Count == 0)
            return new(false, null, "Aucun download link exploitable pour cette release.", 0,
                datEntries.Max(e => e.Roms.Count));

        var romsRoot = (await prefs.LoadAllAsync()).ResolvedPathReleases;

        var sets = datEntries.Select(e => new SetProgress { Entry = e }).ToList();
        int totalNeeded = sets.Max(s => s.Entry.Roms.Count);

        System.Diagnostics.Debug.WriteLine($"[BUILD] {sets.Count} set(s), {links.Count} lien(s)");
        foreach (var s in sets)
            System.Diagnostics.Debug.WriteLine($"[BUILD]   Set: {s.Entry.RomPath} — {s.Entry.Roms.Count} ROM(s): {string.Join(", ", s.Entry.Roms.Select(r => $"{r.Name}(size={r.Size},crc={r.Crc32})"))}");
        foreach (var l in links)
            System.Diagnostics.Debug.WriteLine($"[BUILD]   Lien: #{l.Id} class={l.LinkClass} param={l.LinkParameter}");

        var workDir = Path.Combine(AppPaths.Working, "ReleaseBuilder", $"dz{demozooId}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            int linkIndex   = 0;
            int failedLinks = 0;
            var failReasons = new System.Collections.Generic.List<string>();
            // 2026-07-29, retour utilisateur : le message "Téléchargement incomplet : 1/3"
            // ne disait ni quel fichier avait été trouvé, ni pourquoi les autres manquaient.
            // On garde une trace de CHAQUE mismatch de taille rencontré (avec le lien source)
            // pour pouvoir l'expliquer précisément dans le message final. 2026-07-30, retour
            // utilisateur : "check juste la taille et les crc32" — on inclut maintenant aussi le
            // CRC32 réel du fichier téléchargé (pas seulement celui attendu par le DAT), pour que
            // le message affiche exactement ce que l'utilisateur vérifie déjà manuellement (taille
            // + CRC32 des deux côtés, comme dans WinRAR).
            var allMismatches = new List<(string Rom, long FileSize, long DatSize, string? DatCrc32, string? FileCrc32, string LinkDesc)>();

            foreach (var link in links)
            {
                ct.ThrowIfCancellationRequested();
                linkIndex++;
                System.Diagnostics.Debug.WriteLine($"[LOOP] Itération {linkIndex}/{links.Count} — lien #{link.Id}");
                try
                {
                // Bande de pourcentage allouée à CE lien — le téléchargement (souvent
                // l'étape la plus longue, en particulier releases à lien unique) progresse
                // maintenant en continu dans cette bande au lieu de rester figé sur une
                // seule valeur fixe du début à la fin (demande utilisateur : certaines
                // releases sont longues à télécharger, il faut un état de complétion).
                double pctStart = 5 + 90.0 * (linkIndex - 1) / links.Count;
                double pctEnd   = 5 + 90.0 * linkIndex / links.Count;
                progress?.Report(new($"Lien {linkIndex}/{links.Count} : {link.LinkClass ?? link.Url ?? "?"}…", (int)pctStart));

                var dlSw = System.Diagnostics.Stopwatch.StartNew();
                void ReportDownloadProgress(long bytesRead, long? totalBytes)
                {
                    if (dlSw.ElapsedMilliseconds < 150 && totalBytes != bytesRead) return;
                    dlSw.Restart();
                    double frac = totalBytes is > 0 ? Math.Min(1.0, (double)bytesRead / totalBytes.Value) : 0;
                    int pct = (int)(pctStart + (pctEnd - pctStart) * frac);
                    string sizeInfo = totalBytes.HasValue
                        ? $"{bytesRead / 1024.0 / 1024.0:F1} / {totalBytes.Value / 1024.0 / 1024.0:F1} Mo"
                        : $"{bytesRead / 1024.0 / 1024.0:F1} Mo";
                    progress?.Report(new(
                        $"Lien {linkIndex}/{links.Count} : téléchargement… {sizeInfo}", pct));
                }

                string? downloadedFile;
                string? resolvedUrl = null;
                // 2026-07-29, retour utilisateur : "il faudrait avoir le lien vers le fichier
                // téléchargé même quand il a échoué" — inclut l'URL résolue (quand elle est
                // connue au moment de l'échec) dans chaque raison, pour pouvoir vérifier/
                // télécharger le fichier manuellement.
                string LinkLabel() => resolvedUrl != null
                    ? $"{link.LinkClass ?? link.Url ?? $"lien #{link.Id}"} ({resolvedUrl})"
                    : (link.LinkClass ?? link.Url ?? $"lien #{link.Id}");
                try
                {
                    // Résoudre l'URL en premier pour pouvoir vérifier le cache mismatch
                    resolvedUrl = await ResolveUrlAsync(link, ct);
                    if (resolvedUrl == null)
                    {
                        failedLinks++;
                        failReasons.Add($"Lien vide (URL non résolue) : {link.LinkClass ?? link.Url ?? link.Id.ToString()}");
                        continue;
                    }

                    // 2026-07-30, retour utilisateur : après un "Réessayer" ayant retrouvé le
                    // .BIN (coche verte), un second clic sur "Lancer" a fait DISPARAÎTRE la
                    // coche et rien ne s'est lancé. Cause : le lien scene.org contient PLUSIEURS
                    // fichiers (SNCartDemo.BIN qui matche, SNCartDemo.txt qui ne matche pas —
                    // mismatch légitime depuis le filtre par nom ajouté plus haut). Le premier
                    // essai a donc enregistré CE mismatch (SNCartDemo.txt) dans le cache
                    // DownloadAttempts, keyé par l'URL du lien entier — et le skip précédent
                    // (basé sur "cette URL a un mismatch connu") ignorait alors TOUT le lien au
                    // second essai, y compris pour retélécharger le .BIN qui matchait très bien.
                    // Le cache par URL est donc trop grossier dès qu'un lien contient plusieurs
                    // fichiers avec des résultats mixtes : on ne l'utilise plus pour SAUTER le
                    // téléchargement (seulement pour l'affichage informatif du panneau "Fichiers
                    // incompatibles", cf. LoadDownloadMismatchesAsync côté ViewModel) — chaque
                    // clic sur "Lancer" retélécharge et re-matche réellement.
                    downloadedFile = await DownloadResolvedAsync(resolvedUrl, link, workDir, ct, ReportDownloadProgress);
                }
                catch (TaskCanceledException)
                {
                    failedLinks++;
                    var reason = $"Timeout : {LinkLabel()}";
                    failReasons.Add(reason);
                    System.Diagnostics.Debug.WriteLine($"[ReleaseBuilder] {reason}");
                    continue;
                }
                catch (Exception ex)
                {
                    failedLinks++;
                    var reason = $"Erreur ({LinkLabel()}) : {ex.Message}";
                    failReasons.Add(reason);
                    System.Diagnostics.Debug.WriteLine($"[ReleaseBuilder] Échec lien {link.Id} : {ex.Message}");
                    continue;
                }
                if (downloadedFile == null)
                {
                    failedLinks++;
                    failReasons.Add($"Téléchargement vide : {LinkLabel()}");
                    continue;
                }

                // Extraction / conversion (logique ArchiveExtractor/DMS/MSA inchangée)
                var candidateFiles = ProcessDownloadedFile(downloadedFile, workDir);

                // Vérifier chaque fichier obtenu contre les sets non complets
                var sizeMismatches = new List<(string Rom, long FileSize, long DatSize, string? DatCrc32, string? FileCrc32)>();
                foreach (var file in candidateFiles)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        TryMatchFileToSets(file, sets, sizeMismatches);
                    }
                    catch (Exception exMatch)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MATCH] EXCEPTION : {exMatch.GetType().Name} — {exMatch.Message}");
                    }
                }

                // Enregistrer les mismatches de taille pour éviter de retélécharger
                if (sizeMismatches.Count > 0 && resolvedUrl != null)
                {
                    var (romName, fileSize, datSize, datCrc32, _) = sizeMismatches[0];
                    _ = downloadAttempts.SaveAsync(new DemoBase.Data.DownloadAttempt(
                        Url:          resolvedUrl,
                        FileName:     romName,
                        DemozooId:    demozooId,
                        SizeOnServer: fileSize,
                        SizeInDat:    datSize,
                        Crc32InDat:   datCrc32,
                        Status:       DemoBase.Data.DownloadAttemptStatus.SizeMismatch,
                        AttemptedAt:  DateTime.UtcNow), ct);
                }

                if (sizeMismatches.Count > 0)
                {
                    var linkDesc = LinkLabel();
                    foreach (var m in sizeMismatches)
                        allMismatches.Add((m.Rom, m.FileSize, m.DatSize, m.DatCrc32, m.FileCrc32, linkDesc));
                }

                // Arrêter seulement si TOUS les sets sont complets
                if (sets.All(s => s.IsComplete))
                {
                    System.Diagnostics.Debug.WriteLine("[LOOP] Tous les sets complets — arrêt anticipé");
                    break;
                }
                }
                catch (Exception exLoop)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOOP] EXCEPTION lien #{link.Id} : {exLoop.GetType().Name} — {exLoop.Message}\n{exLoop.StackTrace}");
                    // On continue malgré l'exception — passer au lien suivant
                }
            }

            System.Diagnostics.Debug.WriteLine($"[LOOP] Boucle terminée — {sets.Count(s => s.IsComplete)}/{sets.Count} sets complets");

            // 2026-07-30, retour utilisateur ("Starstruck", tous les sets passés au vert) :
            // "pourquoi ces messages en dessous ? reliquat d'avant non réinitialisés ?" — un rom
            // réellement trouvé cette fois-ci laissait quand même affiché un mismatch enregistré
            // lors d'une tentative précédente (échouée), rien ne le nettoyait automatiquement.
            // Pour CHAQUE rom désormais satisfait (peu importe le set gagnant final — un même
            // rom peut apparaître dans plusieurs sets), on marque "résolues" les tentatives
            // passées correspondantes (taille+CRC32 du DAT, identifiant fiable) — indépendant du
            // chemin de retour choisi plus bas.
            foreach (var s in sets)
                foreach (var rom in s.Satisfied.Keys)
                    _ = downloadAttempts.MarkResolvedAsync(demozooId, rom.Size, rom.Crc32, ct);

            // Tous les liens parcourus — choisir le meilleur set complet par format
            var completedSets = sets.Where(s => s.IsComplete).ToList();
            if (completedSets.Count > 0)
            {
                static int FormatScore(SetProgress s)
                {
                    var exts = s.Satisfied.Keys
                        .Select(r => System.IO.Path.GetExtension(r.Name).ToLowerInvariant())
                        .ToList();
                    if (exts.Any(e => e is ".ym" or ".sndh" or ".mod" or ".s3m" or ".xm" or ".it"
                                        or ".sid" or ".ay" or ".ahx" or ".hvl" or ".pt3" or ".spc"))
                        return 100;
                    if (exts.Any(e => e is ".flac" or ".wav" or ".aiff"))
                        return 50;
                    if (exts.Any(e => e is ".mp3" or ".ogg" or ".m4a"))
                        return 10;
                    return 30;
                }

                // Construire TOUS les sets complets dans romsRoot
                foreach (var set in completedSets)
                {
                    progress?.Report(new($"Construction de l'archive {set.Entry.RomPath}…", 97));
                    BuildZipForSet(set, romsRoot);
                }

                // Priorité 1 : meilleur format (YM > FLAC > MP3)
                // Priorité 2 : set explicitement sélectionné par l'utilisateur en cas d'égalité
                var bestSet = completedSets
                    .OrderByDescending(FormatScore)
                    .ThenBy(s => preferredDatEntryId.HasValue && s.Entry.Id == preferredDatEntryId.Value ? 0 : 1)
                    .First();

                var bestZip = Path.Combine(romsRoot, bestSet.Entry.RomPath);
                progress?.Report(new("Terminé.", 100));
                return new(true, bestZip, null, bestSet.Satisfied.Count, bestSet.Entry.Roms.Count,
                    bestSet.Satisfied.Keys.Select(r => r.Id).ToList());
            }

            // Aucun set complet (ou le set préféré spécifiquement ne l'est jamais
            // devenu malgré tous les liens traités) — en dernier recours,
            // proposer quand même n'importe quel AUTRE set complet plutôt que
            // rien du tout, tant que ce n'est pas un abandon total.
            if (preferredDatEntryId.HasValue)
            {
                var anyComplete = sets.FirstOrDefault(s => s.IsComplete);
                if (anyComplete != null)
                {
                    progress?.Report(new("Construction de l'archive (version alternative)…", 97));
                    var zipPath = BuildZipForSet(anyComplete, romsRoot);
                    progress?.Report(new("Terminé (version alternative — le fichier sélectionné n'a pas pu être complété).", 100));
                    return new(true, zipPath,
                        "La version sélectionnée n'a pas pu être complétée ; une autre version alternative a été utilisée à la place.",
                        anyComplete.Satisfied.Count, anyComplete.Entry.Roms.Count,
                        anyComplete.Satisfied.Keys.Select(r => r.Id).ToList());
                }
            }

            // Aucun set complet — retourne le set le plus avancé pour information
            var best = sets.OrderByDescending(s => s.Satisfied.Count).First();

            // 2026-07-30, retour utilisateur (Atari 7800, "SN Cart Demo") : "malgré la présence
            // du fichier SNCartDemo.bin le fichier .zip dans \Releases ne s'est pas créé et la
            // release ne se lance pas". Le .BIN suffit à ProSystemLauncher pour lancer la démo
            // (il ignore déjà .txt et choisit lui-même le meilleur fichier par extension), mais
            // Cartridge_512kb_4kb_bankswitch.zip (un format cartouche/flashcart alternatif, pas
            // nécessaire au logiciel) reste introuvable en ligne et bloquait TOUT — aucun ZIP
            // n'était jamais construit tant que le set n'était pas 100% complet. Dernier
            // recours avant l'échec total : si au moins un fichier a été trouvé, construire
            // quand même un ZIP "best effort" avec ce qu'on a et tenter le lancement — le
            // lanceur de la plateforme choisit lui-même le meilleur fichier utilisable parmi
            // ceux présents ; s'il ne trouve vraiment rien d'utilisable, il échouera à son tour
            // avec son propre message clair (pas un blocage silencieux comme ici).
            if (best.Satisfied.Count > 0)
            {
                var partialZip = BuildZipForSet(best, romsRoot);
                var warning = $"Reconstruction partielle : {best.Satisfied.Count}/{best.Entry.Roms.Count} " +
                    "fichier(s) seulement — le jeu peut ne pas fonctionner si un fichier manquant est " +
                    "réellement nécessaire.\n\n" +
                    BuildIncompleteMessage(best, allMismatches, failedLinks, links.Count, failReasons);
                progress?.Report(new("Terminé (reconstruction partielle).", 100));
                return new(true, partialZip, warning, best.Satisfied.Count, best.Entry.Roms.Count,
                    best.Satisfied.Keys.Select(r => r.Id).ToList());
            }

            var errorMsg = best.Satisfied.Count == 0 && failedLinks > 0
                ? $"Tous les liens ont échoué ({failedLinks}/{links.Count}) :\n" +
                  string.Join("\n", failReasons.Take(5))
                : BuildIncompleteMessage(best, allMismatches, failedLinks, links.Count, failReasons);
            return new(false, null, errorMsg, best.Satisfied.Count, best.Entry.Roms.Count,
                best.Satisfied.Keys.Select(r => r.Id).ToList());
        }
        finally
        {
            // 2026-07-30 : la rétention du workDir en mode Debug (ajoutée pour l'investigation
            // Starstruck — "faut vraiment que je vérifie") est désactivée, retour utilisateur
            // ("tu peux réactiver la suppression des fichiers téléchargés en mode debug").
            // Nettoyage systématique, Debug comme Release, comme avant cette investigation.
            try { Directory.Delete(workDir, recursive: true); } catch { /* non bloquant */ }
        }
    }

    // ── Résolution d'URL ────────────────────────────────────────────────────────

    private async Task<string?> ResolveUrlAsync(ReleaseLink link, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(link.LinkParameter) && !string.IsNullOrEmpty(link.LinkClass))
        {
            var url = await _resolver.ResolveFromLinkClassAsync(link.LinkClass!, link.LinkParameter!, ct);
            System.Diagnostics.Debug.WriteLine($"[DL] Résolu depuis LinkClass → {url}");
            return string.IsNullOrEmpty(url) ? null : url;
        }
        if (!string.IsNullOrEmpty(link.Url))
        {
            var url = await _resolver.ResolveAsync(link.Url!, ct);
            System.Diagnostics.Debug.WriteLine($"[DL] Résolu depuis URL → {url}");
            return string.IsNullOrEmpty(url) ? null : url;
        }
        System.Diagnostics.Debug.WriteLine("[DL] Lien sans paramètre ni URL → skip");
        return null;
    }

    // ── Téléchargement d'un lien ────────────────────────────────────────────────

    private async Task<string?> DownloadLinkAsync(ReleaseLink link, string workDir, CancellationToken ct)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[DL] Lien #{link.Id} class={link.LinkClass} param={link.LinkParameter} url={link.Url}");

        var url = await ResolveUrlAsync(link, ct);
        if (url == null) return null;
        return await DownloadResolvedAsync(url, link, workDir, ct);
    }

    private async Task<string?> DownloadResolvedAsync(
        string url, ReleaseLink link, string workDir, CancellationToken ct,
        Action<long, long?>? onProgress = null)
    {
        if (url.StartsWith("ia-multi://", StringComparison.OrdinalIgnoreCase))
        {
            System.Diagnostics.Debug.WriteLine("[DL] ia-multi:// non géré → skip");
            return null;
        }

        System.Diagnostics.Debug.WriteLine($"[DL] GET {url}");
        var response = await SendWithRefererAsync(url, ct);

        if (!response.IsSuccessStatusCode && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // 2026-07-30, retour utilisateur : un lien AtariAge (forums.atariage.com/.../
            // attachment.php) marche très bien dans un navigateur (sans connexion) mais
            // échouait systématiquement depuis DemoBase avec un HTTP 403 — même après passage
            // à un User-Agent de navigateur standard. Hypothèse la plus probable pour un
            // forum IPS/Invision : protection anti-hotlink basique nécessitant soit un en-tête
            // Referer pointant vers le domaine lui-même, soit un cookie de session obtenu en
            // visitant n'importe quelle page du site au préalable — ni l'un ni l'autre n'était
            // envoyé. On retente donc UNE fois : une requête "priming" vers la racine du
            // domaine (établit un cookie de session dans le CookieContainer par défaut du
            // HttpClient statique partagé, qui suit son propre client — donc automatiquement
            // réutilisé pour la requête suivante), puis un nouvel essai avec le Referer pointant
            // vers cette page. Peu coûteux (une requête HTTP de plus, uniquement en cas de 403),
            // pas de nouvelle dépendance. Si ça ne suffit pas (vrai challenge JS/Cloudflare),
            // ça retombera sur l'erreur HTTP normale ci-dessous.
            System.Diagnostics.Debug.WriteLine("[DL] 403 — nouvel essai après cookie de session + Referer");
            response.Dispose();
            response = await RetryWithSessionAsync(url, ct);
        }

        using var _ = response;
        System.Diagnostics.Debug.WriteLine($"[DL] HTTP {(int)response.StatusCode} {response.StatusCode} — Content-Length={response.Content.Headers.ContentLength}");

        if (!response.IsSuccessStatusCode)
        {
            System.Diagnostics.Debug.WriteLine($"[DL] Échec HTTP {(int)response.StatusCode} pour {url}");
            // 2026-07-30 : le code d'échec était avalé silencieusement (retour null → message
            // générique "Téléchargement vide", sans dire POURQUOI) — pour diagnostiquer les cas
            // comme celui-ci (lien AtariAge marchant dans un navigateur mais pas depuis
            // DemoBase), on remonte le code HTTP dans le message d'erreur affiché.
            throw new HttpRequestException(
                $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        }

        var fileName = UrlResolver.SanitizeFilename(
            response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? Path.GetFileName(new Uri(url).LocalPath));
        if (string.IsNullOrWhiteSpace(fileName)) fileName = $"link_{link.Id}.bin";

        var destPath = Path.Combine(workDir, $"{link.Id}_{fileName}");
        var totalBytes = response.Content.Headers.ContentLength;
        System.Diagnostics.Debug.WriteLine($"[DL] Téléchargement → {destPath}");
        await Task.Run(async () =>
        {
            // Copie manuelle par blocs (au lieu de CopyToAsync) pour pouvoir rapporter une
            // progression réelle — indispensable sur les releases à lien unique/gros fichier,
            // où l'ancien rapport "une fois par lien" laissait la barre figée pendant toute
            // la durée du téléchargement (demande utilisateur).
            await using var httpStream = await response.Content.ReadAsStreamAsync(ct);
            await using var fs = File.Create(destPath);
            var buffer = new byte[81920];
            long totalRead = 0;
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;
                onProgress?.Invoke(totalRead, totalBytes);
            }
            onProgress?.Invoke(totalRead, totalBytes); // rapport final garanti (100% de la bande)
        }, ct);

        var size = new FileInfo(destPath).Length;
        System.Diagnostics.Debug.WriteLine($"[DL] OK — {size:N0} octets → {Path.GetFileName(destPath)}");
        return destPath;
    }

    // 2026-07-30 : GET avec un en-tête Referer pointant (par défaut) vers la racine du domaine
    // cible — imite le comportement normal d'un navigateur qui arrive sur un lien de
    // téléchargement depuis une page du même site, contourne les protections anti-hotlink les
    // plus basiques (celles qui vérifient juste la présence/le domaine du Referer, sans vrai
    // challenge JS).
    private async Task<HttpResponseMessage> SendWithRefererAsync(
        string url, CancellationToken ct, string? referer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var refererUrl = referer ?? GetOrigin(url);
        if (refererUrl != null && Uri.TryCreate(refererUrl, UriKind.Absolute, out var refererUri))
            request.Headers.Referrer = refererUri;
        return await Task.Run(
            () => _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct), ct);
    }

    // 2026-07-30 : ré-essai après un 403 — visite d'abord la racine du domaine (établit un
    // cookie de session dans le CookieContainer par défaut du HttpClient statique partagé
    // `_http`, réutilisé automatiquement par toute requête suivante vers le même domaine),
    // puis retente le téléchargement avec le Referer pointant vers cette page. Best-effort :
    // si la requête de "priming" échoue elle-même, on retente quand même le téléchargement
    // original (au pire, même résultat qu'avant).
    private async Task<HttpResponseMessage> RetryWithSessionAsync(string url, CancellationToken ct)
    {
        var origin = GetOrigin(url);
        if (origin == null) return await SendWithRefererAsync(url, ct);

        try
        {
            using var priming = await Task.Run(
                () => _http.GetAsync(origin, HttpCompletionOption.ResponseHeadersRead, ct), ct);
            System.Diagnostics.Debug.WriteLine($"[DL] Priming {origin} → HTTP {(int)priming.StatusCode}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DL] Priming {origin} a échoué : {ex.Message}");
        }

        return await SendWithRefererAsync(url, ct, referer: origin);
    }

    private static string? GetOrigin(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.Host}/" : null;

    // ── Extraction / conversion (réutilise ArchiveExtractor + DMS + MSA tels quels) ──

    private static List<string> ProcessDownloadedFile(string filePath, string workDir)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        var result = new List<string>();

        bool isArchive = ext is ".zip" or ".rar" or ".7z" or ".lha" or ".lzx" or ".lzh"
                              or ".gz" or ".arj" or ".tgz" or ".bz2" or ".adz";

        if (isArchive)
        {
            var extractDir = Path.Combine(workDir, $"extract_{Path.GetFileNameWithoutExtension(filePath)}");
            Directory.CreateDirectory(extractDir);

            if (ext == ".adz")
            {
                // .adz = .adf.gz — renommer puis traiter comme un .gz classique
                var newPath = filePath.Replace(".adz", ".adf.gz");
                File.Move(filePath, newPath, true);
                filePath = newPath;
                ext = ".adf.gz";
            }

            bool ok;
            if (filePath.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || ext == ".tgz")
                ok = ArchiveExtractor.ExtractAny(filePath, extractDir, ".tar.gz");
            else if (ext == ".bz2")
                ok = ArchiveExtractor.ExtractAny(filePath, extractDir, ".bz2");
            else
                ok = ArchiveExtractor.ExtractAny(filePath, extractDir, ext);

            if (ok)
                result.AddRange(Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories));
        }
        else
        {
            result.Add(filePath);
        }

        // Conversion DMS→ADF / MSA→ST sur chaque fichier obtenu (logique inchangée)
        for (int i = 0; i < result.Count; i++)
        {
            var f = result[i];
            var fExt = Path.GetExtension(f).ToLowerInvariant();

            if (fExt == ".dms")
            {
                var adfPath = Path.ChangeExtension(f, ".adf");
                if (File.Exists(adfPath)) File.Delete(adfPath);
                ushort err = DemosceneDownloader.Services.DMS.DMS.ProcessFile(f, adfPath, 6, 0, 0, 0);
                bool ok = err == 0 || (err == 12 && File.Exists(adfPath) && new FileInfo(adfPath).Length > 0);
                if (ok) { try { File.Delete(f); } catch { } result[i] = adfPath; }
            }
            else if (fExt == ".msa")
            {
                var stPath = Path.ChangeExtension(f, ".st");
                if (File.Exists(stPath)) File.Delete(stPath);
                int err = DemosceneDownloader.Services.MSA.DecodeMSA(f, stPath);
                if (err == 0) { try { File.Delete(f); } catch { } result[i] = stPath; }
            }
        }

        return result;
    }

    // ── Message d'erreur détaillé (téléchargement incomplet) ────────────────────

    /// <summary>
    /// Construit un message détaillant, fichier par fichier, ce qui a été trouvé et ce qui
    /// manque encore — et pourquoi (aucun lien n'a fourni le fichier, ou un fichier a été
    /// téléchargé mais ne correspond pas en taille). 2026-07-29, retour utilisateur : le
    /// message précédent ("Téléchargement incomplet : 1/3 fichier(s) trouvé(s).") ne disait
    /// ni quel fichier était trouvé, ni pourquoi les deux autres manquaient.
    /// </summary>
    private static string BuildIncompleteMessage(
        SetProgress best,
        List<(string Rom, long FileSize, long DatSize, string? DatCrc32, string? FileCrc32, string LinkDesc)> mismatches,
        int failedLinks, int totalLinks, List<string> failReasons)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Téléchargement incomplet : {best.Satisfied.Count}/{best.Entry.Roms.Count} fichier(s) trouvé(s).");

        var found   = best.Entry.Roms.Where(r => best.Satisfied.ContainsKey(r)).Select(r => r.Name).ToList();
        var missing = best.Entry.Roms.Where(r => !best.Satisfied.ContainsKey(r)).ToList();

        if (found.Count > 0)
            sb.Append("\n\n✓ Trouvé(s) : " + string.Join(", ", found));

        if (missing.Count > 0)
        {
            sb.Append("\n\n✗ Manquant(s) :");
            foreach (var rom in missing)
            {
                var reasons = mismatches.Where(m => m.Rom == rom.Name).ToList();
                if (reasons.Count > 0)
                {
                    var m = reasons[0];
                    // 2026-07-30, retour utilisateur : "check juste la taille et les crc32" —
                    // le message affiche maintenant les deux CRC32 (serveur vs DAT) en plus des
                    // tailles, exactement ce que l'utilisateur compare déjà manuellement (WinRAR).
                    sb.Append($"\n  • {rom.Name} — un fichier a été téléchargé ({m.LinkDesc}) mais ne " +
                              $"correspond pas au DAT (taille : {m.FileSize:N0} o reçus / {m.DatSize:N0} o attendus" +
                              (string.IsNullOrEmpty(m.FileCrc32) || string.IsNullOrEmpty(m.DatCrc32)
                                  ? ")" : $" — CRC32 : {m.FileCrc32} reçu / {m.DatCrc32} attendu)"));
                }
                else
                {
                    sb.Append($"\n  • {rom.Name} — aucun lien disponible n'a fourni ce fichier");
                }
            }
        }

        if (failedLinks > 0)
        {
            sb.Append($"\n\n{failedLinks}/{totalLinks} lien(s) en erreur :");
            foreach (var r in failReasons.Take(5))
                sb.Append($"\n  • {r}");
        }

        return sb.ToString();
    }

    // ── Correspondance fichier ↔ DatRom (taille + CRC32) ────────────────────────

    private static void TryMatchFileToSets(string filePath, List<SetProgress> sets,
        List<(string Rom, long FileSize, long DatSize, string? DatCrc32, string? FileCrc32)>? sizeMismatches = null)
    {
        if (!File.Exists(filePath)) return;
        var size     = new FileInfo(filePath).Length;
        var fileName = Path.GetFileName(filePath);
        uint? crc = null;

        System.Diagnostics.Debug.WriteLine($"[MATCH] Fichier: {fileName} taille={size}");

        foreach (var set in sets)
        {
            // 2026-07-30, retour utilisateur — log [MATCH] à l'appui, bug confirmé : ce
            // "if (set.IsComplete) continue;" sautait carrément TOUT le set (donc chaque rom pas
            // encore satisfait, y compris ceux jamais comparés) dès que les fichiers ESSENTIELS
            // du set étaient trouvés (SetProgress.IsComplete = tous les essentiels satisfaits,
            // les non-essentiels type .txt ne comptent pas). Cas réel observé : "strstrck.dat"
            // puis "strstrck.tos" matchent (set passe "complet" dès ce moment, .txt étant non
            // essentiel) — et le candidat suivant dans la même archive, "strstrck.txt", qui
            // correspond pourtant PARFAITEMENT (taille+CRC32 exacts), n'est alors plus jamais
            // comparé à ce set : il arrive une itération de fichier trop tard. Le set finit
            // "complet" pour le lancement (les essentiels suffisent) mais l'archive reconstruite
            // n'a jamais cette chance d'inclure le .txt pourtant disponible. Ce garde-fou n'était
            // qu'une optimisation de performance (éviter de comparer un set "déjà bon") — sans
            // valeur ajoutée réelle vu la taille des DAT (quelques roms par set), et avec ce coût
            // de correction en moins : supprimé. Seul le filtre par ROM individuel juste en
            // dessous (déjà satisfait → skip) reste nécessaire et suffisant.
            foreach (var rom in set.Entry.Roms)
            {
                if (set.Satisfied.ContainsKey(rom)) continue;
                System.Diagnostics.Debug.WriteLine($"[MATCH]   vs ROM: {rom.Name} size={rom.Size} crc={rom.Crc32}");
                // 2026-07-30, retour utilisateur, sans détour : "oublie le nom du fichier !! on
                // s'en fout !! check juste la taille et les crc32 des fichiers que tu télécharges
                // versus ce que tu as en BDD." Confirmation par preuve (WinRAR + dump BDD) : le
                // nom n'a jamais été un identifiant fiable ici. Le vrai MATCH ci-dessous (bloc
                // CRC32 plus bas) n'a d'ailleurs JAMAIS dépendu du nom — seule cette branche
                // "taille différente" (donc PAS un match, juste une candidature à signaler comme
                // diagnostic) essayait encore de filtrer par nom puis par extension, ce qui a
                // produit deux séries de faux positifs/négatifs successives. On arrête d'essayer
                // de deviner une correspondance "plausible" par un quelconque nom : toute
                // candidate de taille différente est signalée telle quelle, brute — charge à
                // l'utilisateur (ou au message affiché) de juger avec les tailles réelles sous
                // les yeux, exactement comme il le fait déjà manuellement avec WinRAR.
                if (rom.Size != size)
                {
                    System.Diagnostics.Debug.WriteLine($"[MATCH]     → taille différente ({size} vs {rom.Size})");
                    // 2026-07-30 : on calcule aussi le CRC32 réel du fichier téléchargé (mis en
                    // cache dans `crc` pour ne pas le recalculer à chaque rom comparé) pour
                    // l'afficher à côté de celui attendu par le DAT — l'utilisateur veut voir
                    // les deux valeurs, pas juste la taille.
                    string? fileCrcHex = null;
                    try { crc ??= DatMaker.Crc32.GetFileCRC32(filePath); fileCrcHex = crc.Value.ToString("x8"); }
                    catch { /* CRC non calculable (fichier verrouillé/illisible) — on affiche sans */ }
                    sizeMismatches?.Add((rom.Name, size, rom.Size, rom.Crc32, fileCrcHex));
                    continue;
                }
                if (string.IsNullOrEmpty(rom.Crc32)) { System.Diagnostics.Debug.WriteLine("[MATCH]     → CRC manquant"); continue; }
                if (!uint.TryParse(rom.Crc32, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var expectedCrc))
                    continue;

                try { crc ??= DatMaker.Crc32.GetFileCRC32(filePath); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MATCH]     → CRC exception: {ex.Message}"); continue; }
                if (crc.Value != expectedCrc)
                {
                    System.Diagnostics.Debug.WriteLine($"[MATCH]     → CRC différent ({crc.Value:X8} vs {expectedCrc:X8})");
                    continue;
                }

                set.Satisfied[rom] = filePath;
                System.Diagnostics.Debug.WriteLine($"[MATCH]     → MATCH! Set {set.Entry.RomPath} satisfait {set.Satisfied.Count}/{set.Entry.Roms.Count} (taille+CRC32, aucune dépendance au nom)");
                break;
            }
        }
    }


    // ── Construction du ZIP final ────────────────────────────────────────────────

    private static string BuildZipForSet(SetProgress set, string romsRoot)
    {
        var destZip = Path.Combine(romsRoot, set.Entry.RomPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destZip)!);

        if (File.Exists(destZip)) File.Delete(destZip);

        using var archive = ZipFile.Open(destZip, ZipArchiveMode.Create);
        foreach (var (rom, localPath) in set.Satisfied)
        {
            // Les métadonnées DAT stockent parfois rom.Name avec des antislashs
            // (style Windows) — un ZIP conforme utilise toujours des slashs
            // pour ses entrées. On normalise à l'écriture pour produire une
            // archive standard, lisible par n'importe quel outil (et par notre
            // propre vérification de complétude, cf. DatEntryStatusToColorConverter).
            //
            // Les DAT sont des fichiers XML : un caractère comme "&" y est forcément
            // échappé en "&amp;" (règle XML, pas une bizarrerie du DAT). DatImportService
            // décode maintenant cet échappement à l'import, mais on redécode ici en filet —
            // pour les DatRoms déjà importés avant ce fix, sans devoir forcer un ré-import.
            // Sans ce décodage, le nom d'entrée ZIP contient littéralement "&amp;" (avec le
            // ";" final), et WinUAE écrit ce texte tel quel dans la Startup-Sequence — sur
            // Amiga, ";" est un séparateur de commandes Shell, donc AmigaDOS ne lance qu'un
            // fragment tronqué du nom ("Unknown command") au lieu du vrai exécutable.
            var decodedName = System.Net.WebUtility.HtmlDecode(rom.Name);
            var entryName   = decodedName.Replace('\\', '/').TrimStart('/');
            archive.CreateEntryFromFile(localPath, entryName, CompressionLevel.Optimal);
        }

        return destZip;
    }
}
