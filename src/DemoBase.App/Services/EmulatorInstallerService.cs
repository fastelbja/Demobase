using DemoBase.Core.Diagnostics;
using System.IO;
using System.IO.Compression;
using SevenZipExtractor;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DemoBase.App.Services;

// ─── Résultat d'installation ──────────────────────────────────────────────────

public record InstallResult(bool Success, string? Version = null, string? Error = null);

// ─── Rapport de mise à jour disponible ───────────────────────────────────────

public record UpdateInfo(bool UpdateAvailable, string? LatestVersion, string? InstalledVersion, string? DownloadUrl);

// ─── Service principal ────────────────────────────────────────────────────────

/// <summary>
/// Télécharge, extrait et installe les émulateurs portables dans Emus/{Name}/.
/// Toute extraction est "à plat" : la racine du ZIP est supprimée, les fichiers
/// atterrissent directement dans Emus/{FolderName}/ sans dossier supplémentaire.
/// Les versions installées sont persistées dans Emus/versions.json.
/// </summary>
public class EmulatorInstallerService
{
    // Dossier racine des émulateurs : <exe>/Emus/
    public static string EmusRoot =>
        Path.Combine(AppContext.BaseDirectory, "Emus");

    /// <summary>Dossier racine générique pour n'importe quel RootFolder
    /// (ex. "Emus", "Externals"). EmusRoot reste disponible pour compatibilité
    /// avec le code existant qui ne connaît que les émulateurs.</summary>
    public static string GetRoot(string rootFolder) =>
        Path.Combine(AppContext.BaseDirectory, rootFolder);

    private static string VersionsFile(string rootFolder) =>
        Path.Combine(GetRoot(rootFolder), "versions.json");

    // Client HTTP partagé.
    //
    // Historique de ce User-Agent (pour éviter de refaire les mêmes erreurs) :
    //   1. "DemoBase/1.0" — bloqué par plusieurs sites anciens qui exigent un UA
    //      "de navigateur" (commence par "Mozilla/5.0").
    //   2. Un faux User-Agent Chrome complet — a déclenché les protections anti-bot
    //      (Cloudflare/Wordfence) de plusieurs petits sites : un UA Chrome sans les
    //      autres signaux d'un vrai navigateur (Sec-Fetch-*, cookies, fingerprint TLS
    //      JA3 cohérent) est un classique marqueur de bot, donc PIRE que pas de
    //      mimétisme du tout.
    //   3. (actuel) UA hybride "Mozilla/5.0 (...) DemoBase-Installer/1.0" : commence
    //      par "Mozilla/5.0" pour satisfaire les vérifications naïves qui cherchent
    //      ce préfixe, mais reste honnête sur la nature de l'outil — n'imite pas un
    //      vrai Chrome, donc ne déclenche pas les heuristiques anti-bot avancées.
    private static readonly HttpClient _http = BuildHttpClient();

    // 2026-08-21, retour utilisateur : les 5 entrées SourceForge (EightyOne, Fuse,
    // Hatari, VICE, Handy) échouent avec _http quelle que soit l'URL SourceForge
    // essayée (master.dl direct, sourceforge.net/.../download, downloads.sourceforge.net,
    // ?use_mirror=X) — confirmé en récupérant ces pages directement : SourceForge
    // renvoie systématiquement la page interstitielle HTML "Your download will start
    // shortly..." (déclenchement du vrai téléchargement par JavaScript côté navigateur,
    // pas par une redirection HTTP serveur). Capture d'écran utilisateur à l'appui : la
    // MÊME URL fonctionne dans Firefox (avec ~5s d'attente pour le timer JS) — la
    // différence n'est donc pas l'URL mais l'empreinte du client HTTP. Client HTTP dédié,
    // utilisé UNIQUEMENT pour les hôtes *.sourceforge.net (ne touche pas _http, dont le
    // réglage actuel — UA hybride, HTTP/1.1 forcé — a été spécifiquement ajusté pour
    // D'AUTRES sites et ne doit pas régresser : un vrai User-Agent Chrome complet avait
    // déjà, par le passé, déclenché des protections anti-bot PIRES sur d'autres sites,
    // cf. commentaire ci-dessous sur _http). Ce client dédié imite un vrai navigateur
    // moderne (HTTP/2 si possible, UA Chrome complet, en-têtes Accept/Accept-Language) —
    // pari raisonnable puisque exactement cette combinaison réussit dans un vrai
    // navigateur pour ces mêmes URLs.
    private static readonly HttpClient _httpSourceForge = BuildSourceForgeHttpClient();

    private static HttpClient BuildSourceForgeHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client  = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        // HTTP/2 si le serveur le supporte (négociation normale, pas forcée comme pour
        // _http) — un vrai navigateur moderne utilise HTTP/2 avec sourceforge.net.
        client.DefaultRequestVersion = System.Net.HttpVersion.Version20;
        client.DefaultVersionPolicy  = HttpVersionPolicy.RequestVersionOrLower;

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
        client.DefaultRequestHeaders.Accept.ParseAdd(
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7");
        return client;
    }

    private static HttpClient BuildHttpClient()
    {
        // AllowAutoRedirect=true (par défaut) ne suit PAS les redirections qui
        // changent de protocole (https → http) depuis .NET 5, pour des raisons de
        // sécurité — c'est exactement ce que fait carpeludum.com (Kega Fusion) et
        // probablement d'autres vieux sites. On désactive le suivi automatique et
        // on gère nous-mêmes la boucle de redirection dans DownloadArchiveAsync,
        // où l'on peut explicitement accepter un downgrade de protocole pour des
        // sources de confiance (catalogue contrôlé par nous, pas une page tierce).
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        var client  = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(10) };

        // Force HTTP/1.1 : plusieurs CDN anciens gèrent mal la négociation HTTP/2 par
        // défaut de .NET 5+, et renvoient une page HTML au lieu du binaire. NE PAS
        // étendre ce réglage à SourceForge (cf. _httpSourceForge ci-dessus) : ce forçage
        // HTTP/1.1 était originellement motivé PAR SourceForge, mais leur infrastructure
        // a depuis changé — HTTP/1.1 semble maintenant contribuer au déclenchement de la
        // page interstitielle plutôt qu'à l'éviter (un vrai navigateur moderne utilise
        // HTTP/2 avec sourceforge.net).
        client.DefaultRequestVersion       = System.Net.HttpVersion.Version11;
        client.DefaultVersionPolicy        = HttpVersionPolicy.RequestVersionExact;

        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DemoBase-Installer/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("*/*");
        return client;
    }

    /// <summary>Vrai si l'hôte de <paramref name="url"/> appartient à la famille de
    /// domaines SourceForge (sourceforge.net et tous ses sous-domaines CDN, ex.
    /// *.dl.sourceforge.net, downloads.sourceforge.net) — utilisé pour router ces
    /// requêtes vers <see cref="_httpSourceForge"/> plutôt que <see cref="_http"/>.</summary>
    private static bool IsSourceForgeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Host.EndsWith("sourceforge.net", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// GET avec suivi manuel des redirections (jusqu'à 10 sauts), y compris les
    /// changements de protocole https→http que HttpClient refuse de suivre par
    /// défaut. Nécessaire pour certains vieux sites (ex. carpeludum.com) dont la
    /// chaîne de téléchargement redirige vers une URL http en clair.
    /// </summary>
    private static async Task<HttpResponseMessage> GetFollowingRedirectsAsync(
        string url, HttpCompletionOption completionOption, CancellationToken ct)
    {
        var current = url;
        for (int hop = 0; hop < 10; hop++)
        {
            var client   = IsSourceForgeHost(current) ? _httpSourceForge : _http;
            var response = await client.GetAsync(current, completionOption, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Moved
                                     or System.Net.HttpStatusCode.Found
                                     or System.Net.HttpStatusCode.SeeOther
                                     or System.Net.HttpStatusCode.TemporaryRedirect
                                     or System.Net.HttpStatusCode.PermanentRedirect)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) break;
                current = location.IsAbsoluteUri
                    ? location.ToString()
                    : new Uri(new Uri(current), location).ToString();
                continue;
            }
            return response;
        }
        // Dernier essai sans intercepter le résultat (laisse EnsureSuccessStatusCode
        // remonter une erreur explicite si on est toujours en boucle de redirection).
        var lastClient = IsSourceForgeHost(current) ? _httpSourceForge : _http;
        return await lastClient.GetAsync(current, completionOption, ct);
    }

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    // ── API publique ──────────────────────────────────────────────────────────

    /// <summary>Installe un émulateur (téléchargement + extraction plate).</summary>
    public async Task<InstallResult> InstallAsync(
        EmulatorDownloadEntry entry,
        IProgress<(string Message, int Percent)>? progress = null,
        CancellationToken ct = default)
    {
        // Cas spécial "Unreal Speccy" : le catalogue pointe vers GitHub (djdron/UnrealSpeccyP)
        // pour l'affichage, mais ce dépôt n'a plus AUCUN build "classique" (0.37.9 by SMT,
        // celui compatible avec -i/notre format ini) — ses seuls builds Windows sont un
        // portage SDL2 récent (0.0.7x/0.0.8x) dont le support de -i n'est pas garanti (vécu :
        // l'émulateur ne répondait plus aux arguments après un re-téléchargement). On
        // court-circuite donc la résolution GitHub/Strategy et on installe toujours le build
        // classique hébergé sur Mega via UnrealSpeccyClassicBuildService, quelle que soit la
        // Strategy déclarée dans le catalogue.
        if (string.Equals(entry.FolderName, "Unreal Speccy", StringComparison.OrdinalIgnoreCase))
            return await InstallUnrealSpeccyClassicAsync(entry, progress, ct);

        try
        {
            progress?.Report(($"Recherche de la dernière version de {entry.DisplayName}…", 5));

            var (downloadUrl, version) = entry.Strategy switch
            {
                DownloadStrategy.GitHub      => await ResolveGitHubAsync(entry, ct),
                DownloadStrategy.SourceForge => ResolveSourceForge(entry),
                DownloadStrategy.DirectUrl   => (entry.Source, entry.VersionOverride ?? ParseVersionFromUrl(entry.Source)),
                DownloadStrategy.PageScrape      => await ResolvePageScrapeAsync(entry, ct),
                DownloadStrategy.ManualDownload  => throw new ManualDownloadRequiredException(entry.Source, entry.DisplayName),
                _ => throw new NotSupportedException($"Unknown strategy: {entry.Strategy}"),
            };

            if (string.IsNullOrEmpty(downloadUrl))
                return new(false, Error: $"Could not find the download URL for {entry.DisplayName}.");

            progress?.Report(($"Downloading {entry.DisplayName} {version}…", 15));
            // Referer optionnel — contourne la protection hotlink de certains sites (ex. dcmoto)
            if (!string.IsNullOrEmpty(entry.Referer))
                _http.DefaultRequestHeaders.Referrer = new Uri(entry.Referer);
            string archive;
            try
            {
                archive = await DownloadArchiveAsync(downloadUrl, entry.FolderName, progress, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Un seul retry automatique pour les incidents réseau transitoires
                // (timeout, connexion reset, EOF inattendu) — vécu avec plusieurs
                // mirrors instables (SourceForge, cngsoft.no-ip.org…). Évite d'avoir
                // à cliquer "Réessayer" manuellement pour un simple accroc ponctuel.
                progress?.Report(($"Network hiccup, retrying {entry.DisplayName}…", 15));
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
                archive = await DownloadArchiveAsync(downloadUrl, entry.FolderName, progress, ct);
            }
            finally
            {
                _http.DefaultRequestHeaders.Referrer = null;
            }

            progress?.Report(($"Extracting to {entry.RootFolder}/{entry.FolderName}/…", 80));
            var destDir = Path.Combine(GetRoot(entry.RootFolder), entry.FolderName);
            try
            {
                // Un ZIP classique → extraction ZipFile. Sinon (archive 7-Zip brute
                // OU exécutable auto-extractible SFX comme « mameXXXX_64bit.exe »)
                // → extraction via SevenZipExtractor, qui préserve l'arborescence.
                if (IsValidZip(archive))
                    ExtractFlat(archive, destDir);
                else
                    ExtractSevenZipFlat(archive, destDir);
            }
            catch (Exception exExtract)
            {
                try { File.Delete(archive); } catch { }
                return new(false, Error:
                    $"Le fichier téléchargé n'a pas pu être extrait (ni ZIP, ni archive " +
                    $"7-Zip/SFX valide — le serveur a peut-être renvoyé une page d'erreur). " +
                    $"Détail : {exExtract.Message}. URL: {downloadUrl}");
            }

            // Supprimer l'archive temporaire
            try { File.Delete(archive); } catch { }

            // Exécuter install.bat si présent (ex. DCMOTO qui concatène des .dat en .exe)
            await RunPostInstallBatAsync(destDir, progress, ct);


            // Sauvegarder la version
            await SaveVersionAsync(entry.FolderName, version, downloadUrl, entry.RootFolder);

            // Configuration BIOS automatique (best-effort) : si cet émulateur a besoin du pack
            // BIOS Recalbox (DuckStation/PCSX2/Flycast/melonDS/XM6 TypeG) et que le pack est
            // déjà téléchargé, ses fichiers sont recherchés par taille+CRC32 et copiés/
            // configurés immédiatement — sans attendre un re-téléchargement manuel du pack.
            // No-op pour tous les autres émulateurs. Ne bloque jamais l'installation.
            BiosPackService.ConfigureEmulatorBiosIfNeeded(entry.FolderName);

            // Idem pour Mesen (MesenCE) : dépose un settings.json pré-rempli pour éviter
            // l'assistant "MesenCE - Emulator Configuration" au premier lancement. No-op pour
            // tous les autres émulateurs. Ne bloque jamais l'installation.
            MesenSetupService.DeployIfMesenFolder(entry.FolderName, destDir);

            progress?.Report(($"{entry.DisplayName} {version} installed.", 100));
            return new(true, version);
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: "Installation canceled.");
        }
        catch (ManualDownloadRequiredException ex)
        {
            // Ouvrir la page de téléchargement dans le navigateur par défaut
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ex.PageUrl) { UseShellExecute = true }); }
            catch { }
            return new(false, Error: $"{ex.EmuName} doit être téléchargé manuellement. La page de téléchargement a été ouverte dans votre navigateur : {ex.PageUrl}");
        }
        catch (HttpRequestException ex)
        {
            // ex.Message seul est souvent générique ("An error occurred while sending
            // the request."). Le code HTTP réel (ex.StatusCode) et la cause profonde
            // (ex.InnerException — DNS, TLS, connexion refusée…) sont essentiels au
            // diagnostic, donc on les inclut explicitement.
            var status = ex.StatusCode.HasValue ? $"HTTP {(int)ex.StatusCode}" : "no response";
            var detail = ex.InnerException?.Message;
            var msg    = string.IsNullOrEmpty(detail)
                ? $"Network error ({status}): {ex.Message}"
                : $"Network error ({status}): {ex.Message} — {detail}";
            return new(false, Error: msg);
        }
        catch (Exception ex)
        {
            var detail = ex.InnerException?.Message;
            var msg    = string.IsNullOrEmpty(detail) ? ex.Message : $"{ex.Message} — {detail}";
            return new(false, Error: $"Error: {msg}");
        }
    }

    /// <summary>
    /// Installation dédiée "Unreal Speccy" — voir le commentaire dans InstallAsync : toujours
    /// via UnrealSpeccyClassicBuildService (site DemoBase), jamais via GitHub/Strategy. Écrase
    /// systématiquement le contenu du dossier, y compris s'il contient déjà (par erreur) le
    /// build SDL2 récupéré via un précédent téléchargement GitHub.
    /// </summary>
    private async Task<InstallResult> InstallUnrealSpeccyClassicAsync(
        EmulatorDownloadEntry entry,
        IProgress<(string Message, int Percent)>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report(($"Téléchargement du build classique {entry.DisplayName}…", 15));

            var destDir = Path.Combine(GetRoot(entry.RootFolder), entry.FolderName);
            var (success, message) = await new UnrealSpeccyClassicBuildService()
                .DownloadAndInstallAsync(destDir, ct);

            if (!success)
                return new(false, Error: message);

            var downloadUrl = $"{UnrealSpeccyClassicBuildService.MegaFolderUrl} ({UnrealSpeccyClassicBuildService.MegaSubFolder}/{UnrealSpeccyClassicBuildService.ZipFileName})";
            await SaveVersionAsync(entry.FolderName, UnrealSpeccyClassicBuildService.Version, downloadUrl, entry.RootFolder);

            progress?.Report(($"{entry.DisplayName} {UnrealSpeccyClassicBuildService.Version} installé.", 100));
            return new(true, UnrealSpeccyClassicBuildService.Version);
        }
        catch (OperationCanceledException)
        {
            return new(false, Error: "Installation annulée.");
        }
        catch (Exception ex)
        {
            return new(false, Error: $"Erreur : {ex.Message}");
        }
    }

    /// <summary>Vérifie si une mise à jour est disponible sans télécharger.</summary>
    public async Task<UpdateInfo> CheckUpdateAsync(
        EmulatorDownloadEntry entry, CancellationToken ct = default)
    {
        // "Unreal Speccy" : build classique à version fixe (0.37.9, hébergé sur le site, cf.
        // InstallUnrealSpeccyClassicAsync) — jamais de vérification GitHub, qui ne connaît
        // plus que le portage SDL2 incompatible et signalerait à tort une "mise à jour".
        if (string.Equals(entry.FolderName, "Unreal Speccy", StringComparison.OrdinalIgnoreCase))
        {
            var installedClassic = await LoadVersionAsync(entry.FolderName, entry.RootFolder);
            return new(false, UnrealSpeccyClassicBuildService.Version, installedClassic?.Version, null);
        }

        try
        {
            var installed = await LoadVersionAsync(entry.FolderName, entry.RootFolder);
            var (downloadUrl, latestVersion) = entry.Strategy switch
            {
                DownloadStrategy.GitHub      => await ResolveGitHubAsync(entry, ct),
                DownloadStrategy.SourceForge => ResolveSourceForge(entry),
                DownloadStrategy.DirectUrl   => (entry.Source, entry.VersionOverride ?? ParseVersionFromUrl(entry.Source)),
                DownloadStrategy.PageScrape  => await ResolvePageScrapeAsync(entry, ct),
                _ => (null, null),
            };

            // Mettre à jour le timestamp de vérification
            if (installed != null)
            {
                installed.LastChecked = DateTime.UtcNow;
                await SaveVersionAsync(entry.FolderName, installed.Version, installed.DownloadUrl, entry.RootFolder);
            }

            bool updateAvailable = installed != null
                && !string.IsNullOrEmpty(latestVersion)
                && latestVersion != installed.Version;

            return new(updateAvailable, latestVersion, installed?.Version, downloadUrl);
        }
        catch
        {
            return new(false, null, null, null);
        }
    }

    /// <summary>Retourne la version installée, ou null si non installé.</summary>
    public async Task<InstalledEmulatorVersion?> GetInstalledVersionAsync(string folderName, string rootFolder = "Emus")
        => await LoadVersionAsync(folderName, rootFolder);

    /// <summary>Vérifie si l'émulateur est installé (dossier + exe présents).</summary>
    public bool IsInstalled(EmulatorDownloadEntry entry)
    {
        var root = GetRoot(entry.RootFolder);

        // 1. Dossier exact (cas normal après installation par DemoBase : Emus\Ryujinx\)
        if (ContainsExe(Path.Combine(root, entry.FolderName))) return true;

        // 2. Dossier dont le nom COMMENCE par FolderName (insensible à la casse) :
        //    ex. "ryujinx-1.3.3-win_x64" quand FolderName = "Ryujinx".
        //    Couvre les installations manuelles qui gardent le nom du zip/archive.
        if (Directory.Exists(root))
        {
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (name.StartsWith(entry.FolderName, StringComparison.OrdinalIgnoreCase)
                        && ContainsExe(dir)) return true;
                }
            }
            catch { /* accès refusé → ignorer */ }
        }

        return false;
    }

    private static bool ContainsExe(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        try { return Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories).Length > 0; }
        catch { return false; }
    }

    // ── Résolution GitHub ─────────────────────────────────────────────────────

    private static async Task<(string Url, string Version)> ResolveGitHubAsync(
        EmulatorDownloadEntry entry, CancellationToken ct)
    {
        // /releases/latest exclut les pre-releases — plusieurs émulateurs (CXBX-Reloaded,
        // Xenia Canary…) ne publient QUE des pre-releases et n'ont jamais de "latest" stable,
        // ce qui renvoie un 404. On utilise donc /releases (liste complète, triée du plus
        // récent au plus ancien, pre-releases incluses) et on prend la première qui a un
        // asset Windows exploitable.
        // per_page=100 (maximum autorisé par l'API GitHub) — certains projets
        // publient des dizaines de releases entre deux builds Windows (ex.
        // ZXTune, désormais centré sur l'Android, où les releases récentes ne
        // contiennent souvent que des APK/XAPK). 30 ne suffisait pas toujours
        // à remonter jusqu'à la dernière release avec un zip Windows.
        var apiUrl = $"https://api.github.com/repos/{entry.Source}/releases?per_page=100";

        using var apiResponse = await _http.GetAsync(apiUrl, ct);
        if (apiResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // GitHub limite les appels API anonymes à 60/heure. Avec ~28 émulateurs
            // dont plusieurs sur GitHub, quelques sessions de test suffisent à
            // l'atteindre — message explicite plutôt qu'un "403" cryptique.
            var remaining = apiResponse.Headers.TryGetValues("X-RateLimit-Remaining", out var v)
                ? v.FirstOrDefault() : null;
            var resetMsg = "";
            if (apiResponse.Headers.TryGetValues("X-RateLimit-Reset", out var resetVals)
                && long.TryParse(resetVals.FirstOrDefault(), out var resetUnix))
            {
                var resetTime = DateTimeOffset.FromUnixTimeSeconds(resetUnix).ToLocalTime();
                resetMsg = $" Resets at {resetTime:HH:mm}.";
            }
            throw new InvalidOperationException(
                $"Limite de l'API GitHub atteinte (quota anonyme : 60 requêtes/heure, " +
                $"remaining: {remaining ?? "0"}).{resetMsg} Please try again later.");
        }
        apiResponse.EnsureSuccessStatusCode();
        var json   = await apiResponse.Content.ReadAsStringAsync(ct);
        var releases = JsonNode.Parse(json)?.AsArray() ?? [];

        // Chercher l'asset Windows correspondant au pattern dans chaque release.
        //
        // PASSE 1 (stricte) : si un pattern est spécifié, on ne l'accepte que
        // s'il matche EXACTEMENT dans une release donnée — sinon on passe à la
        // release suivante SANS fallback local. C'est ce qui évite de tomber
        // sur les releases "-osfree" de DOSBox-X (qui n'ont pas les variantes
        // lowend mais ont des assets mingw64 "x64" qu'un fallback trop
        // permissif attraperait à tort).
        string? strictMatchUrl = null;
        string  strictMatchTag = "";

        foreach (var release in releases)
        {
            var tag    = release?["tag_name"]?.GetValue<string>() ?? "unknown";
            var assets = release?["assets"]?.AsArray() ?? [];
            var usableAssets = FilterUsableAssets(assets);

            if (!string.IsNullOrEmpty(entry.AssetPattern))
            {
                foreach (var asset in usableAssets)
                {
                    var n = asset?["name"]?.GetValue<string>() ?? "";
                    if (MatchesPattern(n, entry.AssetPattern))
                    {
                        strictMatchUrl = asset?["browser_download_url"]?.GetValue<string>();
                        strictMatchTag = tag;
                        break;
                    }
                }
                if (strictMatchUrl != null) break;
            }
            else
            {
                var url = FirstLooseZip(usableAssets);
                if (url != null) return (url, tag.TrimStart('v'));
            }
        }

        if (strictMatchUrl != null)
            return (strictMatchUrl, strictMatchTag.TrimStart('v'));

        // PASSE 2 (fallback) : aucune release n'a satisfait le pattern exact —
        // plutôt que d'échouer complètement (régression vécue avec PPSSPP,
        // puNES, Stella, TIC-80, Xenia Canary : leur AssetPattern ne matchait
        // plus exactement le nommage réel), on retombe sur "premier zip
        // Windows plausible" dans la release la plus récente qui en propose un.
        // Moins précis qu'un match exact, mais mieux qu'un échec sec — et sans
        // risque de repiquer le cas DOSBox-X puisqu'on ne l'atteint QUE si la
        // passe stricte n'a absolument rien trouvé.
        foreach (var release in releases)
        {
            var tag    = release?["tag_name"]?.GetValue<string>() ?? "unknown";
            var assets = release?["assets"]?.AsArray() ?? [];
            var usableAssets = FilterUsableAssets(assets);

            var url = FirstLooseZip(usableAssets);
            if (url != null) return (url, tag.TrimStart('v'));
        }

        return ("", "");
    }

    /// <summary>Exclut systématiquement : archives de symboles (-pdb, .pdb),
    /// builds ARM64, builds lowend9x (Win9x/NT4 uniquement, incompatibles
    /// Windows 10+).</summary>
    private static JsonNode?[] FilterUsableAssets(JsonArray assets) => assets
        .Where(a =>
        {
            var n = a?["name"]?.GetValue<string>() ?? "";
            return !n.Contains("pdb",      StringComparison.OrdinalIgnoreCase)
                && !n.Contains("arm64",    StringComparison.OrdinalIgnoreCase)
                && !n.Contains("lowend9x", StringComparison.OrdinalIgnoreCase);
        })
        .ToArray();

    /// <summary>"Premier zip Windows plausible" : contient "win" ou "x64" ; à
    /// défaut, premier zip tout court. Utilisé en absence de pattern, ou en
    /// dernier recours si le pattern ne matche jamais nulle part.</summary>
    private static string? FirstLooseZip(JsonNode?[] usableAssets)
    {
        foreach (var asset in usableAssets)
        {
            var n = asset?["name"]?.GetValue<string>() ?? "";
            if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                && (n.Contains("win", StringComparison.OrdinalIgnoreCase)
                    || n.Contains("x64", StringComparison.OrdinalIgnoreCase)))
                return asset?["browser_download_url"]?.GetValue<string>();
        }
        foreach (var asset in usableAssets)
        {
            var n = asset?["name"]?.GetValue<string>() ?? "";
            if (n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return asset?["browser_download_url"]?.GetValue<string>();
        }
        return null;
    }

    private static string? FindWindowsAsset(JsonArray assets, string assetPattern)
        => throw new InvalidOperationException("FindWindowsAsset remplacé par logique inline dans ResolveGitHubAsync");

    // ── Post-install batch ────────────────────────────────────────────────────

    /// <summary>
    /// Cherche un install.bat dans destDir et ses sous-dossiers directs,
    /// et l'exécute si trouvé. Utilisé par ex. par DCMOTO qui distribue
    /// l'exe en deux fichiers .dat à concaténer via un install.bat.
    /// </summary>

    /// vers Emus\WinUAE\Configs\ après l'installation de WinUAE.
    /// </summary>


    /// Appelé aussi au démarrage si WinUAE était déjà installé.
    /// </summary>

    private static async Task RunPostInstallBatAsync(
        string destDir, IProgress<(string, int)>? progress, CancellationToken ct)
    {
        // Chercher tous les install.bat dans destDir et sous-dossiers
        var bats = Directory.GetFiles(destDir, "install.bat", SearchOption.AllDirectories);

        if (bats.Length == 0) return;

        foreach (var bat in bats)
        {
            // Supprimer la ligne "pause" du .bat pour éviter un blocage interactif
            try
            {
                var batContent = await File.ReadAllTextAsync(bat, ct);
                var patched    = System.Text.RegularExpressions.Regex.Replace(
                    batContent, @"^\s*pause\s*$", "",
                    System.Text.RegularExpressions.RegexOptions.Multiline |
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                await File.WriteAllTextAsync(bat, patched, ct);
            }
            catch { /* non bloquant */ }

            progress?.Report(($"Running post-install: {Path.GetDirectoryName(bat)}…", 90));
            System.Diagnostics.Debug.WriteLine($"[Installer] Running post-install: {bat}");

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName         = bat,
                WorkingDirectory = Path.GetDirectoryName(bat)!,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };

            using var proc = System.Diagnostics.Process.Start(psi)!;
            await proc.WaitForExitAsync(ct);
            System.Diagnostics.Debug.WriteLine($"[Installer] install.bat exit: {proc.ExitCode}");
        }
    }

    // ── Résolution SourceForge ────────────────────────────────────────────────

    private static (string Url, string Version) ResolveSourceForge(EmulatorDownloadEntry entry)
    {
        // SourceForge redirect /files/latest/download → dernier fichier uploadé
        var url = $"https://sourceforge.net/projects/{entry.Source}/files/latest/download";
        return (url, "latest");
    }

    // ── Résolution PageScrape ─────────────────────────────────────────────────

    private async Task<(string Url, string Version)> ResolvePageScrapeAsync(
        EmulatorDownloadEntry entry, CancellationToken ct)
    {
        // Télécharge la page HTML et cherche un lien href correspondant à AssetPattern
        var html = await _http.GetStringAsync(entry.Source, ct);

        // Convertir AssetPattern (glob) en regex simple : * → .*
        var pattern = System.Text.RegularExpressions.Regex.Escape(entry.AssetPattern)
            .Replace("\\*", ".*");
        var regex   = new System.Text.RegularExpressions.Regex(
            $"href=\"([^\"]*{pattern})\"",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        var match = regex.Match(html);
        if (!match.Success)
            throw new InvalidOperationException(
                $"PageScrape: aucun lien trouvé pour le pattern '{entry.AssetPattern}' sur {entry.Source}");

        var href = match.Groups[1].Value;
        // Résoudre les URL relatives
        var baseUri = new Uri(entry.Source);
        var url     = new Uri(baseUri, href).ToString();
        var version = ParseVersionFromUrl(url);
        System.Diagnostics.Debug.WriteLine($"[PageScrape] {entry.FolderName} → {url} (v{version})");
        return (url, version);
    }

    // ── Téléchargement ────────────────────────────────────────────────────────

    private static async Task<string> DownloadArchiveAsync(
        string url, string folderName,
        IProgress<(string, int)>? progress, CancellationToken ct)
        => await DownloadArchiveAsync(url, folderName, progress, ct, allowSourceForgeHtmlFallback: true);

    private static async Task<string> DownloadArchiveAsync(
        string url, string folderName,
        IProgress<(string, int)>? progress, CancellationToken ct,
        bool allowSourceForgeHtmlFallback)
    {
        var tmpDir  = Path.Combine(Path.GetTempPath(), "DemoBaseInstall");
        Directory.CreateDirectory(tmpDir);
        var tmpFile = Path.Combine(tmpDir, $"{folderName}_{Guid.NewGuid():N}.zip");

        using var response = await GetFollowingRedirectsAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        // 2026-08-21, retour utilisateur : plusieurs entrées SourceForge (EightyOne, Fuse,
        // Hatari, VICE, Handy) renvoyaient un HTTP 200 "réussi" mais dont le corps était en
        // fait une page HTML (interstitiel/erreur du CDN de mirroir), détecté seulement
        // après coup par ExtractSevenZipFlat avec un message peu clair ("Aucune signature
        // d'archive 7-Zip trouvée"). On vérifie maintenant le Content-Type dès les en-têtes
        // reçus — si le serveur annonce explicitement du HTML/texte au lieu d'un binaire, on
        // échoue tout de suite avec un message qui pointe directement vers la vraie cause,
        // sans même télécharger le corps. Ce contrôle est best-effort : certains serveurs
        // mal configurés renvoient un Content-Type générique/absent même pour du vrai HTML,
        // auquel cas ce garde-fou ne se déclenche pas et l'erreur d'extraction habituelle
        // (avec l'URL) prend le relais comme avant.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is "text/html" or "text/plain" or "application/xhtml+xml")
        {
            // 2026-08-21 : pour SourceForge spécifiquement, la page interstitielle "Your
            // download will start shortly..." contient — pour les clients sans JavaScript
            // (accessibilité/SEO) — un lien de secours direct vers le vrai fichier sur le
            // CDN de mirroir (dl.sourceforge.net) ou une balise <meta http-equiv="refresh">.
            // Avant d'abandonner, on tente d'extraire ce lien du HTML reçu et de relancer le
            // téléchargement dessus UNE fois (allowSourceForgeHtmlFallback évite toute boucle
            // si jamais cette seconde URL renvoie elle aussi du HTML).
            if (allowSourceForgeHtmlFallback && IsSourceForgeHost(url))
            {
                var html = await response.Content.ReadAsStringAsync(ct);
                var extractedUrl = TryExtractSourceForgeRealDownloadUrl(html, url);
                if (extractedUrl != null)
                {
                    PerfLogger.Mark($"SOURCEFORGE: page interstitielle détectée pour '{url}' — nouvelle tentative via '{extractedUrl}'");
                    return await DownloadArchiveAsync(extractedUrl, folderName, progress, ct, allowSourceForgeHtmlFallback: false);
                }
            }

            throw new InvalidDataException(
                $"le serveur a répondu avec du contenu texte/HTML (Content-Type: {mediaType}) " +
                "au lieu du fichier binaire attendu — probablement une page d'erreur ou un " +
                "interstitiel de sélection de mirroir plutôt que le vrai téléchargement.");
        }

        var total = response.Content.Headers.ContentLength ?? 0L;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        await using var file   = File.Create(tmpFile);

        var buffer    = new byte[81920];
        long downloaded = 0;
        int  read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int pct = (int)(15 + 60 * downloaded / total);
                progress?.Report(($"Downloading… {downloaded / 1024 / 1024} MB / {total / 1024 / 1024} MB", pct));
            }
        }

        return tmpFile;
    }

    /// <summary>
    /// Cherche, dans le HTML de la page interstitielle SourceForge "Your download will
    /// start shortly...", une URL de téléchargement direct exploitable sans JavaScript :
    /// en priorité une balise &lt;meta http-equiv="refresh" content="N;url=..."&gt;, sinon
    /// le premier lien pointant vers un CDN de mirroir SourceForge
    /// (*.dl.sourceforge.net / downloads.sourceforge.net). Retourne null si rien trouvé
    /// (page vraiment vide de fallback non-JS, ou structure de page différente de celle
    /// observée le 2026-08-21).
    /// </summary>
    private static string? TryExtractSourceForgeRealDownloadUrl(string html, string pageUrl)
    {
        var metaRefresh = Regex.Match(html,
            @"<meta[^>]+http-equiv\s*=\s*[""']refresh[""'][^>]*content\s*=\s*[""'][^;]*;\s*url\s*=\s*([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (metaRefresh.Success)
        {
            var refreshUrl = System.Net.WebUtility.HtmlDecode(metaRefresh.Groups[1].Value.Trim());
            return Uri.TryCreate(refreshUrl, UriKind.Absolute, out _)
                ? refreshUrl
                : new Uri(new Uri(pageUrl), refreshUrl).ToString();
        }

        var mirrorLink = Regex.Match(html,
            @"https?://[a-z0-9.-]*(?:dl\.sourceforge\.net|downloads\.sourceforge\.net)/[^""'\s<>]+",
            RegexOptions.IgnoreCase);
        return mirrorLink.Success ? System.Net.WebUtility.HtmlDecode(mirrorLink.Value) : null;
    }

    // ── Extraction plate ──────────────────────────────────────────────────────

    /// <summary>
    /// Extrait le ZIP dans destDir en supprimant le dossier racine s'il existe
    /// (ex: "stella-6.7-win64/stella.exe" → destDir/stella.exe).
    /// Les sous-dossiers internes sont préservés.
    /// </summary>
    public static void ExtractFlat(string zipPath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var zip = ZipFile.OpenRead(zipPath);

        // Ignorer les artefacts macOS (__MACOSX/, ._*) — fréquents dans les zips
        // multi-plateformes (ex. KEGS) et inutiles sous Windows.
        var realEntries = zip.Entries
            .Where(e => !e.FullName.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase)
                     && !Path.GetFileName(e.FullName).StartsWith("._"))
            .ToList();

        // Trouver le préfixe commun (dossier racine du ZIP)
        var commonPrefix = FindCommonPrefix(realEntries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .Select(e => e.FullName));

        foreach (var entry in realEntries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // dossier

            // Calculer le chemin relatif en supprimant le préfixe commun
            var relative = entry.FullName;
            if (!string.IsNullOrEmpty(commonPrefix) && relative.StartsWith(commonPrefix))
                relative = relative[commonPrefix.Length..];

            relative = relative.Replace('/', Path.DirectorySeparatorChar)
                               .TrimStart(Path.DirectorySeparatorChar);
            if (string.IsNullOrEmpty(relative)) continue;

            // Sanitizer les caractères invalides sous Windows (':', '*', '?', '"',
            // '<', '>', '|') — certains zips créés sous macOS/Linux en contiennent
            // (resource forks, attributs étendus, noms avec ':') qui font échouer
            // File.Create avec "La syntaxe du nom de fichier… n'est pas correcte".
            var segments = relative.Split(Path.DirectorySeparatorChar);
            for (int i = 0; i < segments.Length; i++)
                foreach (var bad in InvalidWindowsChars)
                    segments[i] = segments[i].Replace(bad, '_');
            relative = string.Join(Path.DirectorySeparatorChar, segments);

            var destPath = Path.GetFullPath(Path.Combine(destDir, relative));
            if (!destPath.StartsWith(Path.GetFullPath(destDir)))
                continue; // path traversal → ignorer

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                entry.ExtractToFile(destPath, overwrite: true);
            }
            catch (IOException)
            {
                // Une entrée individuelle invalide (nom toujours problématique après
                // sanitization, chemin trop long…) ne doit pas faire échouer toute
                // l'installation — on l'ignore et on continue les autres fichiers.
            }
        }
    }

    private static readonly char[] InvalidWindowsChars = [':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Extrait une archive 7-Zip — y compris un exécutable auto-extractible (SFX,
    /// ex. « mameXXXX_64bit.exe ») — dans destDir, en préservant l'arborescence
    /// interne (indispensable pour MAME : hash/, plugins/, roms/, bgfx/…).
    ///
    /// SevenZipExtractor devine le format d'après l'extension : on présente donc
    /// le fichier sous une COPIE renommée « .7z » pour forcer le handler 7-Zip,
    /// qui sait localiser la charge 7z placée après le stub PE d'un SFX.
    /// </summary>
    public static void ExtractSevenZipFlat(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);

        // Un exécutable auto-extractible 7-Zip (SFX, ex. MAME) = stub PE (« MZ »)
        // suivi de l'archive 7z. Le handler 7z de 7z.dll exige la signature 7z au
        // TOUT DÉBUT du flux (il ne la cherche pas), donc on ne peut pas simplement
        // renommer le .exe en .7z. On localise la signature 7z et on découpe la
        // charge (de la signature à la fin) dans un .7z temporaire autonome, qui
        // s'ouvre alors normalement. Pour un vrai .7z, la signature est à l'offset 0
        // et on l'ouvre directement.
        string sevenZipPath = archivePath;
        bool   tempFile     = false;

        long sigOffset = FindSevenZipSignature(archivePath);
        if (sigOffset < 0)
            throw new InvalidDataException("Aucune signature d'archive 7-Zip trouvée dans le fichier.");

        if (sigOffset > 0
            || !Path.GetExtension(archivePath).Equals(".7z", StringComparison.OrdinalIgnoreCase))
        {
            sevenZipPath = Path.Combine(
                Path.GetDirectoryName(archivePath)!,
                Path.GetFileNameWithoutExtension(archivePath) + ".payload.7z");
            using (var src = File.OpenRead(archivePath))
            using (var dst = File.Create(sevenZipPath))
            {
                src.Position = sigOffset;
                src.CopyTo(dst);
            }
            tempFile = true;
        }

        try
        {
            using var sevenZip = new ArchiveFile(sevenZipPath);

            // Préfixe racine commun (comme ExtractFlat). MAME n'en a pas (mame.exe
            // + hash/ + plugins/… à la racine) → rien n'est retiré, tout préservé.
            // (Énumérer les métadonnées est bon marché ; c'est le décodage qui coûte.)
            var commonPrefix = FindCommonPrefix(
                sevenZip.Entries.Where(e => !e.IsFolder)
                                .Select(e => e.FileName.Replace('\\', '/')));

            var destFull = Path.GetFullPath(destDir);

            // Extraction EN UN SEUL PASSAGE : SevenZipExtractor décode l'archive une
            // seule fois et écrit tous les fichiers au fil de l'eau. Indispensable
            // pour un 7z SOLIDE avec des dizaines de milliers de petits fichiers
            // (hash/, plugins/…) : l'ancienne extraction fichier-par-fichier
            // (entry.Extract) redécompressait le bloc solide depuis le début à chaque
            // fichier → O(n²), plusieurs minutes. Le callback mappe chaque entrée
            // vers son chemin de destination (ou null pour l'ignorer).
            sevenZip.Extract(entry =>
            {
                if (entry.IsFolder) return null;

                var relative = entry.FileName.Replace('\\', '/');
                if (!string.IsNullOrEmpty(commonPrefix) && relative.StartsWith(commonPrefix))
                    relative = relative[commonPrefix.Length..];

                relative = relative.Replace('/', Path.DirectorySeparatorChar)
                                   .TrimStart(Path.DirectorySeparatorChar);
                if (string.IsNullOrEmpty(relative)) return null;

                var segments = relative.Split(Path.DirectorySeparatorChar);
                for (int i = 0; i < segments.Length; i++)
                    foreach (var bad in InvalidWindowsChars)
                        segments[i] = segments[i].Replace(bad, '_');
                relative = string.Join(Path.DirectorySeparatorChar, segments);

                var destPath = Path.GetFullPath(Path.Combine(destDir, relative));
                if (!destPath.StartsWith(destFull))
                    return null; // path traversal → ignorer

                // Créer le dossier parent avant d'écrire (le callback fournit juste
                // le chemin cible ; SevenZipExtractor y écrit le flux).
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                return destPath;
            });
        }
        finally
        {
            if (tempFile) { try { File.Delete(sevenZipPath); } catch { } }
        }
    }

    /// <summary>
    /// Cherche la signature de début d'archive 7-Zip (37 7A BC AF 27 1C) dans le
    /// fichier et renvoie son offset absolu, ou -1 si absente. Lecture par blocs
    /// avec chevauchement pour ne pas manquer une signature à cheval sur deux blocs.
    /// </summary>
    private static long FindSevenZipSignature(string path)
    {
        byte[] sig = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        int overlap = sig.Length - 1;

        using var fs = File.OpenRead(path);
        byte[] buf = new byte[(1 << 20) + overlap];   // 1 MiB + chevauchement
        long absStart = 0;                             // position absolue de buf[0]
        int  have     = 0;

        while (true)
        {
            int read = fs.Read(buf, have, buf.Length - have);
            int total = have + read;
            int limit = total - sig.Length + 1;

            for (int i = 0; i < limit; i++)
            {
                int j = 0;
                while (j < sig.Length && buf[i + j] == sig[j]) j++;
                if (j == sig.Length) return absStart + i;
            }

            if (read == 0) break; // EOF atteint

            int keep = Math.Min(overlap, total);
            Array.Copy(buf, total - keep, buf, 0, keep);
            absStart += total - keep;
            have = keep;
        }
        return -1;
    }

    private static string FindCommonPrefix(IEnumerable<string> paths)
    {
        var list = paths.ToList();
        if (list.Count == 0) return "";

        // Vérifier si tous les chemins commencent par le même segment
        var firstSlash = list[0].IndexOfAny(['/', '\\']);
        if (firstSlash <= 0) return "";

        var candidate = list[0][..(firstSlash + 1)];
        return list.All(p => p.StartsWith(candidate)) ? candidate : "";
    }

    // ── Persistance des versions ──────────────────────────────────────────────

    private static async Task<InstalledEmulatorVersion?> LoadVersionAsync(string folderName, string rootFolder)
    {
        var versionsFile = VersionsFile(rootFolder);
        if (!File.Exists(versionsFile)) return null;
        try
        {
            var json = await File.ReadAllTextAsync(versionsFile);
            var dict = JsonSerializer.Deserialize<Dictionary<string, InstalledEmulatorVersion>>(json);
            return dict?.GetValueOrDefault(folderName);
        }
        catch { return null; }
    }

    private static async Task SaveVersionAsync(string folderName, string version, string downloadUrl, string rootFolder)
    {
        var root         = GetRoot(rootFolder);
        var versionsFile = VersionsFile(rootFolder);
        Directory.CreateDirectory(root);
        Dictionary<string, InstalledEmulatorVersion> dict;
        try
        {
            if (File.Exists(versionsFile))
            {
                var existing = await File.ReadAllTextAsync(versionsFile);
                dict = JsonSerializer.Deserialize<Dictionary<string, InstalledEmulatorVersion>>(existing)
                       ?? [];
            }
            else dict = [];
        }
        catch { dict = []; }

        if (dict.TryGetValue(folderName, out var existing2))
        {
            existing2.Version      = version;
            existing2.LastChecked  = DateTime.UtcNow;
            existing2.DownloadUrl  = downloadUrl;
        }
        else
        {
            dict[folderName] = new InstalledEmulatorVersion
            {
                Version      = version,
                InstalledAt  = DateTime.UtcNow,
                LastChecked  = DateTime.UtcNow,
                DownloadUrl  = downloadUrl,
            };
        }

        await File.WriteAllTextAsync(versionsFile, JsonSerializer.Serialize(dict, _json));
    }

    /// <summary>Vérifie la signature "PK" en tête de fichier (ZIP) avant de tenter l'extraction.</summary>
    private static bool IsValidZip(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 4) return false;
            Span<byte> header = stackalloc byte[4];
            if (fs.Read(header) != 4) return false;
            return header[0] == 0x50 && header[1] == 0x4B; // "PK"
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Matching simple type glob : '*' correspond à n'importe quelle séquence de caractères.
    /// Insensible à la casse.
    /// </summary>
    private static bool MatchesPattern(string name, string pattern)
    {
        var regex = "^" + Regex.Escape(pattern)
            .Replace("\\*", ".*")
            .Replace("\\?", ".") + "$";
        return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase);
    }

    private static string ParseVersionFromUrl(string url)
    {
        // Tente d'extraire un numéro de version de l'URL (ex. "Altirra4.40.zip" → "4.40")
        var m = Regex.Match(Path.GetFileName(url), @"(\d+[\.\d]+)");
        return m.Success ? m.Groups[1].Value : "unknown";
    }
}

/// <summary>
/// Lancée quand l'émulateur nécessite un téléchargement manuel (hotlink bloqué).
/// L'URL contient la page officielle à ouvrir dans le navigateur.
/// </summary>
public class ManualDownloadRequiredException : Exception
{
    public string PageUrl    { get; }
    public string EmuName    { get; }

    public ManualDownloadRequiredException(string pageUrl, string emuName)
        : base($"{emuName} doit être téléchargé manuellement depuis {pageUrl}")
    {
        PageUrl = pageUrl;
        EmuName = emuName;
    }
}
