using CommunityToolkit.Mvvm.ComponentModel;
using DemoBase.App.Services;
using DemoBase.Data;
using System.IO;
using System.Windows.Controls;

namespace DemoBase.App.Views.WizardPages;

public partial class ReadyPageViewModel : ObservableObject
{
    [ObservableProperty] private bool   _isSeeding = true;
    [ObservableProperty] private bool   _seedDone;
    [ObservableProperty] private string _seedSummary = "";

    public async Task RunSeedAsync(EmulatorSeedService seedService, PreferencesService prefs,
        DemoBase.App.Services.DbSetupDownloadService megaService,
        DemoBase.App.Services.EmulatorConfigExportService exportService,
        DemoBase.Data.ReleaseProfileOverrideExportService profileOverrideExportService)
    {
        IsSeeding = true;
        SeedDone  = false;

        try
        {
            var result = await seedService.SeedAllAsync();
            var failedSuffix = result.Failed > 0
                ? $" ({result.Failed} skipped due to an isolated issue — see Preferences to configure manually.)"
                : "";
            SeedSummary =
                $"{result.TotalSeeded} emulators registered in the database " +
                $"({result.NewlyCreated} new), {result.ExecutablesDetected} " +
                $"executable(s) found in Emus/.{failedSuffix}";
        }
        catch (Exception ex)
        {
            SeedSummary = $"Warning: automatic emulator registration ran into " +
                           $"an issue ({ex.Message}). You can configure them " +
                           $"manually from Preferences.";
        }
        finally
        {
            IsSeeding = false;
            SeedDone  = true;
        }

        // Auto-sauvegarder le chemin recoil2png.exe si pas encore configuré
        await AutoSaveRecoilPathAsync(prefs);

        // ── Télécharger et importer les configs depuis le site ────────────────
        await ImportConfigsFromMegaAsync(megaService, exportService, prefs, profileOverrideExportService);
    }

    private static async Task ImportConfigsFromMegaAsync(
        DemoBase.App.Services.DbSetupDownloadService megaService,
        DemoBase.App.Services.EmulatorConfigExportService exportService,
        DemoBase.Data.PreferencesService prefs,
        DemoBase.Data.ReleaseProfileOverrideExportService profileOverrideExportService)
    {
        // Bug corrigé le 2026-07-25 (retour utilisateur : ni les .uae, ni les 2 JSON
        // n'étaient importés lors d'une installation neuve via le wizard) : ce
        // ConfigsUpdateService était construit ICI sans son 4e paramètre
        // (ReleaseProfileOverrideExportService, optionnel = null par défaut) — donc
        // release_profile_overrides.json n'était jamais importé pendant le wizard, alors
        // que l'instance équivalente enregistrée en DI (utilisée par la vérification en
        // tâche de fond après le wizard, App.xaml.cs) l'avait bien. Threadé depuis
        // App.xaml.cs → SetupWizardWindow → SetupWizardViewModel → ici.
        var updateSvc = new DemoBase.App.Services.ConfigsUpdateService(
            megaService, exportService, prefs, profileOverrideExportService);
        await updateSvc.CheckAndUpdateAsync();
    }

    private static async Task AutoSaveRecoilPathAsync(PreferencesService prefs)
    {
        try
        {
            var existing = await prefs.LoadAllAsync();
            if (!string.IsNullOrWhiteSpace(existing.PathRecoil2Png)) return;

            var recoilPath = Path.Combine(
                AppContext.BaseDirectory, "Externals", "RECOIL", "recoil2png.exe");
            if (!File.Exists(recoilPath)) return;

            await prefs.SetAsync(PrefKeys.PathRecoil2Png, recoilPath);
        }
        catch
        {
            // Non bloquant — l'utilisateur pourra configurer manuellement
        }
    }
}

public partial class ReadyPage : UserControl
{
    public ReadyPageViewModel Vm { get; } = new();

    public ReadyPage(EmulatorSeedService seedService, PreferencesService prefs,
        DemoBase.App.Services.DbSetupDownloadService megaService,
        DemoBase.App.Services.EmulatorConfigExportService exportService,
        DemoBase.Data.ReleaseProfileOverrideExportService profileOverrideExportService)
    {
        InitializeComponent();
        DataContext = Vm;
        Loaded += async (_, _) => await Vm.RunSeedAsync(
            seedService, prefs, megaService, exportService, profileOverrideExportService);
    }
}
