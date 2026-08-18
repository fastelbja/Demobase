using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using DemoBase.App.Services;
using DemoBase.App.ViewModels;

namespace DemoBase.App;

public partial class PreferencesViewModel : ObservableObject
{
    private readonly PreferencesService  _prefs;
    private readonly ThemeService        _themeService;
    private readonly LocalizationService _locService;
    private readonly IServiceProvider    _services;
    private readonly DemoBase.Core.Interfaces.IReleaseService? _releaseService;

    // ─── Chemins ─────────────────────────────────────────────────────────────

    [ObservableProperty] private string _pathConfigs   = PreferencesService.DefaultPathConfigs;
    [ObservableProperty] private string _pathEmulators = PreferencesService.DefaultPathEmulators;
    [ObservableProperty] private string _pathImages    = PreferencesService.DefaultPathImages;
    [ObservableProperty] private string _pathDats      = PreferencesService.DefaultPathDats;
    [ObservableProperty] private string _pathReleases  = PreferencesService.DefaultPathReleases;
    [ObservableProperty] private string? _pathRecoil2Png;

    // ─── UI ──────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _theme       = "Light";
    [ObservableProperty] private string _language    = "fr";
    [ObservableProperty] private bool   _demoEffects = true;

    // ─── Vues / Favoris automatiques ────────────────────────────────────────────

    /// <summary>
    /// Seuil de vues à partir duquel une release est automatiquement ajoutée aux
    /// favoris. 0 = désactivé. Stocké dans la table Preferences ("config").
    /// </summary>
    [ObservableProperty] private int    _autoFavoriteViewThreshold = 0;
    [ObservableProperty] private int    _slideshowDurationSeconds  = 5;
    [ObservableProperty] private string _resetViewsStatus = string.Empty;

    // ─── Audio / lecteur tracker ─────────────────────────────────────────────

    // 2026-08-06/07, retours utilisateur successifs — gain UADE (UC_GAIN) fixé en dur
    // à 1.8 d'abord, puis rendu réglable ici ("mets un slider dans l'écran de
    // préférences. l'écran de lecture est déjà bien chargé"), puis panoramique stéréo
    // (UC_PANNING_VALUE) déplacé du player vers cette même page ("peux tu faire de
    // meme pour le 'Panoramique' [...] et enleve le du player. Crée du coup une
    // section 'UADE' dans les préférences pour mettre le panoramique et le replay
    // gain.") : les deux réglages sont regroupés dans la section "UADE" de
    // PreferencesView.xaml, tous deux sans effet "à chaud" (UC_GAIN/UC_PANNING_VALUE
    // ne sont lus par libuade qu'à la création du state natif) — un simple champ ici,
    // appliqué au clic sur "Sauvegarder", suffit pour les deux.
    // 1.6 (au lieu de 1.8) depuis le 2026-08-07 (retour utilisateur : "fixe le gain à
    // 1.6 [...] pour les nouvelles installations") — même valeur que
    // TrackerPlayer.Core.Players.UadePlayer.GainAmount et
    // AppPreferences.UadeGainAmount, pour rester cohérent tant qu'aucune préférence
    // n'a encore été chargée depuis la base (ce champ n'est de toute façon écrasé par
    // LoadAsync qu'une fois la valeur réelle lue).
    [ObservableProperty] private double _uadeGainAmount     = 1.6;
    /// <summary>Panoramique stéréo UADE — adoucit le hard-panning Amiga (chip Paula,
    /// 100% gauche/droite par voie). Désactivé par défaut (son Amiga brut).</summary>
    [ObservableProperty] private bool   _uadePanningEnabled = false;
    /// <summary>Intensité du panoramique (0.0-2.0, 0.7 = réglage historique UADE).</summary>
    [ObservableProperty] private double _uadePanningAmount  = 0.7;

    // ─── BIOS ─────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isBiosDownloading;
    [ObservableProperty] private string _biosDownloadLabel   = string.Empty;
    [ObservableProperty] private int    _biosDownloadPercent;
    [ObservableProperty] private string? _biosStatusMessage;

    // ─── État ────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool   _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;

    public PreferencesViewModel(PreferencesService prefs, ThemeService themeService,
                                    LocalizationService locService,
                                    IServiceProvider services,
                                    DemoBase.Core.Interfaces.IReleaseService? releaseService = null)
    {
        _prefs        = prefs;
        _themeService = themeService;
        _locService   = locService;
        _services     = services;
        _releaseService = releaseService;
        _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var p = await _prefs.LoadAllAsync();
            PathConfigs   = ToRelative(p.ResolvedPathConfigs);
            PathEmulators = ToRelative(p.ResolvedPathEmulators);
            PathImages    = ToRelative(p.ResolvedPathImages);
            PathDats      = ToRelative(p.ResolvedPathDats);
            PathReleases  = ToRelative(p.ResolvedPathReleases);
            PathRecoil2Png = ToRelative(p.PathRecoil2Png);

            // Auto-détecter recoil2png.exe si le champ est vide et que l'exe existe
            // dans le dossier par défaut (installé via le wizard Externals)
            if (string.IsNullOrWhiteSpace(PathRecoil2Png))
            {
                var defaultRecoil = System.IO.Path.Combine(
                    AppContext.BaseDirectory, "Externals", "RECOIL", "recoil2png.exe");
                if (System.IO.File.Exists(defaultRecoil))
                    PathRecoil2Png = ToRelative(defaultRecoil);
            }
            Theme         = p.Theme;
            Language      = p.Language;
            DemoEffects   = p.DemoEffects;
            AutoFavoriteViewThreshold  = p.AutoFavoriteViewThreshold;
            SlideshowDurationSeconds   = p.SlideshowDurationSeconds;
            UadeGainAmount             = p.UadeGainAmount;
            UadePanningEnabled         = p.UadePanningEnabled;
            UadePanningAmount          = p.UadePanningAmount;
        }
        finally { IsLoading = false; }
    }

    // ─── Sauvegarder ─────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsLoading = true;
        StatusMessage = string.Empty;
        try
        {
            await _prefs.SaveAllAsync(new AppPreferences
            {
                PathConfigs   = ToAbsolute(PathConfigs),
                PathEmulators = ToAbsolute(PathEmulators),
                PathImages    = ToAbsolute(PathImages),
                PathDats      = ToAbsolute(PathDats),
                PathReleases  = ToAbsolute(PathReleases),
                PathRecoil2Png = ToAbsolute(PathRecoil2Png),
                Theme         = Theme,
                DemoEffects   = DemoEffects,
                Language      = Language,
                AutoFavoriteViewThreshold  = AutoFavoriteViewThreshold,
                SlideshowDurationSeconds   = SlideshowDurationSeconds,
                UadeGainAmount             = UadeGainAmount,
                UadePanningEnabled         = UadePanningEnabled,
                UadePanningAmount          = UadePanningAmount,
            });

            // Appliquer le gain et le panoramique UADE immédiatement (comme le thème/
            // la langue ci-dessous) — n'ont d'effet que sur la PROCHAINE ouverture d'un
            // fichier UADE (UC_GAIN/UC_PANNING_VALUE lus uniquement à la création du
            // state natif, cf. TrackerPlayer.Core.Players.UadePlayer.EnsureState), pas
            // besoin de redémarrer l'application ni de relancer la piste en cours.
            TrackerPlayer.Core.Players.UadePlayer.GainAmount     = UadeGainAmount;
            TrackerPlayer.Core.Players.UadePlayer.PanningEnabled = UadePanningEnabled;
            TrackerPlayer.Core.Players.UadePlayer.PanningAmount  = UadePanningAmount;

            // Appliquer le thème immédiatement
            var appTheme = Theme == "Dark" ? AppTheme.Dark : AppTheme.Light;
            _themeService.Apply(appTheme);

            // Appliquer la langue immédiatement
            _locService.Apply(Language);
            // Rafraîchir les labels de navigation
            _services.GetService<MainViewModel>()?.RefreshNavLabels();

            // Traduire les types de release selon la langue choisie
            try
            {
                var factory = _services.GetRequiredService<Microsoft.EntityFrameworkCore.IDbContextFactory<DemoBase.Data.Context.DemoBaseDbContext>>();
                await using var ctx = await factory.CreateDbContextAsync();
                var connStr = ctx.Database.GetConnectionString()!;
                await DemoBase.Data.ReleaseTypeTranslationService.ApplyAsync(connStr, Language);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Prefs] ReleaseType translation failed (non-blocking): {ex.Message}");
            }

            StatusMessage = DemoBase.App.Services.LocalizationService.Get("Pref_Saved");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Erreur : {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    // ─── Réinitialiser ───────────────────────────────────────────────────────

    [RelayCommand]
    private void Reset()
    {
        PathConfigs   = ToRelative(PreferencesService.DefaultPathConfigs);
        PathEmulators = ToRelative(PreferencesService.DefaultPathEmulators);
        PathImages    = ToRelative(PreferencesService.DefaultPathImages);
        PathDats      = ToRelative(PreferencesService.DefaultPathDats);
        PathReleases  = ToRelative(PreferencesService.DefaultPathReleases);
        PathRecoil2Png = null;
        Theme         = "Light";
        Language      = "fr";
        DemoEffects   = true;
        AutoFavoriteViewThreshold = 0;
        SlideshowDurationSeconds  = 5;
        UadeGainAmount            = 1.6;
        UadePanningEnabled        = false;
        UadePanningAmount         = 0.7;
        StatusMessage = string.Empty;
    }

    // ─── Parcourir dossier ───────────────────────────────────────────────────

    [RelayCommand]
    private void BrowseConfigs()   => PathConfigs   = Browse(PathConfigs)   ?? PathConfigs;
    [RelayCommand]
    private void BrowseEmulators() => PathEmulators = Browse(PathEmulators) ?? PathEmulators;
    [RelayCommand]
    private void BrowseImages()    => PathImages    = Browse(PathImages)    ?? PathImages;
    [RelayCommand]
    private void BrowseDats()      => PathDats      = Browse(PathDats)      ?? PathDats;
    [RelayCommand]
    private void BrowseReleases()  => PathReleases  = Browse(PathReleases)  ?? PathReleases;

    [RelayCommand]
    private void BrowseRecoil()
    {
        var currentAbs = ToAbsolute(PathRecoil2Png);
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title           = "Sélectionner recoil2png.exe",
            Filter          = "recoil2png.exe|recoil2png.exe|Exécutables (*.exe)|*.exe|Tous (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = !string.IsNullOrEmpty(currentAbs)
                ? System.IO.Path.GetDirectoryName(currentAbs) ?? AppContext.BaseDirectory
                : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog() == true)
            PathRecoil2Png = ToRelative(dlg.FileName);
    }

    // ── Chemin relatif / absolu ───────────────────────────────────────────────
    // Affiché et édité en relatif au dossier de l'application (plus lisible,
    // ex. ".\Configs" plutôt que "C:\...\bin\Release\net8.0-windows\Configs").
    // Toujours stocké en absolu dans AppPreferences (utilisé tel quel par tout
    // le reste de l'application — launchers, services…).

    private static string ToRelative(string? absolute)
    {
        if (string.IsNullOrEmpty(absolute)) return absolute ?? "";
        var baseDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        var norm    = absolute.TrimEnd('\\', '/');
        if (norm.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
        {
            var rel = absolute[baseDir.Length..].TrimStart('\\', '/');
            return string.IsNullOrEmpty(rel) ? absolute : $".\\{rel}";
        }
        return absolute;
    }

    private static string? ToAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        return System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, path));
    }

    private static string? Browse(string current)
    {
        var currentAbs = ToAbsolute(current);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Choisir un dossier",
            InitialDirectory = !string.IsNullOrEmpty(currentAbs) && Directory.Exists(currentAbs)
                ? currentAbs
                : AppContext.BaseDirectory,
        };
        return dlg.ShowDialog() == true ? ToRelative(dlg.FolderName) : null;
    }

    // ─── Télécharger le pack BIOS ────────────────────────────────────────────

    [RelayCommand(CanExecute = nameof(CanDownloadBiosPack))]
    private async Task DownloadBiosPack()
    {
        IsBiosDownloading   = true;
        BiosStatusMessage   = null;
        BiosDownloadPercent = 0;
        BiosDownloadLabel   = string.Empty;

        try
        {
            var progress = new Progress<(string Label, int Percent)>(p =>
            {
                BiosDownloadLabel   = p.Label;
                BiosDownloadPercent = p.Percent;
            });

            var svc = new DemoBase.App.Services.BiosPackService();
            var (success, message) = await svc.DownloadAndInstallAsync(progress);

            BiosStatusMessage = success ? $"✓ {message}" : $"✗ {message}";
        }
        catch (Exception ex)
        {
            BiosStatusMessage = $"✗ Erreur : {ex.Message}";
        }
        finally
        {
            IsBiosDownloading   = false;
            BiosDownloadPercent = 0;
            BiosDownloadLabel   = string.Empty;
        }
    }

    private bool CanDownloadBiosPack() => !IsBiosDownloading;

    partial void OnIsBiosDownloadingChanged(bool value)
        => DownloadBiosPackCommand.NotifyCanExecuteChanged();

    // ─── Reset vus ───────────────────────────────────────────────────────────

    /// <summary>
    /// Remet à zéro le compteur de vues de TOUTES les releases (table Releases,
    /// colonne ViewCount). Demande confirmation avant toute action — irréversible.
    /// </summary>
    [RelayCommand]
    private async Task ResetViewCounts()
    {
        if (_releaseService == null) return;

        var result = System.Windows.MessageBox.Show(
            "Ceci va remettre à zéro le compteur de vues de TOUTES les releases " +
            "(utilisé par le filtre \"Non vu\" et l'ajout automatique aux favoris). " +
            "Cette action est irréversible. Continuer ?",
            "Réinitialiser les vues",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        ResetViewsStatus = string.Empty;
        try
        {
            await _releaseService.ResetAllViewCountsAsync();
            ResetViewsStatus = DemoBase.App.Services.LocalizationService.Get("Pref_ViewsReset");
        }
        catch (Exception ex)
        {
            ResetViewsStatus = $"Erreur : {ex.Message}";
        }
    }
}
