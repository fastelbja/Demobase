using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;

namespace DemoBase.App.Services.ReleaseBuilder;

/// <summary>
/// Résout les URLs de téléchargement Demozoo depuis le couple (link_class, parameter).
///
/// Port fidèle de PrepareDownloadLinkFromDemozoo() + tous les parsers HTML
/// de clsDownload.cs du projet original.
///
/// Adaptation DemoBase (câblage externe uniquement, logique inchangée) :
/// IHttpClientFactory remplacé par deux HttpClient directs — DemoBase n'a pas
/// le même pattern de DI nommée que le projet d'origine. "noRedirectClient"
/// doit avoir AllowAutoRedirect=false sur son handler.
/// </summary>
public sealed class UrlResolver(HttpClient downloadClient, HttpClient noRedirectClient)
{
    // Cache des URLs résolues — évite de refaire les requêtes HTML sur le même paramètre
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _cache = new();

    /// <summary>Vide le cache des URLs résolues (utile après mise à jour du code).</summary>
    public void ClearCache() => _cache.Clear();

    public async Task<string> ResolveFromLinkClassAsync(
        string linkClass, string parameter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return string.Empty;

        string cacheKey = $"{linkClass}:{parameter}";
        if (_cache.TryGetValue(cacheKey, out string? cached)) return cached;

        string resolved = await ResolveInternalAsync(linkClass, parameter, ct);
        if (!string.IsNullOrEmpty(resolved))
            _cache[cacheKey] = resolved;
        return resolved;
    }

    private async Task<string> ResolveInternalAsync(
        string linkClass, string parameter, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(parameter)) return string.Empty;

        string url = linkClass switch
        {
            "AmigascneFile"           => "https://ftp.amigascne.org/pub/amiga/" + parameter,
            "AsciiarenaRelease"       => await ParseAsync("https://www.asciiarena.se/release/" + parameter, ParseAsciiArena, ct),
            "AtarimaniaPage"          => await ParseAsync("http://www.atarimania.com/" + parameter + ".html", ParseAtariMania, ct),
            "BaseUrl"                 => parameter,
            "WikipediaPage"           => parameter,
            "BandcampTrack"           => BuildBandcamp(parameter),
            "BitjamSong"              => "http://www.bitfellas.org/e107_plugins/radio/radio.php?info&id=" + parameter,
            "CappedVideo"             => string.Empty,
            "CsdbMusic"               => await ParseAsync("https://csdb.dk/sid/?id=" + parameter, ParseCsdbMusic, ct),
            "CsdbRelease"             => await ParseAsync("https://csdb.dk/release/?id=" + parameter, ParseCsdb, ct),
            "Defacto2File"            => "https://defacto2.net/d/" + parameter,
            "DemosceneTvVideo"        => string.Empty,
            "DhsVideoDbVideo"         => await ParseAsync("http://dhs.nu/video.php?ID=" + parameter, ParseDhs, ct),
            "DiscogsRelease"          => string.Empty,
            "DOPEdition"              => string.Empty,
            "EventsRetrosceneRelease" => await ParseEventsRetrosceneAsync(parameter, ct),
            "FujiologyFile"           => "https://fujiology.org" + parameter,
            "GameboyDemospottingDemo" => "https://web.archive.org/web/20110921111050/http://gameboy.modermodemet.se/en/demo/" + parameter,
            "GithubAccount"           => "https://github.com/" + parameter,
            "GithubDirectory"         => "https://github.com/" + parameter,
            "GithubRepo"              => await ParseAsync("https://github.com/" + parameter, ParseGithub, ct),
            "HallOfLightGame"         => string.Empty,
            "HearthisTrack"           => "https://hearthis.at/" + parameter,
            "InternetArchivePage"     => await ParseAsync("https://archive.org/metadata/" + parameter, ParseInternetArchive, ct),
            "KestraBitworldRelease"   => string.Empty,
            "ModarchiveModule"        => await ParseAsync("https://modarchive.org/module.php?" + parameter, ParseModArchive, ct),
            "ModlandFile"             => "https://ftp.modland.com" + parameter,
            "NectarineSong"           => "https://scenestream.net/demovibes/song/" + parameter + "/",
            "PaduaOrgFile"            => "https://files.scene.org/get:fi-http/mirrors/padua/" + parameter.Replace("#", "%23"),
            "Pico8Cart"               => "https://www.lexaloffle.com/bbs/?tid=" + parameter,
            "PixeljointImage"         => "http://pixeljoint.com/pixelart/" + parameter + ".htm",
            "Plus4WorldProduction"    => await ParseAsync("http://plus4world.powweb.com/software/" + parameter, ParsePlus4World, ct),
            "SceneOrgFile"            => "https://files.scene.org/get:de-https/" + parameter.Replace("#", "%23"),
            "ScenesatTrack"           => await ParseAsync("https://scenesat.com/track/" + parameter, ParseSceneSat, ct),
            "ShadertoyShader"         => "https://www.shadertoy.com/view/" + parameter,
            "SixteenColorsPack"       => await ParseAsync("https://16colo.rs/pack/" + parameter + "/", ParseSixteenColors, ct),
            "SoundcloudTrack"         => "https://soundcloud.com/" + parameter,
            "SpeccyPlProduction"      => "https://speccy.pl/archive/prod.php?id=" + parameter,
            "SpectrumComputingRelease"=> "https://spectrumcomputing.co.uk/entry/" + parameter,
            "SpotifyTrack"            => "https://play.spotify.com/track/" + parameter,
            "Tic80Cart"               => await ParseAsync("https://tic80.com/play?cart=" + parameter, ParseTic80, ct),
            "UntergrundFile"          => "http://ftp.untergrund.net/" + parameter,
            "VimeoVideo"              => "https://vimeo.com/" + parameter,
            "YoutubeVideo"            => "https://youtube.com/watch?v=" + parameter,
            "ZxArtMusic"              => await ParseAsync("https://zxart.ee/eng/authors/" + parameter, ParseZxArt, ct),
            "ZxArtPicture"            => await ParseAsync("https://zxart.ee/eng/authors/" + parameter, ParseZxArt, ct),
            "ZxArtProduction"         => "https://zxart.ee/eng/authors/" + parameter,
            "ZxDemoItem"              => string.Empty,
            "WaybackMachinePage"      => "https://web.archive.org/web/" + parameter,
            "ArtcityImage"            => await ParseAsync("http://artcity.bitfellas.org/index.php?a=show&id=" + parameter, ParseArtcity, ct),
            "C64chProduction"         => "https://c64.ch/productions/" + parameter,
            "ZXPressIssue"            => "https://zxpress.ru/issue.php?id=" + parameter,
            _                         => string.Empty,
        };

        // 2026-07-31, retour utilisateur (2e report, release "Chanel5" — Abduction 1995) :
        // le fix précédent (NormalizeDownloadUrl dans EmulatorService.cs) ne couvrait que
        // le chemin de lecture ad-hoc ("Lire" sans correspondance DAT) — il ne passe PAS
        // par UrlResolver. Or le lien discmaster est stocké en LinkClass="BaseUrl" (donc
        // résolu ici, via ResolveInternalAsync, pas via ResolveAsync), et le cas "BaseUrl"
        // renvoyait le parameter tel quel (URL /view/... non modifiée) — d'où le
        // téléchargement de la page HTML (8 133 o) au lieu du fichier réel (745 460 o
        // attendus). Fix générique ici (couvre tous les LinkClass, pas seulement BaseUrl) :
        // même normalisation que NormalizeKnownReplacements (déjà utilisée pour le cas
        // scene.org /view/ → /get/ juste en dessous, même famille de bug).
        url = NormalizeKnownReplacements(url);

        // Validation finale : l'URL doit être absolue et http/https/ftp (ou ia-multi pour Internet Archive)
        if (string.IsNullOrEmpty(url)) return string.Empty;
        if (url.StartsWith("ia-multi://", StringComparison.OrdinalIgnoreCase)) return url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https" or "ftp"))
            return string.Empty;

        return url;
    }

    // ──────────────────────────────────────────────────────────────
    // Résolution directe depuis une URL brute (Pouet)
    // ──────────────────────────────────────────────────────────────

    public async Task<string> ResolveAsync(string rawUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawUrl)) return string.Empty;

        string trimmed = rawUrl.Trim();

        // Rejette d'emblée les URLs non-HTTP
        if (!trimmed.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("ftp",  StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out _))
            return string.Empty;

        string url = NormalizeFtpUrls(trimmed);
        url = NormalizeKnownReplacements(url);
        url = await FollowRedirectsAsync(url, ct);

        // Parser spécialisé si le domaine est connu
        string? host = GetHost(url);
        if (host is not null)
        {
            string? resolved = host switch
            {
                "modarchive.org"  => await TryParseAsync(url, ParseModArchive, ct),
                "dhs.nu"          => await TryParseAsync(url, ParseDhs, ct),
                "github.com"      => await TryParseAsync(url, ParseGithub, ct),
                "csdb.dk"         => await TryParseAsync(url, ParseCsdb, ct),
                "atarimania.com"  => await TryParseAsync(url, ParseAtariMania, ct),
                "zxart.ee"        => await TryParseAsync(url, ParseZxArt, ct),
                "scenesat.com"    => await TryParseAsync(url, ParseSceneSat, ct),
                "16colo.rs"       => await TryParseAsync(url, ParseSixteenColors, ct),
                "asciiarena.se"   => await TryParseAsync(url, ParseAsciiArena, ct),
                "artcity.bitfellas.org" => await TryParseAsync(url, ParseArtcity, ct),
                // Téléchargements directs — pas de parsing HTML nécessaire
                "amp.dascene.net" => url,  // downmod.php retourne le fichier directement
                "bmf.wz.cz"       => await TryParseAsync(url, ParseBmfWzCz, ct),
                "zxaaa.net"       => await TryParseAsync(url, ParseZxaaa, ct),
                _                 => null,
            };
            if (!string.IsNullOrEmpty(resolved)) return resolved;
        }

        return url;
    }

    // ──────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────

    private async Task<string> ParseAsync(string url, Func<string, Task<string>> parser, CancellationToken ct)
    {
        try { return await parser(url); }
        catch { return string.Empty; }
    }

    private async Task<string?> TryParseAsync(string url, Func<string, Task<string>> parser, CancellationToken ct)
    {
        try
        {
            string r = await parser(url);
            return string.IsNullOrEmpty(r) ? null : r;
        }
        catch { return null; }
    }

    private static string BuildBandcamp(string parameter)
    {
        if (!parameter.Contains('/')) return string.Empty;
        int slash = parameter.IndexOf('/');
        return "https://" + parameter[..slash] + ".bandcamp.com/track/" + parameter[(slash + 1)..];
    }

    /// <summary>
    /// Construit l'URL de téléchargement asciiarena.se directement depuis le parameter (nom de fichier).
    /// Pattern : /collections/{stem_lower}/{filename_lower}
    /// Ex: "IH-MACGY.TXT" → https://www.asciiarena.se/collections/ih-macgy/ih-macgy.txt
    /// Évite le parsing HTML et les 403 liés à la casse.
    /// </summary>
    private static string BuildAsciiArena(string parameter)
    {
        string lower = parameter.ToLowerInvariant();
        string stem  = Path.GetFileNameWithoutExtension(lower);
        return "https://www.asciiarena.se/collections/" + stem + "/" + lower;
    }

    private async Task<string> ParseTic80(string url)
    {
        // Page ex: https://tic80.com/play?cart=4505
        // Contient deux URLs /cart/xxx/ : d'abord cover.gif (og:image), puis name.tic (download)
        // On cherche spécifiquement le lien se terminant par .tic
        string html = await FetchPageAsync(url);
        const string prefix = "https://tic80.com/cart/";
        int pos = 0;
        while (true)
        {
            pos = html.IndexOf(prefix, pos, StringComparison.Ordinal);
            if (pos < 0) return string.Empty;
            int end = html.IndexOfAny(new[] { '"', ')', '\n', ' ' }, pos);
            if (end > pos)
            {
                string candidate = html[pos..end];
                if (candidate.EndsWith(".tic", StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            pos++;
        }
    }

    private async Task<string> ParsePlus4World(string url)
    {
        // Page ex: http://plus4world.powweb.com/software/LALASKB
        // Contient : <a href="http://plus4world.powweb.com/dl/...">Download from Plus/4 World</a>
        string html = await FetchPageAsync(url);
        const string prefix = "http://plus4world.powweb.com/dl/";
        int pos = html.IndexOf(prefix, StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        int end = html.IndexOfAny(new[] { '"', '\'' }, pos);
        return end > pos ? html[pos..end] : string.Empty;
    }

    private async Task<string> ParseEventsRetrosceneAsync(string parameter, CancellationToken ct)
    {
        string url     = "http://events.retroscene.org/" + parameter;
        string result  = await ParseAsync(url, ParseEventsRetroscene, ct);
        return string.IsNullOrEmpty(result) ? url : result;
    }

    // ──────────────────────────────────────────────────────────────
    // Normalisation statique
    // ──────────────────────────────────────────────────────────────

    private static string NormalizeFtpUrls(string url)
    {
        var map = new Dictionary<string, string>
        {
            ["ftp://de.aminet.net"]    = "http://de.aminet.net",
            ["ftp://c64.rulez.org"]    = "http://c64.rulez.org",
            ["ftp.scs-trc.net"]        = "static.zedz.net",
            ["//ftp.amigascne.org/"]   = "https://ftp.amigascne.org/",
        };
        foreach (var (from, to) in map)
            url = url.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return url;
    }

    private static string NormalizeKnownReplacements(string url)
    {
        url = url.Replace("http://noname.c64.org/csdb/release/download.php?id=",
                          "https://csdb.dk/release/download.php?id=", StringComparison.OrdinalIgnoreCase);
        url = url.Replace("https://noname.c64.org/csdb/release/download.php?id=",
                          "https://csdb.dk/release/download.php?id=", StringComparison.OrdinalIgnoreCase);
        url = url.Replace("a8.fandal.cz/detail.php?files_id",
                          "a8.fandal.cz/download.php?files_id", StringComparison.OrdinalIgnoreCase);
        url = url.Replace("ftp://ftp.byterapers.com/pub/demos/",
                          "http://ftp.byterapers.com/pub/", StringComparison.OrdinalIgnoreCase);
        if (url.Contains("scene.org", StringComparison.OrdinalIgnoreCase))
            url = url.Replace("/view/", "/get/", StringComparison.OrdinalIgnoreCase);
        // 2026-07-31, retour utilisateur : discmaster.textfiles.com/view/... est une page
        // HTML (lecteur + bouton "download"), pas le fichier — /file/... sert le fichier
        // brut à l'identique (confirmé par l'utilisateur avec deux URLs exactes du même
        // fichier UNBORN.S3M, puis reconfirmé avec EIN_COMM.S3M après un premier fix
        // incomplet qui ne couvrait pas ce chemin de résolution LinkClass="BaseUrl").
        if (url.Contains("discmaster.textfiles.com", StringComparison.OrdinalIgnoreCase))
            url = url.Replace("/view/", "/file/", StringComparison.OrdinalIgnoreCase);
        return url;
    }

    private async Task<string> FollowRedirectsAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = noRedirectClient;
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                string? location = response.Headers.Location?.ToString();
                if (!string.IsNullOrEmpty(location)) return location;
            }
        }
        catch { }
        return url;
    }

    private async Task<string> FetchPageAsync(string url)
    {
        var client = downloadClient;
        try
        {
            return await client.GetStringAsync(url);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Certains serveurs (ex: asciiarena.se) retournent 403 si le chemin est en majuscules.
            // On retente avec le chemin en minuscules.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw;
            string lowerPath = uri.AbsolutePath.ToLowerInvariant();
            if (lowerPath == uri.AbsolutePath) throw; // déjà en minuscules → pas de retry
            string lowerUrl = uri.GetLeftPart(UriPartial.Authority) + lowerPath
                            + (uri.Query.Length > 0 ? uri.Query : "");
            return await client.GetStringAsync(lowerUrl);
        }
    }

    private static string? GetHost(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? uri.Host[4..] : uri.Host;
    }

    // ──────────────────────────────────────────────────────────────
    // Parsers HTML — port fidèle de clsDownload.cs
    // ──────────────────────────────────────────────────────────────

    private async Task<string> ParseModArchive(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("https://api.modarchive.org/", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        string link = html[pos..];
        return link[..link.IndexOf('"')];
    }

    private async Task<string> ParseDhs(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("http://vdb.dhs.nu/", StringComparison.Ordinal);
        if (pos < 0) pos = html.IndexOf("https://www.youtube.com/embed/", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        string link = html[pos..html.IndexOf('"', pos)];
        return link.Replace("/embed/", "/watch?v=", StringComparison.Ordinal);
    }

    private async Task<string> ParseGithub(string url)
    {
        string html = await FetchPageAsync(url);
        int zipPos = html.IndexOf(".zip", StringComparison.Ordinal);
        if (zipPos < 0) return string.Empty;
        int j = zipPos - 6;
        string test;
        do { test = html[j..zipPos]; j--; } while (!test.Contains(':'));
        return "https://github.com/" + test[3..] + ".zip";
    }

    private async Task<string> ParseCsdb(string url)
    {
        // Pour download.php : suit d'abord la redirection directe (peut être ftp://)
        if (url.Contains("download.php", StringComparison.OrdinalIgnoreCase))
        {
            string redirected = await FollowRedirectsAsync(url, CancellationToken.None);
            if (!redirected.Equals(url, StringComparison.OrdinalIgnoreCase))
                return redirected; // Redirection directe vers ftp:// ou autre
        }

        string html = await FetchPageAsync(url);
        foreach (var prefix in new[] { "https://csdb.dk/getinternalfile.php", "http://csdb.dk/getinternalfile.php", "ftp://" })
        {
            int pos = html.IndexOf(prefix, StringComparison.Ordinal);
            if (pos >= 0) return html[pos..html.IndexOf('<', pos)];
        }
        return url;
    }

    private async Task<string> ParseCsdbMusic(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("https://hvsc.csdb.dk", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        return html[pos..html.IndexOf('"', pos)];
    }

    private async Task<string> ParseAtariMania(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("pgedump.awp?", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        string link = html[pos..html.IndexOf('\'', pos)];
        return "http://www.atarimania.com/" + link;
    }

    private async Task<string> ParseZxArt(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("https://zxart.ee/file/id", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        return html[pos..html.IndexOf("\">", pos, StringComparison.Ordinal)];
    }

    private async Task<string> ParseSceneSat(string url)
    {
        string html = await FetchPageAsync(url);
        int mp3Pos = html.IndexOf(".mp3", StringComparison.Ordinal);
        if (mp3Pos < 0) return string.Empty;
        int j = mp3Pos - 6;
        string test;
        do { test = html[j..mp3Pos]; j--; } while (!test.Contains("href="));
        return "https://scenesat.com" + test[6..] + ".mp3";
    }

    private async Task<string> ParseSixteenColors(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("/archive/", StringComparison.Ordinal);
        if (pos < 0) return url;
        string link = html[pos..html.IndexOf('"', pos)];
        return "https://16colo.rs" + link;
    }

    private async Task<string> ParseAsciiArena(string url)
    {
        string html = await FetchPageAsync(url);

        // Dans le HTML source Next.js, le JSON est sérialisé avec guillemets échappés :
        // \"collyFileUrl\":\"/collections/ih-nmr/ih-nmr.txt\"
        // Il faut chercher ce pattern avec backslashes littéraux.
        const string marker    = "\\\"collyFileUrl\\\":\\\"";
        const string markerAlt = "\"collyFileUrl\":\""; // fallback JSON non échappé

        int pos = html.IndexOf(marker, StringComparison.Ordinal);
        if (pos >= 0)
        {
            pos += marker.Length;
            int end = html.IndexOf("\\\"", pos, StringComparison.Ordinal);
            if (end > pos)
            {
                string path = html[pos..end];
                if (!string.IsNullOrWhiteSpace(path))
                    return "https://www.asciiarena.se" + path;
            }
        }

        // Fallback : JSON non échappé
        pos = html.IndexOf(markerAlt, StringComparison.Ordinal);
        if (pos >= 0)
        {
            pos += markerAlt.Length;
            int end = html.IndexOf('"', pos);
            if (end > pos)
            {
                string path = html[pos..end];
                if (!string.IsNullOrWhiteSpace(path))
                    return "https://www.asciiarena.se" + path;
            }
        }

        return string.Empty;
    }

    private async Task<string> ParseBmfWzCz(string url)
    {
        // Page type : http://bmf.wz.cz:8080/?font=image-font-017
        // On cherche le premier <a href="?font=xxx&download=N"> (N numérique)
        // On ignore : <s href=...> (versions barrées) et download=src (pseudo source)
        string html = await FetchPageAsync(url);

        // Base : scheme + authority (ex: http://bmf.wz.cz:8080/)
        if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUri))
            return string.Empty;

        // On ne cherche qu'après <summary>download</summary>
        int summaryPos = html.IndexOf("<summary>download</summary>", StringComparison.OrdinalIgnoreCase);
        if (summaryPos < 0) return string.Empty;

        int pos = summaryPos + "<summary>download</summary>".Length;
        while (true)
        {
            // Cherche la prochaine balise <a (minuscules ou majuscules)
            int tagPos = html.IndexOf("<a ", pos, StringComparison.OrdinalIgnoreCase);
            if (tagPos < 0) break;

            // Fin de la balise ouvrante
            int tagEnd = html.IndexOf('>', tagPos);
            if (tagEnd < 0) break;
            string tag = html[tagPos..tagEnd];

            // Extrait href="..." ou href='...'
            int hrefPos = tag.IndexOf("href=", StringComparison.OrdinalIgnoreCase);
            if (hrefPos >= 0)
            {
                hrefPos += 5;
                char quote = tag.Length > hrefPos ? tag[hrefPos] : '\0';
                if (quote is '"' or '\'')
                {
                    int hrefEnd = tag.IndexOf(quote, hrefPos + 1);
                    if (hrefEnd > hrefPos)
                    {
                        string href = tag[(hrefPos + 1)..hrefEnd]
                            .Replace("&amp;", "&")
                            .Trim();

                        // Filtre : doit contenir &download= mais pas download=src
                        int dlPos = href.IndexOf("&download=", StringComparison.OrdinalIgnoreCase);
                        if (dlPos >= 0)
                        {
                            string dlValue = href[(dlPos + "&download=".Length)..];
                            // Garde uniquement les valeurs numériques (pas "src", pas version "1.2")
                            if (dlValue.Length > 0 && dlValue.All(char.IsDigit))
                            {
                                // Uri résout le relatif (?font=xxx&download=17) depuis la base
                                return new Uri(baseUri, href).ToString();
                            }
                        }
                    }
                }
            }

            pos = tagEnd + 1;
        }

        return string.Empty;
    }

    private async Task<string> ParseInternetArchive(string url)
    {
        // url = https://archive.org/metadata/{identifier}
        string identifier = url.Split('/').LastOrDefault() ?? "";

        string json = await FetchPageAsync(url);

        // Extraire tous les fichiers "original" non-metadata
        var files = new List<string>();
        int pos = 0;
        while (true)
        {
            int namePos = json.IndexOf("\"name\":\"", pos, StringComparison.Ordinal);
            if (namePos < 0) break;
            namePos += 8;
            int nameEnd = json.IndexOf('"', namePos);
            if (nameEnd < 0) break;
            string fname = json[namePos..nameEnd];

            // Cherche "source" dans les 300 chars suivants
            int srcPos = json.IndexOf("\"source\":\"", namePos, Math.Min(300, json.Length - namePos), StringComparison.Ordinal);
            bool isOriginal = srcPos >= 0 && json.Length > srcPos + 10 &&
                json[(srcPos + 10)..].StartsWith("original", StringComparison.Ordinal);

            bool isMeta = fname.EndsWith("_files.xml",   StringComparison.OrdinalIgnoreCase)
                       || fname.EndsWith("_meta.xml",    StringComparison.OrdinalIgnoreCase)
                       || fname.EndsWith("_reviews.xml", StringComparison.OrdinalIgnoreCase)
                       || fname.StartsWith("__ia_thumb",  StringComparison.OrdinalIgnoreCase)
                       || fname.EndsWith(".sqlite",       StringComparison.OrdinalIgnoreCase)
                       || fname.EndsWith(".torrent",      StringComparison.OrdinalIgnoreCase)
                       || fname.Contains("screenshot",    StringComparison.OrdinalIgnoreCase);

            if (isOriginal && !isMeta)
                files.Add(fname);

            pos = nameEnd + 1;
        }

        if (files.Count == 0)
        {
            // Certains items n'ont pas de fichier avec "source":"original"
            // (item n'ayant qu'un seul fichier uploadé directement, ou champ source absent)
            // → deuxième passe : on prend tous les fichiers non-metadata, quelle que soit la source
            pos = 0;
            while (true)
            {
                int namePos = json.IndexOf("\"name\":\"", pos, StringComparison.Ordinal);
                if (namePos < 0) break;
                namePos += 8;
                int nameEnd = json.IndexOf('"', namePos);
                if (nameEnd < 0) break;
                string fname = json[namePos..nameEnd];

                bool isMeta = fname.EndsWith("_files.xml",   StringComparison.OrdinalIgnoreCase)
                           || fname.EndsWith("_meta.xml",    StringComparison.OrdinalIgnoreCase)
                           || fname.EndsWith("_reviews.xml", StringComparison.OrdinalIgnoreCase)
                           || fname.StartsWith("__ia_thumb",  StringComparison.OrdinalIgnoreCase)
                           || fname.EndsWith(".sqlite",       StringComparison.OrdinalIgnoreCase)
                           || fname.EndsWith(".torrent",      StringComparison.OrdinalIgnoreCase)
                           || fname.Contains("screenshot",    StringComparison.OrdinalIgnoreCase);

                // Exclure aussi les fichiers dérivés typiques d'IA (formats non originaux)
                bool isDerived = fname.EndsWith(".png",  StringComparison.OrdinalIgnoreCase)
                              || fname.EndsWith(".jpg",  StringComparison.OrdinalIgnoreCase)
                              || fname.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                              || fname.EndsWith(".gif",  StringComparison.OrdinalIgnoreCase)
                              || fname.EndsWith(".xml",  StringComparison.OrdinalIgnoreCase)
                              || fname.EndsWith(".pdf",  StringComparison.OrdinalIgnoreCase);

                if (!isMeta && !isDerived)
                    files.Add(fname);

                pos = nameEnd + 1;
            }
        }

        if (files.Count == 0)
            return string.Empty;

        // Retourne une URL spéciale encodant tous les fichiers à télécharger
        return "ia-multi://" + identifier + "|" + string.Join("|", files.Select(Uri.EscapeDataString));
    }

    private async Task<string> ParseEventsRetroscene(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf("https://events.retroscene.org/files/", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        return html[pos..html.IndexOf("\">", pos, StringComparison.Ordinal)];
    }

    private async Task<string> ParseArtcity(string url)
    {
        string html = await FetchPageAsync(url);
        int pos = html.IndexOf(">full resolution<", StringComparison.Ordinal);
        if (pos < 0) return string.Empty;
        int j = pos - 6;
        string test;
        do { test = html[j..pos]; j--; } while (!test.Contains("href="));
        string link = "http://artcity.bitfellas.org/" + test[6..];
        return link[..^1];
    }

    /// <summary>
    /// zxaaa.net (get.php?id=X&amp;f=...&amp;t=...&amp;c=...) : "t" (timestamp) et "c"
    /// (hash) forment un jeton signé à courte durée de vie. Un lien stocké en base
    /// (typiquement scrapé depuis Pouet il y a des mois/années) a presque toujours un
    /// jeton expiré : le serveur répond alors par une redirection 301 vers la page de la
    /// démo (view_demo.php) au lieu d'envoyer le fichier — constaté par l'utilisateur sur
    /// plusieurs releases.
    ///
    /// Plutôt que de réutiliser le lien stocké, on récupère systématiquement un lien
    /// frais : on extrait l'"id" de la démo depuis l'URL fournie (get.php OU
    /// view_demo.php, les deux le portent), on recharge la page view_demo.php?id=X, et on
    /// en extrait le lien get.php "Download" qui vient d'y être généré avec un jeton
    /// valide à l'instant présent. Le téléchargement effectif a lieu juste après
    /// (ReleaseBuilderService.DownloadLinkAsync), donc la fenêtre de validité du jeton
    /// n'a pas le temps d'expirer.
    /// </summary>
    private async Task<string> ParseZxaaa(string url)
    {
        var id = ExtractQueryParam(url, "id");
        if (string.IsNullOrEmpty(id)) return url;

        string html;
        try { html = await FetchPageAsync($"https://zxaaa.net/view_demo.php?id={id}"); }
        catch { return url; }

        // On ne suppose pas le séparateur exact entre paramètres ("&" vs l'entité HTML
        // "&amp;" couramment utilisée dans les attributs href) : on repère juste le début
        // "get.php?id=X", puis on lit jusqu'à la première guillemet/espace/parenthèse qui
        // termine l'attribut, et on normalise les entités ensuite.
        string marker = $"get.php?id={id}";
        int pos = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return url;

        int end = html.IndexOfAny(new[] { '"', '\'', ')', ' ', '\n', '\r' }, pos);
        if (end <= pos) return url;

        string link = html[pos..end].Replace("&amp;", "&");
        return "https://zxaaa.net/" + link;
    }

    /// <summary>Extrait la valeur d'un paramètre de query string par recherche simple
    /// (pas de dépendance à System.Web.HttpUtility, indisponible dans ce projet WPF).</summary>
    private static string? ExtractQueryParam(string url, string name)
    {
        string marker = name + "=";
        int pos = url.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (pos < 0) return null;
        pos += marker.Length;
        int end = url.IndexOfAny(new[] { '&', '#' }, pos);
        return end > pos ? url[pos..end] : url[pos..];
    }

    // ──────────────────────────────────────────────────────────────
    // Nettoyage des noms de fichiers
    // ──────────────────────────────────────────────────────────────

    public static string SanitizeFilename(string name)
    {
        // Port de ArrangeWindowsName2() du projet original
        var decoded = Uri.UnescapeDataString(name);
        decoded = decoded
            .Replace("%21", "!").Replace("%23", "#").Replace("%24", "$")
            .Replace("%2B", "+").Replace("%25", "%").Replace("%26", "&")
            .Replace("%20", " ").Replace("%28", "(").Replace("%29", ")")
            .Replace("%2C", ",").Replace("%27", "'")
            .Replace("%5b", "[").Replace("%5d", "]")
            .Replace("%5B", "[").Replace("%5D", "]")
            .Replace("%5E", "^").Replace("%3C", "<").Replace("%3E", ">");

        var invalid = Path.GetInvalidFileNameChars();
        string result = string.Concat(decoded.Select(c => invalid.Contains(c) ? '_' : c));

        while (result.EndsWith('.')) result = result[..^1];
        if (result.EndsWith(';'))    result = result[..^1];

        return result;
    }
}
