using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DemoBase.Core.Enums;
using DemoBase.Core.Models;
using DemoBase.Core.Interfaces;
using System;
using System.Collections.ObjectModel;
using System.IO;
using DemoBase.App.Services;
using DemoBase.Core.Diagnostics;

namespace DemoBase.App.ViewModels.Emulators;

// ─── EmulatorSettingsViewModel ────────────────────────────────────────────────
// Vue principale : liste des émulateurs + sélection

public partial class EmulatorSettingsViewModel : ObservableObject
{
    private readonly IUnitOfWork _uow;
    private readonly DemoBase.App.Services.EmulatorInstallerService _installerService;
    private readonly DemoBase.App.Services.EmulatorSeedService _seedService;
    private readonly DemoBase.App.Services.EmulatorConfigExportService? _exportService;
    private readonly DemoBase.Data.ReleaseProfileOverrideExportService? _profileOverrideExportService;

    [ObservableProperty] private ObservableCollection<EmulatorItemViewModel> _emulators = [];
    [ObservableProperty] private EmulatorItemViewModel? _selected;
    [ObservableProperty] private bool    _isLoading;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>ViewModel partagé pour le téléchargement du pack BIOS.</summary>
    public DemoBase.App.ViewModels.BiosPageViewModel BiosVm { get; } = new();

    [RelayCommand]
    private async Task DownloadBiosPack() => await BiosVm.DownloadBiosPackCommand.ExecuteAsync(null);

    public EmulatorSettingsViewModel(IUnitOfWork uow,
        DemoBase.App.Services.EmulatorInstallerService installerService,
        DemoBase.App.Services.EmulatorSeedService seedService,
        DemoBase.App.Services.EmulatorConfigExportService? exportService = null,
        DemoBase.Data.ReleaseProfileOverrideExportService? profileOverrideExportService = null)
    {
        _uow                           = uow;
        _installerService              = installerService;
        _seedService                   = seedService;
        _exportService                 = exportService;
        _profileOverrideExportService  = profileOverrideExportService;
    }

    /// <summary>
    /// Ouvre le gestionnaire de téléchargement/mise à jour des émulateurs et
    /// outils externes — même composant que les étapes "Émulateurs"/"Outils
    /// externes" du wizard, mais accessible à tout moment depuis l'app (le
    /// wizard ne se rouvre plus une fois terminé, donc c'est le seul moyen de
    /// relancer un téléchargement en échec, ou de vérifier les mises à jour).
    /// </summary>
    [RelayCommand]
    private async Task OpenDownloadManager()
    {
        var window = new DemoBase.App.EmulatorDownloadManagerWindow(
            _installerService, _exportService, _profileOverrideExportService)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.ShowDialog();
        // Seeder après fermeture du gestionnaire : enregistre les émulateurs
        // nouvellement téléchargés. On le fait ICI (post-installation) et non
        // dans LoadAsync (navigation) pour éviter 43 queries à chaque affichage.
        try
        {
            using (PerfLogger.Begin("EmulatorSettings.SeedAllAsync (post-install)"))
                await _seedService.SeedAllAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[EmulatorSettings] Seed post-gestionnaire échoué : {ex.Message}");
        }
        using (PerfLogger.Begin("EmulatorSettings.LoadAsync"))
            await LoadAsync();
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;

        try
        {
            List<DemoBase.Core.Models.Emulator> list;
            using (PerfLogger.Begin("EmulatorSettings.GetAllWithConfigsAsync"))
                list = (await _uow.Emulators.GetAllWithConfigsAsync()).ToList();
            Emulators = new ObservableCollection<EmulatorItemViewModel>(
                list.Select(e => new EmulatorItemViewModel(e, _uow)));
            Selected  = Emulators.FirstOrDefault();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private void SetSelected(EmulatorItemViewModel? vm)
    {
        foreach (var e in Emulators) e.IsSelected = false;
        if (vm != null) vm.IsSelected = true;
        Selected = vm;
    }

    [RelayCommand]
    private async Task AddEmulatorAsync()
    {
        // Les IDs 0-99 sont réservés aux émulateurs gérés par DemoBase.
        // Les émulateurs créés manuellement démarrent à 100.
        var nextId = await _uow.Emulators.NextManualIdAsync();
        var emulator = new Emulator
        {
            Id              = nextId,
            Name            = "Nouvel émulateur",
            Version         = "",
            ExecutablePath  = "",
            Status          = EmulatorStatus.Active,
        };
        await _uow.Emulators.AddAsync(emulator);
        await _uow.SaveChangesAsync();

        var vm = new EmulatorItemViewModel(emulator, _uow) { IsEditing = true };
        Emulators.Add(vm);
        Selected = vm;
    }

    [RelayCommand]
    private async Task DeleteEmulatorAsync(EmulatorItemViewModel? vm)
    {
        if (vm == null) return;
        await _uow.Emulators.DeleteAsync(vm.Emulator.Id);
        await _uow.SaveChangesAsync();
        Emulators.Remove(vm);
        Selected = Emulators.FirstOrDefault();
    }
}

// ─── EmulatorItemViewModel ────────────────────────────────────────────────────
// Représente un émulateur avec ses profils

public partial class EmulatorItemViewModel : ObservableObject
{
    public readonly Emulator Emulator;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private string       _name;
    [ObservableProperty] private EmulatorType  _emulatorType;
    [ObservableProperty] private string  _version;
    [ObservableProperty] private string  _executablePath;
    [ObservableProperty] private string? _website;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private bool    _isEditing;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private bool    _isSelected;
    [ObservableProperty] private string? _executableStatus;  // "✓ Trouvé" / "✗ Introuvable"
    [ObservableProperty] private bool    _executableOk;

    [ObservableProperty]
    private ObservableCollection<ProfileViewModel> _profiles = [];
    [ObservableProperty]
    private ProfileViewModel? _selectedProfile;

    public EmulatorItemViewModel(Emulator emulator, IUnitOfWork uow)
    {
        Emulator       = emulator;
        _uow           = uow;
        _name          = emulator.Name;
        _emulatorType  = emulator.EmulatorType;
        _version       = emulator.Version;
        _executablePath = ToRelative(emulator.ExecutablePath);
        _website       = emulator.Website;
        _notes         = emulator.Notes;

        Profiles = new ObservableCollection<ProfileViewModel>(
            emulator.Configurations.Select(c => new ProfileViewModel(c, uow) { ParentVm = this }));
        SelectedProfile = Profiles.FirstOrDefault();

        CheckExecutable();
    }

    // ── Chemin relatif / absolu ───────────────────────────────────────────────
    // Stocké en base TOUJOURS en absolu (tous les launchers — HatariLauncher,
    // FSUaeLauncher, DuckStationLauncher, etc. — utilisent ExecutablePath
    // directement comme chemin absolu pour File.Exists/Process.Start). Seuls
    // l'affichage et l'édition dans cette page utilisent un chemin relatif au
    // dossier de l'application, plus lisible (ex. ".\Emus\Altirra\Altirra64.exe").

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

    private static string ToAbsolute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path ?? "";
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    // ── Vérification de l'exécutable ─────────────────────────────────────────

    [RelayCommand]
    private void BrowseExecutable()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Sélectionner l'exécutable de l'émulateur",
            Filter = "Exécutables (*.exe)|*.exe|Tous les fichiers (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
        {
            ExecutablePath = ToRelative(dlg.FileName);
            CheckExecutable();
        }
    }

    partial void OnExecutablePathChanged(string value) => CheckExecutable();
    partial void OnEmulatorTypeChanged(EmulatorType value) => CheckExecutable();

    private void CheckExecutable()
    {
        // Pas d'émulateur séparé pour ce type — ExecutablePath n'est pas utilisé au
        // lancement (cf. WindowsLauncher), donc "introuvable" serait trompeur ici.
        if (EmulatorType == EmulatorType.Windows)
        {
            ExecutableOk     = true;
            ExecutableStatus = DemoBase.App.Services.LocalizationService.Get("Msg_NoExecutableNeeded");
            return;
        }

        var ok = File.Exists(ToAbsolute(ExecutablePath));
        ExecutableOk     = ok;
        ExecutableStatus = ok ? DemoBase.App.Services.LocalizationService.Get("Msg_Found") : DemoBase.App.Services.LocalizationService.Get("Msg_NotFound");
    }

    // ── Sauvegarde ────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            Emulator.Name           = Name.Trim();
            Emulator.EmulatorType   = EmulatorType;
            Emulator.Version        = Version.Trim();
            Emulator.ExecutablePath = ToAbsolute(ExecutablePath.Trim());
            Emulator.Website        = string.IsNullOrWhiteSpace(Website)     ? null : Website.Trim();
            Emulator.Notes          = string.IsNullOrWhiteSpace(Notes)       ? null : Notes.Trim();
            await _uow.Emulators.UpdateAsync(Emulator);
            await _uow.SaveChangesAsync();
            IsEditing = false;
        }
        finally { IsSaving = false; }
    }

    [RelayCommand]
    private void Edit() => IsEditing = true;

    [RelayCommand]
    private void CancelEdit()
    {
        Name           = Emulator.Name;
        Version        = Emulator.Version;
        ExecutablePath = ToRelative(Emulator.ExecutablePath);
        Website        = Emulator.Website;
        Notes          = Emulator.Notes;
        IsEditing      = false;
    }

    // ── Profils ───────────────────────────────────────────────────────────────
    // Les réglages spécifiques au type d'émulateur (WinUAE/Altirra/Hatari) sont
    // désormais portés par chaque ProfileViewModel (un profil = une config matérielle
    // précise), et non plus ici au niveau de l'émulateur — voir ProfileViewModel.

    [RelayCommand]
    private async Task AddProfileAsync()
    {
        var platforms = (await _uow.Platforms.GetAllAsync())
            .OrderBy(p => p.Name).ToList();
        if (!platforms.Any()) return;

        var firstPlatform = platforms.First();
        var config = new EmulatorConfig
        {
            EmulatorId  = Emulator.Id,
            PlatformId  = firstPlatform.Id,
            ProfileName = "Default",
            CommandLine = "{file}",
            IsDefault   = !Profiles.Any(),
        };

        // Insertion via EF avec tracking pour obtenir l'Id généré
        var saved = await _uow.Emulators.AddConfigAsync(config);
        saved.Platform = firstPlatform;

        var vm = new ProfileViewModel(saved, _uow) { IsEditing = true, ParentVm = this };
        Profiles.Add(vm);
        SelectedProfile = vm;
    }

    [RelayCommand]
    private async Task DeleteProfileAsync(ProfileViewModel? vm)
    {
        if (vm == null) return;
        await _uow.Emulators.DeleteConfigAsync(vm.Config.Id);
        Profiles.Remove(vm);
        SelectedProfile = Profiles.FirstOrDefault();
    }
}

// ─── ProfileViewModel ─────────────────────────────────────────────────────────
// Un profil = un émulateur sur une plateforme donnée

public partial class ProfileViewModel : ObservableObject
{
    public readonly EmulatorConfig Config;
    private readonly IUnitOfWork _uow;

    /// <summary>Référence vers le ViewModel émulateur parent — utilisé pour
    /// recharger tous les profils après un ApplyToAll.</summary>
    public EmulatorItemViewModel? ParentVm { get; set; }

    [ObservableProperty] private string  _profileName;
    [ObservableProperty] private int     _platformId;
    [ObservableProperty] private string  _platformName = string.Empty;
    [ObservableProperty] private string  _commandLine;
    [ObservableProperty] private string? _workingDirectory;
    [ObservableProperty] private string? _configFilePath;
    [ObservableProperty] private bool    _isDefault;
    [ObservableProperty] private bool    _fullScreen;
    [ObservableProperty] private string? _preLaunchScript;
    [ObservableProperty] private string? _notes;
    [ObservableProperty] private bool    _isEditing;
    [ObservableProperty] private bool    _isSaving;

    // Plateformes disponibles pour le sélecteur
    [ObservableProperty]
    private ObservableCollection<Platform> _availablePlatforms = [];

    // Variables disponibles (aide à la saisie)
    public static string VariablesHelp =>
        "{file} = chemin du fichier  ·  {dir} = répertoire  ·  {filename} = nom sans extension  ·  {ext} = extension";

    public ProfileViewModel(EmulatorConfig config, IUnitOfWork uow)
    {
        Config           = config;
        _uow             = uow;
        _profileName     = config.ProfileName;
        _platformId      = config.PlatformId;
        _platformName    = config.Platform?.Name ?? "";
        _commandLine     = config.CommandLine;
        _workingDirectory = config.WorkingDirectory;
        _configFilePath  = ToRelative(config.ConfigFilePath);
        _isDefault       = config.IsDefault;
        _fullScreen      = config.FullScreen;
        _preLaunchScript = config.PreLaunchScript;
        _notes           = config.Notes;

        _ = LoadPlatformsAsync();

        // Réglages spécifiques au type d'émulateur (WinUAE/Altirra/Hatari) — portés
        // par CE profil (Config.Id), pas par l'émulateur : deux profils du même
        // émulateur (ex. "Atari ST 512K" / "Atari ST 1024K") ont chacun les leurs.
        // Config.Emulator est déjà chargé (Include côté repository), donc accessible
        // directement sans requête supplémentaire.
        var emulatorType = config.Emulator?.EmulatorType;
        if (emulatorType == EmulatorType.WinUAE)
            _ = LoadWinUAESettingsAsync();
        else if (emulatorType == EmulatorType.Altirra)
            _ = LoadAltirraSettingsAsync();
        else if (emulatorType == EmulatorType.Hatari)
            _ = LoadHatariSettingsAsync();
        else if (emulatorType == EmulatorType.Cpcec)
            _ = LoadCpcecSettingsAsync();
        else if (emulatorType == EmulatorType.Zxsec)
            _ = LoadZxsecSettingsAsync();
        else if (emulatorType == EmulatorType.Csfec)
            _ = LoadCsfecSettingsAsync();
        else if (emulatorType == EmulatorType.Msxec)
            _ = LoadMsxecSettingsAsync();
        else if (emulatorType == EmulatorType.DOSBox)
            _ = LoadDosBoxXSettingsAsync();
        else if (emulatorType == EmulatorType.ViceC64)
            _ = LoadViceC64SettingsAsync();
        else if (emulatorType == EmulatorType.ViceC128)
            _ = LoadViceC128SettingsAsync();
        else if (emulatorType == EmulatorType.ViceVic20)
            _ = LoadViceVic20SettingsAsync();
        else if (emulatorType == EmulatorType.VicePet)
            _ = LoadVicePetSettingsAsync();
        else if (emulatorType == EmulatorType.ViceC64Dtv)
            _ = LoadViceC64DtvSettingsAsync();
        else if (emulatorType == EmulatorType.VicePlus4)
            _ = LoadVicePlus4SettingsAsync();
        else if (emulatorType == EmulatorType.Tic80)
            _ = LoadTic80SettingsAsync();
        else if (emulatorType == EmulatorType.MicroW8)
            _ = LoadMicroW8SettingsAsync();
        else if (emulatorType == EmulatorType.UnrealSpeccy)
            _ = LoadUnrealSpeccySettingsAsync();
        else if (emulatorType == EmulatorType.EightyOne)
            _ = LoadEightyOneSettingsAsync();
        else if (emulatorType == EmulatorType.ZEsarUX)
            _ = LoadZEsarUXSettingsAsync();
        else if (emulatorType == EmulatorType.KegaFusion)
            _ = LoadKegaFusionSettingsAsync();
        else if (emulatorType == EmulatorType.Browser)
            _ = LoadBrowserSettingsAsync();
        else if (emulatorType == EmulatorType.Java)
            _ = LoadJavaSettingsAsync();
        else if (emulatorType == EmulatorType.Fuse)
            _ = LoadFuseSettingsAsync();
        else if (emulatorType == EmulatorType.BlastEm)
            _ = LoadBlastEmSettingsAsync();
        else if (emulatorType == EmulatorType.Arculator)
            _ = LoadArculatorSettingsAsync();
        else if (emulatorType == EmulatorType.PPSSPP)
            _ = LoadPPSSPPSettingsAsync();
        else if (emulatorType == EmulatorType.BlueMSX)
            _ = LoadBlueMSXSettingsAsync();
        else if (emulatorType == EmulatorType.DuckStation)
            _ = LoadDuckStationSettingsAsync();
        else if (emulatorType == EmulatorType.PuNES)
            _ = LoadPuNESSettingsAsync();
        else if (emulatorType == EmulatorType.Ares)
            _ = LoadAresSettingsAsync();
        else if (emulatorType == EmulatorType.Ruffle)
            _ = LoadRuffleSettingsAsync();
        else if (emulatorType == EmulatorType.Mame)
            _ = LoadMameSettingsAsync();
        else if (emulatorType == EmulatorType.Stella)
            _ = LoadStellaSettingsAsync();
        else if (emulatorType == EmulatorType.ProSystem)
            _ = LoadProSystemSettingsAsync();
        else if (emulatorType == EmulatorType.Xenia)
            _ = LoadXeniaSettingsAsync();
        else if (emulatorType == EmulatorType.CxbxReloaded)
            _ = LoadCxbxReloadedSettingsAsync();
        else if (emulatorType == EmulatorType.AppleWin)
            _ = LoadAppleWinSettingsAsync();
        else if (emulatorType == EmulatorType.GSplus)
            _ = LoadGSplusSettingsAsync();
        else if (emulatorType == EmulatorType.Pemsa)
            _ = LoadPemsaSettingsAsync();
        else if (emulatorType == EmulatorType.Mesen)
            _ = LoadMesenSettingsAsync();
        else if (emulatorType == EmulatorType.Azahar)
            _ = LoadAzaharSettingsAsync();
        else if (emulatorType == EmulatorType.Pcsx2)
            _ = LoadPcsx2SettingsAsync();
        else if (emulatorType == EmulatorType.Trs80gp)
            _ = LoadTrs80gpSettingsAsync();
        else if (emulatorType == EmulatorType.Oricutron)
            _ = LoadOricutronSettingsAsync();
        else if (emulatorType == EmulatorType.Dolphin)
            _ = LoadDolphinSettingsAsync();
        else if (emulatorType == EmulatorType.SimCoupe)
            _ = LoadSimCoupeSettingsAsync();
        else if (emulatorType == EmulatorType.Flycast)
            _ = LoadFlycastSettingsAsync();
        else if (emulatorType == EmulatorType.JzIntv)
            _ = LoadJzIntvSettingsAsync();
        else if (emulatorType == EmulatorType.Dcmoto)
            _ = LoadDcmotoSettingsAsync();
        else if (emulatorType == EmulatorType.Xm6TypeG)
            _ = LoadXm6TypeGSettingsAsync();
        else if (emulatorType == EmulatorType.BeebEm)
            _ = LoadBeebEmSettingsAsync();
        else if (emulatorType == EmulatorType.SQLux)
            _ = LoadSQLuxSettingsAsync();
        // Ryujinx / RPCS3 retirés le 2026-07-24 (enum conservé, plus de settings dédiés).
        else if (emulatorType == EmulatorType.BigPEmu)
            _ = LoadBigPEmuSettingsAsync();
        else if (emulatorType == EmulatorType.Handy)
            _ = LoadHandySettingsAsync();
        else if (emulatorType == EmulatorType.GeePee32)
            _ = LoadGeePee32SettingsAsync();
        else if (emulatorType == EmulatorType.Ep128Emu)
            _ = LoadEp128EmuSettingsAsync();
        else if (emulatorType == EmulatorType.Mz800Emu)
            _ = LoadMz800EmuSettingsAsync();
        else if (emulatorType == EmulatorType.ColEm)
            _ = LoadColEmSettingsAsync();
    }

    // ── WinUAE Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private WinUAESettingsViewModel? _winUAESettings;
    // ── Altirra Settings ─────────────────────────────────────────────────────
    [ObservableProperty] private AltirraSettingsViewModel? _altirraSettings;
    // ── Hatari Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private HatariSettingsViewModel? _hatariSettings;
    // ── CPCEC Settings ───────────────────────────────────────────────────────
    [ObservableProperty] private CpcecSettingsViewModel? _cpcecSettings;
    // ── ZXSEC Settings ───────────────────────────────────────────────────────
    [ObservableProperty] private ZxsecSettingsViewModel? _zxsecSettings;
    // ── CSFEC Settings ───────────────────────────────────────────────────────
    [ObservableProperty] private CsfecSettingsViewModel? _csfecSettings;
    // ── MSXEC Settings ───────────────────────────────────────────────────────
    [ObservableProperty] private MsxecSettingsViewModel? _msxecSettings;
    // ── DOSBox-X Settings ────────────────────────────────────────────────────
    [ObservableProperty] private DosBoxXSettingsViewModel? _dosBoxXSettings;
    // ── VICE Settings (C64) ──────────────────────────────────────────────────
    [ObservableProperty] private ViceC64SettingsViewModel? _viceC64Settings;
    // ── VICE Settings (C128) ─────────────────────────────────────────────────
    [ObservableProperty] private ViceC128SettingsViewModel? _viceC128Settings;
    // ── VICE Settings (VIC-20) ───────────────────────────────────────────────
    [ObservableProperty] private ViceVic20SettingsViewModel? _viceVic20Settings;
    // ── VICE Settings (PET) ──────────────────────────────────────────────────
    [ObservableProperty] private VicePetSettingsViewModel? _vicePetSettings;
    // ── VICE Settings (C64-DTV) ──────────────────────────────────────────────
    [ObservableProperty] private ViceC64DtvSettingsViewModel? _viceC64DtvSettings;
    // ── VICE Settings (Plus/4, C16) ──────────────────────────────────────────
    [ObservableProperty] private VicePlus4SettingsViewModel? _vicePlus4Settings;
    // ── TIC-80 Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private Tic80SettingsViewModel?    _tic80Settings;
    // ── Pemsa Settings (PICO-8) ──────────────────────────────────────────────
    [ObservableProperty] private PemsaSettingsViewModel?    _pemsaSettings;
    [ObservableProperty] private MesenSettingsViewModel?    _mesenSettings;
    [ObservableProperty] private AzaharSettingsViewModel?   _azaharSettings;
    // ── MicroW8 Settings ─────────────────────────────────────────────────────
    [ObservableProperty] private MicroW8SettingsViewModel?      _microW8Settings;
    // ── UnrealSpeccy Settings ─────────────────────────────────────────────────
    [ObservableProperty] private UnrealSpeccySettingsViewModel? _unrealSpeccySettings;
    // ── EightyOne Settings ────────────────────────────────────────────────────
    [ObservableProperty] private EightyOneSettingsViewModel?    _eightyOneSettings;
    // ── ZEsarUX Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private ZEsarUXSettingsViewModel?      _zEsarUXSettings;
    [ObservableProperty] private KegaFusionSettingsViewModel?   _kegaFusionSettings;
    [ObservableProperty] private BrowserSettingsViewModel?      _browserSettings;
    [ObservableProperty] private JavaSettingsViewModel?         _javaSettings;
    [ObservableProperty] private FuseSettingsViewModel?         _fuseSettings;
    [ObservableProperty] private BlastEmSettingsViewModel?     _blastEmSettings;
    [ObservableProperty] private ArculatorSettingsViewModel?   _arculatorSettings;
    // Propriété manuelle : CommunityToolkit génèrerait PpssppSettings (pas PPSSPPSettings)
    private PPSSPPSettingsViewModel? _ppssppSettings;
    public PPSSPPSettingsViewModel? PPSSPPSettings
    {
        get => _ppssppSettings;
        set => SetProperty(ref _ppssppSettings, value);
    }
    [ObservableProperty] private BlueMSXSettingsViewModel?     _blueMSXSettings;
    [ObservableProperty] private DuckStationSettingsViewModel?  _duckStationSettings;
    [ObservableProperty] private Pcsx2SettingsViewModel?        _pcsx2Settings;
    [ObservableProperty] private Trs80gpSettingsViewModel?      _trs80gpSettings;
    [ObservableProperty] private OricutronSettingsViewModel?    _oricutronSettings;
    [ObservableProperty] private DolphinSettingsViewModel?      _dolphinSettings;
    [ObservableProperty] private SimCoupeSettingsViewModel?     _simCoupeSettings;
    [ObservableProperty] private FlycastSettingsViewModel?      _flycastSettings;
    [ObservableProperty] private JzIntvSettingsViewModel?       _jzIntvSettings;
    [ObservableProperty] private DcmotoSettingsViewModel?       _dcmotoSettings;
    [ObservableProperty] private Xm6TypeGSettingsViewModel?     _xm6TypeGSettings;
    [ObservableProperty] private BeebEmSettingsViewModel?       _beebEmSettings;
    [ObservableProperty] private SQLuxSettingsViewModel?        _sinclairQlSettings;
    [ObservableProperty] private PuNESSettingsViewModel?        _puNESSettings;
    [ObservableProperty] private AresSettingsViewModel?          _aresSettings;
    [ObservableProperty] private RuffleSettingsViewModel?        _ruffleSettings;
    [ObservableProperty] private MameSettingsViewModel?          _mameSettings;
    [ObservableProperty] private StellaSettingsViewModel?        _stellaSettings;
    [ObservableProperty] private ProSystemSettingsViewModel?     _proSystemSettings;
    [ObservableProperty] private XeniaSettingsViewModel?         _xeniaSettings;
    // Ryujinx / RPCS3 Settings retirés le 2026-07-24 (enum conservé).
    // ── BigPEmu Settings ──────────────────────────────────────────────────────
    [ObservableProperty] private BigPEmuSettingsViewModel?        _bigPEmuSettings;
    // ── Handy Settings ────────────────────────────────────────────────────────
    [ObservableProperty] private HandySettingsViewModel?          _handySettings;
    // ── GeePee32 Settings ─────────────────────────────────────────────────────
    [ObservableProperty] private GeePee32SettingsViewModel?       _geePee32Settings;
    // ── ep128emu Settings ─────────────────────────────────────────────────────
    [ObservableProperty] private Ep128EmuSettingsViewModel?       _ep128EmuSettings;
    // ── mz800emu Settings ─────────────────────────────────────────────────────
    [ObservableProperty] private Mz800EmuSettingsViewModel?       _mz800EmuSettings;
    // ── ColEm Settings ─────────────────────────────────────────────────────────
    [ObservableProperty] private ColEmSettingsViewModel?           _colEmSettings;
    [ObservableProperty] private CxbxReloadedSettingsViewModel?  _cxbxReloadedSettings;
    [ObservableProperty] private AppleWinSettingsViewModel?      _appleWinSettings;
    [ObservableProperty] private GSplusSettingsViewModel?        _gSplusSettings;

    public async Task LoadAltirraSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        AltirraSettings = new AltirraSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadHatariSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        HatariSettings = new HatariSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadCpcecSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        CpcecSettings = new CpcecSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadZxsecSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ZxsecSettings = new ZxsecSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadCsfecSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        CsfecSettings = new CsfecSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadMsxecSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        MsxecSettings = new MsxecSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadDosBoxXSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        DosBoxXSettings = new DosBoxXSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadViceC64SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ViceC64Settings = new ViceC64SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadViceC128SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ViceC128Settings = new ViceC128SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadViceVic20SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ViceVic20Settings = new ViceVic20SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadVicePetSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        VicePetSettings = new VicePetSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadViceC64DtvSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ViceC64DtvSettings = new ViceC64DtvSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadVicePlus4SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        VicePlus4Settings = new VicePlus4SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadTic80SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Tic80Settings = new Tic80SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadPemsaSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        PemsaSettings = new PemsaSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadMesenSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        MesenSettings = new MesenSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadAzaharSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        AzaharSettings = new AzaharSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadMicroW8SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        MicroW8Settings = new MicroW8SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadUnrealSpeccySettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        UnrealSpeccySettings = new UnrealSpeccySettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadEightyOneSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        EightyOneSettings = new EightyOneSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadZEsarUXSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ZEsarUXSettings = new ZEsarUXSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadKegaFusionSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        KegaFusionSettings = new KegaFusionSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadBrowserSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        BrowserSettings = new BrowserSettingsViewModel(Config.Id, settings, _uow, Config.Emulator?.ExecutablePath);
    }

    public async Task LoadJavaSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        JavaSettings = new JavaSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadFuseSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        FuseSettings = new FuseSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadBlastEmSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        BlastEmSettings = new BlastEmSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadArculatorSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ArculatorSettings = new ArculatorSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadPPSSPPSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        PPSSPPSettings = new PPSSPPSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadBlueMSXSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        BlueMSXSettings = new BlueMSXSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadDuckStationSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        DuckStationSettings = new DuckStationSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadSQLuxSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        SinclairQlSettings = new SQLuxSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadBeebEmSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        BeebEmSettings = new BeebEmSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadXm6TypeGSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Xm6TypeGSettings = new Xm6TypeGSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadDcmotoSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        DcmotoSettings = new DcmotoSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadJzIntvSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        JzIntvSettings = new JzIntvSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadFlycastSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        FlycastSettings = new FlycastSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadSimCoupeSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        SimCoupeSettings = new SimCoupeSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadDolphinSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        DolphinSettings = new DolphinSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadOricutronSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        OricutronSettings = new OricutronSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadTrs80gpSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Trs80gpSettings = new Trs80gpSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadPcsx2SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Pcsx2Settings = new Pcsx2SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadPuNESSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        PuNESSettings = new PuNESSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadAresSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        AresSettings = new AresSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadRuffleSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        RuffleSettings = new RuffleSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadMameSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        MameSettings = new MameSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadStellaSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        StellaSettings = new StellaSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadProSystemSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ProSystemSettings = new ProSystemSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadXeniaSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        XeniaSettings = new XeniaSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadBigPEmuSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        BigPEmuSettings = new BigPEmuSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadHandySettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        HandySettings = new HandySettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadGeePee32SettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        GeePee32Settings = new GeePee32SettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadEp128EmuSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Ep128EmuSettings = new Ep128EmuSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadMz800EmuSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        Mz800EmuSettings = new Mz800EmuSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadColEmSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        ColEmSettings = new ColEmSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadCxbxReloadedSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        CxbxReloadedSettings = new CxbxReloadedSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadWinUAESettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        WinUAESettings = new WinUAESettingsViewModel(Config.Id, settings, _uow,
            emulatorIdForApply: Config.EmulatorId,
            onAppliedToAll: async () =>
            {
                // Recharger le WinUAESettings de tous les profils actifs en mémoire
                // pour que l'affichage reflète la nouvelle valeur d'écran
                var parent = ParentVm;
                if (parent == null) return;
                foreach (var p in parent.Profiles)
                    if (p.WinUAESettings != null)
                        await p.LoadWinUAESettingsAsync();
            });
    }

    public async Task LoadAppleWinSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        AppleWinSettings = new AppleWinSettingsViewModel(Config.Id, settings, _uow);
    }

    public async Task LoadGSplusSettingsAsync()
    {
        var settings = await _uow.Emulators.GetSettingsAsync(Config.Id);
        GSplusSettings = new GSplusSettingsViewModel(Config.Id, settings, _uow);
    }

    private async Task LoadPlatformsAsync()
    {
        var platforms = (await _uow.Platforms.GetAllAsync()).OrderBy(p => p.Name);
        AvailablePlatforms = new ObservableCollection<Platform>(platforms);
    }

    [RelayCommand]
    private void BrowseConfigFile()
    {
        var configsDir = DemoBase.App.Services.AppPaths.Configs;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Sélectionner le fichier de configuration",
            InitialDirectory = System.IO.Directory.Exists(configsDir) ? configsDir : AppContext.BaseDirectory,
            // Filtre volontairement ouvert : certains fichiers de config d'émulateurs
            // n'ont pas d'extension standard (*.uae/*.cfg/*.ini/*.conf) — trop restrictif.
            Filter = "Tous les fichiers (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            ConfigFilePath = ToRelative(dlg.FileName);
    }

    // ── Chemin relatif / absolu (même principe que EmulatorItemViewModel) ─────
    // Config.ConfigFilePath reste TOUJOURS absolu en base (WinUAELauncher,
    // HatariLauncher, etc. l'utilisent tel quel) — seuls l'affichage et
    // l'édition dans cette page convertissent en relatif.

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
        return Path.IsPathRooted(path)
            ? path
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            // Recherché AVANT d'assigner PlatformId : si on ne resynchronise pas aussi la
            // navigation Config.Platform (qui pointe encore sur l'ANCIENNE plateforme, chargée
            // via .Include() à la création du profil), EF Core fait le "fixup" de la relation
            // depuis cette navigation encore en mémoire et écrase silencieusement le PlatformId
            // qu'on vient de modifier au moment du SaveChangesAsync — le profil revient alors
            // sur l'ancienne plateforme après l'enregistrement, alors que rien ne signale
            // d'erreur côté UI.
            var plat = AvailablePlatforms.FirstOrDefault(p => p.Id == PlatformId);

            Config.ProfileName      = ProfileName.Trim();
            Config.PlatformId       = PlatformId;
            if (plat != null) Config.Platform = plat;
            Config.CommandLine      = CommandLine.Trim();
            Config.WorkingDirectory = string.IsNullOrWhiteSpace(WorkingDirectory) ? null : WorkingDirectory.Trim();
            Config.ConfigFilePath   = string.IsNullOrWhiteSpace(ConfigFilePath)   ? null : ToAbsolute(ConfigFilePath.Trim());
            Config.IsDefault        = IsDefault;
            Config.FullScreen       = FullScreen;
            Config.PreLaunchScript  = string.IsNullOrWhiteSpace(PreLaunchScript)  ? null : PreLaunchScript.Trim();
            Config.Notes            = string.IsNullOrWhiteSpace(Notes)            ? null : Notes.Trim();

            await _uow.Emulators.UpdateConfigAsync(Config);

            // Un seul profil par défaut par plateforme (tous émulateurs confondus) —
            // nécessaire maintenant qu'on peut avoir plusieurs profils sur la même
            // plateforme (ex. "Atari ST 512K" / "Atari ST 1024K"). À faire APRÈS la
            // sauvegarde de CE profil, pour ne pas se dé-défaulter soi-même au passage.
            if (IsDefault)
            {
                await _uow.Emulators.ClearDefaultForPlatformAsync(PlatformId, Config.Id);

                // Rafraîchir l'affichage : ClearDefaultForPlatformAsync corrige bien la BDD,
                // mais les autres ProfileViewModel déjà en mémoire (profils du même émulateur
                // ciblant la même plateforme, ex. "Atari ST 512K" / "Atari ST 1024K") gardaient
                // sinon le badge "Par défaut" affiché jusqu'au rechargement complet de la page.
                if (ParentVm != null)
                {
                    foreach (var sibling in ParentVm.Profiles)
                    {
                        if (ReferenceEquals(sibling, this)) continue;
                        if (sibling.PlatformId == PlatformId && sibling.IsDefault)
                        {
                            sibling.IsDefault        = false;
                            sibling.Config.IsDefault = false;
                        }
                    }
                }
            }

            // Sauvegarder aussi les réglages spécifiques à l'émulateur (Hatari/Altirra/WinUAE) —
            // un seul bouton enregistre tout désormais, plutôt que deux sauvegardes séparées
            // (le profil lui-même, puis la configuration spécifique) qui perturbaient
            // l'utilisateur : il fallait penser à cliquer les deux pour ne rien perdre.
            if (WinUAESettings != null)
                await WinUAESettings.SaveWinUAESettingsCommand.ExecuteAsync(null);
            else if (AltirraSettings != null)
                await AltirraSettings.SaveAltirraSettingsCommand.ExecuteAsync(null);
            else if (HatariSettings != null)
                await HatariSettings.SaveHatariSettingsCommand.ExecuteAsync(null);
            else if (CpcecSettings != null)
                await CpcecSettings.SaveCpcecSettingsCommand.ExecuteAsync(null);
            else if (ZxsecSettings != null)
                await ZxsecSettings.SaveZxsecSettingsCommand.ExecuteAsync(null);
            else if (CsfecSettings != null)
                await CsfecSettings.SaveCsfecSettingsCommand.ExecuteAsync(null);
            else if (MsxecSettings != null)
                await MsxecSettings.SaveMsxecSettingsCommand.ExecuteAsync(null);
            else if (DosBoxXSettings != null)
                await DosBoxXSettings.SaveDosBoxXSettingsCommand.ExecuteAsync(null);
            else if (ViceC64Settings != null)
                await ViceC64Settings.SaveViceC64SettingsCommand.ExecuteAsync(null);
            else if (ViceC128Settings != null)
                await ViceC128Settings.SaveViceC128SettingsCommand.ExecuteAsync(null);
            else if (ViceVic20Settings != null)
                await ViceVic20Settings.SaveViceVic20SettingsCommand.ExecuteAsync(null);
            else if (VicePetSettings != null)
                await VicePetSettings.SaveVicePetSettingsCommand.ExecuteAsync(null);
            else if (ViceC64DtvSettings != null)
                await ViceC64DtvSettings.SaveViceC64DtvSettingsCommand.ExecuteAsync(null);
            else if (VicePlus4Settings != null)
                await VicePlus4Settings.SaveVicePlus4SettingsCommand.ExecuteAsync(null);
            else if (Tic80Settings != null)
                await Tic80Settings.SaveCommand.ExecuteAsync(null);
            else if (MicroW8Settings != null)
                await MicroW8Settings.SaveCommand.ExecuteAsync(null);
            else if (UnrealSpeccySettings != null)
                await UnrealSpeccySettings.SaveCommand.ExecuteAsync(null);
            else if (EightyOneSettings != null)
                await EightyOneSettings.SaveCommand.ExecuteAsync(null);
            else if (ZEsarUXSettings != null)
                await ZEsarUXSettings.SaveCommand.ExecuteAsync(null);
            else if (KegaFusionSettings != null)
                await KegaFusionSettings.SaveCommand.ExecuteAsync(null);
            else if (BrowserSettings != null)
                await BrowserSettings.SaveCommand.ExecuteAsync(null);
            else if (JavaSettings != null)
                await JavaSettings.SaveCommand.ExecuteAsync(null);
            else if (FuseSettings != null)
                await FuseSettings.SaveCommand.ExecuteAsync(null);
            else if (BlastEmSettings != null)
                await BlastEmSettings.SaveCommand.ExecuteAsync(null);
            else if (ArculatorSettings != null)
                await ArculatorSettings.SaveCommand.ExecuteAsync(null);
            else if (PPSSPPSettings != null)
                await PPSSPPSettings.SaveCommand.ExecuteAsync(null);
            else if (BlueMSXSettings != null)
                await BlueMSXSettings.SaveCommand.ExecuteAsync(null);
            else if (DuckStationSettings != null)
                await DuckStationSettings.SaveCommand.ExecuteAsync(null);
            else if (Pcsx2Settings != null)
                await Pcsx2Settings.SaveCommand.ExecuteAsync(null);
            else if (Trs80gpSettings != null)
                await Trs80gpSettings.SaveCommand.ExecuteAsync(null);
            else if (OricutronSettings != null)
                await OricutronSettings.SaveCommand.ExecuteAsync(null);
            else if (DolphinSettings != null)
                await DolphinSettings.SaveCommand.ExecuteAsync(null);
            else if (SimCoupeSettings != null)
                await SimCoupeSettings.SaveCommand.ExecuteAsync(null);
            else if (FlycastSettings != null)
                await FlycastSettings.SaveCommand.ExecuteAsync(null);
            else if (JzIntvSettings != null)
                await JzIntvSettings.SaveCommand.ExecuteAsync(null);
            else if (DcmotoSettings != null)
                await DcmotoSettings.SaveCommand.ExecuteAsync(null);
            else if (Xm6TypeGSettings != null)
                await Xm6TypeGSettings.SaveCommand.ExecuteAsync(null);
            else if (BeebEmSettings != null)
                await BeebEmSettings.SaveCommand.ExecuteAsync(null);
            else if (SinclairQlSettings != null)
                await SinclairQlSettings.SaveCommand.ExecuteAsync(null);
            else if (PuNESSettings != null)
                await PuNESSettings.SaveCommand.ExecuteAsync(null);
            else if (AresSettings != null)
                await AresSettings.SaveCommand.ExecuteAsync(null);
            else if (RuffleSettings != null)
                await RuffleSettings.SaveCommand.ExecuteAsync(null);
            else if (MameSettings != null)
                await MameSettings.SaveCommand.ExecuteAsync(null);
            else if (MesenSettings != null)
                await MesenSettings.SaveCommand.ExecuteAsync(null);
            else if (AzaharSettings != null)
                await AzaharSettings.SaveCommand.ExecuteAsync(null);
            else if (StellaSettings != null)
                await StellaSettings.SaveCommand.ExecuteAsync(null);
            else if (ProSystemSettings != null)
                await ProSystemSettings.SaveCommand.ExecuteAsync(null);
            else if (XeniaSettings != null)
                await XeniaSettings.SaveCommand.ExecuteAsync(null);
            else if (BigPEmuSettings != null)
                await BigPEmuSettings.SaveCommand.ExecuteAsync(null);
            else if (HandySettings != null)
                await HandySettings.SaveCommand.ExecuteAsync(null);
            else if (GeePee32Settings != null)
                await GeePee32Settings.SaveCommand.ExecuteAsync(null);
            else if (Ep128EmuSettings != null)
                await Ep128EmuSettings.SaveCommand.ExecuteAsync(null);
            else if (Mz800EmuSettings != null)
                await Mz800EmuSettings.SaveCommand.ExecuteAsync(null);
            else if (ColEmSettings != null)
                await ColEmSettings.SaveCommand.ExecuteAsync(null);
            else if (CxbxReloadedSettings != null)
                await CxbxReloadedSettings.SaveCommand.ExecuteAsync(null);
            else if (AppleWinSettings != null)
                await AppleWinSettings.SaveCommand.ExecuteAsync(null);
            else if (GSplusSettings != null)
                await GSplusSettings.SaveCommand.ExecuteAsync(null);

            if (plat != null) PlatformName = plat.Name;

            IsEditing = false;
        }
        finally { IsSaving = false; }
    }

    [RelayCommand]
    private void Edit() => IsEditing = true;

    [RelayCommand]
    private void CancelEdit()
    {
        ProfileName      = Config.ProfileName;
        PlatformId       = Config.PlatformId;
        CommandLine      = Config.CommandLine;
        WorkingDirectory = Config.WorkingDirectory;
        ConfigFilePath   = ToRelative(Config.ConfigFilePath);
        IsDefault        = Config.IsDefault;
        FullScreen       = Config.FullScreen;
        PreLaunchScript  = Config.PreLaunchScript;
        Notes            = Config.Notes;
        IsEditing        = false;
    }

    // Test rapide de la ligne de commande
    [RelayCommand]
    private void TestCommandLine()
    {
        const string testFile = @"C:\demo\example.exe";
        var result = DemoBase.App.Services.EmulatorLaunchService
            .SubstituteVars(CommandLine, testFile);
        System.Windows.MessageBox.Show(
            $"Ligne de commande générée :\n\n{result}",
            "Test", System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Information);
    }
}

// ─── WinUAESettingsViewModel ──────────────────────────────────────────────────
// Gère les paramètres spécifiques à WinUAE (stockés dans EmulatorSettings),
// PAR PROFIL (EmulatorConfig) — deux profils du même émulateur WinUAE (ex.
// "Amiga 1200" et "Amiga 4000") ont chacun leurs propres valeurs.

public partial class WinUAESettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    // ── Clés de settings ──────────────────────────────────────────────────────
    public const string KEY_KICKSTART        = "kickstart_path";
    public const string KEY_DISPLAY_FRIENDLY = "gfx_display_friendlyname";
    public const string KEY_DISPLAY_NAME     = "gfx_display_name";
    public const string KEY_DISPLAY_INDEX    = "gfx_display";
    // Cases "CPU options" de WinUAE — "auto" (défaut) laisse le .uae de base inchangé ;
    // "true"/"false" force la valeur, pour éviter d'avoir à dupliquer un .uae juste pour
    // ces 2 cases à cocher (cf. WinUAELauncher.ApplyCycleExactOverrides).
    public const string KEY_CYCLE_EXACT        = "cpu_cycle_exact";
    public const string KEY_MEMORY_CYCLE_EXACT = "cpu_memory_cycle_exact";

    private readonly int        _emulatorIdForApply;
    private readonly Func<Task>? _onAppliedToAll;

    [ObservableProperty] private string  _kickstartPath    = string.Empty;
    [ObservableProperty] private string? _uaeDisplayFriendly;
    [ObservableProperty] private string? _uaeDisplayName;
    [ObservableProperty] private string? _uaeDisplayIndex;  // ex. "2"
    [ObservableProperty] private string  _cycleExact       = "auto"; // "auto" | "true" | "false"
    [ObservableProperty] private string  _memoryCycleExact = "auto"; // "auto" | "true" | "false"
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private bool    _isApplyingToAll;
    [ObservableProperty] private string? _saveMessage;

    public WinUAESettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow,
        int emulatorIdForApply = 0,
        Func<Task>? onAppliedToAll = null)
    {
        _emulatorConfigId   = emulatorConfigId;
        _uow                = uow;
        _emulatorIdForApply = emulatorIdForApply;
        _onAppliedToAll     = onAppliedToAll;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        KickstartPath   = ToRelative(s.GetValueOrDefault(KEY_KICKSTART));
        UaeDisplayFriendly = s.GetValueOrDefault(KEY_DISPLAY_FRIENDLY);
        UaeDisplayName     = s.GetValueOrDefault(KEY_DISPLAY_NAME);
        UaeDisplayIndex    = s.GetValueOrDefault(KEY_DISPLAY_INDEX);
        CycleExact         = s.GetValueOrDefault(KEY_CYCLE_EXACT)        ?? "auto";
        MemoryCycleExact   = s.GetValueOrDefault(KEY_MEMORY_CYCLE_EXACT) ?? "auto";
    }

    [RelayCommand]
    private void BrowseKickstart()
    {
        // Sous-dossier "Amiga" du BIOS configuré (pas AppContext.BaseDirectory en
        // dur) — respecte le chemin choisi par l'utilisateur dans le wizard/les
        // préférences, avec repli sur le dossier BIOS racine puis le dossier de
        // l'app si aucun des deux n'existe encore.
        var biosRoot = DemoBase.App.Services.AppPaths.Bios;
        var biosDir  = System.IO.Path.Combine(biosRoot, "Amiga");
        var initialDir = System.IO.Directory.Exists(biosDir) ? biosDir
                        : System.IO.Directory.Exists(biosRoot) ? biosRoot
                        : AppContext.BaseDirectory;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Sélectionner le fichier Kickstart ROM",
            InitialDirectory = initialDir,
            // Filtre ouvert : les ROMs Kickstart du pack BIOS ont des extensions non
            // standard (ex. .A500, .A1200...) non couvertes par *.rom;*.bin.
            Filter           = "Tous les fichiers (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            KickstartPath = ToRelative(dlg.FileName);
    }

    // ── Chemin relatif / absolu ────────────────────────────────────────────────
    // Les settings restent stockés en absolu (WinUAELauncher les utilise tels
    // quels) — seuls l'affichage et l'édition convertissent en relatif.
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

    [RelayCommand]
    private async Task SaveWinUAESettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_KICKSTART]        = ToAbsolute(KickstartPath),
                [KEY_DISPLAY_FRIENDLY] = UaeDisplayFriendly,
                [KEY_DISPLAY_NAME]     = UaeDisplayName,
                [KEY_DISPLAY_INDEX]    = UaeDisplayIndex,
                [KEY_CYCLE_EXACT]        = CycleExact,
                [KEY_MEMORY_CYCLE_EXACT] = MemoryCycleExact,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }

    /// <summary>
    /// Applique les 3 réglages écran (gfx_display_friendlyname, gfx_display_name,
    /// gfx_display) à TOUTES les configurations WinUAE du même émulateur,
    /// sans toucher au Kickstart propre à chaque profil.
    /// </summary>
    [RelayCommand]
    private async Task ApplyDisplayToAllConfigs()
    {
        if (_emulatorIdForApply == 0) return;
        IsApplyingToAll = true;
        SaveMessage = null;
        try
        {
            var configs = (await _uow.Emulators.GetConfigsForEmulatorAsync(_emulatorIdForApply)).ToList();
            var displaySettings = new Dictionary<string, string?>
            {
                [KEY_DISPLAY_FRIENDLY] = UaeDisplayFriendly,
                [KEY_DISPLAY_NAME]     = UaeDisplayName,
                [KEY_DISPLAY_INDEX]    = UaeDisplayIndex,
            };
            foreach (var cfg in configs)
                await _uow.Emulators.SaveSettingsAsync(cfg.Id, displaySettings);
            SaveMessage = $"✓ Appliqué à {configs.Count} configuration(s)";
            if (_onAppliedToAll != null)
                await _onAppliedToAll();
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsApplyingToAll = false; }
    }
}

// ─── AltirraSettingsViewModel ─────────────────────────────────────────────────
// Gère les paramètres spécifiques à Altirra (stockés dans EmulatorSettings),
// PAR PROFIL (EmulatorConfig). Contrairement à WinUAE, les valeurs stockées
// sont les tokens bruts attendus par la ligne de commande Altirra
// ("800"/"800xl"/"5200", "pal"/"ntsc", ...), portés par le Tag de chaque
// ComboBoxItem — pas par son Content affiché.

public partial class AltirraSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    // ── Clés de settings ──────────────────────────────────────────────────────
    public const string KEY_HARDWARE     = "hardware_model";
    public const string KEY_VIDEO        = "video_standard";
    public const string KEY_BASIC        = "basic_enabled";
    public const string KEY_ARTIFACT     = "artifacting";
    public const string KEY_FULLSCREEN   = "fullscreen";
    public const string KEY_NOBORDERLESS = "no_borderless";

    [ObservableProperty] private string _hardwareModel = "800";
    [ObservableProperty] private string _videoStandard = "pal";
    [ObservableProperty] private bool   _basicEnabled  = false;
    [ObservableProperty] private string _artifacting   = "none";
    [ObservableProperty] private bool   _fullScreen    = false;
    [ObservableProperty] private bool   _noBorderless  = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public AltirraSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        HardwareModel = s.GetValueOrDefault(KEY_HARDWARE)     ?? "800";
        VideoStandard = s.GetValueOrDefault(KEY_VIDEO)        ?? "pal";
        BasicEnabled  = s.GetValueOrDefault(KEY_BASIC)        == "true";
        Artifacting   = s.GetValueOrDefault(KEY_ARTIFACT)     ?? "none";
        FullScreen    = s.GetValueOrDefault(KEY_FULLSCREEN)   == "true";
        NoBorderless  = s.GetValueOrDefault(KEY_NOBORDERLESS) == "true";
    }

    [RelayCommand]
    private async Task SaveAltirraSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_HARDWARE]     = HardwareModel,
                [KEY_VIDEO]        = VideoStandard,
                [KEY_BASIC]        = BasicEnabled.ToString().ToLower(),
                [KEY_ARTIFACT]     = Artifacting,
                [KEY_FULLSCREEN]   = FullScreen.ToString().ToLower(),
                [KEY_NOBORDERLESS] = NoBorderless.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── HatariSettingsViewModel ────────────────────────────────────────────────
// Gère les paramètres spécifiques à Hatari (stockés dans EmulatorSettings),
// PAR PROFIL (EmulatorConfig). Comme pour Altirra, les valeurs stockées sont
// les tokens bruts attendus par la ligne de commande Hatari ("st"/"ste"/"tt"/
// "falcon", "rgb"/"mono"/...), portés par le Tag de chaque ComboBoxItem — pas
// par son Content affiché.

public partial class HatariSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    // ── Clés de settings ──────────────────────────────────────────────────────
    public const string KEY_MACHINE    = "machine_type";
    public const string KEY_MONITOR    = "monitor";
    public const string KEY_TOS        = "tos_path";
    public const string KEY_BORDERS    = "borders";
    public const string KEY_FULLSCREEN = "fullscreen";
    public const string KEY_STATUSBAR  = "statusbar";
    public const string KEY_DRIVELED   = "drive_led";
    public const string KEY_STRAM      = "st_ram";
    public const string KEY_TTRAM      = "tt_ram";
    public const string KEY_FASTBOOT   = "fast_boot";
    public const string KEY_TIMERD     = "timer_d";
    // Fenêtre "CPU emulation parameters" de Hatari (cf. HatariLauncher / manuel officiel).
    public const string KEY_PREFETCH     = "prefetch";
    public const string KEY_CPUEXACT     = "cpu_exact";
    public const string KEY_DATACACHE    = "data_cache";
    public const string KEY_MMU          = "mmu";
    public const string KEY_ADDR24       = "addr24";
    public const string KEY_FPUSOFTFLOAT = "fpu_softfloat";
    // Résolution VDI étendue (bas du dialogue "Atari monitor" de Hatari).
    public const string KEY_VDIENABLED = "vdi_enabled";
    public const string KEY_VDIWIDTH   = "vdi_width";
    public const string KEY_VDIHEIGHT  = "vdi_height";
    public const string KEY_VDIPLANES  = "vdi_planes";
    // Dialogue "CPU options" de Hatari. "auto" = comportement historique DemoBase
    // (cpulevel déduit de la machine, cpuclock/fpu non forcés).
    public const string KEY_CPUTYPE  = "cpu_type";
    public const string KEY_CPUCLOCK = "cpu_clock";
    public const string KEY_FPU      = "fpu";

    [ObservableProperty] private string _machineType  = "st";
    [ObservableProperty] private string _monitor      = "rgb";
    [ObservableProperty] private string _tosPath      = string.Empty;
    [ObservableProperty] private bool   _borders      = true;
    [ObservableProperty] private bool   _fullScreen   = false;
    [ObservableProperty] private bool   _statusBar    = true;
    [ObservableProperty] private bool   _driveLed     = false;
    // Valeur brute passée à --memsize (cf. HatariLauncher) : "256"/"0"/"1"/"2"/"2560"/
    // "4"/"8"/"10"/"14" — pas directement le nombre de MiB affiché (256 KiB et 2.5 MiB
    // se codent différemment, cf. manuel Hatari).
    [ObservableProperty] private string _stRam        = "1";
    // Valeur brute passée à --ttram, en MiB (0 = désactivé) — pertinent seulement en
    // mode TT/Falcon.
    [ObservableProperty] private string _ttRam        = "0";
    // Accélération du boot / émulation (activées par défaut, cf. HatariLauncher).
    [ObservableProperty] private bool   _fastBoot     = true;
    [ObservableProperty] private bool   _timerD       = true;
    // Défauts alignés sur la GUI Hatari pour un CPU >=030 (cf. HatariLauncher) : cycle-exact
    // et data-cache activés, le reste désactivé — "expérimental" pour la plupart d'après le
    // manuel officiel, à ne changer que si besoin précis pour une demo donnée.
    [ObservableProperty] private bool   _prefetch     = false;
    [ObservableProperty] private bool   _cpuExact     = true;
    [ObservableProperty] private bool   _dataCache    = true;
    [ObservableProperty] private bool   _mmu          = false;
    [ObservableProperty] private bool   _addr24       = false;
    [ObservableProperty] private bool   _fpuSoftfloat = false;
    // VDI étendu désactivé par défaut (cf. HatariLauncher : 99% des demos/jeux n'en ont
    // pas besoin) ; taille/profondeur par défaut = valeurs par défaut de la GUI Hatari.
    [ObservableProperty] private bool   _vdiEnabled   = false;
    [ObservableProperty] private string _vdiWidth     = "640";
    [ObservableProperty] private string _vdiHeight    = "480";
    [ObservableProperty] private string _vdiPlanes    = "4"; // 1=2 couleurs, 2=4 couleurs, 4=16 couleurs
    // "auto" = comportement historique DemoBase (cpulevel déduit de la machine,
    // cpuclock/fpu laissés aux défauts de Hatari selon la machine choisie).
    [ObservableProperty] private string _cpuType  = "auto";
    [ObservableProperty] private string _cpuClock = "auto";
    [ObservableProperty] private string _fpu      = "auto";
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public HatariSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        MachineType = s.GetValueOrDefault(KEY_MACHINE)    ?? "st";
        Monitor     = s.GetValueOrDefault(KEY_MONITOR)    ?? "rgb";
        TosPath     = ToRelative(s.GetValueOrDefault(KEY_TOS));
        Borders     = s.GetValueOrDefault(KEY_BORDERS)    != "false";
        FullScreen  = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
        // Mêmes valeurs par défaut que la GUI officielle de Hatari : barre de statut
        // visible, LED disque en overlay masquée (cf. doc Hatari, section "Indicators").
        StatusBar   = s.GetValueOrDefault(KEY_STATUSBAR)  != "false";
        DriveLed    = s.GetValueOrDefault(KEY_DRIVELED)   == "true";
        StRam       = s.GetValueOrDefault(KEY_STRAM)      ?? "1";
        TtRam       = s.GetValueOrDefault(KEY_TTRAM)      ?? "0";
        FastBoot    = s.GetValueOrDefault(KEY_FASTBOOT)   != "false";
        TimerD      = s.GetValueOrDefault(KEY_TIMERD)     != "false";
        Prefetch     = s.GetValueOrDefault(KEY_PREFETCH)     == "true";
        CpuExact     = s.GetValueOrDefault(KEY_CPUEXACT)     != "false";
        DataCache    = s.GetValueOrDefault(KEY_DATACACHE)    != "false";
        Mmu          = s.GetValueOrDefault(KEY_MMU)          == "true";
        Addr24       = s.GetValueOrDefault(KEY_ADDR24)       == "true";
        FpuSoftfloat = s.GetValueOrDefault(KEY_FPUSOFTFLOAT) == "true";
        VdiEnabled   = s.GetValueOrDefault(KEY_VDIENABLED)   == "true";
        VdiWidth     = s.GetValueOrDefault(KEY_VDIWIDTH)     ?? "640";
        VdiHeight    = s.GetValueOrDefault(KEY_VDIHEIGHT)    ?? "480";
        VdiPlanes    = s.GetValueOrDefault(KEY_VDIPLANES)    ?? "4";
        CpuType      = s.GetValueOrDefault(KEY_CPUTYPE)      ?? "auto";
        CpuClock     = s.GetValueOrDefault(KEY_CPUCLOCK)     ?? "auto";
        Fpu          = s.GetValueOrDefault(KEY_FPU)          ?? "auto";
    }

    [RelayCommand]
    private void BrowseTos()
    {
        var biosRoot = DemoBase.App.Services.AppPaths.Bios;
        var biosDir  = System.IO.Path.Combine(biosRoot, "AtariST");
        var initialDir = System.IO.Directory.Exists(biosDir) ? biosDir
                        : System.IO.Directory.Exists(biosRoot) ? biosRoot
                        : AppContext.BaseDirectory;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title            = "Sélectionner le fichier ROM TOS",
            InitialDirectory = initialDir,
            // Filtre ouvert, même raison que BrowseKickstart ci-dessus.
            Filter           = "Tous les fichiers (*.*)|*.*",
        };
        if (dlg.ShowDialog() == true)
            TosPath = ToRelative(dlg.FileName);
    }

    // ── Chemin relatif / absolu ────────────────────────────────────────────────
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

    [RelayCommand]
    private async Task SaveHatariSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MACHINE]    = MachineType,
                [KEY_MONITOR]    = Monitor,
                [KEY_TOS]        = ToAbsolute(TosPath),
                [KEY_BORDERS]    = Borders.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
                [KEY_STATUSBAR]  = StatusBar.ToString().ToLower(),
                [KEY_DRIVELED]   = DriveLed.ToString().ToLower(),
                [KEY_STRAM]      = StRam,
                [KEY_TTRAM]      = TtRam,
                [KEY_FASTBOOT]   = FastBoot.ToString().ToLower(),
                [KEY_TIMERD]     = TimerD.ToString().ToLower(),
                [KEY_PREFETCH]     = Prefetch.ToString().ToLower(),
                [KEY_CPUEXACT]     = CpuExact.ToString().ToLower(),
                [KEY_DATACACHE]    = DataCache.ToString().ToLower(),
                [KEY_MMU]          = Mmu.ToString().ToLower(),
                [KEY_ADDR24]       = Addr24.ToString().ToLower(),
                [KEY_FPUSOFTFLOAT] = FpuSoftfloat.ToString().ToLower(),
                [KEY_VDIENABLED]   = VdiEnabled.ToString().ToLower(),
                [KEY_VDIWIDTH]     = VdiWidth,
                [KEY_VDIHEIGHT]    = VdiHeight,
                [KEY_VDIPLANES]    = VdiPlanes,
                [KEY_CPUTYPE]      = CpuType,
                [KEY_CPUCLOCK]     = CpuClock,
                [KEY_FPU]          = Fpu,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── CpcecSettingsViewModel ───────────────────────────────────────────────────
// Gère les paramètres spécifiques à CPCEC (stockés dans EmulatorSettings), PAR
// PROFIL (EmulatorConfig). Comme Altirra/Hatari, les valeurs stockées sont les
// tokens bruts attendus par la ligne de commande CPCEC ("0".."3" pour -mX,
// "0".."6" pour -kX), portés par le Tag de chaque ComboBoxItem.

public partial class CpcecSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    // ── Clés de settings ──────────────────────────────────────────────────────
    public const string KEY_MODEL      = "machine_model";
    public const string KEY_RAM        = "ram";
    public const string KEY_FULLSCREEN = "fullscreen";
    public const string KEY_INDICATORS = "indicators";
    public const string KEY_CRTC       = "crtc_type";

    [ObservableProperty] private string _machineModel = "2";
    [ObservableProperty] private string _ram          = "1";
    [ObservableProperty] private bool   _fullScreen   = false;
    // Décoché par défaut : masque l'oscilloscope audio et les compteurs de disquette/cassette
    // que CPCEC affiche par défaut à l'écran (demande utilisateur) — cf. CpcecLauncher (-o/-O).
    [ObservableProperty] private bool   _showIndicators = false;
    // Type de CRTC (0 à 4, défaut 1 — celui de CPCEC lui-même). Certaines productions de la
    // scène CPC exigent un CRTC précis pour s'afficher correctement, cf. CpcecLauncher.
    [ObservableProperty] private string _crtcType       = "1";
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public CpcecSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        MachineModel    = s.GetValueOrDefault(KEY_MODEL)      ?? "2";
        Ram             = s.GetValueOrDefault(KEY_RAM)        ?? "1";
        FullScreen      = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
        ShowIndicators  = s.GetValueOrDefault(KEY_INDICATORS) == "true";
        CrtcType        = s.GetValueOrDefault(KEY_CRTC)       ?? "1";
    }

    [RelayCommand]
    private async Task SaveCpcecSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MODEL]      = MachineModel,
                [KEY_RAM]        = Ram,
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
                [KEY_INDICATORS] = ShowIndicators.ToString().ToLower(),
                [KEY_CRTC]       = CrtcType,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── ZxsecSettingsViewModel ───────────────────────────────────────────────────
// Spectrum 48K/128K/+2/+3. Pas de réglage RAM (fixée par le modèle, contrairement
// au CPC) — cf. ZxsecLauncher pour le détail des différences avec CPCEC.

public partial class ZxsecSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_MODEL      = "machine_model";
    public const string KEY_FULLSCREEN = "fullscreen";
    public const string KEY_INDICATORS = "indicators";

    [ObservableProperty] private string _machineModel   = "1";
    [ObservableProperty] private bool   _fullScreen     = false;
    [ObservableProperty] private bool   _showIndicators = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ZxsecSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        MachineModel   = s.GetValueOrDefault(KEY_MODEL)      ?? "1";
        FullScreen     = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
        ShowIndicators = s.GetValueOrDefault(KEY_INDICATORS) == "true";
    }

    [RelayCommand]
    private async Task SaveZxsecSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MODEL]      = MachineModel,
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
                [KEY_INDICATORS] = ShowIndicators.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── CsfecSettingsViewModel ───────────────────────────────────────────────────
// Commodore 64. Pas de réglage modèle (un seul C64 émulé par CSFEC) — cf.
// CsfecLauncher pour le détail des différences avec CPCEC. RAM par défaut "0"
// (64K, config standard du C64), pas "1" comme CPCEC.

public partial class CsfecSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_RAM        = "ram";
    public const string KEY_FULLSCREEN = "fullscreen";
    public const string KEY_INDICATORS = "indicators";

    [ObservableProperty] private string _ram            = "0";
    [ObservableProperty] private bool   _fullScreen     = false;
    [ObservableProperty] private bool   _showIndicators = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public CsfecSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Ram            = s.GetValueOrDefault(KEY_RAM)        ?? "0";
        FullScreen     = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
        ShowIndicators = s.GetValueOrDefault(KEY_INDICATORS) == "true";
    }

    [RelayCommand]
    private async Task SaveCsfecSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_RAM]        = Ram,
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
                [KEY_INDICATORS] = ShowIndicators.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── MsxecSettingsViewModel ────────────────────────────────────────────────────
// MSX/MSX2/MSX2+. 3 modèles seulement (pas de 4e comme CPCEC/ZXSEC) — cf.
// MsxecLauncher pour le détail des différences avec CPCEC.

public partial class MsxecSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_MODEL      = "machine_model";
    public const string KEY_RAM        = "ram";
    public const string KEY_FULLSCREEN = "fullscreen";
    public const string KEY_INDICATORS = "indicators";

    [ObservableProperty] private string _machineModel   = "1";
    [ObservableProperty] private string _ram            = "1";
    [ObservableProperty] private bool   _fullScreen     = false;
    [ObservableProperty] private bool   _showIndicators = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public MsxecSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        MachineModel   = s.GetValueOrDefault(KEY_MODEL)      ?? "1";
        Ram            = s.GetValueOrDefault(KEY_RAM)        ?? "1";
        FullScreen     = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
        ShowIndicators = s.GetValueOrDefault(KEY_INDICATORS) == "true";
    }

    [RelayCommand]
    private async Task SaveMsxecSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MODEL]      = MachineModel,
                [KEY_RAM]        = Ram,
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
                [KEY_INDICATORS] = ShowIndicators.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── DosBoxXSettingsViewModel ──────────────────────────────────────────────────
// DOSBox-X. Contrairement aux frères de CPCEC, les valeurs Cycles sont du texte
// libre ("auto"/"max"/"fixed 4000"...) plutôt qu'un ComboBox fermé — trop
// dépendant du jeu/de la demo précise pour des presets figés, cf. DosBoxXLauncher.

public partial class DosBoxXSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_MACHINE    = "machine";
    public const string KEY_CPUTYPE    = "cputype";
    public const string KEY_CYCLES     = "cycles";
    public const string KEY_MEMSIZE    = "memsize";
    public const string KEY_SBTYPE     = "sbtype";
    public const string KEY_GUS        = "gus";
    public const string KEY_FULLSCREEN_DBX = "fullscreen";

    [ObservableProperty] private string _machine    = "svga_s3";
    [ObservableProperty] private string _cpuType    = "auto";
    [ObservableProperty] private string _cycles     = "auto";
    [ObservableProperty] private string _memSize    = "16";
    [ObservableProperty] private string _sbType     = "sb16";
    [ObservableProperty] private bool   _gus        = false;
    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public DosBoxXSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Machine    = s.GetValueOrDefault(KEY_MACHINE)        ?? "svga_s3";
        CpuType    = s.GetValueOrDefault(KEY_CPUTYPE)        ?? "auto";
        Cycles     = s.GetValueOrDefault(KEY_CYCLES)         ?? "auto";
        MemSize    = s.GetValueOrDefault(KEY_MEMSIZE)        ?? "16";
        SbType     = s.GetValueOrDefault(KEY_SBTYPE)         ?? "sb16";
        Gus        = s.GetValueOrDefault(KEY_GUS)            == "true";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN_DBX) == "true";
    }

    [RelayCommand]
    private async Task SaveDosBoxXSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MACHINE]        = Machine,
                [KEY_CPUTYPE]        = CpuType,
                [KEY_CYCLES]         = Cycles,
                [KEY_MEMSIZE]        = MemSize,
                [KEY_SBTYPE]         = SbType,
                [KEY_GUS]            = Gus.ToString().ToLower(),
                [KEY_FULLSCREEN_DBX] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── ViceC64SettingsViewModel ──────────────────────────────────────────────────
// Commodore 64, via VICE (x64sc). Contrairement à WinUAE/Hatari/Altirra, aucune
// ROM système à fournir par l'utilisateur — VICE embarque son propre jeu de
// ROMs C64 — donc pas de champ "ROM" ici, comme pour CSFEC. Cf. ViceC64Launcher
// pour le détail et la justification de chaque option de ligne de commande.

public partial class ViceC64SettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_REGION     = "region";
    public const string KEY_SIDENGINE  = "sidengine";
    public const string KEY_SIDMODEL   = "sidmodel";
    public const string KEY_REU        = "reu";
    public const string KEY_REUSIZE    = "reusize";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string _region     = "pal";
    [ObservableProperty] private string _sidEngine  = "1";
    [ObservableProperty] private string _sidModel   = "0";
    [ObservableProperty] private bool   _reu        = false;
    [ObservableProperty] private string _reuSize    = "512";
    [ObservableProperty] private bool   _trueDrive  = false;
    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ViceC64SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Region     = s.GetValueOrDefault(KEY_REGION)    ?? "pal";
        SidEngine  = s.GetValueOrDefault(KEY_SIDENGINE)  ?? "1";
        SidModel   = s.GetValueOrDefault(KEY_SIDMODEL)   ?? "0";
        Reu        = s.GetValueOrDefault(KEY_REU)        == "true";
        ReuSize    = s.GetValueOrDefault(KEY_REUSIZE)    ?? "512";
        TrueDrive  = s.GetValueOrDefault(KEY_TRUEDRIVE)  == "true";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
    }

    [RelayCommand]
    private async Task SaveViceC64Settings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_REGION]     = Region,
                [KEY_SIDENGINE]  = SidEngine,
                [KEY_SIDMODEL]   = SidModel,
                [KEY_REU]        = Reu.ToString().ToLower(),
                [KEY_REUSIZE]    = ReuSize,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── ViceC128SettingsViewModel ──────────────────────────────────────────────
// Commodore 128, via VICE (x128). Frère de ViceC64SettingsViewModel — reprend
// à l'identique Region/SidEngine/SidModel/Reu/ReuSize/TrueDrive/FullScreen
// (mêmes puces VIC-II/SID que le C64, cf. ViceC128Launcher) et ajoute Go64
// (démarrage en mode compatible C64) et Columns (40/80 colonnes, propre au
// C128 natif).

public partial class ViceC128SettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_REGION     = "region";
    public const string KEY_SIDENGINE  = "sidengine";
    public const string KEY_SIDMODEL   = "sidmodel";
    public const string KEY_REU        = "reu";
    public const string KEY_REUSIZE    = "reusize";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_GO64       = "go64";
    public const string KEY_COLUMNS    = "columns";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string _region     = "pal";
    [ObservableProperty] private string _sidEngine  = "1";
    [ObservableProperty] private string _sidModel   = "0";
    [ObservableProperty] private bool   _reu        = false;
    [ObservableProperty] private string _reuSize    = "512";
    [ObservableProperty] private bool   _trueDrive  = false;
    [ObservableProperty] private bool   _go64       = false;
    [ObservableProperty] private string _columns    = "80";
    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ViceC128SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Region     = s.GetValueOrDefault(KEY_REGION)    ?? "pal";
        SidEngine  = s.GetValueOrDefault(KEY_SIDENGINE)  ?? "1";
        SidModel   = s.GetValueOrDefault(KEY_SIDMODEL)   ?? "0";
        Reu        = s.GetValueOrDefault(KEY_REU)        == "true";
        ReuSize    = s.GetValueOrDefault(KEY_REUSIZE)    ?? "512";
        TrueDrive  = s.GetValueOrDefault(KEY_TRUEDRIVE)  == "true";
        Go64       = s.GetValueOrDefault(KEY_GO64)       == "true";
        Columns    = s.GetValueOrDefault(KEY_COLUMNS)    ?? "80";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
    }

    [RelayCommand]
    private async Task SaveViceC128Settings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_REGION]     = Region,
                [KEY_SIDENGINE]  = SidEngine,
                [KEY_SIDMODEL]   = SidModel,
                [KEY_REU]        = Reu.ToString().ToLower(),
                [KEY_REUSIZE]    = ReuSize,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_GO64]       = Go64.ToString().ToLower(),
                [KEY_COLUMNS]    = Columns,
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── ViceVic20SettingsViewModel ─────────────────────────────────────────────
// VIC-20, via VICE (xvic). Pas de SID ni de REU (cf. ViceVic20Launcher) :
// seulement Region, Memory (liste de blocs RAM), TrueDrive et FullScreen
// (-VICfull, puce vidéo "VIC" propre au VIC-20, pas "VIC-II").

public partial class ViceVic20SettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_REGION     = "region";
    public const string KEY_MEMORY     = "memory";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string  _region     = "pal";
    [ObservableProperty] private string  _memory     = "";
    [ObservableProperty] private bool    _trueDrive  = false;
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ViceVic20SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Region     = s.GetValueOrDefault(KEY_REGION)    ?? "pal";
        Memory     = s.GetValueOrDefault(KEY_MEMORY)     ?? "";
        TrueDrive  = s.GetValueOrDefault(KEY_TRUEDRIVE)  == "true";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
    }

    [RelayCommand]
    private async Task SaveViceVic20Settings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_REGION]     = Region,
                [KEY_MEMORY]     = Memory,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── VicePetSettingsViewModel ────────────────────────────────────────────────
// PET, via VICE (xpet). Pas de Region/SidEngine/SidModel/Reu (cf.
// VicePetLauncher) : seulement Model (-model <token>), TrueDrive et
// FullScreen (-CRTCfull, puce CRTC propre au PET).

public partial class VicePetSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_MODEL      = "model";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string  _model      = "8032";
    [ObservableProperty] private bool    _trueDrive  = false;
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public VicePetSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Model      = s.GetValueOrDefault(KEY_MODEL)      ?? "8032";
        TrueDrive  = s.GetValueOrDefault(KEY_TRUEDRIVE)  == "true";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
    }

    [RelayCommand]
    private async Task SaveVicePetSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MODEL]      = Model,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── ViceC64DtvSettingsViewModel ────────────────────────────────────────────
// Commodore 64 DTV, via VICE (x64dtv). Frère de ViceC64SettingsViewModel —
// pas de réglage SID exposé (DTVSID est forcé en dur côté launcher, ce n'est
// pas un vrai choix sur cette machine) ; ajoute DtvRevision (2/3) à la place.

public partial class ViceC64DtvSettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_REGION     = "region";
    public const string KEY_DTVREV     = "dtvrev";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string  _region      = "pal";
    [ObservableProperty] private string  _dtvRevision = "3";
    [ObservableProperty] private bool    _trueDrive   = false;
    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ViceC64DtvSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Region      = s.GetValueOrDefault(KEY_REGION)     ?? "pal";
        DtvRevision = s.GetValueOrDefault(KEY_DTVREV)      ?? "3";
        TrueDrive   = s.GetValueOrDefault(KEY_TRUEDRIVE)   == "true";
        FullScreen  = s.GetValueOrDefault(KEY_FULLSCREEN)  == "true";
    }

    [RelayCommand]
    private async Task SaveViceC64DtvSettings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_REGION]     = Region,
                [KEY_DTVREV]     = DtvRevision,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── VicePlus4SettingsViewModel ──────────────────────────────────────────────
// Commodore Plus/4 et C16, via VICE (xplus4 — un seul exécutable pour les
// deux). Pas de réglage SID (puce TED vidéo+son, pas de SID intégré, cf.
// VicePlus4Launcher) : Model (-model plus4/c16), Region, TrueDrive,
// FullScreen (-TEDfull, puce TED propre au Plus/4/C16).

public partial class VicePlus4SettingsViewModel : ObservableObject
{
    private readonly int          _emulatorConfigId;
    private readonly IUnitOfWork  _uow;

    public const string KEY_MODEL      = "model";
    public const string KEY_REGION     = "region";
    public const string KEY_TRUEDRIVE  = "truedrive";
    public const string KEY_FULLSCREEN = "fullscreen";

    [ObservableProperty] private string  _model      = "plus4";
    [ObservableProperty] private string  _region     = "pal";
    [ObservableProperty] private bool    _trueDrive  = false;
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public VicePlus4SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Load(settings);
    }

    private void Load(Dictionary<string, string?> s)
    {
        Model      = s.GetValueOrDefault(KEY_MODEL)      ?? "plus4";
        Region     = s.GetValueOrDefault(KEY_REGION)     ?? "pal";
        TrueDrive  = s.GetValueOrDefault(KEY_TRUEDRIVE)  == "true";
        FullScreen = s.GetValueOrDefault(KEY_FULLSCREEN) == "true";
    }

    [RelayCommand]
    private async Task SaveVicePlus4Settings()
    {
        IsSaving = true;
        SaveMessage = null;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [KEY_MODEL]      = Model,
                [KEY_REGION]     = Region,
                [KEY_TRUEDRIVE]  = TrueDrive.ToString().ToLower(),
                [KEY_FULLSCREEN] = FullScreen.ToString().ToLower(),
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex)
        {
            SaveMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsSaving = false; }
    }
}

// ─── TIC-80 Settings ─────────────────────────────────────────────────────────

public partial class Tic80SettingsViewModel : ObservableObject
{
    private readonly int             _emulatorConfigId;
    private readonly IUnitOfWork     _uow;

    [ObservableProperty] private bool   _skip       = true;  // --skip par défaut
    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Tic80SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;

        if (settings.TryGetValue(DemoBase.App.Services.Tic80Settings.Skip, out var s))
            Skip = s != "false";
        if (settings.TryGetValue(DemoBase.App.Services.Tic80Settings.Fullscreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.Tic80Settings.Skip]       = Skip       ? "true" : "false",
                [DemoBase.App.Services.Tic80Settings.Fullscreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Pemsa Settings (PICO-8) ─────────────────────────────────────────────────

public partial class PemsaSettingsViewModel : ObservableObject
{
    private readonly int             _emulatorConfigId;
    private readonly IUnitOfWork     _uow;

    [ObservableProperty] private bool   _noSplash   = true;  // --no-splash par défaut
    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public PemsaSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;

        if (settings.TryGetValue(DemoBase.App.Services.PemsaSettings.NoSplash, out var s))
            NoSplash = s != "false";
        if (settings.TryGetValue(DemoBase.App.Services.PemsaSettings.Fullscreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.PemsaSettings.NoSplash]   = NoSplash   ? "true" : "false",
                [DemoBase.App.Services.PemsaSettings.Fullscreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Azahar Settings ───────────────────────────────────────

public partial class AzaharSettingsViewModel : ObservableObject
{
    private readonly int             _emulatorConfigId;
    private readonly IUnitOfWork     _uow;

    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public AzaharSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.AzaharKeys.Fullscreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.AzaharKeys.Fullscreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Mesen Settings ──────────────────────────────────────────────────────────

public partial class MesenSettingsViewModel : ObservableObject
{
    private readonly int             _emulatorConfigId;
    private readonly IUnitOfWork     _uow;

    [ObservableProperty] private bool   _fullScreen = false;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public MesenSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.MesenKeys.Fullscreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.MesenKeys.Fullscreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── MicroW8 Settings ────────────────────────────────────────────────────────

public partial class MicroW8SettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    // Filtres disponibles (libellé UI → valeur CLI)
    public static IReadOnlyList<KeyValuePair<string,string>> FilterOptions { get; } =
    [
        new KeyValuePair<string,string>("auto_crt (défaut)", "auto_crt"),
        new KeyValuePair<string,string>("nearest — pixel net", "nearest"),
        new KeyValuePair<string,string>("fast_crt — CRT rapide", "fast_crt"),
        new KeyValuePair<string,string>("ss_crt — CRT super-samplé", "ss_crt"),
        new KeyValuePair<string,string>("chromatic_crt — CRT RGB", "chromatic_crt"),
    ];

    [ObservableProperty] private string  _filter    = "auto_crt";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public MicroW8SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.MicroW8Settings.Filter, out var f)
            && !string.IsNullOrWhiteSpace(f))
            Filter = f;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.MicroW8Settings.Filter] = Filter,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── UnrealSpeccy Settings ───────────────────────────────────────────────────

public partial class UnrealSpeccySettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    // FolderName exact du catalogue (EmulatorDownloadCatalog/EmulatorSeedCatalog) — dossier
    // réel dans Emus\, avec l'espace. Utilisé pour localiser où extraire le pack TSL.
    private const string EmuFolderName = "Unreal Speccy";

    // Liste complète des valeurs HIMEM= documentées dans le commentaire de unreal.ini
    // ("high memory: PENTAGON, SCORPION, PROFSCORP, PROFI, ATM450, ATM710, KAY, ATM3, TSL")
    // — PROFSCORP et ATM3 manquaient jusqu'ici, seuls 7 des 9 modèles étaient proposés.
    public static IReadOnlyList<KeyValuePair<string,string>> MachineOptions { get; } =
    [
        new KeyValuePair<string,string>("Pentagon 128 (défaut — démos scène)", "PENTAGON"),
        new KeyValuePair<string,string>("Scorpion ZS 256", "SCORPION"),
        new KeyValuePair<string,string>("Scorpion PROF-ROM (SMUC)", "PROFSCORP"),
        new KeyValuePair<string,string>("KAY 1024", "KAY"),
        new KeyValuePair<string,string>("Profi 1024", "PROFI"),
        new KeyValuePair<string,string>("ATM Turbo 1 (v4.50)", "ATM450"),
        new KeyValuePair<string,string>("ATM Turbo 2 (v7.10)", "ATM710"),
        new KeyValuePair<string,string>("ATM3 (ZX Evolution, hors TS-Config)", "ATM3"),
        new KeyValuePair<string,string>("ZX Evolution / TS-Config (TSL)", "TSL"),
    ];

    // Valeurs documentées dans le commentaire [VIDEO] de unreal.ini — cf.
    // UnrealSpeccyLauncher.PrepareIni pour l'injection dans la clé video=.
    public static IReadOnlyList<KeyValuePair<string,string>> VideoFilterOptions { get; } =
    [
        new KeyValuePair<string,string>("Normal (rapide, écran Spectrum standard)", "normal"),
        new KeyValuePair<string,string>("Double (x2 — pentagon 512x192, profi 512x240...)", "double"),
        new KeyValuePair<string,string>("Triple (x3, net — défaut, recommandé démos scène)", "triple"),
        new KeyValuePair<string,string>("Quad (x4, pour écran LCD 1280x1024)", "quad"),
        new KeyValuePair<string,string>("Text (lecture e-zines, polices 4x8 → 8x8/8x16)", "text"),
        new KeyValuePair<string,string>("Resampler (conversion 50Hz → fréquence de l'écran)", "resampler"),
        new KeyValuePair<string,string>("Bilinear (interpolation couleur, MMX)", "bilinear"),
        new KeyValuePair<string,string>("Scale (mise à l'échelle pseudo-vectorielle)", "scale"),
        new KeyValuePair<string,string>("AdvMAME (algorithme AdvanceMAME x2/x3/x4)", "advmame"),
        new KeyValuePair<string,string>("TV (émulation TV couleur, fenêtré)", "tv"),
        new KeyValuePair<string,string>("Chunky Overlay 16-bit (fenêtré)", "ch_ov"),
        new KeyValuePair<string,string>("Chunky Hardware 32-bit (fenêtré)", "ch_hw"),
        new KeyValuePair<string,string>("Chunky filtré 320x240x16", "ch_bl"),
        new KeyValuePair<string,string>("Chunky filtré 640x480x16", "ch_b"),
        new KeyValuePair<string,string>("Chunky 4x4 32-bit (lent, précision totale)", "ch4true"),
    ];

    [ObservableProperty] private string  _machine     = "PENTAGON";
    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private string  _videoFilter = "triple";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    // Téléchargement du pack TSL (roms/boot.$b/wc.img, cf. UnrealSpeccyTslPackService) —
    // déclenché dès que l'utilisateur sélectionne "TSL", pas au clic sur Enregistrer.
    [ObservableProperty] private bool    _isDownloadingTslPack;
    [ObservableProperty] private string? _tslPackMessage;

    public UnrealSpeccySettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.UnrealSpeccySettings.Machine, out var m)
            && !string.IsNullOrWhiteSpace(m)) Machine = m.ToUpperInvariant();
        if (settings.TryGetValue(DemoBase.App.Services.UnrealSpeccySettings.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.UnrealSpeccySettings.VideoFilter, out var vf)
            && !string.IsNullOrWhiteSpace(vf)) VideoFilter = vf.ToLowerInvariant();
    }

    // Généré par [ObservableProperty] sur _machine — déclenché à chaque changement de
    // Machine, y compris depuis le constructeur (rechargement d'un profil déjà en TSL) : le
    // pack étant vérifié de façon idempotente (UnrealSpeccyTslPackService.IsInstalled), ce
    // n'est jamais un problème de le re-déclencher à l'ouverture de la fiche.
    partial void OnMachineChanged(string value)
    {
        if (!string.Equals(value, "TSL", StringComparison.OrdinalIgnoreCase)) return;
        _ = EnsureTslPackAsync();
    }

    private async Task EnsureTslPackAsync()
    {
        var exeDir = System.IO.Path.Combine(
            DemoBase.App.Services.EmulatorInstallerService.EmusRoot, EmuFolderName);

        if (DemoBase.App.Services.UnrealSpeccyTslPackService.IsInstalled(exeDir))
        {
            TslPackMessage = "Pack TSL déjà présent.";
            return;
        }

        IsDownloadingTslPack = true;
        TslPackMessage = "Téléchargement du pack TSL (roms, boot.$b, wc.img) depuis Mega…";
        try
        {
            var svc = new DemoBase.App.Services.UnrealSpeccyTslPackService();
            var (success, message) = await svc.DownloadAndInstallAsync(exeDir);
            TslPackMessage = success ? $"✓ {message}" : $"✗ {message}";
        }
        catch (Exception ex)
        {
            TslPackMessage = $"✗ Erreur : {ex.Message}";
        }
        finally { IsDownloadingTslPack = false; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var settings = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.UnrealSpeccySettings.Machine]     = Machine,
                [DemoBase.App.Services.UnrealSpeccySettings.FullScreen]  = FullScreen ? "true" : "false",
                [DemoBase.App.Services.UnrealSpeccySettings.VideoFilter] = VideoFilter,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, settings);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── UnrealSpeccy Settings (machine type) ────────────────────────────────────

// ─── EightyOne Settings ──────────────────────────────────────────────────────

public partial class EightyOneSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public EightyOneSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    { _emulatorConfigId = emulatorConfigId; _uow = uow; }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, new());
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── ZEsarUX Settings ────────────────────────────────────────────────────────

public partial class ZEsarUXSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    /// <summary>
    /// Liste complète des machines ZEsarUX 13.0 (source : zesarux.exe --help).
    /// La valeur est l'ID exact à passer à --machine.
    /// </summary>
    public static IReadOnlyList<KeyValuePair<string,string>> MachineOptions { get; } =
    [
        // ── Sinclair ZX ──────────────────────────────────────────────────────
        new KeyValuePair<string,string>("ZX80 — Sinclair ZX-80", "ZX80"),
        new KeyValuePair<string,string>("ZX81 — Sinclair ZX-81", "ZX81"),
        // ── ZX Spectrum (Sinclair) ────────────────────────────────────────────
        new KeyValuePair<string,string>("16k — ZX Spectrum 16K", "16k"),
        new KeyValuePair<string,string>("48k — ZX Spectrum 48K", "48k"),
        new KeyValuePair<string,string>("48kp — ZX Spectrum+ 48K", "48kp"),
        new KeyValuePair<string,string>("128k — ZX Spectrum 128K", "128k"),
        new KeyValuePair<string,string>("P2 — ZX Spectrum +2", "P2"),
        new KeyValuePair<string,string>("P2A40 — ZX Spectrum +2A (ROM v4.0)", "P2A40"),
        new KeyValuePair<string,string>("P2A41 — ZX Spectrum +2A (ROM v4.1)", "P2A41"),
        new KeyValuePair<string,string>("P340 — ZX Spectrum +3 (ROM v4.0)", "P340"),
        new KeyValuePair<string,string>("P341 — ZX Spectrum +3 (ROM v4.1)", "P341"),
        // ── Clones russes ────────────────────────────────────────────────────
        new KeyValuePair<string,string>("Pentagon — Pentagon 128", "Pentagon"),
        // ── Spectrum Next / évolutions ───────────────────────────────────────
        new KeyValuePair<string,string>("TBBlue — ZX Spectrum Next / TBBlue", "TBBlue"),
        new KeyValuePair<string,string>("ZXUNO — ZX-Uno", "ZXUNO"),
        new KeyValuePair<string,string>("TSConf — ZX-Evolution TS-Conf", "TSConf"),
        new KeyValuePair<string,string>("BaseConf — ZX-Evolution BaseConf", "BaseConf"),
        new KeyValuePair<string,string>("Prism — Prism 512", "Prism"),
        new KeyValuePair<string,string>("Chloe140 — Chloe 140 SE", "Chloe140"),
        new KeyValuePair<string,string>("Chloe280 — Chloe 280 SE", "Chloe280"),
        new KeyValuePair<string,string>("Chrome — Chrome", "Chrome"),
        // ── Timex ─────────────────────────────────────────────────────────────
        new KeyValuePair<string,string>("TC2048 — Timex Computer 2048", "TC2048"),
        new KeyValuePair<string,string>("TC2068 — Timex Computer 2068", "TC2068"),
        new KeyValuePair<string,string>("TS1000 — Timex Sinclair 1000", "TS1000"),
        new KeyValuePair<string,string>("TS1500 — Timex Sinclair 1500", "TS1500"),
        new KeyValuePair<string,string>("TS2068 — Timex Sinclair 2068", "TS2068"),
        // ── Microdigital (brésil) ─────────────────────────────────────────────
        new KeyValuePair<string,string>("TK90X — Microdigital TK90X", "TK90X"),
        new KeyValuePair<string,string>("TK95 — Microdigital TK95", "TK95"),
        new KeyValuePair<string,string>("TK80 — Microdigital TK80", "TK80"),
        new KeyValuePair<string,string>("TK85 — Microdigital TK85", "TK85"),
        // ── Clones argentins Czerweny ─────────────────────────────────────────
        new KeyValuePair<string,string>("CZSPEC — Czerweny CZ Spectrum", "CZSPEC"),
        new KeyValuePair<string,string>("CZ2000 — Czerweny CZ 2000", "CZ2000"),
        // ── Inves (espagne) ───────────────────────────────────────────────────
        new KeyValuePair<string,string>("Inves — Inves Spectrum+", "Inves"),
        // ── Autres Sinclair ───────────────────────────────────────────────────
        new KeyValuePair<string,string>("QL — Sinclair QL", "QL"),
        new KeyValuePair<string,string>("Z88 — Cambridge Z88", "Z88"),
        new KeyValuePair<string,string>("ACE — Jupiter Ace", "ACE"),
        new KeyValuePair<string,string>("Sam — Sam Coupe", "Sam"),
        new KeyValuePair<string,string>("MK14 — MK14", "MK14"),
        // ── Amstrad ──────────────────────────────────────────────────────────
        new KeyValuePair<string,string>("CPC464 — Amstrad CPC 464", "CPC464"),
        new KeyValuePair<string,string>("CPC664 — Amstrad CPC 664", "CPC664"),
        new KeyValuePair<string,string>("CPC6128 — Amstrad CPC 6128", "CPC6128"),
        new KeyValuePair<string,string>("PCW8256 — Amstrad PCW 8256", "PCW8256"),
        // ── Autres plateformes ────────────────────────────────────────────────
        new KeyValuePair<string,string>("MSX1 — MSX1", "MSX1"),
        new KeyValuePair<string,string>("SMS — Sega Master System", "SMS"),
        new KeyValuePair<string,string>("Coleco — Colecovision", "Coleco"),
        new KeyValuePair<string,string>("SG1000 — Sega SG-1000", "SG1000"),
        new KeyValuePair<string,string>("SVI318 — Spectravideo SVI 318", "SVI318"),
        new KeyValuePair<string,string>("SVI328 — Spectravideo SVI 328", "SVI328"),
    ];

    [ObservableProperty] private string  _machine    = "48k";
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ZEsarUXSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.ZEsarUXSettings.Machine, out var m)
            && !string.IsNullOrWhiteSpace(m)) Machine = m;
        if (settings.TryGetValue(DemoBase.App.Services.ZEsarUXSettings.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.ZEsarUXSettings.Machine]    = Machine,
                [DemoBase.App.Services.ZEsarUXSettings.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Kega Fusion Settings ────────────────────────────────────────────────────

public partial class KegaFusionSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> ConsoleOptions { get; } =
    [
        new("Auto-détection (recommandé)",      "auto"),
        new("-gen / -md  — Genesis / Mega Drive", "-gen"),
        new("-sms         — Master System / SG-1000 / SC-3000", "-sms"),
        new("-gg          — Game Gear",          "-gg"),
        new("-32x         — Sega 32X",           "-32x"),
        new("-scd / -mcd  — Sega CD / Mega CD", "-scd"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> CountryOptions { get; } =
    [
        new("-auto — Auto-détection",  "-auto"),
        new("-usa  — USA (NTSC)",      "-usa"),
        new("-jap  — Japon (NTSC-J)",  "-jap"),
        new("-eur  — Europe (PAL)",    "-eur"),
    ];

    [ObservableProperty] private string  _console    = "auto";
    [ObservableProperty] private string  _country    = "-auto";
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public KegaFusionSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.KegaFusionSettings.Console, out var c)
            && !string.IsNullOrWhiteSpace(c)) Console = c;
        if (settings.TryGetValue(DemoBase.App.Services.KegaFusionSettings.Country, out var r)
            && !string.IsNullOrWhiteSpace(r)) Country = r;
        if (settings.TryGetValue(DemoBase.App.Services.KegaFusionSettings.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.KegaFusionSettings.Console]    = Console,
                [DemoBase.App.Services.KegaFusionSettings.Country]    = Country,
                [DemoBase.App.Services.KegaFusionSettings.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Browser Settings ────────────────────────────────────────────────────────

public partial class BrowserSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> ModeOptions { get; } =
    [
        new KeyValuePair<string,string>("Navigateur par défaut du système", "default"),
        new KeyValuePair<string,string>("Navigateur personnalisé (ExecutablePath)", "custom"),
    ];

    [ObservableProperty] private string  _mode            = "default";
    [ObservableProperty] private bool    _allowFileAccess = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public BrowserSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow, string? emulatorExecutablePath = null)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.BrowserSettings.Mode, out var m)
            && !string.IsNullOrWhiteSpace(m))
        {
            // Réglage déjà sauvegardé explicitement par l'utilisateur — on le
            // respecte tel quel, sans jamais l'écraser.
            Mode = m;
        }
        else if (!string.IsNullOrWhiteSpace(emulatorExecutablePath)
                 && System.IO.File.Exists(emulatorExecutablePath))
        {
            // Aucun réglage sauvegardé pour ce profil (premier réglage, ou
            // créé par le wizard) ET un exécutable valide est configuré sur
            // l'émulateur → "personnalisé" est plus logique que "défaut du
            // système" : si l'utilisateur (ou le wizard) a pris la peine de
            // pointer vers un navigateur précis, c'est celui-là qu'on attend
            // au lancement, pas le navigateur par défaut de Windows.
            Mode = "custom";
        }
        if (settings.TryGetValue(DemoBase.App.Services.BrowserSettings.AllowFileAccess, out var a))
            AllowFileAccess = a != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.BrowserSettings.Mode]            = Mode,
                [DemoBase.App.Services.BrowserSettings.AllowFileAccess] = AllowFileAccess ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Java Settings ────────────────────────────────────────────────────────────

public partial class JavaSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private string  _jvmArgs    = "-Xmx512m";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public JavaSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.JavaSettings.JvmArgs, out var a)
            && a != null) JvmArgs = a;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.JavaSettings.JvmArgs] = JvmArgs,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Fuse Settings ───────────────────────────────────────────────────────────

public partial class FuseSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> MachineOptions { get; } =
    [
        new KeyValuePair<string,string>("48 — ZX Spectrum 48K (défaut)",           "48"),
        new KeyValuePair<string,string>("16 — ZX Spectrum 16K",                    "16"),
        new KeyValuePair<string,string>("128 — ZX Spectrum 128K",                  "128"),
        new KeyValuePair<string,string>("plus2 — ZX Spectrum +2",                  "plus2"),
        new KeyValuePair<string,string>("plus2a — ZX Spectrum +2A",                "plus2a"),
        new KeyValuePair<string,string>("plus3 — ZX Spectrum +3",                  "plus3"),
        new KeyValuePair<string,string>("plus3e — ZX Spectrum +3e",                "plus3e"),
        new KeyValuePair<string,string>("pentagon — Pentagon 128",                  "pentagon"),
        new KeyValuePair<string,string>("pentagon512 — Pentagon 512",               "pentagon512"),
        new KeyValuePair<string,string>("pentagon1024 — Pentagon 1024",             "pentagon1024"),
        new KeyValuePair<string,string>("scorpion — Scorpion ZS 256",              "scorpion"),
        new KeyValuePair<string,string>("tc2048 — Timex Computer 2048",            "tc2048"),
        new KeyValuePair<string,string>("tc2068 — Timex Computer 2068",            "tc2068"),
        new KeyValuePair<string,string>("ts2068 — Timex Sinclair 2068",            "ts2068"),
        new KeyValuePair<string,string>("se — Spectrum SE",                        "se"),
    ];

    [ObservableProperty] private string  _machine = "48";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public FuseSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.FuseSettings.Machine, out var m)
            && !string.IsNullOrWhiteSpace(m)) Machine = m;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.FuseSettings.Machine] = Machine,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── BlastEm Settings ────────────────────────────────────────────────────────

public partial class BlastEmSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public BlastEmSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.BlastEmSettings.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.BlastEmSettings.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Arculator Settings ──────────────────────────────────────────────────────

public partial class ArculatorSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    /// <summary>Profils machines courants — noms des .cfg dans configs/</summary>
    public static IReadOnlyList<KeyValuePair<string,string>> ConfigOptions { get; } =
    [
        new KeyValuePair<string,string>("A3000  — ARM2, 1 Mo (démos scène)",    "A3000"),
        new KeyValuePair<string,string>("A3010  — ARM2, 1 Mo",                  "A3010"),
        new KeyValuePair<string,string>("A3020  — ARM250, 2 Mo",                "A3020"),
        new KeyValuePair<string,string>("A4000  — ARM250, 2 Mo",                "A4000"),
        new KeyValuePair<string,string>("A305   — ARM2, 512 Ko (premier Arc)",  "A305"),
        new KeyValuePair<string,string>("A310   — ARM2, 1 Mo",                  "A310"),
        new KeyValuePair<string,string>("A410   — ARM2, 1 Mo",                  "A410"),
        new KeyValuePair<string,string>("A420   — ARM2, 2 Mo",                  "A420"),
        new KeyValuePair<string,string>("A440   — ARM2, 4 Mo",                  "A440"),
        new KeyValuePair<string,string>("A5000  — ARM3, 2-4 Mo (rapide)",       "A5000"),
        new KeyValuePair<string,string>("A540   — ARM3, 4-8 Mo (haut de gamme)","A540"),
    ];

    [ObservableProperty] private string  _config  = "A3000";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;
    [ObservableProperty] private string? _scanMessage;
    [ObservableProperty] private ObservableCollection<string> _availableConfigs = [];

    public bool HasAvailableConfigs => AvailableConfigs.Count > 0;

    partial void OnAvailableConfigsChanged(ObservableCollection<string> value)
        => OnPropertyChanged(nameof(HasAvailableConfigs));

    public ArculatorSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.ArculatorSettings.Config, out var c)
            && !string.IsNullOrWhiteSpace(c)) Config = c;
    }

    [RelayCommand]
    private void SelectConfig(string name) => Config = name;

    [RelayCommand]
    private void ScanConfigs()
    {
        AvailableConfigs.Clear();
        ScanMessage = null;
        try
        {
            // Chercher configs/ à côté de l'exe Arculator dans les emplacements communs
            var searchRoots = new[]
            {
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86),
                System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
                @"C:\Emulators", @"D:\Emulators", @"E:\Emulators",
            };

            var found = new List<string>();
            foreach (var root in searchRoots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var subDir in new[] { "Arculator", "arculator", "arc" })
                {
                    var cfgDir = System.IO.Path.Combine(root, subDir, "configs");
                    if (!System.IO.Directory.Exists(cfgDir)) continue;
                    foreach (var f in System.IO.Directory.GetFiles(cfgDir, "*.cfg"))
                        found.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                }
            }

            if (found.Count > 0)
            {
                foreach (var name in found.OrderBy(x => x))
                    AvailableConfigs.Add(name);
                OnPropertyChanged(nameof(HasAvailableConfigs));
            }
            else
            {
                ScanMessage = "Aucun dossier configs/ trouvé — vérifiez l'emplacement d'Arculator.";
            }
        }
        catch (Exception ex) { ScanMessage = $"Erreur : {ex.Message}"; }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.ArculatorSettings.Config] = Config,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

public partial class PPSSPPSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen    = false;
    [ObservableProperty] private bool    _escapeExit    = true;  // recommandé pour frontend
    [ObservableProperty] private bool    _pauseMenuExit = true;  // "Exit to menu" → "Exit"
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public PPSSPPSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.PPSSPPKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.PPSSPPKeys.EscapeExit, out var ee))
            EscapeExit = ee != "false";
        if (settings.TryGetValue(DemoBase.App.Services.PPSSPPKeys.PauseMenuExit, out var pme))
            PauseMenuExit = pme != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.PPSSPPKeys.FullScreen]    = FullScreen    ? "true" : "false",
                [DemoBase.App.Services.PPSSPPKeys.EscapeExit]    = EscapeExit    ? "true" : "false",
                [DemoBase.App.Services.PPSSPPKeys.PauseMenuExit] = PauseMenuExit ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── BlueMSX Settings ────────────────────────────────────────────────────────

public partial class BlueMSXSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> MachineOptions { get; } =
    [
        // C-BIOS : pas de ROM propriétaire nécessaire
        new KeyValuePair<string,string>("MSX2 - C-BIOS (défaut, sans ROM)",     "MSX2 - C-BIOS"),
        new KeyValuePair<string,string>("MSX - C-BIOS (MSX1, sans ROM)",         "MSX - C-BIOS"),
        new KeyValuePair<string,string>("MSX 2+ - C-BIOS (sans ROM)",            "MSX 2+ - C-BIOS"),
        // Machines réelles (nécessitent les ROMs correspondantes dans Machines/)
        new KeyValuePair<string,string>("MSX TurboR - Panasonic FS-A1GT",        "MSX TurboR - Panasonic FS-A1GT"),
        new KeyValuePair<string,string>("MSX2 - National FS-5500 F2 (japonais)", "MSX2 - National FS-5500 F2"),
        new KeyValuePair<string,string>("MSX2 - Philips NMS 8250",               "MSX2 - Philips NMS 8250"),
        new KeyValuePair<string,string>("MSX2 - Sony HB-F700P (européen)",       "MSX2 - Sony HB-F700P"),
        new KeyValuePair<string,string>("MSX 2+ - Panasonic FS-A1WX",            "MSX 2+ - Panasonic FS-A1WX"),
        new KeyValuePair<string,string>("MSX - Spectravideo SVI-728",             "SVI - Spectravideo SVI-728"),
        new KeyValuePair<string,string>("ColecoVision",                           "ColecoVision"),
        new KeyValuePair<string,string>("SEGA - SG-1000",                         "SEGA - SG-1000"),
        new KeyValuePair<string,string>("SEGA - SC-3000",                         "SEGA - SC-3000"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> FreqOptions { get; } =
    [
        new KeyValuePair<string,string>("50 Hz (PAL — Europe, défaut)",  "50"),
        new KeyValuePair<string,string>("60 Hz (NTSC — Japon, USA)",     "60"),
    ];

    [ObservableProperty] private string  _machine    = "MSX2 - C-BIOS";
    [ObservableProperty] private string  _freq       = "50";
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public BlueMSXSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.BlueMSXKeys.Machine, out var m)
            && !string.IsNullOrWhiteSpace(m)) Machine = m;
        if (settings.TryGetValue(DemoBase.App.Services.BlueMSXKeys.Freq, out var f)
            && !string.IsNullOrWhiteSpace(f)) Freq = f;
        if (settings.TryGetValue(DemoBase.App.Services.BlueMSXKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.BlueMSXKeys.Machine]    = Machine,
                [DemoBase.App.Services.BlueMSXKeys.Freq]       = Freq,
                [DemoBase.App.Services.BlueMSXKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── DuckStation Settings ─────────────────────────────────────────────────────

public partial class DuckStationSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _batch      = true;  // quitte après power-off (recommandé frontend)
    [ObservableProperty] private bool    _fastBoot   = false;
    [ObservableProperty] private bool    _noGui      = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public DuckStationSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.DuckStationKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.DuckStationKeys.Batch, out var b))
            Batch = b != "false";
        if (settings.TryGetValue(DemoBase.App.Services.DuckStationKeys.FastBoot, out var fb))
            FastBoot = fb == "true";
        if (settings.TryGetValue(DemoBase.App.Services.DuckStationKeys.NoGui, out var ng))
            NoGui = ng == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.DuckStationKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.DuckStationKeys.Batch]      = Batch      ? "true" : "false",
                [DemoBase.App.Services.DuckStationKeys.FastBoot]   = FastBoot   ? "true" : "false",
                [DemoBase.App.Services.DuckStationKeys.NoGui]      = NoGui      ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── puNES Settings ──────────────────────────────────────────────────────────

public partial class PuNESSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> ScaleOptions { get; } =
    [
        new KeyValuePair<string,string>("1x — taille native (256×240)",   "1x"),
        new KeyValuePair<string,string>("2x — 512×480 (défaut)",          "2x"),
        new KeyValuePair<string,string>("3x — 768×720",                   "3x"),
        new KeyValuePair<string,string>("4x — 1024×960",                  "4x"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> FilterOptions { get; } =
    [
        new KeyValuePair<string,string>("Aucun (pixel perfect)",         "none"),
        new KeyValuePair<string,string>("HQ2X",                         "hq2x"),
        new KeyValuePair<string,string>("HQ3X",                         "hq3x"),
        new KeyValuePair<string,string>("HQ4X",                         "hq4x"),
        new KeyValuePair<string,string>("Scale2X",                       "scale2x"),
        new KeyValuePair<string,string>("Scale3X",                       "scale3x"),
        new KeyValuePair<string,string>("xBRZ 2X",                       "xbrz2x"),
        new KeyValuePair<string,string>("xBRZ 3X",                       "xbrz3x"),
        new KeyValuePair<string,string>("xBRZ 4X",                       "xbrz4x"),
        new KeyValuePair<string,string>("NTSC CRT LMP88959",             "ntsc crt lmp88959"),
        new KeyValuePair<string,string>("NTSC NES LMP88959",             "ntsc nes lmp88959"),
        new KeyValuePair<string,string>("PAL CRT LMP88959",              "pal crt lmp88959"),
    ];

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private string  _scale      = "2x";
    [ObservableProperty] private string  _filter     = "none";
    [ObservableProperty] private bool    _vSync      = true;
    [ObservableProperty] private bool    _portable   = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public PuNESSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.PuNESKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.PuNESKeys.Scale, out var sc)
            && !string.IsNullOrWhiteSpace(sc)) Scale = sc;
        if (settings.TryGetValue(DemoBase.App.Services.PuNESKeys.Filter, out var fi)
            && !string.IsNullOrWhiteSpace(fi)) Filter = fi;
        if (settings.TryGetValue(DemoBase.App.Services.PuNESKeys.VSync, out var vs))
            VSync = vs != "false";
        if (settings.TryGetValue(DemoBase.App.Services.PuNESKeys.Portable, out var po))
            Portable = po != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.PuNESKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.PuNESKeys.Scale]      = Scale,
                [DemoBase.App.Services.PuNESKeys.Filter]     = Filter,
                [DemoBase.App.Services.PuNESKeys.VSync]      = VSync ? "true" : "false",
                [DemoBase.App.Services.PuNESKeys.Portable]   = Portable ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── ares Settings ───────────────────────────────────────────────────────────

public partial class AresSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> SystemOptions { get; } =
    [
        new KeyValuePair<string,string>("(auto-détecté selon l'extension)",      ""),
        // Nintendo
        new KeyValuePair<string,string>("Famicom (NES)",                         "Famicom"),
        new KeyValuePair<string,string>("Famicom Disk System",                   "Famicom Disk System"),
        new KeyValuePair<string,string>("Super Famicom (SNES)",                  "Super Famicom"),
        new KeyValuePair<string,string>("Satellaview",                           "Satellaview"),
        new KeyValuePair<string,string>("Game Boy",                              "Game Boy"),
        new KeyValuePair<string,string>("Game Boy Color",                        "Game Boy Color"),
        new KeyValuePair<string,string>("Game Boy Advance",                      "Game Boy Advance"),
        new KeyValuePair<string,string>("Nintendo 64",                           "Nintendo 64"),
        new KeyValuePair<string,string>("64DD",                                  "64DD"),
        // Sega
        new KeyValuePair<string,string>("SG-1000",                               "SG-1000"),
        new KeyValuePair<string,string>("Master System",                         "Master System"),
        new KeyValuePair<string,string>("Game Gear",                             "Game Gear"),
        new KeyValuePair<string,string>("Mega Drive (Genesis)",                  "Mega Drive"),
        new KeyValuePair<string,string>("Mega CD",                               "Mega CD"),
        new KeyValuePair<string,string>("Mega CD 32X",                           "Mega CD 32X"),
        new KeyValuePair<string,string>("32X",                                   "32X"),
        // NEC
        new KeyValuePair<string,string>("PC Engine (TurboGrafx-16)",             "PC Engine"),
        new KeyValuePair<string,string>("PC Engine CD",                          "PC Engine CD"),
        new KeyValuePair<string,string>("SuperGrafx",                            "SuperGrafx"),
        // SNK
        new KeyValuePair<string,string>("Neo Geo AES (console)",                "Neo Geo AES"),
        new KeyValuePair<string,string>("Neo Geo MVS (arcade)",                  "Neo Geo MVS"),
        new KeyValuePair<string,string>("Neo Geo Pocket",                        "Neo Geo Pocket"),
        new KeyValuePair<string,string>("Neo Geo Pocket Color",                  "Neo Geo Pocket Color"),
        // Bandai
        new KeyValuePair<string,string>("WonderSwan",                            "WonderSwan"),
        new KeyValuePair<string,string>("WonderSwan Color",                      "WonderSwan Color"),
        // Autres
        new KeyValuePair<string,string>("ColecoVision",                          "ColecoVision"),
        new KeyValuePair<string,string>("MSX",                                   "MSX"),
        new KeyValuePair<string,string>("MSX2",                                  "MSX2"),
        new KeyValuePair<string,string>("PlayStation",                           "PlayStation"),
    ];

    [ObservableProperty] private string  _system       = "";
    [ObservableProperty] private bool    _fullScreen   = false;
    [ObservableProperty] private bool    _kiosk        = false;
    [ObservableProperty] private bool    _noFilePrompt = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public AresSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.AresKeys.System, out var sys))
            System = sys ?? "";
        if (settings.TryGetValue(DemoBase.App.Services.AresKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.AresKeys.Kiosk, out var k))
            Kiosk = k == "true";
        if (settings.TryGetValue(DemoBase.App.Services.AresKeys.NoFilePrompt, out var nfp))
            NoFilePrompt = nfp != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.AresKeys.System]       = System,
                [DemoBase.App.Services.AresKeys.FullScreen]   = FullScreen   ? "true" : "false",
                [DemoBase.App.Services.AresKeys.Kiosk]        = Kiosk        ? "true" : "false",
                [DemoBase.App.Services.AresKeys.NoFilePrompt] = NoFilePrompt ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Ruffle (Flash) Settings ─────────────────────────────────────────────────

public partial class RuffleSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public RuffleSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.RuffleKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.RuffleKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── MAME Settings ───────────────────────────────────────────────────────────

public partial class MameSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    // Systèmes courants proposés dans le sélecteur d'aide (libellé lisible →
    // nom EXACT du driver MAME). Le champ Machine reste libre : MAME a des
    // milliers de drivers ; le sélecteur ne fait que remplir le champ pour les
    // cas fréquents. Noms de drivers vérifiés sur la doc/les sources MAME.
    public static IReadOnlyList<KeyValuePair<string,string>> MachineSuggestions { get; } =
    [
        new KeyValuePair<string,string>("— choisir un système —",           ""),
        // SNK Neo Geo
        new KeyValuePair<string,string>("Neo Geo CD / CDZ",                  "neocdz"),
        new KeyValuePair<string,string>("Neo Geo CD (modèle initial)",       "neocd"),
        new KeyValuePair<string,string>("Neo Geo AES (console)",             "aes"),
        new KeyValuePair<string,string>("Neo Geo MVS (arcade)",              "neogeo"),
        // Consoles
        new KeyValuePair<string,string>("Atari 2600",                        "a2600"),
        new KeyValuePair<string,string>("Atari 5200",                        "a5200"),
        new KeyValuePair<string,string>("Atari 7800",                        "a7800"),
        new KeyValuePair<string,string>("ColecoVision",                      "coleco"),
        new KeyValuePair<string,string>("Intellivision",                     "intv"),
        new KeyValuePair<string,string>("Vectrex",                           "vectrex"),
        new KeyValuePair<string,string>("Nintendo (NES / Famicom)",          "nes"),
        new KeyValuePair<string,string>("Super Nintendo (SNES)",             "snes"),
        new KeyValuePair<string,string>("Game Boy",                          "gameboy"),
        new KeyValuePair<string,string>("Game Boy Color",                    "gbcolor"),
        new KeyValuePair<string,string>("Game Boy Advance",                  "gba"),
        new KeyValuePair<string,string>("Sega SG-1000",                      "sg1000"),
        new KeyValuePair<string,string>("Sega Master System",                "sms"),
        new KeyValuePair<string,string>("Sega Game Gear",                    "gamegear"),
        new KeyValuePair<string,string>("Sega Mega Drive / Genesis",         "genesis"),
        new KeyValuePair<string,string>("PC Engine / TurboGrafx-16",         "pce"),
        new KeyValuePair<string,string>("SuperGrafx",                        "sgx"),
        new KeyValuePair<string,string>("WonderSwan",                        "wswan"),
        new KeyValuePair<string,string>("WonderSwan Color",                  "wscolor"),
        // Ordinateurs
        new KeyValuePair<string,string>("Amstrad CPC 6128",                  "cpc6128"),
        new KeyValuePair<string,string>("Amstrad CPC 464",                   "cpc464"),
        new KeyValuePair<string,string>("ZX Spectrum 48K",                   "spectrum"),
        new KeyValuePair<string,string>("ZX Spectrum 128K",                  "spec128"),
        new KeyValuePair<string,string>("Commodore 64",                      "c64"),
        new KeyValuePair<string,string>("Commodore 128",                     "c128"),
        new KeyValuePair<string,string>("Commodore VIC-20",                  "vic20"),
        new KeyValuePair<string,string>("Commodore Amiga 500",               "a500"),
        new KeyValuePair<string,string>("Commodore Amiga 1200",              "a1200"),
        new KeyValuePair<string,string>("Atari ST",                          "st"),
        new KeyValuePair<string,string>("Atari STE",                         "ste"),
        new KeyValuePair<string,string>("MSX",                               "msx"),
        new KeyValuePair<string,string>("MSX2",                              "msx2"),
        new KeyValuePair<string,string>("Apple II",                          "apple2"),
        new KeyValuePair<string,string>("Apple IIe",                         "apple2e"),
        new KeyValuePair<string,string>("Sharp X68000",                      "x68000"),
        new KeyValuePair<string,string>("BBC Micro B",                       "bbcb"),
        new KeyValuePair<string,string>("Oric-1",                            "oric1"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> MediaOptions { get; } =
    [
        new KeyValuePair<string,string>("(auto — selon l'extension)", ""),
        new KeyValuePair<string,string>("CD-ROM  (-cdrm)",            "cdrm"),
        new KeyValuePair<string,string>("Cartouche  (-cart)",         "cart"),
        new KeyValuePair<string,string>("Disquette  (-flop1)",        "flop1"),
    ];

    [ObservableProperty] private string  _machine    = "";
    [ObservableProperty] private string  _machinePreset = "";
    [ObservableProperty] private string  _mediaSlot  = "";
    [ObservableProperty] private string  _romPath    = "";
    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _skipInfo   = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public MameSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.MameKeys.Machine, out var m))
            Machine = m ?? "";
        if (settings.TryGetValue(DemoBase.App.Services.MameKeys.MediaSlot, out var ms))
            MediaSlot = ms ?? "";
        if (settings.TryGetValue(DemoBase.App.Services.MameKeys.RomPath, out var rp))
            RomPath = rp ?? "";
        if (settings.TryGetValue(DemoBase.App.Services.MameKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.MameKeys.SkipInfo, out var si))
            SkipInfo = si != "false";
    }

    // Sélecteur d'aide : choisir un système dans la liste remplit le champ Machine
    // avec le nom de driver correspondant. Le champ reste ensuite librement éditable.
    partial void OnMachinePresetChanged(string value)
    {
        if (!string.IsNullOrEmpty(value))
            Machine = value;
    }

    [RelayCommand]
    private void BrowseRomPath()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title            = "Choisir le dossier des BIOS MAME",
            InitialDirectory = !string.IsNullOrWhiteSpace(RomPath)
                               && System.IO.Directory.Exists(RomPath)
                ? RomPath
                : AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog() == true)
            RomPath = dlg.FolderName;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.MameKeys.Machine]    = Machine?.Trim() ?? "",
                [DemoBase.App.Services.MameKeys.MediaSlot]  = MediaSlot ?? "",
                [DemoBase.App.Services.MameKeys.RomPath]    = RomPath?.Trim() ?? "",
                [DemoBase.App.Services.MameKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.MameKeys.SkipInfo]   = SkipInfo   ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Stella Settings ─────────────────────────────────────────────────────────

public partial class StellaSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> PaletteOptions { get; } =
    [
        new KeyValuePair<string,string>("Standard Stella",         "standard"),
        new KeyValuePair<string,string>("z26 (autre palette)",     "z26"),
        new KeyValuePair<string,string>("Utilisateur (user)",      "user"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> ZoomOptions { get; } =
    [
        new KeyValuePair<string,string>("1× (256×224)",  "1"),
        new KeyValuePair<string,string>("2× (512×448)",  "2"),
        new KeyValuePair<string,string>("3× (768×672)",  "3"),
        new KeyValuePair<string,string>("4× (1024×896)", "4"),
    ];

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _vSync      = true;
    [ObservableProperty] private string  _zoom       = "2";
    [ObservableProperty] private string  _palette    = "standard";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public StellaSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.StellaKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.StellaKeys.VSync, out var vs))
            VSync = vs != "false";
        if (settings.TryGetValue(DemoBase.App.Services.StellaKeys.Zoom, out var z)
            && !string.IsNullOrWhiteSpace(z)) Zoom = z;
        if (settings.TryGetValue(DemoBase.App.Services.StellaKeys.Palette, out var p)
            && !string.IsNullOrWhiteSpace(p)) Palette = p;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.StellaKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.StellaKeys.VSync]      = VSync      ? "true" : "false",
                [DemoBase.App.Services.StellaKeys.Zoom]       = Zoom,
                [DemoBase.App.Services.StellaKeys.Palette]    = Palette,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── ProSystem Settings ───────────────────────────────────────────────────────

public partial class ProSystemSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    // Résolutions proposées le 2026-07-24 à la demande de l'utilisateur — patchées dans
    // ProSystem.ini ([Display] Mode.Width/Mode.Height) juste avant chaque lancement par
    // ProSystemLauncher.ApplyDisplaySettings (ProSystem n'a pas d'option CLI équivalente).
    public static IReadOnlyList<string> ResolutionOptions { get; } =
    [
        "640x480",
        "800x600",
        "1024x768",
        "1280x1024",
        "1280x800",
        "1680x1050",
        "1280x720",
        "1600x900",
    ];

    [ObservableProperty] private bool    _fullScreen;
    [ObservableProperty] private string  _selectedResolution = DemoBase.App.Services.ProSystemKeys.DefaultResolution;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ProSystemSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;

        if (settings.TryGetValue(DemoBase.App.Services.ProSystemKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.ProSystemKeys.Resolution, out var res)
            && !string.IsNullOrWhiteSpace(res))
            SelectedResolution = res;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.ProSystemKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.ProSystemKeys.Resolution] = SelectedResolution,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Xenia Settings ──────────────────────────────────────────────────────────

public partial class XeniaSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> GpuOptions { get; } =
    [
        new KeyValuePair<string,string>("any — auto-détection (défaut)", "any"),
        new KeyValuePair<string,string>("vulkan — recommandé (AMD/NVIDIA/Intel)", "vulkan"),
        new KeyValuePair<string,string>("d3d12 — meilleures perfs sur certains jeux", "d3d12"),
    ];

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _vSync      = true;
    [ObservableProperty] private string  _gpu        = "any";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public XeniaSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.XeniaKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.XeniaKeys.VSync, out var vs))
            VSync = vs != "false";
        if (settings.TryGetValue(DemoBase.App.Services.XeniaKeys.Gpu, out var g)
            && !string.IsNullOrWhiteSpace(g)) Gpu = g;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.XeniaKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.XeniaKeys.VSync]      = VSync      ? "true" : "false",
                [DemoBase.App.Services.XeniaKeys.Gpu]        = Gpu,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── CXBX-Reloaded Settings ──────────────────────────────────────────────────

public partial class CxbxReloadedSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public CxbxReloadedSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, new Dictionary<string, string?>());
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── AppleWin Settings ───────────────────────────────────────────────────────

public partial class AppleWinSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> ModelOptions { get; } =
    [
        new KeyValuePair<string,string>("Enhanced Apple //e (défaut recommandé)", "apple2ee"),
        new KeyValuePair<string,string>("Apple //e",                              "apple2e"),
        new KeyValuePair<string,string>("Apple II+",                              "apple2p"),
        new KeyValuePair<string,string>("Apple II",                               "apple2"),
        new KeyValuePair<string,string>("Apple II J-Plus",                        "apple2jp"),
    ];

    public static IReadOnlyList<KeyValuePair<string,string>> FreqOptions { get; } =
    [
        new KeyValuePair<string,string>("60 Hz — NTSC (USA/Japon, défaut)", "60hz"),
        new KeyValuePair<string,string>("50 Hz — PAL (Europe)",             "50hz"),
    ];

    [ObservableProperty] private string  _model   = "apple2ee";
    [ObservableProperty] private string  _freq    = "60hz";
    [ObservableProperty] private bool    _powerOn = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public AppleWinSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.AppleWinKeys.Model, out var m)
            && !string.IsNullOrWhiteSpace(m)) Model = m;
        if (settings.TryGetValue(DemoBase.App.Services.AppleWinKeys.Freq, out var f)
            && !string.IsNullOrWhiteSpace(f)) Freq = f;
        if (settings.TryGetValue(DemoBase.App.Services.AppleWinKeys.PowerOn, out var po))
            PowerOn = po != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.AppleWinKeys.Model]   = Model,
                [DemoBase.App.Services.AppleWinKeys.Freq]    = Freq,
                [DemoBase.App.Services.AppleWinKeys.PowerOn] = PowerOn ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── GSplus Settings ─────────────────────────────────────────────────────────

public partial class GSplusSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public static IReadOnlyList<KeyValuePair<string,string>> SlotOptions { get; } =
    [
        new KeyValuePair<string,string>("Auto (selon extension)",          ""),
        new KeyValuePair<string,string>("s5d1 — Slot 5 Drive 1 (3.5\", ProDOS .2mg/.po)", "s5d1"),
        new KeyValuePair<string,string>("s6d1 — Slot 6 Drive 1 (5.25\", DOS .dsk/.woz)", "s6d1"),
        new KeyValuePair<string,string>("s6d2 — Slot 6 Drive 2",          "s6d2"),
        new KeyValuePair<string,string>("s7d1 — Slot 7 Drive 1 (HD .hdv)", "s7d1"),
    ];

    [ObservableProperty] private string  _slot      = "";
    [ObservableProperty] private bool    _resizeable = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public GSplusSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.GSplusKeys.Slot, out var sl))
            Slot = sl ?? "";
        if (settings.TryGetValue(DemoBase.App.Services.GSplusKeys.Resizeable, out var r))
            Resizeable = r != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.GSplusKeys.Slot]      = Slot,
                [DemoBase.App.Services.GSplusKeys.Resizeable] = Resizeable ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Pcsx2SettingsViewModel ───────────────────────────────────────────────────

public partial class Pcsx2SettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _batch      = true;  // quitte après power-off
    [ObservableProperty] private bool    _fastBoot   = false;
    [ObservableProperty] private bool    _noGui      = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Pcsx2SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.Pcsx2Keys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.Pcsx2Keys.BatchMode, out var b))
            Batch = b != "false";
        if (settings.TryGetValue(DemoBase.App.Services.Pcsx2Keys.FastBoot, out var fb))
            FastBoot = fb == "true";
        if (settings.TryGetValue(DemoBase.App.Services.Pcsx2Keys.NoGui, out var ng))
            NoGui = ng == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.Pcsx2Keys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.Pcsx2Keys.BatchMode]  = Batch      ? "true" : "false",
                [DemoBase.App.Services.Pcsx2Keys.FastBoot]   = FastBoot   ? "true" : "false",
                [DemoBase.App.Services.Pcsx2Keys.NoGui]      = NoGui      ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Trs80gpSettingsViewModel ─────────────────────────────────────────────────

public partial class Trs80gpSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _isModel1    = false;
    [ObservableProperty] private bool    _isModel3    = true;
    [ObservableProperty] private bool    _isModel4    = false;
    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Trs80gpSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        var model = settings.GetValueOrDefault(DemoBase.App.Services.Trs80gpKeys.Model, "3");
        IsModel1 = model == "1";
        IsModel3 = model == "3";
        IsModel4 = model == "4";
        if (settings.TryGetValue(DemoBase.App.Services.Trs80gpKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var model = IsModel1 ? "1" : IsModel4 ? "4" : "3";
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.Trs80gpKeys.Model]      = model,
                [DemoBase.App.Services.Trs80gpKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── OricutronSettingsViewModel ───────────────────────────────────────────────

public partial class OricutronSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _isOric1     = false;
    [ObservableProperty] private bool    _isAtmos     = true;
    [ObservableProperty] private bool    _isTelestrat = false;
    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public OricutronSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        var machine = settings.GetValueOrDefault(DemoBase.App.Services.OricutronKeys.Machine, "atmos");
        IsOric1     = machine == "oric1";
        IsAtmos     = machine == "atmos";
        IsTelestrat = machine == "telestrat";
        if (settings.TryGetValue(DemoBase.App.Services.OricutronKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var machine = IsOric1 ? "oric1" : IsTelestrat ? "telestrat" : "atmos";
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.OricutronKeys.Machine]     = machine,
                [DemoBase.App.Services.OricutronKeys.FullScreen]  = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── DolphinSettingsViewModel ─────────────────────────────────────────────────

public partial class DolphinSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen = false;
    [ObservableProperty] private bool    _batch      = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public DolphinSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.DolphinKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        if (settings.TryGetValue(DemoBase.App.Services.DolphinKeys.BatchMode, out var b))
            Batch = b != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.DolphinKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.DolphinKeys.BatchMode]  = Batch      ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── SimCoupeSettingsViewModel ────────────────────────────────────────────────

public partial class SimCoupeSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public SimCoupeSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.SimCoupeKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.SimCoupeKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── FlycastSettingsViewModel ─────────────────────────────────────────────────

public partial class FlycastSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen   = false;
    [ObservableProperty] private bool    _regionJapan  = false;
    [ObservableProperty] private bool    _regionUSA    = false;
    [ObservableProperty] private bool    _regionEurope = false;
    [ObservableProperty] private bool    _regionDefault = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public FlycastSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.FlycastKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        var region = settings.GetValueOrDefault(DemoBase.App.Services.FlycastKeys.Region, "3");
        RegionJapan   = region == "0";
        RegionUSA     = region == "1";
        RegionEurope  = region == "2";
        RegionDefault = region == "3" || region is null;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var region = RegionJapan ? "0" : RegionUSA ? "1" : RegionEurope ? "2" : "3";
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.FlycastKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.FlycastKeys.Region]     = region,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── JzIntvSettingsViewModel ──────────────────────────────────────────────────

public partial class JzIntvSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public JzIntvSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.JzIntvKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.JzIntvKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── DcmotoSettingsViewModel ──────────────────────────────────────────────────

public partial class DcmotoSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private string  _machine     = string.Empty;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public static IReadOnlyList<KeyValuePair<string,string>> MachineOptions { get; } =
    [
        new("(auto-détection)", ""),
        new("MO5",              "mo5"),
        new("MO5E",             "mo5e"),
        new("MO5NR",            "mo5nr"),
        new("MO6",              "mo6"),
        new("Olivetti PC128",   "pc128"),
        new("T9000",            "t9000"),
        new("TO7",              "to7"),
        new("TO7/70",           "to770"),
        new("TO8",              "to8"),
        new("TO8D",             "to8d"),
        new("TO9",              "to9"),
        new("TO9+",             "to9p"),
    ];

    public DcmotoSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        Machine = settings.GetValueOrDefault(DemoBase.App.Services.DcmotoKeys.Machine, string.Empty) ?? string.Empty;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.DcmotoKeys.Machine] = Machine,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Xm6TypeGSettingsViewModel ────────────────────────────────────────────────

public partial class Xm6TypeGSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Xm6TypeGSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.Xm6TypeGKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.Xm6TypeGKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── BeebEmSettingsViewModel ──────────────────────────────────────────────────

public partial class BeebEmSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isModelB       = true;
    [ObservableProperty] private bool    _isModelBPlus   = false;
    [ObservableProperty] private bool    _isModelMaster  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public BeebEmSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.BeebEmKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
        var model = settings.GetValueOrDefault(DemoBase.App.Services.BeebEmKeys.Model, "ModelB");
        IsModelB      = model == "ModelB" || model == "b";
        IsModelBPlus  = model == "BPlus"  || model == "bplus";
        IsModelMaster = model == "Master128" || model == "master128";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var model = IsModelBPlus ? "BPlus" : IsModelMaster ? "Master128" : "ModelB";
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.BeebEmKeys.FullScreen] = FullScreen ? "true" : "false",
                [DemoBase.App.Services.BeebEmKeys.Model]      = model,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── SQLuxSettingsViewModel ───────────────────────────────────────────────────

public partial class SQLuxSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _fullScreen  = false;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public SQLuxSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.SQLuxKeys.FullScreen, out var fs))
            FullScreen = fs == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.SQLuxKeys.FullScreen] = FullScreen ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// RyujinxSettingsViewModel retiré le 2026-07-24 (Ryujinx retiré des émulateurs — enum
// EmulatorType.Ryujinx conservé, cf. EmulatorSeedCatalog.cs).

// ─── ColEm Settings ──────────────────────────────────────────────────────────

public partial class ColEmSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public const string KEY_VIDEO    = "video_standard";
    public const string KEY_SGM      = "sgm";

    [ObservableProperty] private string _videoStandard = "ntsc";
    [ObservableProperty] private bool   _sgmEnabled;
    [ObservableProperty] private bool   _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public ColEmSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(KEY_VIDEO, out var v) && !string.IsNullOrWhiteSpace(v))
            VideoStandard = v;
        SgmEnabled = settings.GetValueOrDefault(KEY_SGM) == "true";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, new Dictionary<string, string?>
            {
                [KEY_VIDEO] = VideoStandard,
                [KEY_SGM]   = SgmEnabled ? "true" : "false",
            });
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── mz800emu Settings ───────────────────────────────────────────────────────

public partial class Mz800EmuSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    public const string KEY_MACHINE = "machine"; // "mz800" | "mz700-pal" | "mz700-ntsc" | "mz1500"

    public static IReadOnlyList<KeyValuePair<string, string>> MachineOptions { get; } =
    [
        new KeyValuePair<string, string>("MZ-800  (mz800emu.exe)",         "mz800"),
        new KeyValuePair<string, string>("MZ-700 PAL  (mz700emu-pal.exe)", "mz700-pal"),
        new KeyValuePair<string, string>("MZ-700 NTSC (mz700emu-ntsc.exe)","mz700-ntsc"),
        new KeyValuePair<string, string>("MZ-1500 (mz1500emu.exe)",        "mz1500"),
    ];

    [ObservableProperty] private string  _machineType = "mz800";
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Mz800EmuSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(KEY_MACHINE, out var m) && !string.IsNullOrWhiteSpace(m))
            MachineType = m;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId,
                new Dictionary<string, string?> { [KEY_MACHINE] = MachineType });
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── ep128emu Settings ───────────────────────────────────────────────────────

public partial class Ep128EmuSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    // Clés partagées avec Ep128EmuLauncher (définie dans Ep128EmuKeys)
    private const string KEY_MACHINE  = DemoBase.App.Services.Ep128EmuKeys.KEY_MACHINE;
    private const string KEY_CFG_FILE = DemoBase.App.Services.Ep128EmuKeys.KEY_CFG_FILE;

    public static IReadOnlyList<KeyValuePair<string, string>> MachineOptions { get; } =
    [
        new KeyValuePair<string, string>("Enterprise 64/128 (ep128)", "ep128"),
        new KeyValuePair<string, string>("ZX Spectrum (zx)",          "zx"),
        new KeyValuePair<string, string>("Amstrad CPC (cpc)",         "cpc"),
        new KeyValuePair<string, string>("Videoton TVC (tvc)",        "tvc"),
    ];

    [ObservableProperty] private string  _machineType = "ep128";
    [ObservableProperty] private string? _cfgFile;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public Ep128EmuSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(KEY_MACHINE,  out var m) && !string.IsNullOrWhiteSpace(m)) MachineType = m;
        if (settings.TryGetValue(KEY_CFG_FILE, out var c)) CfgFile = c;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [KEY_MACHINE]    = MachineType,
                [KEY_CFG_FILE]   = CfgFile,
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── GeePee32 Settings ───────────────────────────────────────────────────────

public partial class GeePee32SettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _noSplash  = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public GeePee32SettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.GeePee32Keys.NoSplash, out var ns))
            NoSplash = ns != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.GeePee32Keys.NoSplash] = NoSplash ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── BigPEmu Settings ────────────────────────────────────────────────────────

public partial class BigPEmuSettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _palMode    = true;
    [ObservableProperty] private bool    _localData  = true;
    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public BigPEmuSettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
        if (settings.TryGetValue(DemoBase.App.Services.BigPEmuKeys.PalMode, out var pm))
            PalMode = pm != "false";
        if (settings.TryGetValue(DemoBase.App.Services.BigPEmuKeys.LocalData, out var ld))
            LocalData = ld != "false";
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            var s = new Dictionary<string, string?>
            {
                [DemoBase.App.Services.BigPEmuKeys.PalMode]   = PalMode   ? "true" : "false",
                [DemoBase.App.Services.BigPEmuKeys.LocalData] = LocalData ? "true" : "false",
            };
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, s);
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// ─── Handy Settings ───────────────────────────────────────────────────────────

public partial class HandySettingsViewModel : ObservableObject
{
    private readonly int         _emulatorConfigId;
    private readonly IUnitOfWork _uow;

    [ObservableProperty] private bool    _isSaving;
    [ObservableProperty] private string? _saveMessage;

    public HandySettingsViewModel(int emulatorConfigId,
        Dictionary<string, string?> settings, IUnitOfWork uow)
    {
        _emulatorConfigId = emulatorConfigId;
        _uow              = uow;
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        IsSaving = true;
        try
        {
            await _uow.Emulators.SaveSettingsAsync(_emulatorConfigId, new Dictionary<string, string?>());
            SaveMessage = DemoBase.App.Services.LocalizationService.Get("Msg_ConfigSaved");
        }
        catch (Exception ex) { SaveMessage = $"✗ Erreur : {ex.Message}"; }
        finally { IsSaving = false; }
    }
}

// Rpcs3SettingsViewModel retiré le 2026-07-24 (RPCS3 retiré des émulateurs — enum
// EmulatorType.Rpcs3 conservé, cf. EmulatorSeedCatalog.cs).
