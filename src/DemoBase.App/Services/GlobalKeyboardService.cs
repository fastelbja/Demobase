using CommunityToolkit.Mvvm.Input;
using DemoBase.App.ViewModels;
using DemoBase.App.ViewModels.Emulators;
using DemoBase.App.ViewModels.Releases;
using DemoBase.Core.Interfaces;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoBase.App.Services;

/// <summary>
/// Centralise tous les raccourcis clavier de l'application.
/// S'attache à la MainWindow et intercepte les touches globales
/// avant qu'elles atteignent les contrôles enfants.
///
/// Raccourcis implémentés :
///   Alt+1..8    → navigation sidebar (Releases, Favoris, Soundtracks,
///                 Graphics, Groups, Artists, Platforms, Parties)
///   Alt+E       → Émulateurs
///   Alt+P       → Préférences
///   Alt+←  /  Backspace → GoBack (historique de navigation)
///   Ctrl+F / /  → focus SearchBox (liste releases)
///   F5          → lancer la release sélectionnée / affichée
///   Entrée      → ouvrir la fiche de la release sélectionnée
///   Échap       → retour (GoBack)
///   1..5        → onglets de la fiche détail (Info/Crédits/UsedIn/Média/Files)
///   F           → toggle favori (fiche détail)
///   Espace      → play/pause (musique ou vidéo chargée dans la fiche détail),
///                 sauf si le focus est sur un bouton/case/item de liste (2026-07-29)
///   ↑ / ↓       → couvre aussi MediaBrowser (onglet Music) en plus des listes
///                 Releases/Releaser/Party déjà gérées (2026-07-29)
/// </summary>
public class GlobalKeyboardService
{
    private readonly MainViewModel _mainVm;
    private readonly INavigationService _navigation;
    private Window? _window;

    // Vues nommées dont plusieurs coexistent dans le visual tree avec des ZIndex différents.
    // On les enregistre explicitement pour cibler la SearchBox de la vue ACTIVE.
    private readonly Dictionary<Type, Func<FrameworkElement?>> _activeViewResolvers = new();

    public GlobalKeyboardService(MainViewModel mainVm, INavigationService navigation)
    {
        _mainVm     = mainVm;
        _navigation = navigation;
    }

    /// <summary>
    /// Enregistre une résolution de vue pour un type de ViewModel.
    /// Quand ce VM est actif, FocusSearchBox cherchera la SearchBox dans cette vue.
    /// </summary>
    public void RegisterView<TViewModel>(Func<FrameworkElement?> viewResolver)
        => _activeViewResolvers[typeof(TViewModel)] = viewResolver;

    /// <summary>Attache le service à la fenêtre principale.</summary>
    public void Attach(Window window)
    {
        _window = window;
        window.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Normaliser les touches du pavé numérique → touches numériques standard.
        // Key.NumPad1..9 → Key.D1..9, Key.NumPad0 → Key.D0.
        var key       = NormalizeNumPad(e.Key);
        var systemKey = NormalizeNumPad(e.SystemKey);

        var ctrl  = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt   = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var none  = Keyboard.Modifiers == ModifierKeys.None;

        // ── Flèches haut/bas : navigation dans la liste active ───────────────
        if (none && (e.Key == Key.Up || e.Key == Key.Down) && !IsTextInputFocused())
        {
            if (NavigateActiveList(e.Key == Key.Down ? 1 : -1))
            {
                e.Handled = true;
                return;
            }
        }

        // ── Alt+1..8 : navigation sidebar ────────────────────────────────────
        if (alt && !ctrl && !shift)
        {
            var handled = systemKey switch
            {
                // ── Sections principales ──────────────────────────────────────
                Key.D1 => Execute(_mainVm.NavigateToReleasesCommand),
                Key.D2 => Execute(_mainVm.NavigateToGroupsCommand),
                Key.D3 => Execute(_mainVm.NavigateToArtstsCommand),
                Key.D4 => Execute(_mainVm.NavigateToPlatformsCommand),
                Key.D5 => Execute(_mainVm.NavigateToPartiesCommand),
                // ── Favoris ───────────────────────────────────────────────────
                Key.D6 => Execute(_mainVm.NavigateToFavoritesCommand),
                Key.D7 => Execute(_mainVm.NavigateToFavSoundtracksCommand),
                Key.D8 => Execute(_mainVm.NavigateToFavGraphicsCommand),
                // ── Gestion ───────────────────────────────────────────────────
                Key.E  => Execute(_mainVm.NavigateToEmulatorsCommand),
                Key.M  => Execute(_mainVm.NavigateToMediaBrowserCommand),
                Key.P  => Execute(_mainVm.NavigateToPreferencesCommand),
                Key.Left when _navigation.CanGoBack => GoBack(),
                _ => false,
            };
            if (handled) { e.Handled = true; return; }
        }

        // ── Backspace sans modificateur : GoBack ─────────────────────────────
        if (none && e.Key == Key.Back && _navigation.CanGoBack
            && !IsTextInputFocused())
        {
            GoBack();
            e.Handled = true;
            return;
        }

        // ── Échap : GoBack ────────────────────────────────────────────────────
        if (none && e.Key == Key.Escape && !IsTextInputFocused())
        {
            if (_navigation.CanGoBack) GoBack();
            e.Handled = true;
            return;
        }

        // ── Ctrl+F ou / : focus SearchBox ─────────────────────────────────────
        if ((ctrl && !alt && !shift && e.Key == Key.F) ||
            (none && e.Key == Key.OemQuestion && !IsTextInputFocused()))
        {
            System.Diagnostics.Debug.WriteLine(
                $"[Keyboard] Ctrl+F détecté — focused={Keyboard.FocusedElement?.GetType().Name}");
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        // ── F5 : lancer ──────────────────────────────────────────────────────
        if (none && e.Key == Key.F5)
        {
            if (LaunchCurrent()) { e.Handled = true; return; }
        }

        // ── Entrée : ouvrir fiche depuis la liste ─────────────────────────────
        if (none && e.Key == Key.Enter && !IsTextInputFocused())
        {
            if (OpenSelected()) { e.Handled = true; return; }
        }

        // ── 1..5 : onglets de la fiche détail ────────────────────────────────
        if (none && key >= Key.D1 && key <= Key.D5 && !IsTextInputFocused())
        {
            if (SelectDetailTab(key - Key.D1)) { e.Handled = true; return; }
        }

        // ── F : toggle favori ────────────────────────────────────────────────
        if (none && key == Key.F && !IsTextInputFocused())
        {
            if (ToggleFavorite()) { e.Handled = true; return; }
        }

        // ── Espace : play/pause (musique ou vidéo) ────────────────────────────
        // Exclut les boutons/cases focusés : Espace y active normalement le
        // contrôle (comportement WPF standard) — pas question de le voler ici.
        if (none && e.Key == Key.Space && !IsTextInputFocused() && !IsButtonLikeFocused())
        {
            if (TogglePlayPause()) { e.Handled = true; return; }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static bool Execute(IRelayCommand cmd)
    {
        if (!cmd.CanExecute(null)) return false;
        cmd.Execute(null);
        return true;
    }

    private bool GoBack()
    {
        _navigation.GoBack();
        return true;
    }

    /// <summary>
    /// Convertit une touche du pavé numérique en sa touche équivalente du
    /// clavier principal — NumPad0→D0, NumPad1→D1 … NumPad9→D9.
    /// Toute autre touche est retournée telle quelle.
    /// Permet aux raccourcis clavier de fonctionner avec les deux claviers.
    /// </summary>
    private static Key NormalizeNumPad(Key k) => k switch
    {
        Key.NumPad0 => Key.D0,
        Key.NumPad1 => Key.D1,
        Key.NumPad2 => Key.D2,
        Key.NumPad3 => Key.D3,
        Key.NumPad4 => Key.D4,
        Key.NumPad5 => Key.D5,
        Key.NumPad6 => Key.D6,
        Key.NumPad7 => Key.D7,
        Key.NumPad8 => Key.D8,
        Key.NumPad9 => Key.D9,
        _ => k,
    };

    /// <summary>Vérifie si le focus est dans un TextBox/RichTextBox (saisie texte).</summary>
    private static bool IsTextInputFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is TextBox or RichTextBox or PasswordBox;
    }

    /// <summary>Vérifie si le focus est sur un contrôle où Espace a déjà un sens
    /// natif WPF (bouton, case à cocher, item de liste…) — évite de lui voler la touche.</summary>
    private static bool IsButtonLikeFocused()
    {
        var focused = Keyboard.FocusedElement;
        return focused is System.Windows.Controls.Primitives.ButtonBase
            or ListBoxItem
            or ComboBox;
    }

    /// <summary>Donne le focus à la SearchBox de la vue active.</summary>
    private void FocusSearchBox()
    {
        if (_window == null) return;

        // Si on a un resolver pour le ViewModel actif, chercher dans cette vue précise.
        // Sinon fallback sur la recherche globale (IsVisible=True).
        TextBox? searchBox = null;
        var vmType = _mainVm.CurrentViewModel?.GetType();
        if (vmType != null && _activeViewResolvers.TryGetValue(vmType, out var resolver))
        {
            var view = resolver();
            if (view != null)
                searchBox = FindDescendant<TextBox>(view, tb =>
                    tb.Name == "SearchBox" || tb.Tag?.ToString() == "search");
        }

        // Fallback : chercher dans toute la fenêtre avec le filtre IsVisible
        searchBox ??= FindDescendant<TextBox>(_window, tb =>
            (tb.Name == "SearchBox" || tb.Tag?.ToString() == "search")
            && tb.IsVisible);

        System.Diagnostics.Debug.WriteLine(
            $"[Keyboard] FocusSearchBox → {(searchBox == null ? "NOT FOUND" : $"Name={searchBox.Name}")}");

        if (searchBox != null)
        {
            var box = searchBox;
            box.Dispatcher.InvokeAsync(() =>
            {
                box.Focus();
                Keyboard.Focus(box);
                box.SelectAll();
            }, System.Windows.Threading.DispatcherPriority.Input);
        }
    }

    /// <summary>Lance la release courante (fiche détail ou item sélectionné).</summary>
    private bool LaunchCurrent()
    {
        // Fiche détail affichée → lancer directement
        if (_mainVm.ReleaseDetailVm?.LaunchCommand is { } launchCmd
            && launchCmd.CanExecute(null))
        {
            launchCmd.Execute(null);
            return true;
        }

        // Depuis une liste (Releaser, Party) → naviguer vers la fiche puis lancer
        int? releaseId = GetSelectedReleaseId();
        if (releaseId == null) return false;

        _navigation.NavigateTo<ReleaseDetailViewModel>(releaseId.Value);
        System.Windows.Application.Current.Dispatcher.BeginInvoke(async () =>
        {
            await System.Threading.Tasks.Task.Delay(300);
            _mainVm.ReleaseDetailVm?.LaunchCommand?.Execute(null);
        }, System.Windows.Threading.DispatcherPriority.Background);
        return true;
    }

    /// <summary>Ouvre la fiche de la release sélectionnée dans la liste active.</summary>
    private bool OpenSelected()
    {
        if (_mainVm.CurrentViewModel is ReleaseListViewModel rlVm
            && rlVm.SelectedRelease != null)
        {
            var current = rlVm.SelectedRelease;
            rlVm.SelectedRelease = null;
            rlVm.SelectedRelease = current;
            return true;
        }

        int? releaseId = GetSelectedReleaseId();
        if (releaseId == null) return false;
        _navigation.NavigateTo<ReleaseDetailViewModel>(releaseId.Value);
        return true;
    }

    /// <summary>Retourne l'Id de la release sélectionnée dans la vue active, ou null.</summary>
    private int? GetSelectedReleaseId()
    {
        if (_mainVm.CurrentViewModel is ReleaserDetailViewModel rdVm
            && rdVm.SelectedRelease != null)
            return rdVm.SelectedRelease.Id;

        if (_mainVm.CurrentViewModel is PartyDetailViewModel pdVm
            && pdVm.SelectedPlacing != null)
            return pdVm.SelectedPlacing.ReleaseId;

        return null;
    }

    /// <summary>Sélectionne l'onglet index (0-based) de la fiche détail.</summary>
    private bool SelectDetailTab(int index)
    {
        if (_window == null) return false;
        var tabs = FindDescendant<TabControl>(_window, tc => tc.Name == "MainTabs");
        if (tabs == null || index >= tabs.Items.Count) return false;
        tabs.SelectedIndex = index;
        return true;
    }

    /// <summary>
    /// Navigue dans la liste active (ReleaseListViewModel ou ReleaserDetailViewModel)
    /// avec un offset de +1 (bas) ou -1 (haut), et fait défiler la vue en conséquence.
    /// </summary>
    private bool NavigateActiveList(int offset)
    {
        // ReleaseListView active — SelectByOffset + NavigateTo intégrés dans SelectRelease
        if (_mainVm.CurrentViewModel is ReleaseListViewModel rlVm)
        {
            var vmType = rlVm.GetType();
            FrameworkElement? view = null;
            if (_activeViewResolvers.TryGetValue(vmType, out var resolver))
                view = resolver();

            int newIndex = rlVm.SelectByOffset(offset);
            if (newIndex < 0) return false;

            // Naviguer vers la fiche (comme SelectRelease le fait)
            if (rlVm.SelectedRelease != null)
                _navigation.NavigateTo<ReleaseDetailViewModel>(rlVm.SelectedRelease.Id);

            if (view is DemoBase.App.Views.Releases.ReleaseListView rlView)
                rlView.ScrollToIndex(newIndex);

            return true;
        }

        // ReleaserDetailViewModel active
        if (_mainVm.CurrentViewModel is ReleaserDetailViewModel rdVm)
        {
            int newIndex = rdVm.SelectByOffset(offset);
            if (newIndex < 0) return false;

            if (rdVm.SelectedRelease != null)
                _navigation.NavigateTo<ReleaseDetailViewModel>(rdVm.SelectedRelease.Id);

            if (_activeViewResolvers.TryGetValue(typeof(ReleaserDetailViewModel), out var rdResolver)
                && rdResolver() is DemoBase.App.Views.Releasers.ReleaserDetailView relView)
                relView.ScrollToSelected();

            return true;
        }

        // PartyDetailViewModel active
        if (_mainVm.CurrentViewModel is PartyDetailViewModel pdVm)
        {
            int newIndex = pdVm.SelectByOffset(offset);
            if (newIndex < 0) return false;

            if (pdVm.SelectedPlacing != null)
                _navigation.NavigateTo<ReleaseDetailViewModel>(pdVm.SelectedPlacing.ReleaseId);

            if (_activeViewResolvers.TryGetValue(typeof(PartyDetailViewModel), out var resolver)
                && resolver() is DemoBase.App.Views.Parties.PartyDetailView partyView)
            {
                partyView.ScrollToSelected();
            }
            return true;
        }

        // MediaBrowserViewModel active, onglet Music (le grid Graphics n'est pas une
        // liste linéaire — pas de navigation ↑↓ dessus). Le scroll dans la vue est
        // déjà géré automatiquement par MediaBrowserView.OnMusicVmPropertyChanged
        // dès que SelectedItem change, pas besoin de le refaire ici.
        if (_mainVm.CurrentViewModel is MediaBrowserViewModel mbVm && !mbVm.IsGraphicsActive)
        {
            int newIndex = mbVm.Music.SelectByOffset(offset);
            if (newIndex < 0) return false;

            if (mbVm.Music.SelectedItem != null)
                _navigation.NavigateTo<ReleaseDetailViewModel>(mbVm.Music.SelectedItem.Id);

            return true;
        }

        return false;
    }

    /// <summary>
    /// Bascule play/pause sur le média actuellement chargé — musique (SoundtrackPlayer
    /// de la fiche détail, singleton réutilisé partout y compris depuis MediaBrowser)
    /// en priorité, sinon vidéo (VideoPlayer/InlineVideoPlayer de la fiche détail).
    /// Retourne false si rien n'est chargé, pour laisser passer la touche Espace
    /// ailleurs (ex. cases à cocher, boutons).
    /// </summary>
    private bool TogglePlayPause()
    {
        var soundtrackVm = _mainVm.ReleaseDetailVm?.SoundtrackPlayer?.Vm;
        if (soundtrackVm is { IsLoaded: true })
        {
            soundtrackVm.PlayPauseCommand.Execute(null);
            return true;
        }

        var videoVm = _mainVm.ReleaseDetailVm?.VideoPlayer ?? _mainVm.ReleaseDetailVm?.InlineVideoPlayer;
        if (videoVm != null)
        {
            if (videoVm.IsPlaying) videoVm.PauseCommand.Execute(null);
            else                   videoVm.PlayCommand.Execute(null);
            return true;
        }

        return false;
    }

    /// <summary>Toggle le favori de la release affichée.</summary>
    private bool ToggleFavorite()
    {
        if (_mainVm.ReleaseDetailVm?.ToggleFavoriteCommand is { } cmd
            && cmd.CanExecute(null))
        {
            cmd.Execute(null);
            return true;
        }
        return false;
    }

    /// <summary>Cherche récursivement un descendant visuel du type T vérifiant le prédicat.</summary>
    private static T? FindDescendant<T>(DependencyObject parent, Func<T, bool>? predicate = null)
        where T : DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t && (predicate == null || predicate(t)))
                return t;
            var found = FindDescendant<T>(child, predicate);
            if (found != null) return found;
        }
        return null;
    }
}
