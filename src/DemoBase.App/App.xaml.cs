using DemoBase.App.Services;
using AppEmulatorService = DemoBase.App.Services.EmulatorService;
using DemoBase.App.ViewModels;
using DemoBase.App.ViewModels.Emulators;
using DemoBase.App.ViewModels.Library;
using DemoBase.App.ViewModels.Releases;
using DemoBase.Core.Interfaces;
using DemoBase.Data;
using DemoBase.Data.Context;
using DemoBase.Data.Repositories;
using DemoBase.Import;
using DemoBase.Media;
using DemoBase.Core.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Windows;

namespace DemoBase.App;

public partial class App : Application
{
    internal IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        PerfLogger.Initialize();
        PerfLogger.Mark("Application startup");

        // 2026-07-30, retour utilisateur : "je vois souvent ce genre d'exception dans
        // visual studio [...] possible de les logger [...] (à logguer uniquement en
        // environnement de debug)" — voir FirstChanceExceptionLogger pour le détail ;
        // no-op silencieux en Release (DebugHelper.IsDebugMode).
        DemoBase.App.Services.FirstChanceExceptionLogger.Initialize();

        // Base unique dans un sous-dossier Database/ à côté de l'exécutable
        // → facilite le déploiement (tout est dans le dossier de l'app).
        // Fusion config.db/dats.db/demobase.db en un seul fichier : l'app n'étant
        // pas encore déployée au moment de cette fusion, aucune migration de
        // données existantes n'était nécessaire (cf. RESUME_PROJET.md).
        var dbDir   = Path.Combine(AppContext.BaseDirectory, "Database");
        var dbPath  = Path.Combine(dbDir, "demobase.db");
        var connStr = $"Data Source={dbPath}";

        // 2026-08-07, retour utilisateur (deux rapports indépendants : lancement de
        // DemoBase depuis un partage SMB provoquant soit un blocage silencieux
        // ("loading circle... puis plus rien"), soit un APPCRASH avec KERNELBASE.dll
        // et une boucle procmon dans C:\Windows\CSC\...\namespace\<IP du serveur> —
        // c'est-à-dire le cache "Offline Files" de Windows) : marque de diagnostic
        // AVANT même la création du dossier Database/, pour que perf_log.txt dise
        // explicitement si AppContext.BaseDirectory a été détecté comme un chemin
        // réseau. Utile pour un futur rapport similaire : permet de distinguer "la
        // détection a marché mais quelque chose D'AUTRE bloque quand même" de "la
        // détection elle-même a raté" (lecteur mappé via un outil tiers que
        // DriveInfo.DriveType ne reconnaît pas comme réseau, par exemple).
        if (DbInitializer.IsNetworkPath(AppContext.BaseDirectory))
            PerfLogger.Mark(
                "Chemin réseau détecté pour le dossier de l'application (SMB/NFS) — " +
                "journal_mode=DELETE forcé sur les bases SQLite pour éviter le mode WAL " +
                "(non fiable sur ce type de partage, cf. sqlite.org/wal.html). Si l'appli " +
                "reste bloquée ou plante malgré tout après cette ligne, la cause probable " +
                "n'est plus SQLite mais le cache \"Offline Files\" de Windows pour ce " +
                "partage (dossier C:\\Windows\\CSC\\...) — voir RESUME_PROJET.md.");

        // ── Étape 1 : créer le schéma SQLite ─────────────────────────────────
        // Database/ est une EXCEPTION volontaire : il doit exister avant même
        // que le wizard ne démarre, car c'est cette base qui stocke les chemins
        // choisis DANS le wizard (BIOS, Configs, Releases, Working). Sans lui,
        // impossible de persister quoi que ce soit. Tous les autres dossiers ne
        // sont créés qu'à la fin du wizard via AppPaths.CreateDirectories().
        Directory.CreateDirectory(dbDir);
        try
        {
            using (PerfLogger.Begin("DbInitializer.InitializeAsync"))
                await DbInitializer.InitializeAsync(connStr);
        }
        catch (Exception ex)
        {
            // 2026-08-02, retour utilisateur ("un utilisateur me dit que le
            // logiciel ne se lance pas si il est déposé sur un partage SMB") :
            // avant ce correctif, une exception ICI (SQLite, permissions,
            // disque...) provoquait un plantage silencieux — OnStartup est
            // "async void", donc rien n'attrapait l'exception au niveau de
            // l'appelant ; elle remontait telle quelle et tuait le process sans
            // aucun message. DbInitializer.InitializeAsync bascule désormais
            // automatiquement sur journal_mode=DELETE quand la base est détectée
            // sur un chemin réseau (WAL n'est pas fiable sur SMB/NFS), ce qui
            // couvre la cause la plus probable — mais ce garde-fou reste utile
            // pour toute AUTRE cause (permissions, disque plein, partage
            // inaccessible…) : au moins l'utilisateur voit un message clair au
            // lieu d'un plantage muet.
            // 2026-08-07 : message enrichi suite à un rapport où le blocage/plantage
            // n'était PAS une exception ICI (journal_mode=DELETE avait déjà été
            // appliqué avec succès), mais survenait plus loin, avec une signature
            // procmon renvoyant au cache "Offline Files" (CSC) de Windows pour ce
            // partage — piste ajoutée en complément de la recommandation "copier en
            // local", pour les utilisateurs qui veulent vraiment rester sur le réseau.
            MessageBox.Show(
                "DemoBase n'a pas pu initialiser sa base de données et ne peut pas démarrer.\n\n" +
                $"Erreur : {ex.Message}\n\n" +
                "Si l'application est lancée depuis un partage réseau (SMB), essayez de la " +
                "copier sur un disque local — SQLite ne fonctionne pas de façon fiable sur " +
                "certains partages réseau.\n\n" +
                "Si vous devez absolument rester sur le réseau et que le problème persiste, " +
                "essayez de désactiver le mode \"Fichiers hors connexion\" de Windows pour ce " +
                "partage (Panneau de configuration > Centre de synchronisation > Gérer les " +
                "fichiers hors connexion) — certains serveurs SMB déclenchent ce cache local " +
                "de façon instable.",
                "DemoBase — Erreur de démarrage",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Current.Shutdown(-1);
            return;
        }

        // Note pour le débogage : chemin de la base affiché dans la barre de titre
        Current.MainWindow?.Dispatcher.Invoke(() => { });
        System.Diagnostics.Debug.WriteLine($"[DemoBase] DB: {dbPath}");

        // ── Étape 2 : démarrer le host DI ────────────────────────────────────
        _host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(cfg =>
            {
                cfg.SetBasePath(AppContext.BaseDirectory)
                   .AddJsonFile("appsettings.json", optional: true)
                   .AddJsonFile("appsettings.user.json", optional: true);
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddDbContextFactory<DemoBaseDbContext>(opt =>
                    opt.UseSqlite(connStr, sqliteOpt =>
                        sqliteOpt.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery))
                       .UseLoggerFactory(LoggerFactory.Create(b => b.ClearProviders()))
                       .ConfigureWarnings(w =>
                            w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId
                                .MultipleCollectionIncludeWarning)));

                services.AddScoped<IUnitOfWork, UnitOfWork>();
                services.AddScoped<IReleaseService,     ReleaseService>();
                services.AddScoped<IReleaseTypeService, ReleaseTypeService>();
                services.AddScoped<IImportService,      MySqlImportService>();
                services.AddScoped<IMediaService,       MediaService>();
                services.AddSingleton<INavigationService, NavigationService>();
                services.AddTransient<DemoBase.App.ViewModels.MediaBrowserViewModel>();
                services.AddSingleton<GlobalKeyboardService>();
                services.AddSingleton<ThemeService>();
                services.AddSingleton<DemoBase.App.Services.LocalizationService>();
                services.AddTransient<DemozooVersionService>();
                services.AddTransient<DemozooImportService>();
                services.AddTransient<ScreenshotDownloadService>();
                services.AddSingleton(_ => new DemoBase.Data.DatImportService(connStr));
                services.AddSingleton<DemoBase.App.Services.DbSetupDownloadService>();
                services.AddSingleton<DemoBase.App.Services.EmulatorConfigExportService>();
                services.AddSingleton<DemoBase.App.Services.ConfigsUpdateService>();
                services.AddSingleton<DemoBase.App.Services.DatsUpdateService>();
                services.AddSingleton<DemoBase.App.Services.AppUpdateService>();
                services.AddSingleton<DemoBase.App.Services.RomScanService>();
                services.AddTransient<DemoBase.App.Services.EmulatorSeedService>();
                services.AddTransient<DemoBase.App.Services.ReleaseBuilder.ReleaseBuilderService>();
                services.AddSingleton(_ => new DemoBase.Data.DownloadAttemptService(connStr));
                services.AddSingleton(_ => new DemoBase.Data.PreferencesService(connStr));
                services.AddSingleton(_ => new DemoBase.Data.FavoriteSoundtrackService(connStr));
                services.AddSingleton(_ => new DemoBase.Data.PlaylistService(connStr));
                services.AddSingleton(_ => new DemoBase.Data.FavoriteGraphicService(connStr));
                // 2026-07-30, demande utilisateur : onglet "Musique (modland)" — catalogue
                // Modland (http://ftp.modland.com/) stocké/indexé localement. Singleton :
                // ModlandService garde un HttpClient statique de toute façon, mais reste
                // cohérent avec les autres services connectés à demobase.db ci-dessus.
                services.AddSingleton(_ => new DemoBase.Data.ModlandCatalogService(connStr));
                services.AddSingleton<DemoBase.App.Services.ModlandService>();
                services.AddSingleton(_ => new DemoBase.Data.ReleaseProfileOverrideService(connStr));
                // 2026-07-25 : override par FICHIER (DatEntry), en complément de l'override par
                // release ci-dessus — cf. RESUME_PROJET.md pour le contexte (release multi-
                // plateforme ET multi-fichier, ex. Amiga AGA + Atari Falcon).
                services.AddSingleton(_ => new DemoBase.Data.DatEntryProfileOverrideService(connStr));
                // 2026-07-25 : fichier préféré par release (quel DatEntry lancer parmi
                // plusieurs) — cf. RESUME_PROJET.md, fenêtre de choix de fichier au clic
                // sur "Lancer".
                services.AddSingleton(_ => new DemoBase.Data.ReleasePreferredFileService(connStr));
                services.AddSingleton(_ => new DemoBase.Data.ReleaseProfileOverrideExportService(connStr));
                // CORRECTIF (StackOverflowException) : était en AddTransient. MainViewModel
                // référence FavSoundtracksVm (propriété calculée = GetRequiredService<...>())
                // depuis OnNavigated(), à CHAQUE navigation dans l'appli (pas seulement vers
                // Favoris), pour appeler StopPlayback() dessus dès qu'on n'est pas sur l'écran
                // Favoris. En Transient, chaque navigation reconstruisait une INSTANCE NEUVE —
                // dont le constructeur déclenche aussitôt deux requêtes SQLite non attendues
                // (GetAllAsync + LoadPlaylistsAsync) — juste pour appeler StopPlayback() sur un
                // player qui n'a jamais existé (no-op silencieux : le vrai lecteur Favoris en
                // cours, s'il y en avait un, n'était donc jamais réellement arrêté). Lors d'une
                // rafale de navigations rapprochées (ex. cascade PlaybackStartFailed → PlayNext
                // sur une release sans fichier jouable), ça empilait des dizaines de requêtes
                // SQLite concurrentes en quelques instants — cause probable du
                // StackOverflowException observé dans Microsoft.Data.Sqlite.dll (ExecuteReaderAsync
                // de FavoriteSoundtrackService.GetAllAsync). Passé en Singleton, comme
                // ReleaseDetailViewModel juste au-dessus (même schéma d'usage exact) : une seule
                // instance vivante pour toute la session, StopPlayback() agit enfin sur le bon
                // objet, et le constructeur (donc les 2 requêtes SQLite) ne s'exécute plus
                // qu'une fois au premier accès.
                services.AddSingleton<FavoriteSoundtracksViewModel>();
                services.AddTransient<FavoriteGraphicsViewModel>();
                services.AddTransient<IEmulatorService, AppEmulatorService>();
                services.AddSingleton<DemoBase.App.Services.EmulatorInstallerService>();
                services.AddTransient<DemoBase.App.ViewModels.EmulatorInstallerViewModel>();
                services.AddSingleton<DemoBase.App.Services.LocalVideoCaptureService>();
                services.AddTransient<PreferencesViewModel>();

                // ── TrackerPlayer ─────────────────────────────────────────────
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Decoders.ModDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Decoders.XmDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Decoders.S3mDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Decoders.StmDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Decoders.DbmDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Players.ZXTuneDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerDecoder, TrackerPlayer.Core.Players.UadeDecoder>();
                services.AddSingleton<TrackerPlayer.Core.Interfaces.ITrackerService, TrackerPlayer.Core.Players.TrackerService>();

                services.AddSingleton<MainViewModel>();
                services.AddTransient<ReleaseListViewModel>();
                services.AddSingleton<ReleaseDetailViewModel>();
                services.AddTransient<ReleaseEditViewModel>();
                services.AddSingleton<ReleaserDetailViewModel>();
                services.AddSingleton<PartyDetailViewModel>();
                // ── Library VMs — Singleton pour conserver l'état (filtre lettre, scroll…)
                services.AddSingleton<GroupListViewModel>();
                services.AddSingleton<ScenerListViewModel>();
                services.AddSingleton<PlatformListViewModel>();
                services.AddSingleton<PartyListViewModel>();
                // ── Gestion (stubs) ───────────────────────────────────────────
                services.AddTransient<EmulatorLaunchService>();
                services.AddTransient<DemoBase.App.ViewModels.Emulators.EmulatorSettingsViewModel>();
                services.AddTransient<ImportViewModel>();
                services.AddTransient<MediaLibraryViewModel>();
                services.AddTransient<PreferencesViewModel>();

                services.AddSingleton<MainWindow>();
                services.AddTransient<DemoBase.App.Views.Settings.PreferencesView>();
                services.AddTransient<ImportProgressWindow>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddProvider(new DemoBase.App.TimestampDebugLoggerProvider());
            })
            .Build();

        await _host.StartAsync();

        // ── Étape 2b : appliquer le thème et la langue sauvegardés ──────────
        try
        {
            var prefsService = _host.Services.GetRequiredService<DemoBase.Data.PreferencesService>();
            var themeService = _host.Services.GetRequiredService<DemoBase.App.Services.ThemeService>();
            var locService   = _host.Services.GetRequiredService<DemoBase.App.Services.LocalizationService>();
            var savedPrefs   = await prefsService.LoadAllAsync();
            var appTheme     = savedPrefs.Theme == "Dark"
                ? DemoBase.App.Services.AppTheme.Dark
                : DemoBase.App.Services.AppTheme.Light;
            themeService.Apply(appTheme);

            // Auto-détection de la langue au premier démarrage (wizard pas encore complété) :
            // si la culture système est française → "fr", sinon → "en".
            var language = savedPrefs.Language;
            if (!savedPrefs.WizardCompleted)
            {
                var culture = System.Globalization.CultureInfo.CurrentUICulture;
                language = culture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase)
                    ? "fr" : "en";
            }
            locService.Apply(language);

            // Nettoyage complet du dossier de travail Working\ au démarrage —
            // fichiers et sous-dossiers, récursif. Tout le contenu est reconstructible
            // (ZIPs extraits pour émulateurs, WAV ZXTune/UADE, images Recoil, scripts…).
            DbInitializer.CleanExtractedCache(WorkingPaths.Root);

            // Rediriger les WAV temporaires ZXTune vers Working\Tracker
            // (au lieu de %TEMP%\TrackerPlayer — cohérent avec la consolidation Working)
            TrackerPlayer.Core.Players.TempDir.Override(WorkingPaths.GetSubdir("Tracker"));

            // 2026-08-06 : le nettoyage des copies compagnon UADE (TFMX mdat/smpl, Thomas
            // Hermann thm/smp...) qu'un arrêt brutal de la session précédente pouvait laisser
            // orphelines dans le dossier d'uade123.exe n'est plus nécessaire — UadePlayer
            // utilise désormais libuade.dll (pont natif) et résout les fichiers compagnons en
            // pointant le répertoire courant du process sur le dossier du fichier ouvert
            // (SetCwdToFileDir), sans plus jamais copier quoi que ce soit. L'ancienne méthode
            // UadePlayer.CleanupOrphanedCompanionCopies() a été retirée avec le reste du code
            // de copie (CopyTfmxCompanionFiles/CopyTfmxForQuery) — cf. RESUME_PROJET.md.

            // Cache persistant des durées UADE (par fichier MD5 + sous-chanson), scanné
            // automatiquement en arrière-plan par UadePlayer à l'ouverture d'un fichier —
            // cf. TrackerPlayer.Core/Players/UadeDurationDatabase.cs. À côté de demobase.db
            // (dossier "Database", jamais nettoyé), PAS dans Working\ (vidé à chaque démarrage
            // par CleanExtractedCache ci-dessus — une base persistante n'a rien à y faire).
            var uadeDurationsDbPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Database", "uade_durations.db");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(uadeDurationsDbPath)!);
            TrackerPlayer.Core.Players.UadeDurationCache.Override(uadeDurationsDbPath);
        }
        catch { /* Thème/langue par défaut si config absente */ }

        // ── Étape 2c : configurer les chemins Externals ─────────────────────
        ConfigureExternalPaths();

        // ── Étape 2d : vérification et mise à jour des configs émulateurs ────
        // Silencieux — ne bloque pas l'application si Mega est indisponible
        // Ne s'exécute pas si le wizard n'a pas encore été complété
        _ = Task.Run(async () =>
        {
            try
            {
                var prefsSvc  = _host.Services.GetRequiredService<DemoBase.Data.PreferencesService>();

                // 2026-08-01, demande utilisateur ("système de mise à jour automatique de
                // l'application ... répertoire 'Updates'") : finalise une mise à jour de
                // l'appli elle-même appliquée par le script PowerShell généré au lancement
                // précédent (cf. AppUpdateService, commentaire de classe) — lit et consomme
                // le marqueur "update_applied.txt" s'il est présent. Volontairement placé
                // AVANT le "return" sur WizardCompleted juste en dessous : un marqueur en
                // attente doit toujours être finalisé, même si le wizard n'est pas terminé.
                var appUpdateSvcFinalize = _host.Services.GetRequiredService<DemoBase.App.Services.AppUpdateService>();
                await appUpdateSvcFinalize.FinalizePendingUpdateAsync();

                var appPrefs  = await prefsSvc.LoadAllAsync();
                if (!appPrefs.WizardCompleted) return;

                var updateSvc = _host.Services.GetRequiredService<DemoBase.App.Services.ConfigsUpdateService>();
                await updateSvc.CheckAndUpdateAsync();

                // 2026-07-27, demande utilisateur : même vérification pour le catalogue
                // DATs (dats_version.txt sur Mega) — même protocole texte de versioning
                // que les configs émulateurs ci-dessus, silencieux, non bloquant. Voir
                // DatsUpdateService pour le détail (réutilise DatImportService.ImportAsync
                // tel quel, qui gère déjà le "supprimer + réimporter" par fichier DAT).
                var datsUpdateSvc = _host.Services.GetRequiredService<DemoBase.App.Services.DatsUpdateService>();
                bool datsUpdated = await datsUpdateSvc.CheckAndUpdateAsync();

                // 2026-07-31, retour utilisateur ("l'import des fichiers DATs a doublé les
                // entrées ROMs !! il fallait faire un annule et remplace. pas un ajout") :
                // ImportAsync gère désormais ce nettoyage lui-même (DatImportService.cs,
                // RemoveOrphanEntriesAsync + RemoveExactDuplicateEntriesAsync), mais cette
                // logique ne tourne qu'À L'INTÉRIEUR d'un import — donc seulement si
                // datsUpdated ci-dessus est vrai (nouvelle version détectée sur Mega), ce qui
                // peut ne pas se reproduire avant longtemps. Appel INCONDITIONNEL ici (coût
                // négligeable — une seule requête GROUP BY/HAVING, suppressions seulement si
                // des doublons existent réellement) pour réparer IMMÉDIATEMENT, dès le
                // prochain lancement après ce correctif, une base déjà polluée par ce bug —
                // sans attendre un futur téléchargement DAT.
                try
                {
                    var datImportSvcRepair = _host.Services.GetRequiredService<DemoBase.Data.DatImportService>();
                    int removed = await datImportSvcRepair.RemoveExactDuplicateEntriesAsync();
                    if (removed > 0)
                        PerfLogger.Mark($"DATS: {removed} DatEntry(ies) en double supprimée(s) au démarrage (réparation)");
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[DATS] Erreur réparation doublons (non bloquante) : {ex.Message}");
                }

                // 2026-07-27, demande utilisateur (suite) : "il faudra aussi lors d'une mise
                // à jour de dat déclencher après ou avant la mise à jour de la bdd demozoo"
                // — les DatEntry fraîchement importés référencent des DemozooId qui peuvent
                // ne pas encore exister dans une base Demozoo locale restée sur un ancien
                // dump. Choix utilisateur (clarifié) : "automatique mais visible" — ouvre la
                // même fenêtre de progression que le bouton sidebar (MainViewModel.
                // RunDemozooImportAsync), juste déclenchée automatiquement au lieu d'un clic.
                // Uniquement si les DATs ont VRAIMENT changé (pas à chaque démarrage) et si
                // une mise à jour Demozoo est réellement disponible (re-vérifiée à cet
                // instant, pas réutilisée depuis l'Étape 3 plus bas pour éviter toute
                // dépendance d'ordre entre les deux blocs).
                if (datsUpdated)
                {
                    using var demozooScope = _host.Services.CreateScope();
                    var demozooVersionSvc  = demozooScope.ServiceProvider.GetRequiredService<DemozooVersionService>();
                    var freshVersionInfo   = await demozooVersionSvc.CheckAsync();
                    if (freshVersionInfo.HasUpdate && !freshVersionInfo.IsFirstImport)
                    {
                        PerfLogger.Mark("DATS: mise à jour appliquée + BDD Demozoo obsolète — déclenchement automatique de l'import Demozoo");
                        // Doit s'exécuter sur le thread UI (création/affichage d'une Window WPF).
                        _ = Current.Dispatcher.InvokeAsync(async () =>
                        {
                            var mainVm = _host.Services.GetRequiredService<MainViewModel>();
                            await mainVm.RunDemozooImportAsync(dbPath);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CONFIG] Erreur vérification mise à jour : {ex.Message}");
            }
        });

        // ── Étape 2e : vérification de mise à jour de l'APPLICATION elle-même ──
        // 2026-08-01, demande utilisateur ("système de mise à jour automatique de
        // l'application en utilisant mon compte mega et en allant regarder dans un
        // répertoire 'Updates'") : bloc volontairement séparé du Task.Run Configs/DATs
        // ci-dessus (pas imbriqué dedans) — pour ne jamais risquer de fermer
        // l'application (cas "Oui" ci-dessous) pendant qu'un import Demozoo
        // potentiellement déclenché par ce même bloc est encore en cours. Choix
        // utilisateur explicite : vérification silencieuse, mais confirmation
        // obligatoire avant d'appliquer quoi que ce soit (pas de mise à jour
        // automatique et silencieuse).
        _ = Task.Run(async () =>
        {
            try
            {
                var prefsSvc2 = _host.Services.GetRequiredService<DemoBase.Data.PreferencesService>();
                var appPrefs2 = await prefsSvc2.LoadAllAsync();
                if (!appPrefs2.WizardCompleted) return;

                var appUpdateSvc = _host.Services.GetRequiredService<DemoBase.App.Services.AppUpdateService>();
                var updateInfo   = await appUpdateSvc.CheckForUpdateAsync();
                if (updateInfo == null) return;

                PerfLogger.Mark(
                    $"APPUPDATE: mise à jour disponible ({updateInfo.LocalVersion} → {updateInfo.RemoteVersion}) — confirmation demandée");

                // Doit s'exécuter sur le thread UI (MessageBox + Shutdown).
                await Current.Dispatcher.InvokeAsync(async () =>
                {
                    var choice = MessageBox.Show(
                        $"Une nouvelle version de DemoBase est disponible ({updateInfo.RemoteVersion}).\n\n" +
                        "Mettre à jour maintenant ? L'application va se fermer, se mettre à jour, " +
                        "puis redémarrer automatiquement.",
                        "Mise à jour disponible",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (choice != MessageBoxResult.Yes) return;

                    var (success, error) = await appUpdateSvc.DownloadAndApplyAsync(updateInfo.RemoteVersion);
                    if (!success)
                    {
                        PerfLogger.Mark($"APPUPDATE: échec de la mise à jour — {error}");
                        MessageBox.Show(
                            $"La mise à jour a échoué :\n{error}\n\nL'application continue avec la version actuelle.",
                            "Échec de la mise à jour",
                            MessageBoxButton.OK,
                            MessageBoxImage.Warning);
                        return;
                    }

                    Current.Shutdown();
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[APPUPDATE] Erreur vérification mise à jour appli : {ex.Message}");
            }
        });

        // ── Étape 3 : vérifier la version du dump Demozoo ────────────────────
        using var scope    = _host.Services.CreateScope();
        var versionService = scope.ServiceProvider.GetRequiredService<DemozooVersionService>();
        var versionInfo    = await versionService.CheckAsync();

        var datsDir2    = System.IO.Path.Combine(AppContext.BaseDirectory, "DATS");
        var datService2 = new DemoBase.Data.DatImportService(connStr);
        // NeedsImportAsync compare la <version> de chaque fichier DATS/*.xml à celle
        // déjà importée en base — détecte donc aussi bien le premier lancement (base
        // vide) qu'un simple remplacement/mise à jour d'un fichier .xml existant.
        // IsFirstRunAsync ne couvrait que le premier cas (base vide), donc un fichier
        // mis à jour après un premier import n'était jamais redétecté au démarrage.
        bool firstDat   = System.IO.Directory.Exists(datsDir2) && await datService2.NeedsImportAsync();

        // ── Étape 4 : splash ─────────────────────────────────────────────────
        // Plus d'import bloquant au démarrage (le wizard s'en charge) → splash toujours visible.
        bool nothingToDo = !firstDat; // on garde la vérification DAT uniquement

        SplashWindow? splash = null;
        if (nothingToDo)
        {
            splash = new SplashWindow();
            splash.Show();
            await Task.Delay(50);
        }

        // ── Étape 5 : ouvrir la MainWindow (toujours avant tout MessageBox) ──
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        Current.MainWindow = mainWindow;
        mainWindow.Show();

        if (splash != null)
            _ = Task.Delay(3000).ContinueWith(_ =>
                Dispatcher.Invoke(() => { if (splash.IsVisible) splash.Close(); }));

        // ── Étape 6 : wizard de configuration tant qu'il n'est pas terminé ──
        // L'import initial sera déclenché par le wizard de configuration,
        // pas automatiquement au premier lancement.
        //
        // Le déclencheur est prefs.WizardCompleted, PAS versionInfo.IsFirstImport :
        // IsFirstImport devient définitivement false dès que l'étape "Base de
        // données" a réussi une fois, même si l'utilisateur a fermé le wizard
        // avant d'avoir fini les étapes suivantes (émulateurs, outils externes,
        // DATS) — dans ce cas IsFirstImport ne redéclencherait plus jamais le
        // wizard, ce qui laissait la configuration bloquée à mi-chemin.
        var prefs      = scope.ServiceProvider.GetRequiredService<DemoBase.Data.PreferencesService>();
        var wizardPrefs = await prefs.LoadAllAsync();

        if (!wizardPrefs.WizardCompleted)
        {
            // Thème sombre et anglais par défaut au tout premier lancement
            // uniquement (avant que la base Demozoo n'ait jamais été importée) —
            // écrits explicitement dans la table Preferences pour que celle-ci
            // reflète réellement le choix. Sur une réouverture ultérieure du
            // wizard (IsFirstImport déjà passé à false), on ne réécrase plus le
            // thème/langue choisis entre-temps par l'utilisateur.
            if (versionInfo.IsFirstImport)
            {
                await prefs.SetAsync(DemoBase.Data.PrefKeys.Theme,    "Dark");
                await prefs.SetAsync(DemoBase.Data.PrefKeys.Language, "en");
            }

            var importSvc    = scope.ServiceProvider.GetRequiredService<DemoBase.Import.DemozooImportService>();
            var emulatorVm   = scope.ServiceProvider.GetRequiredService<DemoBase.App.ViewModels.EmulatorInstallerViewModel>();
            var megaSvc      = scope.ServiceProvider.GetRequiredService<DemoBase.App.Services.DbSetupDownloadService>();
            var datImportSvc = scope.ServiceProvider.GetRequiredService<DemoBase.Data.DatImportService>();
            var seedSvc      = scope.ServiceProvider.GetRequiredService<DemoBase.App.Services.EmulatorSeedService>();
            var installerSvc = scope.ServiceProvider.GetRequiredService<DemoBase.App.Services.EmulatorInstallerService>();
            var exportSvc    = scope.ServiceProvider.GetRequiredService<DemoBase.App.Services.EmulatorConfigExportService>();
            var locSvc       = scope.ServiceProvider.GetRequiredService<DemoBase.App.Services.LocalizationService>();
            // 2026-07-25 : manquait au wizard (résolu ici puis threadé jusqu'à ReadyPage,
            // cf. ReadyPage.xaml.cs) — sans ça, ConfigsUpdateService y était construit
            // avec son 4e paramètre (ReleaseProfileOverrideExportService) resté au défaut
            // null, donc le JSON release_profile_overrides n'était JAMAIS importé lors
            // d'une première installation (uniquement lors des vérifications en tâche de
            // fond après le wizard, cf. Étape 2d ci-dessus).
            var profileOverridesSvc = scope.ServiceProvider
                .GetRequiredService<DemoBase.Data.ReleaseProfileOverrideExportService>();
            var wizard       = new SetupWizardWindow(prefs, importSvc, emulatorVm, megaSvc, datImportSvc, seedSvc, installerSvc, locSvc, exportSvc, profileOverridesSvc);
            wizard.Owner = mainWindow;
            wizard.ShowDialog();
        }
        else if (versionInfo.HasUpdate && !versionInfo.IsFirstImport)
        {
            // Pas de popup au démarrage : on notifie silencieusement le MainViewModel.
            // L'utilisateur verra un bouton dans la sidebar et choisira de mettre à jour quand il le souhaite.
            var mainVm = _host.Services.GetRequiredService<MainViewModel>();
            mainVm.NotifyDemozooUpdate(versionInfo, dbPath);
        }

        // ── Seed émulateurs : toujours à chaque démarrage ───────────────────
        // Crée silencieusement les nouveaux émulateurs ajoutés depuis l'installation initiale.
        var hostRef = _host;
        _ = Task.Run(async () =>
        {
            try
            {
                using var seedScope = hostRef.Services.CreateScope();
                var seedSvc2 = seedScope.ServiceProvider.GetRequiredService<DemoBase.App.Services.EmulatorSeedService>();
                await seedSvc2.SeedAllAsync(CancellationToken.None);
            }
            catch { /* seed silencieux — pas bloquant */ }
        });

        // ── Étape 7 : import DAT si tables vides ─────────────────────────────
        if (firstDat)
        {
            var importWin = new DatImportWindow(connStr);
            importWin.Owner = mainWindow;
            importWin.Show();
            await importWin.RunImportAsync();
        }
    }

    internal static async Task RunInitialImportAsync(IServiceProvider services, ImportProgressWindow progressWindow)
    {
        progressWindow.Show();

        var importService = services.GetRequiredService<DemozooImportService>();
        var cts           = new CancellationTokenSource();

        // InvokeAsync (non bloquant) pour que la ProgressBar reste animée
        var progress = new Progress<DemozooImportProgress>(p =>
            progressWindow.Dispatcher.InvokeAsync(() => progressWindow.Report(p),
                System.Windows.Threading.DispatcherPriority.Render));

        try
        {
            // Task.Run : l'import tourne sur un thread pool, pas sur le thread UI.
            // Sans ça, les continuations après chaque await reprennent sur le thread UI
            // et bloquent le dispatcher — la fenêtre reste figée.
            importService.SetLanguage(DemoBase.App.Services.LocalizationService.CurrentLanguageStatic);
            await Task.Run(() => importService.ImportAsync(progress, cts.Token));
            await Task.Delay(1200);
        }
        catch (OperationCanceledException)
        {
            Application.Current.Shutdown();
            return;
        }
        catch (Exception ex)
        {
            progressWindow.ReportError(ex.Message);
            await Task.Delay(4000);
        }
        finally
        {
            progressWindow.Close();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // On ne passe PAS par Environment.Exit(0) car il exécute les finalizers
        // du CLR avant de terminer le process — notamment les finalizers internes
        // de CommunityToolkit.Mvvm (WeakReferenceMessenger, ObservableObject…)
        // qui peuvent bloquer plusieurs secondes et maintenir les DLL verrouillées.
        //
        // Process.Kill() = TerminateProcess() Win32 : terminaison immédiate, aucun
        // finalizer, aucun handler AppDomain.ProcessExit. Les données sont sûres :
        //   - SQLite en mode WAL est crash-safe par design (auto-récupération)
        //   - Les préférences sont sauvegardées à chaque modification, pas à la sortie
        //   - Les ressources OS (fichiers, sockets, handles) sont libérées par le noyau
        //
        // Le host est stoppé en background (500 ms max) pour permettre aux
        // IHostedService de se terminer proprement avant le kill.

        var host = _host;
        _host = null;

        _ = Task.Run(async () =>
        {
            if (host != null)
            {
                try
                {
                    await host.StopAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                    host.Dispose();
                }
                catch { }
            }
            try { TrackerPlayer.Core.Players.ExternalProcessRegistry.KillAll(); } catch { }
            try { System.Diagnostics.Process.GetCurrentProcess().Kill(); } catch { }
        });

        try { base.OnExit(e); } catch { }
    }

    private static void ConfigureExternalPaths()
    {
        var externalsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Externals");

        // Ajouter Externals au PATH pour que DllImport trouve libopenmpt.dll
        if (System.IO.Directory.Exists(externalsDir))
        {
            var currentPath = Environment.GetEnvironmentVariable("PATH") ?? "";
            if (!currentPath.Contains(externalsDir, StringComparison.OrdinalIgnoreCase))
                Environment.SetEnvironmentVariable("PATH", externalsDir + ";" + currentPath);

            var libopenmpt = System.IO.Path.Combine(externalsDir, "libopenmpt.dll");
            if (System.IO.File.Exists(libopenmpt))
            {
                try { System.Runtime.InteropServices.NativeLibrary.Load(libopenmpt); }
                catch { /* déjà chargée ou incompatible */ }
            }
        }

        // 2026-07-31, retour utilisateur : "openmpt convertit les fichiers selon certains
        // critères (ex: chipytracker, futur composer etc...). visiblement la librairie
        // openmpt ne gère pas cette conversion [...] peux tu vérifier que c'est bien le
        // cas ?" — vérifié côté changelog officiel libopenmpt : ChipTracker/Future Composer
        // (et plusieurs autres formats exotiques) ne sont lisibles par libopenmpt qu'à
        // partir de la version 0.8.0 (31 mai 2025).
        //
        // 2026-07-31 (correctif) : PREMIÈRE version de ce diagnostic imbriquée à tort dans
        // le `if (File.Exists(Externals/libopenmpt.dll))` ci-dessus — sortie sans AUCUNE
        // ligne dans perf_log.txt de l'utilisateur, preuve que son libopenmpt.dll n'est PAS
        // dans Externals/ mais directement à côté de l'exe (AppContext.BaseDirectory,
        // résolution DllImport standard de Windows — cf. le commentaire de
        // NativeTrackerPlayer.cs : "placer openmpt.dll dans le répertoire de l'application",
        // pas spécifiquement Externals/). Sorti de cette condition : le P/Invoke réussit tant
        // que le DLL est trouvable N'IMPORTE OÙ dans la résolution standard (répertoire de
        // l'exe, PATH incluant Externals/ ajouté juste au-dessus, System32...), donc ce
        // diagnostic ne doit dépendre d'AUCUN chemin de fichier deviné.
        try
        {
            var openmptVersion = TrackerPlayer.Core.Players.NativeTrackerPlayer.LibopenmptVersion;
            PerfLogger.Mark($"libopenmpt.dll chargée — version {openmptVersion}");
            if (TrackerPlayer.Core.Players.NativeTrackerPlayer.LibopenmptIsBefore_0_8_0)
                PerfLogger.Mark(
                    $"libopenmpt {openmptVersion} < 0.8.0 : ChipTracker, Future " +
                    "Composer, PumaTracker, Face The Music, Game Music Creator, " +
                    "TCB Tracker, Real Tracker 2, Images Music System et Chuck " +
                    "Biscuits/Black Artist ne peuvent PAS être lus par cette " +
                    "version — mettre à jour libopenmpt.dll (lib.openmpt.org) " +
                    "pour les supporter.");
        }
        catch (Exception ex)
        {
            // Diagnostic seul, jamais bloquant — mais loggué quand même (contrairement à
            // avant) pour distinguer "libopenmpt introuvable du tout" de "version illisible".
            PerfLogger.Mark($"libopenmpt.dll : version indéterminée — {ex.GetType().Name}: {ex.Message}");
        }

        // zxtune.dll — 2026-08-06 : remplace zxtune123.exe (process externe +
        // génération de fichier WAV temporaire) par un pont natif P/Invoke
        // (cf. ZXTunePlayer/ZxTuneNative.cs dans TrackerPlayer.Core). Plus de
        // téléchargement Externals pour ZXTune (retiré du catalogue,
        // EmulatorDownloadCatalog.cs) : même schéma de résolution que
        // libopenmpt.dll ci-dessus — répertoire de l'application en priorité
        // (résolution DllImport standard), Externals/zxtune.dll en repli
        // (Externals déjà ajouté au PATH plus haut).
        var zxtuneDll = System.IO.Path.Combine(externalsDir, "zxtune.dll");
        if (System.IO.File.Exists(zxtuneDll))
        {
            try { System.Runtime.InteropServices.NativeLibrary.Load(zxtuneDll); }
            catch { /* déjà chargée ou incompatible */ }
        }
        try
        {
            if (TrackerPlayer.Core.Players.ZXTunePlayer.IsAvailable)
                PerfLogger.Mark("zxtune.dll chargée (pont natif ZXTune)");
            else
                PerfLogger.Mark(
                    "zxtune.dll introuvable — formats ZXTune (Amiga/ZX/C64/Atari…) indisponibles, " +
                    "repli libopenmpt");
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"zxtune.dll : diagnostic impossible — {ex.GetType().Name}: {ex.Message}");
        }

        // libuade.dll + uadecore.exe — 2026-08-06 : remplace uade123.exe (process
        // externe streaming stdout brut) par un pont natif P/Invoke (cf.
        // UadePlayer/UadeNative.cs dans TrackerPlayer.Core). Contrairement à
        // zxtune.dll, ce pont ne supprime pas le besoin d'un exécutable externe —
        // l'architecture d'UADE isole toujours l'émulation 68k dans un process
        // séparé (uadecore.exe, spawné par libuade.dll elle-même via l'option
        // UC_UADECORE_FILE) — mais élimine le parsing de texte sur stdout/stderr et
        // les copies de fichiers compagnons avec renommage GUID (remplacées par un
        // simple changement de répertoire courant côté UadePlayer).
        //
        // 2026-08-06, retour utilisateur : disposition confirmée — libuade.dll et
        // uadecore.exe DIRECTEMENT dans Externals/ (pas dans un sous-dossier
        // Externals/UADE/), et les ressources UADE (eagleplayer.conf/uaerc/score/
        // players/) dans Externals/basedir/ (UC_BASE_DIR). Plus dans le catalogue de
        // téléchargement (EmulatorDownloadCatalog.cs — retiré, cf. RESUME_PROJET.md),
        // l'utilisateur dépose lui-même ces fichiers à ces emplacements.
        var libuadeDll = System.IO.Path.Combine(externalsDir, "libuade.dll");
        if (System.IO.File.Exists(libuadeDll))
        {
            try { System.Runtime.InteropServices.NativeLibrary.Load(libuadeDll); }
            catch { /* déjà chargée ou incompatible */ }
        }
        var uadecorePath = System.IO.Path.Combine(externalsDir, "uadecore.exe");
        if (System.IO.File.Exists(uadecorePath))
            TrackerPlayer.Core.Players.UadePlayer.UadecoreExePath = uadecorePath;
        var uadeBaseDir = System.IO.Path.Combine(externalsDir, "basedir");
        if (System.IO.Directory.Exists(uadeBaseDir))
            TrackerPlayer.Core.Players.UadePlayer.BaseDirOverride = uadeBaseDir;
        try
        {
            if (TrackerPlayer.Core.Players.UadePlayer.IsAvailable)
                PerfLogger.Mark("libuade.dll chargée (pont natif UADE)");
            else
                PerfLogger.Mark(
                    "libuade.dll/uadecore.exe introuvables — formats UADE (Amiga exotiques) indisponibles");
        }
        catch (Exception ex)
        {
            PerfLogger.Mark($"libuade.dll : diagnostic impossible — {ex.GetType().Name}: {ex.Message}");
        }

        // javaw.exe — JRE dédié DemoBase (Eclipse Temurin 21), extrait dans
        // Externals/JRE/bin/. Prioritaire sur tout Java système dans JavaLauncher
        // (cf. commentaire en tête de ce fichier) — pas besoin d'installer/mettre
        // à jour de Java sur le poste pour lancer des démos .jar.
        var jrePath = System.IO.Path.Combine(externalsDir, "JRE", "bin", "javaw.exe");
        if (System.IO.File.Exists(jrePath))
            DemoBase.App.Services.JavaLauncher.BundledJavaPath = jrePath;
    }
}
