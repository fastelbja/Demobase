using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Data;

namespace DemoBase.App.Converters;

/// <summary>Retourne Visible si la valeur est non nulle et non vide.</summary>
public class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value == null || (value is string s && string.IsNullOrWhiteSpace(s)))
            ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotImplementedException();
}

/// <summary>Retourne Visible si la valeur bool est true.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v is Visibility vis && vis == Visibility.Visible;
}

/// <summary>Inverse d'un bool.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is bool b ? !b : (object)false;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        v is bool b ? !b : (object)false;
}

/// <summary>Convertit un rang entier en label compact : 1 → "★ 1st", 2 → "2nd"…</summary>
public class RankToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
        => (value is int rank && rank > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

public class RankToStringConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not int rank || rank <= 0) return string.Empty;
        return rank switch
        {
            1 => "🏆 1st",
            2 => "2nd",
            3 => "3rd",
            _ => $"{rank}th"
        };
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Convertit une date Demozoo ("1993-08-05", "1993-08", "1993") en string affichable.</summary>
public class ReleaseDateConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string date || string.IsNullOrWhiteSpace(date)) return "Date inconnue";
        var parts = date.Split('-');
        return parts.Length switch
        {
            3 => DateTime.TryParse(date, out var d) ? d.ToString("d MMMM yyyy", new CultureInfo("fr-FR")) : date,
            2 => $"{MonthName(parts[1])} {parts[0]}",
            _ => parts[0]
        };
    }
    private static string MonthName(string m) =>
        int.TryParse(m, out var n) ? new DateTime(2000, n, 1).ToString("MMMM", new CultureInfo("fr-FR")) : m;

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Affiche ★ si favori, ☆ sinon.</summary>
public class FavoriteToStarConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is true ? "★" : "☆";
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Extrait les initiales d'un nom : "Future Crew" → "FC", "Fairlight" → "FA"</summary>
public class InitialsConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string name || string.IsNullOrWhiteSpace(name)) return "?";
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length >= 2
            ? $"{char.ToUpperInvariant(words[0][0])}{char.ToUpperInvariant(words[1][0])}"
            : name[..Math.Min(2, name.Length)].ToUpperInvariant();
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Retourne Visible si la valeur bool est false.</summary>
public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is false ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}
public class EmptyCollectionToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is string s)
            return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
        if (value is System.Collections.ICollection col)
            return col.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) => throw new NotImplementedException();
}

/// <summary>Convertit un pourcentage (0-100) en largeur pixel selon la largeur du conteneur.</summary>
public class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, System.Globalization.CultureInfo c)
    {
        if (values.Length < 2) return 0.0;
        if (values[0] is not double pct || values[1] is not double w) return 0.0;
        return Math.Max(0, Math.Min(w, w * pct / 100.0));
    }
    public object[] ConvertBack(object v, Type[] t, object? p, System.Globalization.CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>Convertit un code pays ISO-2 ("FI", "DE") en emoji drapeau ("🇫🇮", "🇩🇪").</summary>
public class CountryToFlagConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        if (value is not string code || code.Length != 2) return string.Empty;
        return string.Concat(code.ToUpperInvariant()
            .Select(ch => char.ConvertFromUtf32(ch - 'A' + 0x1F1E6)));
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotImplementedException();
}

/// <summary>
/// Retourne une icône unicode selon le type ou supertype de release.
/// Priorité : ReleaseTypeName → Supertype fallback.
/// </summary>
public class ReleaseTypeIconConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var s = (value as string ?? "").ToLowerInvariant();
        return s switch
        {
            // Musique
            var x when x.Contains("tracked music") || x.Contains("tracked")  => "🎹",
            var x when x.Contains("music")                                    => "🎵",
            var x when x.Contains("pack")                                     => "📦",
            // Démos
            var x when x.Contains("demo")                                     => "🖥",
            var x when x.Contains("intro") && x.Contains("64")               => "💾",
            var x when x.Contains("intro") && x.Contains("40")               => "💾",
            var x when x.Contains("intro") && x.Contains("4k")               => "💾",
            var x when x.Contains("intro")                                    => "⚡",
            var x when x.Contains("wild")                                     => "🌀",
            // Graphisme
            var x when x.Contains("graphic") || x == "graphics"              => "🎨",
            var x when x.Contains("ascii")                                    => "📝",
            var x when x.Contains("animation")                                => "🎬",
            // Misc
            var x when x.Contains("game")                                     => "🕹",
            var x when x.Contains("tool")                                     => "🔧",
            var x when x.Contains("exe")                                      => "⚙",
            // Supertype fallback
            "production"                                                       => "🖥",
            "graphics"                                                         => "🎨",
            "music"                                                            => "🎵",
            _                                                                  => "▪",
        };
    }
    public object ConvertBack(object? v, Type t, object? p, CultureInfo c)
        => throw new NotImplementedException();
}

// ─── RomPathToNameConverter ──────────────────────────────────────────────────
// Extrait le nom du fichier depuis le RomPath, sans l'extension .zip

public class RomPathToNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrEmpty(path)) return string.Empty;
        var fileName = Path.GetFileName(path);
        return Path.GetFileNameWithoutExtension(fileName);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── DatEntryStatusToColorConverter ───────────────────────────────────────────
// Fond discret vert si le ZIP du set existe localement et est complet (toutes
// les entrées DatRom retrouvées via taille/CRC32 dans le ZIP), rouge sinon.
// Utilisé sur la liste Releases → Files. S'appuie sur le cache statique
// PreferencesService.LastResolvedPathReleases (mis à jour à chaque chargement des
// préférences) pour rester synchrone — un converter XAML ne peut pas await.

public class DatEntryStatusToColorConverter : IValueConverter
{
    // Petit cache pour éviter de rouvrir/relire le même ZIP à chaque rafraîchissement
    // de la liste (l'ItemsControl peut redemander la valeur plusieurs fois).
    private static readonly Dictionary<string, (bool Complete, DateTime CheckedAt)> _cache = new();
    private static readonly TimeSpan _cacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>Vide le cache pour forcer le recalcul des couleurs (ex. après un build réussi).</summary>
    public static void ClearCache() => _cache.Clear();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not DemoBase.Core.Models.DatEntry entry)
            return System.Windows.Media.Brushes.Transparent;

        bool complete = IsSetComplete(entry);
        // Couleurs discrètes (faible opacité) — cohérent avec le style général de l'app
        return complete
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 48, 209, 88))   // vert
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(70, 255, 69, 58));  // rouge
    }

    private static bool IsSetComplete(DemoBase.Core.Models.DatEntry entry)
    {
        if (string.IsNullOrEmpty(entry.RomPath) || entry.Roms.Count == 0) return false;

        if (_cache.TryGetValue(entry.RomPath, out var cached) &&
            DateTime.UtcNow - cached.CheckedAt < _cacheTtl)
            return cached.Complete;

        bool result = false;
        try
        {
            var zipPath = Path.Combine(DemoBase.Data.PreferencesService.LastResolvedPathReleases, entry.RomPath);
            if (File.Exists(zipPath))
            {
                using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                // Le set est complet si chaque DatRom attendu a une entrée de même
                // nom ET de même taille dans le ZIP (CRC non revérifié ici pour
                // rester rapide à l'affichage — la vérification stricte a lieu
                // lors de la (re)construction par ReleaseBuilderService).
                //
                // Recherche robuste : les métadonnées DAT stockent parfois les
                // chemins avec des antislashs (ex. "textures\Circle.jpg", comme
                // affiché dans l'onglet Files), alors qu'un ZIP stocke toujours
                // ses entrées avec des slashs ("/") — un ZipArchive.GetEntry()
                // exact sur le nom brut échoue donc systématiquement pour tout
                // fichier dans un sous-dossier, faisant passer le set entier en
                // rouge même quand l'archive est réellement complète. On
                // normalise les deux côtés (slash + casse) avant de comparer.
                var zipEntriesByNormalizedName = zip.Entries.ToDictionary(
                    e => NormalizeEntryName(e.FullName),
                    e => e,
                    StringComparer.OrdinalIgnoreCase);

                result = entry.Roms.All(rom =>
                {
                    var key = NormalizeEntryName(rom.Name);
                    // Vérification par nom uniquement — la taille n'est pas comparée
                    // car les DATs sont générés depuis la collection personnelle :
                    // un fichier présent sous le bon nom est forcément le bon fichier.
                    // La vérification par taille génère des faux positifs (FLAC
                    // re-encodé, métadonnées modifiées, outil de compression différent).
                    return zipEntriesByNormalizedName.ContainsKey(key);
                });
            }
        }
        catch { result = false; }

        _cache[entry.RomPath] = (result, DateTime.UtcNow);
        return result;
    }

    /// <summary>Normalise un chemin d'entrée d'archive pour une comparaison
    /// fiable entre le nom stocké en DAT (parfois avec antislashs, style
    /// Windows) et le nom réel dans le ZIP (toujours avec slashs).</summary>
    private static string NormalizeEntryName(string name) => name.Replace('\\', '/').TrimStart('/');

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── FileSizeConverter ────────────────────────────────────────────────────────
// Formate une taille en octets en Ko / Mo

public class FileSizeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size) return "—";
        if (size < 1024)      return $"{size} o";
        if (size < 1024*1024) return $"{size/1024.0:F1} Ko";
        return $"{size/(1024.0*1024):F1} Mo";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── CountryFlagConverter ────────────────────────────────────────────────────
// Convertit un code ISO-2 ("FI", "DE"…) en BitmapImage depuis flagcdn.com

public class CountryFlagConverter : IValueConverter
{
    // Cache en mémoire pour éviter les requêtes répétées
    private static readonly Dictionary<string, BitmapImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code)) return null;
        var key = code.ToLowerInvariant();

        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            // flagcdn.com fournit des PNG de drapeaux en 40x30
            var uri = new Uri($"https://flagcdn.com/40x30/{key}.png");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource        = uri;
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.CreateOptions    = BitmapCreateOptions.None;
            bmp.EndInit();
            _cache[key] = bmp;
            return bmp;
        }
        catch
        {
            _cache[key] = null;
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── CountryNameConverter ────────────────────────────────────────────────────
// Convertit un code ISO-2 ("FI", "DE"…) en nom de pays complet en anglais
// ("Finland", "Germany"…) via System.Globalization.RegionInfo (BCL .NET) —
// pas de table statique à maintenir, contrairement à une liste codée en dur.

public class CountryNameConverter : IValueConverter
{
    private static readonly Dictionary<string, string?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string code || string.IsNullOrWhiteSpace(code)) return null;
        var key = code.ToUpperInvariant();

        if (_cache.TryGetValue(key, out var cached)) return cached;

        try
        {
            var name = new System.Globalization.RegionInfo(key).EnglishName;
            _cache[key] = name;
            return name;
        }
        catch
        {
            // Code non reconnu par RegionInfo (rare, ex. codes obsolètes/region custom)
            _cache[key] = null;
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── EqualityConverter ───────────────────────────────────────────────────────
// Utilisé pour binder des RadioButtons sur une propriété string
// IsChecked = (Value == ConverterParameter)

public class EqualityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString();

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => (value is true) ? parameter?.ToString() ?? string.Empty : Binding.DoNothing;
}

// ─── NullToWildConverter ─────────────────────────────────────────────────────
// Retourne "Wild" si la valeur est nulle ou vide, sinon retourne la valeur

public class NullToWildConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string s && !string.IsNullOrWhiteSpace(s) ? s : "Wild";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── PlatformIconConverter ────────────────────────────────────────────────────
// Mappe le nom d'une plateforme vers une image PNG embarquée dans les ressources

// Logique de correspondance partagée entre PlatformIconConverter (résout l'image) et
// PlatformHasIconConverter (juste un bool, pour adapter le style de la carte — dégradé +
// texte blanc seulement quand une image est réellement affichée derrière).
internal static class PlatformIconLookup
{
    // Mapping nom de plateforme (tel qu'importé depuis Demozoo) → nom de fichier
    // PNG dans Assets/PlatformIcons/. Plusieurs variantes de nom par plateforme
    // (Demozoo n'est pas toujours cohérent sur la casse/l'orthographe exacte —
    // ex. "Amiga OCS/ECS" vs "Amiga OCS"), en plus du repli approximatif
    // (Contains dans les deux sens) plus bas dans Resolve().
    private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["atari st"]             = "atari_st",
        ["atari st/e"]           = "atari_st",
        ["atari ste"]            = "atari_st",

        ["acorn archimedes"]     = "acorn_archimedes_new",

        ["amiga aga"]            = "amiga_aga",
        ["amiga ppc/rtg"]        = "amiga_aga",
        ["amiga ppc"]            = "amiga_aga",
        ["amiga 1200"]           = "amiga_aga",
        ["amiga 4000"]           = "amiga_aga",
        ["amiga cd32"]           = "amiga_aga",
        ["amiga ocs/ecs"]        = "amiga_ocs",
        ["amiga ocs"]            = "amiga_ocs",
        ["amiga 500"]            = "amiga_ocs",
        ["amiga 600"]            = "amiga_ocs",
        ["amiga 2000"]           = "amiga_ocs",
        ["amiga 1000"]           = "amiga_ocs",

        ["apple ii"]             = "apple_ii_new",
        ["apple iigs"]           = "apple_iigs",
        ["apple ii gs"]          = "apple_iigs",

        ["atari 2600"]           = "atari_2600",
        ["atari vcs"]            = "atari_2600",
        ["atari 7800"]           = "atari_7800",
        ["atari 8-bit"]          = "atari_8bit",
        ["atari 8 bit"]          = "atari_8bit",
        ["atari xl/xe"]          = "atari_8bit",
        ["atari 800"]            = "atari_8bit",
        ["atari xe"]             = "atari_8bit",
        ["atari xl"]             = "atari_8bit",
        ["atari falcon"]         = "atari_falcon_new",
        ["atari falcon030"]      = "atari_falcon_new",
        ["atari tt"]             = "atari_tt",
        ["atari tt030"]          = "atari_tt",
        ["atari jaguar"]         = "atari_jaguar",
        ["atari lynx"]           = "atari_lynx",
        ["atari portfolio"]      = "atari_portfolio",

        ["bbc micro"]            = "bbc_micro_new",
        ["amstrad cpc"]          = "amstrad_cpc",
        ["amstrad cpc464"]       = "amstrad_cpc",
        ["amstrad cpc6128"]      = "amstrad_cpc",
        ["amstrad plus"]         = "amstrad_plus",
        ["amstrad cpc plus"]     = "amstrad_plus",
        ["amstrad gx4000"]       = "amstrad_plus",
        ["beos"]                 = "beos",
        ["mac os classic"]       = "mac_os_classic",
        ["mac os (classic)"]     = "mac_os_classic",
        ["classic mac os"]       = "mac_os_classic",
        ["macos"]                = "macos",
        ["mac os x"]             = "macos",
        ["os x"]                 = "macos",
        ["linux"]                = "linux",
        ["freebsd"]              = "freebsd",
        ["free bsd"]             = "freebsd",
        ["enterprise"]           = "enterprise",
        ["enterprise 128"]       = "enterprise",
        ["enterprise 64"]        = "enterprise",
        ["kc 85"]                = "kc85",
        ["kc85"]                 = "kc85",
        ["kc 85/robotron kc 87"] = "kc85",
        ["robotron kc 87"]       = "kc85",
        ["paper"]                = "paper",
        ["browser"]              = "browser",
        ["html5"]                = "browser",
        ["javascript"]           = "browser",

        ["commodore 128"]        = "commodore_128_new",
        ["commodore 64"]         = "commodore_64_new",
        ["commodore c64"]        = "commodore_64_new",
        ["commodore plus/4"]     = "commodore_plus4_new",
        ["commodore 16/plus 4"]  = "commodore_plus4_new",
        ["commodore c16"]        = "commodore_plus4_new",
        ["commodore plus 4"]     = "commodore_plus4_new",
        ["commodore pet"]        = "commodore_pet_new",
        ["commodore vic-20"]     = "commodore_vic20_new",
        ["commodore vic20"]      = "commodore_vic20_new",

        ["elektronika bk"]       = "elektronika_bk",
        ["elektronika bk-0010/11m"] = "elektronika_bk",
        ["electronica bk-0010"]  = "elektronika_bk",

        ["gp2x"]                 = "gp2x",
        ["gp32"]                 = "gp32",
        ["gamepark 32"]          = "gp32",

        ["sega game gear"]       = "game_gear",
        ["game gear"]            = "game_gear",

        ["intellivision"]        = "intellivision",
        ["vectrex"]              = "vectrex",
        ["thomson"]              = "thomson",
        ["thomson mo5"]          = "thomson",
        ["thomson mo6"]          = "thomson",
        ["thomson to7"]          = "thomson",
        ["thomson to8"]          = "thomson",
        ["thomson to9"]          = "thomson",
        ["sharp x68000"]         = "sharp_x68000",
        ["oric"]                 = "oric",
        ["oric atmos"]           = "oric",
        ["oric-1"]               = "oric",
        ["wonderswan"]           = "wonderswan",
        ["wonderswan color"]     = "wonderswan",
        ["colecovision"]         = "colecovision",

        ["ms-dos"]               = "msdos",
        ["msdos"]                = "msdos",

        ["nec pc engine"]        = "pc_engine",
        ["pc engine"]            = "pc_engine",
        ["turbografx"]           = "pc_engine",
        ["msx"]                  = "msx",
        ["msx2"]                 = "msx",
        ["msx turbo r"]          = "msx",

        ["mobile"]               = "mobile",
        ["mobile phone"]         = "mobile",
        ["ios"]                  = "mobile",

        ["nintendo entertainment system"] = "nes",
        ["nes"]                  = "nes",
        ["famicom"]              = "nes",

        ["neo geo pocket color"] = "neogeo_pocket_color",
        ["neogeo pocket color"]  = "neogeo_pocket_color",
        ["neo geo pocket"]       = "neogeo_pocket",
        ["neogeo pocket"]        = "neogeo_pocket",
        ["neo geo"]              = "neogeo",
        ["neogeo"]               = "neogeo",

        ["nintendo 3ds"]         = "nintendo_3ds",
        ["nintendo 64"]          = "nintendo_64",
        ["nintendo ds"]          = "nintendo_ds",
        ["nintendo gamecube"]    = "nintendo_gamecube",
        ["gamecube"]             = "nintendo_gamecube",
        ["game boy advance"]     = "gameboy_advance",
        ["gameboy advance"]      = "gameboy_advance",
        ["game boy color"]       = "gameboy",
        ["game boy"]             = "gameboy",
        ["gameboy"]              = "gameboy",
        ["nintendo switch"]      = "nintendo_switch",
        ["android"]              = "android",

        ["pmd 85"]               = "pmd85",
        ["pmd-85"]               = "pmd85",
        ["pico-8"]               = "pico8",
        ["pico8"]                = "pico8",

        ["raspberry pi"]         = "raspberry_pi",
        ["calculator"]           = "calculator",
        ["texas instruments calculator"] = "calculator",
        ["ti calculator"]        = "calculator",
        ["graphing calculator"]  = "calculator",
        ["sam coupe"]            = "sam_coupe",
        ["sam coupé"]            = "sam_coupe",

        ["dreamcast"]            = "dreamcast",
        ["sega master system"]   = "sega_master_system",
        ["master system"]       = "sega_master_system",
        ["sega genesis/megadrive"] = "megadrive",
        ["sega mega drive"]      = "megadrive",
        ["megadrive"]            = "megadrive",
        ["genesis"]              = "megadrive",
        ["sega saturn"]          = "saturn",

        ["sharp mz"]             = "sharp_mz_new",

        ["sony playstation 2"]   = "ps2",
        ["playstation 2"]        = "ps2",
        ["sony playstation 3"]   = "ps3",
        ["playstation 3"]        = "ps3",
        ["sony playstation"]     = "ps1",
        ["playstation"]          = "ps1",

        ["super nintendo entertainment system"] = "snes",
        ["super nintendo"]       = "snes",
        ["nintendo snes/super famicom"] = "snes",
        ["snes/super famicom"]   = "snes",
        ["super famicom"]        = "snes",
        ["snes"]                 = "snes",

        ["tic-80"]               = "tic80",
        ["tic80"]                = "tic80",
        ["trs-80"]               = "trs80_new",

        ["vtech laser 200"]      = "laser200",
        ["laser 200"]            = "laser200",
        ["vector-06c"]           = "vector06c",
        ["vector 06c"]           = "vector06c",

        ["nintendo wii"]         = "wii",
        ["wii"]                  = "wii",
        ["windows"]              = "windows",
        ["xbox 360"]             = "xbox360",
        ["xbox360"]              = "xbox360",
        ["microsoft xbox"]       = "xbox",
        ["xbox"]                 = "xbox",

        ["zvt pp01"]             = "pp01",
        ["pp-01"]                = "pp01",

        ["zx spectrum 128"]      = "zx_spectrum_128",
        ["zx spectrum enhanced"] = "zx_spectrum_128",
        ["zx spectrum"]          = "zx_spectrum_new",
        ["zx spectrum 48"]       = "zx_spectrum_new",
        ["zx81"]                 = "zx81",
        ["zx-81"]                = "zx81",

        ["sinclair ql"]          = "sinclair_ql_new",
    };

    public static string? Resolve(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_map.TryGetValue(name.Trim(), out var exact)) return exact;

        var lower = name.ToLowerInvariant();

        // Repli approximatif : cherche la clé la PLUS LONGUE qui matche (substring
        // dans un sens ou l'autre), pas la première trouvée dans l'ordre du
        // dictionnaire. Sans ce tri, une clé courte et générique (ex. "xbox")
        // pouvait matcher AVANT une clé plus longue et spécifique (ex. "xbox 360")
        // pour un nom de plateforme réel qui ne matchait exactement aucune des
        // deux (ex. "XBOX360" sans espace) — donnant la mauvaise icône (Xbox
        // premier du nom au lieu de Xbox 360) alors qu'une correspondance plus
        // précise existait dans le dictionnaire.
        string? bestMatch = null;
        int bestLength = -1;
        foreach (var kv in _map)
        {
            if ((lower.Contains(kv.Key) || kv.Key.Contains(lower)) && kv.Key.Length > bestLength)
            {
                bestMatch = kv.Value;
                bestLength = kv.Key.Length;
            }
        }
        return bestMatch;
    }
}

public class PlatformIconConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var iconName = PlatformIconLookup.Resolve(value as string);
        if (iconName == null) return null;

        if (_cache.TryGetValue(iconName, out var cached)) return cached;

        try
        {
            // Essai 1 : ressource embarquée
            var uri = new Uri($"pack://application:,,,/DemoBase.App;component/Assets/PlatformIcons/{iconName}.png");
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _cache[iconName] = bmp;
            return bmp;
        }
        catch
        {
            try
            {
                // Fallback : fichier à côté de l'exe
                var path = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "Assets", "PlatformIcons", $"{iconName}.png");
                if (!System.IO.File.Exists(path)) { _cache[iconName] = null; return null; }
                var bmp2 = new BitmapImage();
                bmp2.BeginInit();
                bmp2.UriSource   = new Uri(path);
                bmp2.CacheOption = BitmapCacheOption.OnLoad;
                bmp2.EndInit();
                bmp2.Freeze();
                _cache[iconName] = bmp2;
                return bmp2;
            }
            catch { _cache[iconName] = null; return null; }
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── PlatformHasIconConverter ─────────────────────────────────────────────────
// Bool pur (même mapping que ci-dessus) — pilote la Visibility du dégradé/texte blanc dans
// PlatformListView.xaml : seules les plateformes AVEC une image ont besoin de ce traitement
// visuel, les autres affichent leur nom directement sur fond de carte normal.

public class PlatformHasIconConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasIcon = PlatformIconLookup.Resolve(value as string) != null;
        // Bindé directement sur une propriété Visibility (le Border dégradé) : renvoyer
        // l'enum attendu plutôt qu'un bool brut, que WPF ne convertit pas automatiquement.
        // Utilisé aussi dans un DataTrigger (qui compare à Value="False") : là, targetType
        // n'est pas Visibility, donc le bool brut est ce qu'il faut.
        if (targetType == typeof(Visibility))
            return hasIcon ? Visibility.Visible : Visibility.Collapsed;
        return hasIcon;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// PlatformNotEmulatedConverter (liste en dur de noms de plateformes) supprimé le
// 2026-07-24 — remplacé par Platform.HasEmulatorConfig (Models.cs), calculé depuis
// les vraies EmulatorConfig en base par PlatformListViewModel.LoadAsync. Le
// DataTrigger dans PlatformListView.xaml se lie désormais directement sur cette
// propriété, plus besoin de converter.

// ─── ThumbnailPathConverter ───────────────────────────────────────────────────
// Convertit un chemin local ou une URL en BitmapImage décodée à 50px
// → zéro dégradation : décode uniquement la taille affichée

public class ThumbnailPathConverter : IValueConverter
{
    // Cache statique par chemin absolu → BitmapImage déjà décodée et gelée.
    // Évite de redécoder la même image à chaque scroll (le ListBox en mode
    // Recycling réutilise les cellules et rappelle le converter sur les mêmes
    // images). Taille raisonnable : ~5 Ko/vignette × 2000 = ~10 Mo max.
    private static readonly Dictionary<string, BitmapImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || string.IsNullOrWhiteSpace(path))
            return null;

        System.Diagnostics.Debug.WriteLine($"[Conv] called path='{path}'");

        // Résoudre les chemins relatifs
        if (!System.IO.Path.IsPathRooted(path))
            path = System.IO.Path.Combine(AppContext.BaseDirectory, path);

        // ConverterParameter = largeur de décodage ("180" pour la grille graphics, défaut 50)
        int decodeWidth = 50;
        if (parameter is string ps && int.TryParse(ps, out var pw))
            decodeWidth = pw;

        var cacheKey = $"{path}@{decodeWidth}";
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            System.Diagnostics.Debug.WriteLine($"[Conv] cache hit key={cacheKey.Substring(cacheKey.LastIndexOf('\\'))} val={cached?.PixelWidth}x{cached?.PixelHeight}");
            return cached;
        }

        if (!System.IO.File.Exists(path))
        {
            System.Diagnostics.Debug.WriteLine($"[ThumbnailPath] NOT FOUND: {path}");
            _cache[cacheKey] = null;
            return null;
        }

        try
        {
            // ReadAllBytes → MemoryStream : évite les problèmes d'URI encoding
            // et de stream disposé trop tôt
            var bytes = System.IO.File.ReadAllBytes(path);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption      = BitmapCacheOption.OnLoad;
            bmp.CreateOptions    = BitmapCreateOptions.None;
            bmp.StreamSource     = new System.IO.MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            System.Diagnostics.Debug.WriteLine($"[ThumbnailPath] OK: {System.IO.Path.GetFileName(path)} {bmp.PixelWidth}x{bmp.PixelHeight} frozen={bmp.IsFrozen}");
            _cache[cacheKey] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ThumbnailPath] ERROR: {path} → {ex.Message}");
            _cache[cacheKey] = null;
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── SecondsToTimeConverter ─────────────────────────────────────────────────
// double (secondes) → "M:SS" ou "H:MM:SS"

[ValueConversion(typeof(double), typeof(string))]
public class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double seconds || double.IsNaN(seconds)) return "0:00";
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes}:{ts.Seconds:D2}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── ReferenceEqualityConverter ─────────────────────────────────────────────
// MultiBinding : value[0] == value[1] (comparaison référence) → bool
// Utilisé pour détecter l'item sélectionné dans la playlist vidéo.

public class ReferenceEqualityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
        => values.Length == 2 && ReferenceEquals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── RomFoundToVisibilityConverter ──────────────────────────────────────────
// MultiBinding : value[0]=DatRom.Id (int), value[1]=HashSet<int> des Id trouvés lors de la
// dernière tentative de reconstruction (ReleaseDetailViewModel.LastBuildFoundRomIds) → Visible
// si cet Id est dans l'ensemble, sinon Collapsed. 2026-07-29, retour utilisateur : "possibile
// de mettre dans la liste des fichiers celui est correspond au dat ?" — utilisé pour afficher
// une coche ✓ sur la ligne du fichier réellement trouvé/téléchargé, dans la liste des DatRom
// attendus (onglet Fichiers).
public class RomFoundToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length == 2 && values[0] is int id && values[1] is HashSet<int> found)
            return found.Contains(id) ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

// ─── FileExtToColorConverter ──────────────────────────────────────────────────

public class FileExtToColorConverter : IValueConverter
{
    private static readonly Dictionary<string, string> _colors = new(StringComparer.OrdinalIgnoreCase)
    {
        // Trackers Amiga/PC
        { ".mod", "#1565C0" },  // bleu
        { ".xm",  "#1565C0" },
        { ".s3m", "#1565C0" },
        { ".it",  "#1565C0" },
        { ".stk", "#1565C0" },
        { ".dbm", "#1565C0" },
        { ".ft2", "#1565C0" },
        // Trackers ZX Spectrum
        { ".pt1", "#6A1B9A" },  // violet
        { ".pt2", "#6A1B9A" },
        { ".pt3", "#6A1B9A" },
        { ".stc", "#6A1B9A" },
        { ".asc", "#6A1B9A" },
        // SNDH / Atari
        { ".sndh", "#E65100" }, // orange
        { ".ym",   "#E65100" },
        // SID C64
        { ".sid",  "#F9A825" }, // jaune
        { ".psid", "#F9A825" },
        // Audio courant
        { ".mp3",  "#2E7D32" }, // vert
        { ".flac", "#2E7D32" },
        { ".ogg",  "#2E7D32" },
        { ".wav",  "#2E7D32" },
    };

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var ext = value as string ?? "";
        var hex = _colors.TryGetValue(ext, out var h) ? h : "#37474F";
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotImplementedException();
}

// ─── FileExtToLabelConverter ──────────────────────────────────────────────────

public class FileExtToLabelConverter : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        var ext = (value as string ?? "").TrimStart('.').ToUpperInvariant();
        return ext.Length > 4 ? ext[..4] : ext.Length == 0 ? "?" : ext;
    }

    public object ConvertBack(object? v, Type t, object? p, CultureInfo c) =>
        throw new NotImplementedException();
}

// ─── IntEqualityConverter ────────────────────────────────────────────────────
// MultiBinding converter : retourne true si les deux valeurs int sont égales.
// Utilisé pour le highlight de la piste en cours dans le MediaBrowser.

public class IntEqualityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 2) return false;
        if (values[0] is int a && values[1] is int b) return a == b;
        return Equals(values[0], values[1]);
    }
    public object[] ConvertBack(object v, Type[] t, object? p, CultureInfo c)
        => throw new NotImplementedException();
}
