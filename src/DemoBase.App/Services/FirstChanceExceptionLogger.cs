using System;
using System.IO;
using System.Linq;

namespace DemoBase.App.Services;

/// <summary>
/// 2026-07-30, retour utilisateur : "je vois souvent ce genre d'exception dans visual
/// studio [Exception levée : 'System.IO.IOException' dans System.Net.Security.dll /
/// System.Net.Sockets.dll]. possible de les logger pour savoir si ce sont de vrais
/// problèmes ou non ? (à logguer uniquement en environnement de debug)".
///
/// Les lignes "Exception levée : ..." que Visual Studio affiche dans sa fenêtre Sortie
/// sont des "first chance exceptions" : le CLR les signale dès qu'elles sont LEVÉES,
/// AVANT de savoir si un bloc catch plus haut va les intercepter et les gérer. Une
/// IOException issue de System.Net.Security/System.Net.Sockets est très
/// vraisemblablement levée en interne par HttpClient/SslStream (ex. le serveur ferme
/// une connexion keep-alive, ou la réinitialise pendant une lecture) — un événement
/// réseau banal que HttpClient encaisse déjà tout seul (retry, nouvelle connexion) ;
/// mais sans plus de contexte, impossible de savoir si c'est vraiment le cas ou si ça
/// correspond à un téléchargement qui échoue pour de bon.
///
/// Ce logger capture CHAQUE IOException levée n'importe où dans le process
/// (AppDomain.FirstChanceException — pas seulement les IOException non gérées) et
/// l'écrit dans "log_first_chance_io_exceptions.txt" à côté de l'exécutable, avec
/// l'heure, le message, l'exception interne éventuelle et les premières frames de la
/// pile — de quoi corréler après coup avec un téléchargement en échec dans
/// l'application (ou constater qu'aucun échec ne leur correspond, confirmant que ce
/// n'est que du bruit réseau normal déjà géré). Plafonné à 500 entrées par session
/// pour ne jamais faire grossir le fichier indéfiniment si une rafale d'exceptions
/// survient (ex. gros lot de téléchargements avec beaucoup de retries).
///
/// Actif UNIQUEMENT en configuration Debug (DebugHelper.IsDebugMode) : un handler
/// FirstChanceException s'exécute pour TOUTE exception levée dans tout le process, y
/// compris celles déjà gérées ailleurs — un coût qu'on ne veut pas payer en Release.
/// </summary>
public static class FirstChanceExceptionLogger
{
    private const int MaxLoggedEntries = 500;
    private static int  _loggedCount;
    private static bool _suppressionNoted;
    private static readonly object _lock = new();

    private static string LogPath =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "log_first_chance_io_exceptions.txt");

    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        if (!DemoBase.App.DebugHelper.IsDebugMode) return;

        _initialized = true;
        AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
        System.Diagnostics.Debug.WriteLine(
            "[FIRSTCHANCE] Logger IOException actif (mode Debug) — voir log_first_chance_io_exceptions.txt");
    }

    private static void OnFirstChanceException(
        object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
    {
        // Uniquement les IOException — celles observées par l'utilisateur dans la
        // fenêtre Sortie de Visual Studio (System.Net.Security.dll/System.Net.Sockets.dll).
        // D'autres types (ex. exceptions applicatives déjà loguées ailleurs via
        // Debug.WriteLine, comme [SCANROMS]/[MATCH]/[BUILD]) ne sont pas concernés ici.
        if (e.Exception is not IOException ex) return;

        lock (_lock)
        {
            if (_loggedCount >= MaxLoggedEntries)
            {
                if (!_suppressionNoted)
                {
                    _suppressionNoted = true;
                    TryAppend($"[{DateTime.Now:HH:mm:ss.fff}] --- {MaxLoggedEntries} IOException loguées, " +
                              "suppression des suivantes pour cette session (évite un fichier sans limite) ---" +
                              Environment.NewLine);
                }
                return;
            }
            _loggedCount++;
        }

        string topFrames;
        try
        {
            var frames = new System.Diagnostics.StackTrace(ex, false).GetFrames();
            topFrames = frames == null
                ? "(pile indisponible)"
                : string.Join(" | ", frames.Take(3).Select(f =>
                    $"{f.GetMethod()?.DeclaringType?.FullName}.{f.GetMethod()?.Name}"));
        }
        catch { topFrames = "(pile indisponible)"; }

        var innerInfo = ex.InnerException != null
            ? $" — inner: {ex.InnerException.GetType().Name} : {ex.InnerException.Message}"
            : "";

        var line = $"[{DateTime.Now:HH:mm:ss.fff}] IOException : {ex.Message}{innerInfo}{Environment.NewLine}" +
                   $"    via: {topFrames}{Environment.NewLine}";

        System.Diagnostics.Debug.WriteLine($"[FIRSTCHANCE] {ex.Message}{innerInfo} — via {topFrames}");
        TryAppend(line);
    }

    private static void TryAppend(string line)
    {
        try { File.AppendAllText(LogPath, line); }
        catch { /* non bloquant — pas grave si le fichier n'est pas accessible */ }
    }
}
