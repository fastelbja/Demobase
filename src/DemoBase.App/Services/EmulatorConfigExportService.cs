using DemoBase.Data;
using DemoBase.Data.Context;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace DemoBase.App.Services;

/// <summary>
/// Export/import JSON des configs et settings d'émulateurs.
/// Export : génère un fichier JSON depuis la base locale.
/// Import : applique un JSON via INSERT OR REPLACE (idempotent).
/// Le JSON est hébergé sur http://demobase.free.fr/DBSetup (même dépôt que les DATs).
///
/// 2026-08-17 : migration Mega.nz → HTTP direct sur le site de l'utilisateur (cf.
/// DbSetupDownloadService). Constantes renommées Mega*→DbSetup* ; le nom de fichier
/// n'est plus une sous-chaîne à rechercher (pas de listing de répertoire disponible sur
/// demobase.free.fr) mais un nom EXACT et fixe, réécrasé à chaque publication.
/// </summary>
public class EmulatorConfigExportService(IDbContextFactory<DemoBaseDbContext> dbFactory)
{
    // ── Constantes ────────────────────────────────────────────────────────────
    public const string DbSetupBaseUrl  = "http://demobase.free.fr/DBSetup";
    public const string DbSetupSubFolder = "Configs";
    public const string DbSetupFileName  = "emulator_configs.json";

    // Settings contenant des chemins absolus. Depuis l'ajout du pack BIOS/ROM
    // (téléchargé au même endroit relatif — AppContext.BaseDirectory\BIOS — sur
    // toutes les machines), ces valeurs sont maintenant exportées en RELATIF
    // (ex: ".\BIOS\bios\amiga\bios\kick34005.A500") si elles vivent bien sous le
    // dossier de l'app, via ToRelativeIfUnderBaseDir/ToAbsoluteFromExport
    // ci-dessous — même convention que ToRelative/ToAbsolute déjà utilisée dans
    // WinUAESettingsViewModel/HatariSettingsViewModel pour l'affichage. Si le
    // chemin pointe ailleurs (BIOS externe propre à cet utilisateur), il reste
    // exclu de l'export, comme avant.
    private static readonly HashSet<string> PathDependentKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "kickstart_path", "kickstart_rom_file", "kickstart_ext_rom_file",
        "tos_path", "tos_file", "rom_path", "rompath", "bios_path",
        "gfx_display_name", "gfx_display_friendlyname", "gfx_display",
        "config_file", "cfg_file", "config",
        "machine_path", "hdf_path",
    };

    // ── Export ────────────────────────────────────────────────────────────────

    public async Task<string> ExportToJsonAsync(string outputPath)
    {
        await using var ctx = await dbFactory.CreateDbContextAsync();

        var configs = await ctx.EmulatorConfigs
            .Include(c => c.Settings)
            .OrderBy(c => c.Id)
            .ToListAsync();

        var export = new EmulatorConfigExport
        {
            ExportedAt = DateTime.UtcNow,
            Configs = configs.Select(c => new EmulatorConfigDto
            {
                Id             = c.Id,
                EmulatorId     = c.EmulatorId,
                PlatformId     = c.PlatformId,
                ProfileName    = c.ProfileName,
                CommandLine    = c.CommandLine,
                IsDefault      = c.IsDefault,
                FullScreen     = c.FullScreen,
                ConfigFilePath = string.IsNullOrEmpty(c.ConfigFilePath)
                    ? null
                    : System.IO.Path.GetFileName(c.ConfigFilePath),
                Settings       = BuildSettingsForExport(c.Settings)
            }).ToList()
        };

        var json = JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented        = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        });

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, json);
        return outputPath;
    }

    private static Dictionary<string, string?> BuildSettingsForExport(
        IEnumerable<DemoBase.Core.Models.EmulatorSetting> settings)
    {
        var result = new Dictionary<string, string?>();
        foreach (var s in settings)
        {
            if (PathDependentKeys.Contains(s.Key.ToLowerInvariant()))
            {
                var rel = ToRelativeIfUnderBaseDir(s.Value);
                if (rel == null) continue; // chemin externe, propre à cette machine → pas exporté
                result[s.Key] = rel;
            }
            else
            {
                result[s.Key] = s.Value;
            }
        }
        return result;
    }

    /// <summary>
    /// Convertit un chemin absolu en relatif (".\sous\dossier\fichier") s'il vit sous
    /// AppContext.BaseDirectory ; retourne null sinon (chemin externe, non portable).
    /// Même convention que ToRelative dans WinUAESettingsViewModel/HatariSettingsViewModel.
    /// </summary>
    private static string? ToRelativeIfUnderBaseDir(string? absolute)
    {
        if (string.IsNullOrWhiteSpace(absolute)) return null;
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var norm    = absolute.TrimEnd('\\', '/');
        if (!norm.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase)) return null;
        var rel = absolute[baseDir.Length..].TrimStart('\\', '/');
        return string.IsNullOrEmpty(rel) ? null : $".\\{rel}";
    }

    /// <summary>
    /// Résout un chemin issu du JSON (relatif, ex. ".\BIOS\...") en chemin absolu
    /// local — symétrique de ToRelativeIfUnderBaseDir. Un chemin déjà absolu
    /// (import d'un ancien JSON, ou valeur non convertie) est laissé tel quel.
    /// </summary>
    private static string? ToAbsoluteFromExport(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    // ── Import ────────────────────────────────────────────────────────────────

    public async Task<(int configs, int settings)> ImportFromJsonAsync(string jsonPath)
    {
        var json   = await File.ReadAllTextAsync(jsonPath);
        var export = JsonSerializer.Deserialize<EmulatorConfigExport>(json)
                     ?? throw new InvalidDataException("JSON invalide.");

        await using var ctx = await dbFactory.CreateDbContextAsync();
        await using var tx  = await ctx.Database.BeginTransactionAsync();

        int configCount = 0, settingCount = 0;

        foreach (var dto in export.Configs)
        {
            // dto.ConfigFilePath (JSON) ne contient que le nom de fichier — l'export
            // retire le chemin absolu d'origine, spécifique à la machine source, via
            // Path.GetFileName (cf. ExportToJsonAsync ci-dessus). On le recombine ici
            // avec AppPaths.Configs, le dossier où ConfigsUpdateService télécharge
            // aussi ces mêmes fichiers depuis le site, pour reconstituer un chemin absolu
            // valide localement — EmulatorConfig.ConfigFilePath doit TOUJOURS être
            // absolu en base (WinUAELauncher/HatariLauncher l'utilisent tel quel avec
            // File.Exists), sinon le profil importé pointe vers un fichier introuvable.
            string? resolvedConfigFilePath = string.IsNullOrWhiteSpace(dto.ConfigFilePath)
                ? null
                : Path.IsPathRooted(dto.ConfigFilePath)
                    ? dto.ConfigFilePath
                    : Path.Combine(AppPaths.Configs, dto.ConfigFilePath);

            // INSERT OR REPLACE sur EmulatorConfigs
            // NB : ctx.Database.ExecuteSqlAsync(FormattableString) est l'API EF Core à
            // paramétrage AUTOMATIQUE — chaque {expr} devient un paramètre SQL typé, pas
            // du texte concaténé. Les valeurs C# (dto.ProfileName, resolvedConfigFilePath,
            // dto.PlatformId nullable...) doivent donc être passées TELLES QUELLES ; les
            // ré-échapper/quoter manuellement (ancien helper Esc()) insérait les apostrophes
            // littéralement dans la valeur stockée (visible en base par des champs entourés
            // de guillemets simples) et transformait un NULL de PlatformId en la chaîne
            // texte "NULL" au lieu d'un vrai NULL SQL.
            await ctx.Database.ExecuteSqlAsync(
                $"""
                INSERT OR REPLACE INTO "EmulatorConfigs"
                    ("Id","EmulatorId","PlatformId","ProfileName","CommandLine","IsDefault","FullScreen","ConfigFilePath")
                VALUES
                    ({dto.Id},{dto.EmulatorId},{dto.PlatformId},
                     {dto.ProfileName},{dto.CommandLine},{(dto.IsDefault ? 1 : 0)},{(dto.FullScreen ? 1 : 0)},{resolvedConfigFilePath});
                """
            );
            configCount++;

            if (dto.Settings == null) continue;

            // Supprimer les settings existants puis réinsérer
            await ctx.Database.ExecuteSqlAsync(
                $"DELETE FROM \"EmulatorSettings\" WHERE \"EmulatorConfigId\" = {dto.Id}");

            foreach (var (key, value) in dto.Settings)
            {
                // kickstart_path/tos_path/etc. sont exportés en relatif (cf.
                // BuildSettingsForExport ci-dessus) — on les résout ici en absolu,
                // symétriquement, pour que WinUAELauncher/HatariLauncher (qui font
                // du File.Exists direct sur la valeur stockée) retrouvent le fichier.
                var resolvedValue = PathDependentKeys.Contains(key.ToLowerInvariant())
                    ? ToAbsoluteFromExport(value)
                    : value;

                await ctx.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "EmulatorSettings" ("EmulatorConfigId","Key","Value")
                    VALUES ({dto.Id},{key},{resolvedValue})
                    """
                );
                settingCount++;
            }
        }

        await tx.CommitAsync();
        return (configCount, settingCount);
    }
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public class EmulatorConfigExport
{
    public DateTime ExportedAt { get; set; }
    public List<EmulatorConfigDto> Configs { get; set; } = [];
}

public class EmulatorConfigDto
{
    public int     Id             { get; set; }
    public int     EmulatorId     { get; set; }
    public int?    PlatformId     { get; set; }
    public string? ProfileName    { get; set; }
    public string? CommandLine    { get; set; }
    public bool    IsDefault      { get; set; }
    public bool    FullScreen     { get; set; }
    public string? ConfigFilePath { get; set; }
    public Dictionary<string, string?>? Settings { get; set; }
}
