using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DemoBase.App.Views.Media;

public partial class ModlandBrowserView : UserControl
{
    public ModlandBrowserView()
    {
        InitializeComponent();

        // 2026-08-01, demande utilisateur : "quand je clique sur un format, peux tu
        // faire revenir le scrollbar tout en haut pour les auteurs ? idem si je clique
        // sur un auteur qu'il revienne tout en haut pour la liste des musiques" — même
        // schéma d'abonnement DataContextChanged que PartyDetailView/ReleaseListView
        // (cf. ScrollResetRequested), avec deux événements distincts côté VM puisque
        // cette vue a deux listes indépendantes à réinitialiser séparément.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is DemoBase.App.ViewModels.ModlandBrowserViewModel oldVm)
            {
                oldVm.AuthorsScrollResetRequested -= OnAuthorsScrollResetRequested;
                oldVm.TracksScrollResetRequested  -= OnTracksScrollResetRequested;
            }
            if (e.NewValue is DemoBase.App.ViewModels.ModlandBrowserViewModel newVm)
            {
                newVm.AuthorsScrollResetRequested += OnAuthorsScrollResetRequested;
                newVm.TracksScrollResetRequested  += OnTracksScrollResetRequested;
            }
        };
    }

    /// <summary>Clic sur le nom d'une piste = lecture immédiate — même principe
    /// "un clic = lecture" que les grilles Graphics/Music du MediaBrowser.</summary>
    private void TrackText_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.DataContext is not DemoBase.App.ViewModels.ModlandTrackItemViewModel item) return;
        if (DataContext is not DemoBase.App.ViewModels.ModlandBrowserViewModel vm) return;

        if (vm.PlayTrackCommand.CanExecute(item))
            vm.PlayTrackCommand.Execute(item);
    }

    // 2026-08-06 : la colonne Pistes n'est plus un ScrollViewer explicite nommé — pour
    // corriger le blocage applicatif sur les listes de plus de 1000 pistes (cf.
    // RESUME_PROJET.md), TracksItemsControl est maintenant un ItemsControl virtualisé
    // qui génère son PROPRE ScrollViewer via son template (nécessaire pour que
    // VirtualizingStackPanel fonctionne). Même pattern de recherche dans l'arbre
    // visuel que la colonne Auteurs ci-dessous.
    private void OnTracksScrollResetRequested()
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var sv in FindVisualChildren<ScrollViewer>(TracksItemsControl))
            {
                sv.ScrollToTop();
                break; // un seul ScrollViewer généré par le template de TracksItemsControl
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // La colonne Auteurs est un ListBox "brut" (pas de ScrollViewer explicite dans le
    // XAML — c'est un ListBox qui gère son propre défilement en interne). Il faut donc
    // retrouver le ScrollViewer généré par son propre template pour l'appeler —
    // FindVisualChildren : même pattern déjà utilisé ailleurs dans le projet (ex.
    // BeebEmSettingsControl.xaml.cs, FlycastSettingsControl.xaml.cs).
    private void OnAuthorsScrollResetRequested()
    {
        Dispatcher.InvokeAsync(() =>
        {
            foreach (var sv in FindVisualChildren<ScrollViewer>(AuthorsListBox))
            {
                sv.ScrollToTop();
                break; // un seul ScrollViewer généré par le template par défaut d'un ListBox
            }
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed) yield return typed;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}
