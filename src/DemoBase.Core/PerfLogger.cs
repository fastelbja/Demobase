using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.Core.Diagnostics;

/// <summary>
/// Logger de performance léger — mesure les durées des opérations clés et les
/// écrit dans un fichier texte sans bloquer le thread UI.
///
/// Usage :
///   using var op = PerfLogger.Begin("LoadReleases");
///   // ... opération ...
///   // Dispose() à la fin du bloc → écrit la durée dans le fichier.
///
/// Ou avec une section nommée explicite :
///   PerfLogger.Log("GetAllReleases DB", sw.ElapsedMilliseconds);
///
/// Le fichier est écrit dans Working/perf_log.txt (à côté de la DB).
/// Il est recréé à chaque démarrage de l'application.
/// </summary>
public static class PerfLogger
{
    private static readonly string _logPath = Path.Combine(
        AppContext.BaseDirectory, "Working", "perf_log.txt");

    // File d'écriture non-bloquante : les messages sont enqueués et écrits
    // par un Task de fond → le thread UI n'attend jamais l'I/O disque.
    private static readonly ConcurrentQueue<string> _queue = new();
    private static readonly SemaphoreSlim _signal = new(0);
    private static bool _initialized;

    // ── Initialisation ────────────────────────────────────────────────────────

    /// <summary>
    /// Démarre le thread de fond et crée/écrase le fichier de log.
    /// À appeler une fois au démarrage de l'application.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
        File.WriteAllText(_logPath,
            $"=== DemoBase Performance Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===\r\n\r\n");

        // Thread de fond dédié à l'écriture — daemon (ne bloque pas la fermeture de l'app).
        var t = new Thread(WriterLoop) { IsBackground = true, Name = "PerfLogger" };
        t.Start();
    }

    // ── API publique ──────────────────────────────────────────────────────────

    /// <summary>
    /// Ouvre un bloc de mesure. La durée est loggée automatiquement au Dispose().
    /// </summary>
    public static PerfScope Begin(string operationName)
        => new(operationName);

    /// <summary>
    /// Log une durée déjà mesurée.
    /// </summary>
    public static void Log(string operationName, long elapsedMs, string? detail = null)
    {
        var msg = detail == null
            ? $"[{DateTime.Now:HH:mm:ss.fff}] {operationName,-55} {elapsedMs,6} ms"
            : $"[{DateTime.Now:HH:mm:ss.fff}] {operationName,-55} {elapsedMs,6} ms  ({detail})";
        Enqueue(msg);
    }

    /// <summary>
    /// Log un message libre (sans durée) — utile pour marquer un début d'étape.
    /// </summary>
    public static void Mark(string message)
        => Enqueue($"[{DateTime.Now:HH:mm:ss.fff}] >>> {message}");

    /// <summary>
    /// Insère une ligne vide de séparation dans le log.
    /// </summary>
    public static void Separator()
        => Enqueue("");

    // ── Internals ─────────────────────────────────────────────────────────────

    private static void Enqueue(string line)
    {
        if (!_initialized) return;
        _queue.Enqueue(line);
        try { _signal.Release(); } catch { }
    }

    private static void WriterLoop()
    {
        while (true)
        {
            _signal.Wait();
            using var sw = new StreamWriter(_logPath, append: true, System.Text.Encoding.UTF8);
            while (_queue.TryDequeue(out var line))
            {
                sw.WriteLine(line);
            }
        }
    }
}

// ── Scope RAII ────────────────────────────────────────────────────────────────

/// <summary>
/// Bloc de mesure RAII : démarre un Stopwatch à la création et loggue la durée
/// au Dispose() (compatible with « using var op = PerfLogger.Begin(...) »).
/// </summary>
public sealed class PerfScope : IDisposable
{
    private readonly string _name;
    private readonly Stopwatch _sw;
    private string? _detail;
    private bool _disposed;

    internal PerfScope(string name)
    {
        _name = name;
        _sw   = Stopwatch.StartNew();
    }

    /// <summary>Ajoute un détail affiché à côté de la durée (ex. nombre de résultats).</summary>
    public PerfScope WithDetail(string detail) { _detail = detail; return this; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sw.Stop();
        PerfLogger.Log(_name, _sw.ElapsedMilliseconds, _detail);
    }
}
