using DemoBase.Data;
using System.IO;
using System.Text.RegularExpressions;

namespace DemoBase.App.Services;

/// <summary>
/// Résultat d'une correspondance entre un fichier vidéo local et une release.
/// </summary>
public class LocalCaptureVideoDto
{
    public string FilePath    { get; init; } = string.Empty;  // chemin absolu
    public string FileName    { get; init; } = string.Empty;  // nom du fichier seul
    public string Resolution  { get; init; } = string.Empty;  // "4K", "FHD", "HD", "SD"
    public string Fps         { get; init; } = string.Empty;  // "50 fps"
    public string Duration    { get; init; } = string.Empty;  // "16 min 28 s"
    public string Tags        { get; init; } = string.Empty;  // "youtube", "making of", etc.
    public string Platform    { get; init; } = string.Empty;  // dossier plateforme
    public string ReleaseType { get; init; } = string.Empty;  // dossier type
    public bool   IsYouTubeCapture => Tags.Contains("youtube", StringComparison.OrdinalIgnoreCase);
    public bool   IsMakingOf       => Tags.Contains("making of", StringComparison.OrdinalIgnoreCase);

    public string Label
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(Resolution)) parts.Add(Resolution);
            if (!string.IsNullOrEmpty(Fps))        parts.Add(Fps);
            if (!string.IsNullOrEmpty(Duration))   parts.Add(Duration);
            if (!string.IsNullOrEmpty(Tags))        parts.Add(Tags);
            return string.Join(" · ", parts);
        }
    }
}

/// <summary>
/// Recherche les captures vidéo locales pour une release donnée.
/// Convention de nommage :
///   {RomsRoot}\Media\Captures\Videos\{Platform}\{ReleaseType}\
///   {Releaser} ({Abbrev}) - {Title} ({Date})[tags...].mp4
/// </summary>
public class LocalVideoCaptureService
{
    private readonly PreferencesService _prefs;

    // Extension vidéo supportées
    private static readonly HashSet<string> VideoExtensions =
        [".mp4", ".mkv", ".avi", ".mov", ".webm"];

    // Pattern de parsing du nom de fichier
    private static readonly Regex FilenamePattern = new(
        @"^(?<releasers>.+?)\s*-\s*(?<title>.+?)\s*\((?<date>\d{4}(?:-\d{2}(?:-\d{2})?)?)\)\s*(?<tags>(?:\[.*?\])*)\s*\.\w+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TagPattern = new(@"\[([^\]]+)\]", RegexOptions.Compiled);

    // Index global : titre normalisé → a des vidéos locales
    private HashSet<string>? _titleIndex;
    private bool _indexBuilt = false;

    public LocalVideoCaptureService(PreferencesService prefs)
    {
        _prefs = prefs;
    }

    /// <summary>
    /// Construit un index de tous les titres de fichiers vidéo disponibles.
    /// Appelé une fois au démarrage — retourne un HashSet de titres normalisés.
    /// </summary>
    public async Task<HashSet<string>> BuildTitleIndexAsync()
    {
        if (_indexBuilt && _titleIndex != null) return _titleIndex;

        var prefs    = await _prefs.LoadAllAsync();
        var romsRoot = prefs.ResolvedPathReleases;
        var index    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(romsRoot))
        {
            var videoRoot = Path.Combine(romsRoot, "Media", "Captures", "Videos");
            if (Directory.Exists(videoRoot))
            {
                foreach (var platformDir in SafeGetDirs(videoRoot))
                foreach (var typeDir     in SafeGetDirs(platformDir))
                foreach (var file        in SafeGetFiles(typeDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!VideoExtensions.Contains(ext)) continue;

                    var nameNoExt = Path.GetFileNameWithoutExtension(file);
                    var dashIdx   = nameNoExt.IndexOf(" - ", StringComparison.Ordinal);
                    if (dashIdx < 0) continue;

                    // Extraire le titre (entre " - " et "(date)")
                    var afterDash = nameNoExt[(dashIdx + 3)..];
                    var dateMatch = Regex.Match(afterDash,
                        @"\((\d{4}(?:-\d{2}(?:-\d{2})?)?)\)");
                    var titleRaw  = dateMatch.Success
                        ? afterDash[..dateMatch.Index].Trim()
                        : afterDash.Trim();

                    if (titleRaw.Length > 0)
                    {
                        var norm = NormalizeTitle(titleRaw);
                        if (norm.Length >= 2) // ignorer les titres trop courts après normalisation
                            index.Add(norm);
                    }
                }
            }
        }

        _titleIndex = index;
        _indexBuilt = true;
        return index;
    }

    /// <summary>
    /// Indique si un titre de release a des vidéos locales connues (via l'index).
    /// </summary>
    public bool HasLocalVideos(string releaseTitle)
    {
        if (_titleIndex == null) return false;
        var norm = NormalizeTitle(releaseTitle);
        if (norm.Length < 2) return false; // titre trop court — trop de faux positifs
        return _titleIndex.Contains(norm);
    }

    /// <summary>
    /// Cherche les fichiers vidéo correspondant à une release.
    /// Matching par : titre (insensible à la casse) + année de la date.
    /// </summary>
    public async Task<IReadOnlyList<LocalCaptureVideoDto>> FindVideosAsync(
        string releaseTitle,
        string? releaseDate,
        IEnumerable<(string Name, string? Abbreviation)> releasers)
    {
        var prefs   = await _prefs.LoadAllAsync();
        var romsRoot = prefs.ResolvedPathReleases;
        if (string.IsNullOrEmpty(romsRoot)) return [];

        var videoRoot = Path.Combine(romsRoot, "Media", "Captures", "Videos");
        if (!Directory.Exists(videoRoot)) return [];

        var year = releaseDate?.Length >= 4 ? releaseDate[..4] : null;

        // Normaliser le titre pour la comparaison
        var titleNorm = NormalizeTitle(releaseTitle);

        // Noms et abréviations de releasers pour le matching
        // Ex: "Future Crew" + "FC" → le fichier "Future Crew (FC) - ..." matchera sur les deux
        var releaserNorms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var releaserAbbrevs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, abbrev) in releasers)
        {
            var n = NormalizeTitle(name);
            if (n.Length > 0) releaserNorms.Add(n);
            if (!string.IsNullOrWhiteSpace(abbrev)) releaserAbbrevs.Add(abbrev.Trim());
        }

        var results = new List<LocalCaptureVideoDto>();

        // Parcourir récursivement — on cherche dans tous les sous-dossiers
        // Structure: videoRoot\{Platform}\{ReleaseType}\{file}
        foreach (var platformDir in SafeGetDirs(videoRoot))
        {
            var platformName = Path.GetFileName(platformDir);
            foreach (var typeDir in SafeGetDirs(platformDir))
            {
                var typeName = Path.GetFileName(typeDir);
                foreach (var file in SafeGetFiles(typeDir))
                {
                    var ext = Path.GetExtension(file).ToLowerInvariant();
                    if (!VideoExtensions.Contains(ext)) continue;

                    var dto = ParseFilename(file, platformName, typeName);
                    if (dto == null) continue;

                    if (MatchesRelease(dto, titleNorm, year, releaserNorms, releaserAbbrevs))
                        results.Add(dto);
                }
            }
        }

        // Trier : captures directes en premier, puis youtube, making of en dernier
        return results
            .OrderBy(v => v.IsYouTubeCapture ? 1 : 0)
            .ThenBy(v => v.IsMakingOf ? 1 : 0)
            .ThenBy(v => v.Resolution switch { "4K" => 0, "FHD" => 1, "HD" => 2, _ => 3 })
            .ToList()
            .AsReadOnly();
    }

    // ─── Parsing ──────────────────────────────────────────────────────────────

    private static LocalCaptureVideoDto? ParseFilename(
        string filePath, string platform, string releaseType)
    {
        var name = Path.GetFileName(filePath);
        // Retirer l'extension pour parser
        var nameNoExt = Path.GetFileNameWithoutExtension(name);

        // Trouver la date entre parenthèses juste avant les crochets
        // Format: "Releasers - Title (date)[tags...]"
        var dateMatch = Regex.Match(nameNoExt,
            @"\((\d{4}(?:-\d{2}(?:-\d{2})?)?)\)\s*(?=\[|$)");
        if (!dateMatch.Success) return null;

        var date    = dateMatch.Groups[1].Value;
        var prefixEnd = dateMatch.Index;
        var prefix  = nameNoExt[..prefixEnd].Trim();
        var tagsRaw = nameNoExt[(dateMatch.Index + dateMatch.Length)..].Trim();

        // Séparer releasers et titre (premier " - ")
        var dashIdx = prefix.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx < 0) return null;
        var releasers = prefix[..dashIdx].Trim();
        var title     = prefix[(dashIdx + 3)..].Trim();

        // Parser les tags entre []
        var tags = TagPattern.Matches(tagsRaw)
            .Select(m => m.Groups[1].Value.Trim())
            .ToList();

        // Catégoriser les tags
        var resolution = tags.FirstOrDefault(t =>
            t is "4K" or "FHD" or "HD" or "SD" or "8K") ?? "";
        var fps = tags.FirstOrDefault(t =>
            Regex.IsMatch(t, @"^\d+ fps$")) ?? "";
        var duration = tags.FirstOrDefault(t =>
            Regex.IsMatch(t, @"\d+ (min|s|h)")) ?? "";
        var otherTags = string.Join(", ", tags.Where(t =>
            t != resolution && t != fps && t != duration));

        return new LocalCaptureVideoDto
        {
            FilePath    = filePath,
            FileName    = name,
            Resolution  = resolution,
            Fps         = fps,
            Duration    = duration,
            Tags        = otherTags,
            Platform    = platform,
            ReleaseType = releaseType,
        };
    }

    private static bool MatchesRelease(
        LocalCaptureVideoDto dto,
        string titleNorm,
        string? year,
        HashSet<string> releaserNorms,
        HashSet<string> releaserAbbrevs)
    {
        var fileNameNoExt = Path.GetFileNameWithoutExtension(dto.FileName);

        // Ré-extraire date depuis le nom de fichier
        var dateMatch = Regex.Match(fileNameNoExt,
            @"\((\d{4}(?:-\d{2}(?:-\d{2})?)?)\)\s*(?=\[|$)");
        if (!dateMatch.Success) return false;

        var fileYear = dateMatch.Groups[1].Value[..4];
        var prefix   = fileNameNoExt[..dateMatch.Index].Trim();
        var dashIdx  = prefix.IndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx < 0) return false;

        var fileReleaserRaw = prefix[..dashIdx].Trim();
        var fileTitleRaw    = prefix[(dashIdx + 3)..].Trim();
        var fileTitleNorm   = NormalizeTitle(fileTitleRaw);

        // 1. Matching titre
        if (!string.Equals(fileTitleNorm, titleNorm, StringComparison.OrdinalIgnoreCase))
            return false;

        // 2. Matching année
        if (year != null && fileYear != year)
            return false;

        // 3. Matching releaser : nom OU abréviation
        // Le fichier contient "Future Crew (FC)" → on extrait nom et abréviation entre ()
        // Pattern : "Name (ABBREV) - ..." ou "Name (ABBREV) + Name2 (ABBREV2) - ..."
        if (releaserNorms.Count > 0 || releaserAbbrevs.Count > 0)
        {
            // Extraire les abréviations entre () dans la partie releaser du fichier
            var fileAbbrevs = Regex.Matches(fileReleaserRaw, @"\(([^)]+)\)")
                .Select(m => m.Groups[1].Value.Trim())
                // Ignorer ceux qui ressemblent à des dates ou contiennent des virgules (multi-abbrev)
                .SelectMany(a => a.Split(',').Select(x => x.Trim()))
                .Where(a => !Regex.IsMatch(a, @"^\d{4}") && a.Length <= 8)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // Nom sans les parenthèses
            var fileReleaserNameOnly = Regex.Replace(fileReleaserRaw, @"\([^)]+\)", "").Trim();
            var fileReleaserNorm = NormalizeTitle(fileReleaserNameOnly);

            // Match si : un nom de releaser DB est dans le nom fichier OU une abbrev correspond
            var nameMatch = releaserNorms.Count == 0 || releaserNorms.Any(r =>
                fileReleaserNorm.Contains(r, StringComparison.OrdinalIgnoreCase) ||
                r.Contains(fileReleaserNorm, StringComparison.OrdinalIgnoreCase));

            var abbrevMatch = releaserAbbrevs.Count == 0 || releaserAbbrevs.Any(a =>
                fileAbbrevs.Contains(a));

            // Les deux doivent matcher (si disponibles) OU au moins le nom
            if (!nameMatch && !abbrevMatch) return false;
        }

        return true;
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string NormalizeTitle(string s)
    {
        // Minuscules, retirer les caractères spéciaux non significatifs
        return Regex.Replace(s.ToLowerInvariant().Trim(), @"[^\w\s]", " ")
                    .Replace("  ", " ")
                    .Trim();
    }

    private static IEnumerable<string> SafeGetDirs(string path)
    {
        try { return Directory.GetDirectories(path); }
        catch { return []; }
    }

    private static IEnumerable<string> SafeGetFiles(string path)
    {
        try { return Directory.GetFiles(path); }
        catch { return []; }
    }
}
