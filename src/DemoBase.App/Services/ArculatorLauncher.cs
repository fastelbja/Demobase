using DemoBase.Core.Models;
using DemoBase.Data;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

// ─── Clés de settings Arculator ──────────────────────────────────────────────

public static class ArculatorSettings
{
    /// <summary>
    /// Nom du profil machine à charger (= nom du fichier .cfg dans configs/ sans extension).
    /// Exemples : A3000, A3010, A5000, A4000, A420.
    /// Arculator.exe lance ce profil directement : Arculator.exe A3000
    /// </summary>
    public const string Config = "config";
}

// ─── Lanceur Arculator ────────────────────────────────────────────────────────
// Arculator — émulateur Acorn Archimedes (ARM2/ARM3, RISC OS 2/3).
// https://b-em.bbcmicro.com/arculator/
//
// Commande : Arculator.exe <config_name>
//   <config_name> = nom du fichier configs/<config_name>.cfg (sans chemin ni extension)
//   Exemples : A3000, A3010, A5000, A4000
//
// ⚠ LIMITATION IMPORTANTE :
//   Arculator ne supporte PAS le chargement d'un fichier ADF/JFD depuis la ligne
//   de commande. Le fichier release (romPath) n'est PAS utilisé directement.
//   DemoBase lance simplement Arculator avec le profil machine configuré.
//
//   Pour lancer des démos automatiquement :
//   1. Installer ADFFS dans votre Arculator (FS directory)
//   2. Configurer un !Boot sur HostFS qui lance la démo au démarrage
//   3. Ou utiliser le profil machine dédié à cette démo
//
// Modèles supportés (créer le .cfg correspondant dans configs/ d'Arculator) :
//   A305/A310 — premier Archimedes, ARM2, 1 Mo
//   A3000     — version compacte, ARM2, 1 Mo (le plus courant pour les démos)
//   A3010     — entrée de gamme, ARM2, 1 Mo
//   A3020     — ARM250, 2 Mo
//   A4000     — ARM250, 2 Mo
//   A410/A420 — ARM2, 1-4 Mo
//   A440      — ARM2, 4 Mo, carte vidéo
//   A5000     — ARM3, 2-4 Mo (machine rapide)
//   A540      — ARM3, 4-8 Mo (haut de gamme)

public class ArculatorLauncher
{
    private readonly PreferencesService _prefs;

    public ArculatorLauncher(PreferencesService prefs)
        => _prefs = prefs;

    public Task<LaunchResult> LaunchAsync(
        Emulator                    emulator,
        EmulatorConfig              config,
        Dictionary<string, string?> settings,
        string                      romPath,
        Release                     release)
    {
        System.Diagnostics.Debug.WriteLine(
            $"[ARCULATOR] LaunchAsync : exe={emulator.ExecutablePath}");

        if (!File.Exists(emulator.ExecutablePath))
            return Task.FromResult(new LaunchResult(false,
                $"Arculator introuvable : {emulator.ExecutablePath}"));

        // Récupérer le nom du config machine (défaut : A3000)
        var cfgName = settings.GetValueOrDefault(ArculatorSettings.Config, "A3000")
                      ?? "A3000";
        if (string.IsNullOrWhiteSpace(cfgName)) cfgName = "A3000";

        // Vérifier que le fichier .cfg existe
        var exeDir  = Path.GetDirectoryName(emulator.ExecutablePath)!;
        var cfgPath = Path.Combine(exeDir, "configs", $"{cfgName}.cfg");
        if (!File.Exists(cfgPath))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[ARCULATOR] Config introuvable : {cfgPath}");
            // Ne pas bloquer — Arculator affichera sa liste de configs
        }

        var args = cfgName;
        return ProcessLaunchHelper.StartAndMonitorAsync(
            emulator.ExecutablePath, args, tag: "ARCULATOR", friendlyName: "Arculator",
            workingDir: exeDir);
    }
}
