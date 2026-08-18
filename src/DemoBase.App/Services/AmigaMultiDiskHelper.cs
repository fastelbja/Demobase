using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace DemoBase.App.Services;

/// <summary>
/// Aide au tri et à l'extraction des disques Amiga multi-disques.
///
/// Reconnaît les patterns de nommage courants sur demoscene :
///   DesertDreamA.adf / DesertDreamB.adf        → suffixe lettre (A, B, C…)
///   Spaceballs-9Fingers-A.adf                  → tiret + lettre
///   atz-ody1.adf / atz-ody2.adf                → suffixe chiffre
///   Desert_Dream_Disk1.adf / ...Disk2.adf      → mot "Disk" + chiffre
///   Odyssey-Side1.adf / Odyssey-Side2.adf      → mot "Side" + chiffre
/// </summary>
public static class AmigaMultiDiskHelper
{
    private static readonly HashSet<string> AdfExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".adf", ".dms", ".ipf", ".adz", ".hdf" };

    private static readonly HashSet<string> IgnoredExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".nfo", ".diz", ".doc", ".pdf",
          ".jpg", ".jpeg", ".png", ".gif", ".bmp" };

    // ── Extraction ────────────────────────────────────────────────────────────

    /// <summary>
    /// Extrait tous les disques Amiga du ZIP dans extractDir,
    /// les trie dans l'ordre disque 1, 2, 3… et retourne leurs chemins.
    /// </summary>
    public static Task<List<string>> ExtractAndSortAsync(
        string zipPath, string extractDir) =>
        Task.Run(() => ExtractAndSortSync(zipPath, extractDir));

    private static List<string> ExtractAndSortSync(string zipPath, string extractDir)
    {
        Directory.CreateDirectory(extractDir);

        var extracted = new List<string>();
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
                if (!AdfExtensions.Contains(ext)) continue;

                var dest = Path.Combine(extractDir, entry.Name);
                if (!File.Exists(dest))
                    entry.ExtractToFile(dest, overwrite: false);
                extracted.Add(dest);
            }
        }

        return SortDisks(extracted);
    }

    // ── Tri ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Trie les fichiers disques dans l'ordre Disk1 → Disk2 → … en détectant
    /// automatiquement le pattern de nommage utilisé.
    /// </summary>
    public static List<string> SortDisks(IEnumerable<string> files)
    {
        var list = files.ToList();
        if (list.Count <= 1) return list;

        return list
            .OrderBy(f => ExtractDiskIndex(Path.GetFileNameWithoutExtension(f)))
            .ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Retourne un index de tri numérique à partir du nom de fichier (sans extension).
    ///
    /// Exemples :
    ///   "DesertDreamA"       → 1   (lettre finale A=1, B=2…)
    ///   "9Fingers-B"         → 2   (tiret+lettre)
    ///   "atz-ody3"           → 3   (chiffre final)
    ///   "OdysseyDisk5"       → 5   (keyword Disk/Side + chiffre)
    ///   "odyssey_disk_5"     → 5
    /// </summary>
    public static int ExtractDiskIndex(string nameNoExt)
    {
        if (string.IsNullOrWhiteSpace(nameNoExt)) return 999;

        // 1. Pattern : "Disk<N>" ou "Side<N>" ou "Disk_<N>" (insensible à la casse)
        var diskWord = Regex.Match(nameNoExt,
            @"[Dd]isk[_\-\s]?(\d+)|[Ss]ide[_\-\s]?(\d+)",
            RegexOptions.IgnoreCase);
        if (diskWord.Success)
        {
            var g = diskWord.Groups[1].Success ? diskWord.Groups[1] : diskWord.Groups[2];
            return int.Parse(g.Value);
        }

        // 2. Pattern : chiffre(s) en fin de nom, optionnellement précédé d'un séparateur
        //    "atz-ody1"  "game3"  "demo-02"
        var trailingDigit = Regex.Match(nameNoExt, @"[-_\s]?(\d+)$");
        if (trailingDigit.Success)
            return int.Parse(trailingDigit.Groups[1].Value);

        // 3. Pattern : lettre majuscule ou minuscule en fin de nom (A=1, B=2…)
        //    "DesertDreamA"  "9Fingers-B"  "demoC"
        var trailingLetter = Regex.Match(nameNoExt, @"[-_\s]?([A-Za-z])$");
        if (trailingLetter.Success)
        {
            char c = char.ToUpper(trailingLetter.Groups[1].Value[0]);
            if (c >= 'A' && c <= 'Z') return c - 'A' + 1;
        }

        // 4. Fallback : tri alphabétique naturel (index 500 + char value)
        return 500;
    }

    // ── Infos ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Retourne vrai si le ZIP contient plusieurs fichiers disque Amiga.
    /// </summary>
    public static bool IsMultiDisk(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            int count = zip.Entries.Count(e =>
                AdfExtensions.Contains(Path.GetExtension(e.Name).ToLowerInvariant()));
            return count > 1;
        }
        catch { return false; }
    }
}
