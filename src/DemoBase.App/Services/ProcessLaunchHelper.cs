using System.Diagnostics;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

/// <summary>
/// Lancement de process mutualisé pour les launchers d'émulateur.
///
/// Deux services rendus par rapport à un simple <see cref="Process.Start(ProcessStartInfo)"/> :
///   1. La sortie stdout/stderr de l'émulateur est capturée et écrite dans la
///      console debug (fenêtre Output de Visual Studio / DebugView), préfixée par
///      un tag — indispensable pour diagnostiquer un émulateur qui refuse un
///      argument et se ferme aussitôt (ex. ares « Unrecognized argument for
///      --system »), cas jusqu'ici totalement muet côté application.
///   2. Détection de sortie précoce : beaucoup d'émulateurs valident leurs
///      arguments au démarrage et sortent immédiatement avec un code non nul en
///      cas d'erreur. On attend brièvement — sur un thread de fond, sans bloquer
///      l'UI — et si le process est déjà mort avec un code ≠ 0, on renvoie un
///      <see cref="LaunchResult"/> d'échec portant le vrai message d'erreur, que
///      l'application peut alors afficher. Un émulateur qui démarre normalement
///      reste en vie : l'attente expire et on renvoie un succès (comportement
///      inchangé pour le cas nominal).
///
/// La lecture de la sortie est ASYNCHRONE (Begin*ReadLine) : aucun risque de
/// blocage même si l'émulateur remplit son buffer de sortie.
/// </summary>
public static class ProcessLaunchHelper
{
    /// <param name="tag">Préfixe des lignes de log debug (ex. "MAME", "ARES").</param>
    /// <param name="friendlyName">Nom affiché dans le message d'erreur remonté à
    ///     l'utilisateur (ex. "MAME"). Null → message d'erreur brut.</param>
    /// <param name="earlyExitMs">Fenêtre de détection de sortie précoce. 0 =
    ///     désactivée (retourne un succès dès que Process.Start réussit).</param>
    public static async Task<LaunchResult> StartAndMonitorAsync(
        string  exePath,
        string  arguments,
        string  tag,
        string? friendlyName = null,
        int     earlyExitMs  = 1200,
        string? workingDir   = null)
    {
        Debug.WriteLine($"[{tag}] Commande : \"{exePath}\" {arguments}");
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName               = exePath,
                Arguments              = arguments,
                WorkingDirectory       = workingDir ?? Path.GetDirectoryName(exePath)!,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            var stderr = new StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) Debug.WriteLine($"[{tag}:out] {e.Data}");
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                Debug.WriteLine($"[{tag}:err] {e.Data}");
                lock (stderr) stderr.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            Debug.WriteLine($"[{tag}] Process.Start : PID={process.Id}");

            if (earlyExitMs > 0
                && await Task.Run(() => process.WaitForExit(earlyExitMs))
                && process.ExitCode != 0)
            {
                await Task.Run(() => process.WaitForExit()); // laisser les handlers vider le buffer
                string captured;
                lock (stderr) captured = stderr.ToString().Trim();

                var body = string.IsNullOrWhiteSpace(captured)
                    ? $"Arrêt au démarrage (code {process.ExitCode})."
                    : captured;
                var msg = string.IsNullOrWhiteSpace(friendlyName) ? body : $"{friendlyName} : {body}";

                Debug.WriteLine($"[{tag}] Sortie précoce (code {process.ExitCode}) : {body}");
                return new(false, msg);
            }

            return new(true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[{tag}] Exception : {ex}");
            var msg = string.IsNullOrWhiteSpace(friendlyName)
                ? ex.Message
                : $"Erreur lancement {friendlyName} : {ex.Message}";
            return new(false, msg);
        }
    }
}
