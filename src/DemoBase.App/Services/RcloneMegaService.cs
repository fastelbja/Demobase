using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DemoBase.App.Services;

/// <summary>
/// Télécharge des fichiers depuis un dossier Mega.nz public via rclone.
/// Contrairement à MegaApiClient qui charge l'arbre entier (lent sur gros dépôts),
/// rclone navigue directement vers le fichier cible.
/// </summary>
public class RcloneMegaService
{
    // URL du dépôt Mega ROMS — structure identique au RomPath des DATs
    public const string MegaRomsUrl = "https://mega.nz/folder/DBtQQKpS#HzwqjyuaI6Pu7EVISWRskQ";

    private static string RcloneExe =>
        Path.Combine(AppContext.BaseDirectory, "Externals", "rclone", "rclone.exe");

    private static string RcloneConfig =>
        Path.Combine(AppContext.BaseDirectory, "Externals", "rclone", "rclone.conf");

    public static bool IsAvailable => File.Exists(RcloneExe);

    /// <summary>
    /// Génère la config rclone pour accéder au dossier Mega public.
    /// rclone supporte les liens publics Mega via le type "mega" avec
    /// un accès anonyme au lien de partage.
    /// </summary>
    private static void EnsureConfig()
    {
        if (File.Exists(RcloneConfig)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(RcloneConfig)!);

        // Config rclone pour un lien public Mega
        // On utilise le type "mega" en mode anonyme avec le lien de partage
        var conf = $"""
            [mega_roms]
            type = mega
            user = anonymous
            pass = 
            """;
        File.WriteAllText(RcloneConfig, conf);
    }

    /// <summary>
    /// Télécharge un fichier depuis Mega par son chemin relatif (= RomPath du DAT).
    /// rclone construit l'URL : mega_roms:/{relativePath}
    /// </summary>
    public async Task<RcloneResult> DownloadAsync(
        string relativePath,
        string destFilePath,
        IProgress<(string message, double pct)>? progress = null,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return new(false, "rclone introuvable dans Externals\\rclone\\rclone.exe");

        EnsureConfig();

        var destDir  = Path.GetDirectoryName(destFilePath)!;
        var fileName = Path.GetFileName(destFilePath);
        Directory.CreateDirectory(destDir);

        // Construire le chemin source Mega
        // rclone copie un fichier depuis un lien public : on passe le lien direct
        var megaLink  = $"{MegaRomsUrl}/{relativePath.Replace('\\', '/')}";

        // rclone copyurl télécharge depuis une URL publique directement
        // Alternative : rclone copy avec remote configuré
        var args = $"copyurl \"{megaLink}\" \"{destFilePath}\" " +
                   $"--config \"{RcloneConfig}\" " +
                   $"--progress --stats 1s " +
                   $"--no-check-certificate";

        progress?.Report(($"rclone : {Path.GetFileName(relativePath)}…", 0));

        var psi = new ProcessStartInfo
        {
            FileName               = RcloneExe,
            Arguments              = args,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        try
        {
            using var process = Process.Start(psi)!;
            string? lastLine = null;

            // Lire stderr (rclone écrit le progress sur stderr)
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data == null) return;
                lastLine = e.Data;
                // Parser le pourcentage si présent
                if (e.Data.Contains('%'))
                {
                    var idx = e.Data.IndexOf('%');
                    if (idx > 0)
                    {
                        var numStr = e.Data[..idx].Trim().Split(' ')[^1];
                        if (double.TryParse(numStr, out var pct))
                            progress?.Report(($"rclone : {Path.GetFileName(relativePath)}… {pct:F0}%", pct));
                    }
                }
            };
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            await process.WaitForExitAsync(ct);

            if (process.ExitCode == 0 && File.Exists(destFilePath))
            {
                progress?.Report(($"rclone : {Path.GetFileName(relativePath)} ✓", 100));
                return new(true, null);
            }
            return new(false, $"rclone exit {process.ExitCode} : {lastLine}");
        }
        catch (OperationCanceledException)
        {
            return new(false, "Téléchargement annulé.");
        }
        catch (Exception ex)
        {
            return new(false, $"rclone erreur : {ex.Message}");
        }
    }
}

public record RcloneResult(bool Success, string? Error);
