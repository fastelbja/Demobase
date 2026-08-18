using DemoBase.Core.Diagnostics;
using System.Diagnostics;
using DemoBase.App.Services;
using DemoBase.App.ViewModels;
using DemoBase.App.ViewModels.Releases;
using System.Windows;

namespace DemoBase.App;

public partial class MainWindow : Window
{
    private readonly ThemeService _themeService;
    private DemoBase.App.ViewModels.MediaBrowserViewModel? _currentMediaBrowserVm;

    public MainWindow(MainViewModel viewModel, ThemeService themeService,
                      DemoBase.Data.PreferencesService prefsService,
                      GlobalKeyboardService keyboardService)
    {
        InitializeComponent();
        _themeService = themeService;
        DataContext   = viewModel;

        // Attacher le service de raccourcis clavier global
        Loaded += (_, _) =>
        {
            keyboardService.Attach(this);
            // Enregistrer les vues qui coexistent dans le visual tree —
            // plusieurs peuvent être IsVisible=True simultanément (ZIndex).
            keyboardService.RegisterView<DemoBase.App.ViewModels.Library.GroupListViewModel>(
                () => GroupListViewInst);
            keyboardService.RegisterView<DemoBase.App.ViewModels.Library.ScenerListViewModel>(
                () => ScenerListViewInst);
            keyboardService.RegisterView<DemoBase.App.ViewModels.Library.PartyListViewModel>(
                () => PartyListViewInst);
            keyboardService.RegisterView<ReleaseListViewModel>(
                () => FindName("ReleaseListViewInst") as FrameworkElement
                   ?? FindDescendantByType<DemoBase.App.Views.Releases.ReleaseListView>(this));
            keyboardService.RegisterView<PartyDetailViewModel>(
                () => PartyDetailViewInst);
            keyboardService.RegisterView<ReleaserDetailViewModel>(
                () => ReleaserDetailViewInst);
        };

        // Assigner les DataContext des vues Singleton de la zone centrale
        GroupListViewInst.DataContext       = viewModel.GroupVm;
        ScenerListViewInst.DataContext      = viewModel.ScenerVm;
        PartyListViewInst.DataContext       = viewModel.PartyVm;
        ReleaserDetailViewInst.DataContext  = viewModel.ReleaserDetailVm;
        PartyDetailViewInst.DataContext     = viewModel.PartyDetailVm;

        // La colonne droite : fiche Release toujours visible, DataContext = ReleaseDetailVm singleton
        ReleaseDetailViewInst.DataContext = viewModel.ReleaseDetailVm;

        // Écouter les changements de CurrentViewModel pour la zone centrale
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.CurrentViewModel)) return;
            var vm = viewModel.CurrentViewModel;
            var vmName = vm?.GetType().Name ?? "null";
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] MainWindow→{vmName}");
            PerfLogger.Separator();
            PerfLogger.Mark($"Navigate → {vmName}");

            bool isGroup       = vm is DemoBase.App.ViewModels.Library.GroupListViewModel;
            bool isScener      = vm is DemoBase.App.ViewModels.Library.ScenerListViewModel;
            bool isParty       = vm is DemoBase.App.ViewModels.Library.PartyListViewModel;
            bool isReleaser    = vm is ReleaserDetailViewModel;
            bool isPartyDetail = vm is PartyDetailViewModel;
            bool isFavSoundtracks  = vm is DemoBase.App.ViewModels.FavoriteSoundtracksViewModel;
            bool isSingleton   = isGroup || isScener || isParty || isReleaser || isPartyDetail;

            // 2026-07-30, demande utilisateur : quand l'oscilloscope/pattern player
            // Modland affiche une piste en cours de lecture, la fenêtre doit recouvrir
            // la colonne de détail de release — MediaBrowserViewModel expose
            // WantsFullWidth pour ça (cf. son commentaire). Un nouveau
            // MediaBrowserViewModel étant créé à chaque navigation (AddTransient), on
            // se désabonne de l'ancien avant de s'abonner au nouveau pour ne pas
            // accumuler les handlers d'instances obsolètes.
            if (_currentMediaBrowserVm != null)
                _currentMediaBrowserVm.PropertyChanged -= OnMediaBrowserVmPropertyChanged;
            _currentMediaBrowserVm = vm as DemoBase.App.ViewModels.MediaBrowserViewModel;
            if (_currentMediaBrowserVm != null)
                _currentMediaBrowserVm.PropertyChanged += OnMediaBrowserVmPropertyChanged;

            // Colonne droite = fiche Release toujours visible — sauf pour Musiques
            // Favorites, où elle n'a aucun sens (pas de "release courante" à
            // afficher pendant la lecture d'un soundtrack favori) : la colonne
            // centrale y prend tout l'espace disponible. Idem pour Médiathèque
            // pendant la lecture active d'une piste Modland (cf. WantsFullWidth).
            // MediaBrowser (Médiathèque) GARDE sinon la colonne droite visible :
            // c'est là que l'utilisateur consulte/ajoute une release à ses favoris
            // (bouton ★ Favori) en parcourant le catalogue — la masquer rendait
            // cette action impossible depuis cet écran.
            bool isFullWidth = isFavSoundtracks || (_currentMediaBrowserVm?.WantsFullWidth ?? false);
            ApplyColumnLayout(isFullWidth);

            System.Windows.Controls.Panel.SetZIndex(GroupListViewInst,      isGroup       ? 2 : 0);
            System.Windows.Controls.Panel.SetZIndex(ScenerListViewInst,     isScener      ? 2 : 0);
            System.Windows.Controls.Panel.SetZIndex(PartyListViewInst,      isParty       ? 2 : 0);
            System.Windows.Controls.Panel.SetZIndex(ReleaserDetailViewInst, isReleaser    ? 2 : 0);
            System.Windows.Controls.Panel.SetZIndex(PartyDetailViewInst,    isPartyDetail ? 2 : 0);

            GroupListViewInst.IsHitTestVisible      = isGroup;
            ScenerListViewInst.IsHitTestVisible     = isScener;
            PartyListViewInst.IsHitTestVisible      = isParty;
            ReleaserDetailViewInst.IsHitTestVisible = isReleaser;
            PartyDetailViewInst.IsHitTestVisible    = isPartyDetail;

            // Focus clavier
            if      (isGroup)       GroupListViewInst.Focus();
            else if (isScener)      ScenerListViewInst.Focus();
            else if (isParty)       PartyListViewInst.Focus();
            else if (isReleaser)    ReleaserDetailViewInst.Focus();
            else if (isPartyDetail) PartyDetailViewInst.Focus();
            else                    MainContent.Focus();

            MainContent.Content          = isSingleton ? null : vm;
            System.Windows.Controls.Panel.SetZIndex(MainContent, isSingleton ? 0 : 1);
            MainContent.IsHitTestVisible = !isSingleton;
        };

        // Navigation initiale vers les releases
        viewModel.NavigateToReleasesCommand.Execute(null);

        // Appliquer la préférence effets démo
        _prefsService = prefsService;
        _ = ApplyDemoEffectsPrefAsync(prefsService);
    }

    private async Task ApplyDemoEffectsPrefAsync(DemoBase.Data.PreferencesService prefsService)
    {
        try
        {
            var prefs = await prefsService.LoadAllAsync();
            if (prefs.DemoEffects)
            {
                _demoEffectsOn        = true;
                DemoEffect.Visibility = Visibility.Visible;
                BtnDemoToggle.Content = DemoBase.App.Services.LocalizationService.Get("Nav_DemoEffectsOn");
            }
        }
        catch { /* pas critique */ }
    }

    /// <summary>Réagit aux changements de WantsFullWidth sur le MediaBrowserViewModel
    /// actuellement affiché (lecture Modland démarrée/arrêtée) sans repasser par toute
    /// la logique de routage de CurrentViewModel — seule la largeur des colonnes change.</summary>
    private void OnMediaBrowserVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DemoBase.App.ViewModels.MediaBrowserViewModel.WantsFullWidth)) return;
        var wantsFullWidth = _currentMediaBrowserVm?.WantsFullWidth ?? false;
        ApplyColumnLayout(wantsFullWidth);
    }

    /// <summary>Bascule la colonne droite (fiche Release) entre visible (50/50) et
    /// masquée (la zone centrale prend toute la largeur) — cf. RESUME_PROJET.md pour
    /// les deux cas d'usage (Musiques Favorites, lecture active d'une piste Modland).</summary>
    private void ApplyColumnLayout(bool isFullWidth)
    {
        CenterColumn.Width   = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        RightColumn.Width    = isFullWidth
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        // MinWidth="200" déclaré en XAML sur RightColumn empêche à lui seul la
        // largeur de retomber à 0 (le Grid respecte le plancher MinWidth même
        // avec Width=0) — sans cette ligne, un fin bandeau de la fiche Release
        // (~200px) restait visible sur la page "plein écran" au lieu de disparaître
        // complètement.
        RightColumn.MinWidth = isFullWidth ? 0 : 200;
        RightSeparator.Width = isFullWidth
            ? new System.Windows.GridLength(0)
            : new System.Windows.GridLength(1);
    }

    private void BtnAbout_Click(object sender, RoutedEventArgs e)
    {
        var mainVm = DataContext as DemoBase.App.ViewModels.MainViewModel;
        new DemoBase.App.Views.AboutDialog(mainVm?.DbVersionLabel).ShowDialog();
    }

    private void BtnTheme_Click(object sender, RoutedEventArgs e) =>
        _themeService.Toggle();

    private bool _demoEffectsOn = false;
    private DemoBase.Data.PreferencesService? _prefsService;

    private void BtnDemoToggle_Click(object sender, RoutedEventArgs e)
    {
        _demoEffectsOn = !_demoEffectsOn;
        DemoEffect.Visibility = _demoEffectsOn ? Visibility.Visible : Visibility.Collapsed;
        BtnDemoToggle.Content = _demoEffectsOn
            ? DemoBase.App.Services.LocalizationService.Get("Nav_DemoEffectsOn")
            : DemoBase.App.Services.LocalizationService.Get("Nav_DemoEffects");
        if (_prefsService != null)
            _ = SaveDemoEffectsPrefAsync(_prefsService, _demoEffectsOn);
    }

    private static async Task SaveDemoEffectsPrefAsync(
        DemoBase.Data.PreferencesService prefs, bool value)
    {
        try
        {
            var p = await prefs.LoadAllAsync();
            p.DemoEffects = value;
            await prefs.SaveAllAsync(p);
        }
        catch { }
    }
    private static T? FindDescendantByType<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            var found = FindDescendantByType<T>(child);
            if (found != null) return found;
        }
        return null;
    }

}
