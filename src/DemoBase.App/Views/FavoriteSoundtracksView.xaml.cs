using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DemoBase.App.ViewModels;
using DemoBase.Core.Models;

namespace DemoBase.App.Views;
public partial class FavoriteSoundtracksView : UserControl
{
    public FavoriteSoundtracksView() => InitializeComponent();

    private void TitleTextBlock_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not DemoBase.Core.Models.FavoriteSoundtrack track) return;
        if (DataContext is not DemoBase.App.ViewModels.FavoriteSoundtracksViewModel vm) return;

        vm.OpenReleaseCommand.Execute(track);
    }

    // ── Sélectionner une playlist (clic sur son en-tête) ──────────────────────
    // Border et non Button : le template partagé "IconButton" centre toujours
    // son contenu (cf. Styles.xaml), impossible d'y aligner le nom à gauche.
    // La sélection déplie la playlist ET en fait la cible du "➕" (colonne des
    // favoris non classés) — cf. FavoriteSoundtracksViewModel.SelectPlaylist.

    private void PlaylistHeader_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not PlaylistItemViewModel playlist) return;
        if (DataContext is not FavoriteSoundtracksViewModel vm) return;

        vm.SelectPlaylistCommand.Execute(playlist);
    }

    // ── Ajouter à une playlist ("➕" sur une piste de la colonne des favoris) ──
    // Si une playlist est déjà sélectionnée (colonne de gauche), on y ajoute
    // directement la piste sans autre confirmation — sinon (aucune sélection,
    // ou avec 130 000+ musiques en base l'utilisateur peut vouloir viser une
    // autre playlist ponctuellement) on ouvre un menu de choix, avec "Nouvelle
    // playlist…" toujours en premier pour créer à la volée.

    private async void AddToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not FavoriteSoundtrack track) return;
        if (DataContext is not FavoriteSoundtracksViewModel vm) return;

        if (vm.ActivePlaylist != null)
        {
            await vm.AddTrackToPlaylistAsync(track, vm.ActivePlaylist);
            return;
        }

        var menu = new ContextMenu();

        var newItem = new MenuItem { Header = DemoBase.App.Services.LocalizationService.Get("PL_NewInMenu") };
        newItem.Click += async (_, _) => await vm.CreatePlaylistAndAddTrackAsync(track);
        menu.Items.Add(newItem);

        if (vm.Playlists.Count > 0)
        {
            menu.Items.Add(new Separator());
            foreach (var playlist in vm.Playlists)
            {
                var menuItem = new MenuItem { Header = playlist.Name };
                menuItem.Click += async (_, _) => await vm.AddTrackToPlaylistAsync(track, playlist);
                menu.Items.Add(menuItem);
            }
        }

        fe.ContextMenu = menu;
        menu.PlacementTarget = fe;
        menu.IsOpen = true;
    }

    // ── Pistes d'une playlist dépliée : réordonner / retirer ─────────────────

    private void PlaylistTrackUp_Click(object sender, RoutedEventArgs e) => MoveTrack(sender, -1);
    private void PlaylistTrackDown_Click(object sender, RoutedEventArgs e) => MoveTrack(sender, +1);

    private void MoveTrack(object sender, int direction)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not FavoriteSoundtrack track) return;
        if (DataContext is not FavoriteSoundtracksViewModel vm) return;
        var playlist = FindAncestorDataContext<PlaylistItemViewModel>(fe);
        if (playlist == null) return;

        _ = vm.MoveTrackAsync(playlist, track, direction);
    }

    private void PlaylistTrackRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not FavoriteSoundtrack track) return;
        if (DataContext is not FavoriteSoundtracksViewModel vm) return;
        var playlist = FindAncestorDataContext<PlaylistItemViewModel>(fe);
        if (playlist == null) return;

        _ = vm.RemoveTrackFromPlaylistAsync(playlist, track);
    }

    /// <summary>Remonte l'arbre visuel jusqu'à trouver un élément dont le
    /// DataContext est du type T (utilisé pour retrouver la playlist parente
    /// d'une piste, dans le template imbriqué).</summary>
    private static T? FindAncestorDataContext<T>(DependencyObject start) where T : class
    {
        var current = start;
        while (current != null)
        {
            if (current is FrameworkElement fe && fe.DataContext is T match)
                return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }
}
