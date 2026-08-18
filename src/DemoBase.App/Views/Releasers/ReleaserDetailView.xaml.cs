using DemoBase.App.Behaviors;
using DemoBase.App.ViewModels.Releases;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace DemoBase.App.Views.Releasers;

public partial class ReleaserDetailView : UserControl
{
    public ReleaserDetailView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Focus();

        var parent = Parent as FrameworkElement;
        if (parent == null) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            ForceScrollerHeight(parent.ActualHeight);
            // Restaurer le scroll après que la hauteur est calculée
            if (DataContext is ReleaserDetailViewModel vm)
            {
                if (vm.SavedReleasesScrollOffset > 0)
                    ReleasesScroller.ScrollToVerticalOffset(vm.SavedReleasesScrollOffset);
                if (vm.SavedRightScrollOffset > 0)
                    RightScroller.ScrollToVerticalOffset(vm.SavedRightScrollOffset);
            }
        });
        parent.SizeChanged += (_, ev) => ForceScrollerHeight(ev.NewSize.Height);

        // Sauvegarder le scroll quand l'utilisateur scrolle
        ReleasesScroller.ScrollChanged += (_, e) =>
        {
            if (DataContext is ReleaserDetailViewModel vm && e.VerticalChange != 0)
                vm.SavedReleasesScrollOffset = ReleasesScroller.VerticalOffset;
        };
        RightScroller.ScrollChanged += (_, e) =>
        {
            if (DataContext is ReleaserDetailViewModel vm && e.VerticalChange != 0)
                vm.SavedRightScrollOffset = RightScroller.VerticalOffset;
        };

        // Cette vue est réutilisée d'une navigation à l'autre (ViewModel singleton, jamais
        // recréé) : Loaded ci-dessus ne se déclenche donc qu'UNE fois pour toute la durée de
        // vie de l'appli, pas à chaque changement de releaser affiché. Sans ce hook, ouvrir un
        // nouveau releaser (ex. depuis les crédits d'une release) laissait le scroll exactement
        // là où il était sur le releaser précédent, au lieu de revenir en haut de la nouvelle
        // liste — cf. PlatformGroups, repeuplé en fin de BuildReleasesFromResultAsync une fois
        // le contenu prêt à être mesuré (contrairement à Releaser, qui change plus tôt).
        if (DataContext is ReleaserDetailViewModel vmInit)
        {
            vmInit.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(ReleaserDetailViewModel.PlatformGroups)) return;
                Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
                {
                    ReleasesScroller.ScrollToVerticalOffset(vmInit.SavedReleasesScrollOffset);
                    RightScroller.ScrollToVerticalOffset(vmInit.SavedRightScrollOffset);
                });
            };
        }
    }

    private void ForceScrollerHeight(double totalHeight)
    {
        if (totalHeight <= 0) return;

        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            double headerH  = HeaderBorder.ActualHeight;
            double toolbarH = LeftColGrid.RowDefinitions[0].ActualHeight
                            + LeftColGrid.RowDefinitions[1].ActualHeight;

            double scrollH = totalHeight - headerH - toolbarH - 2;
            if (scrollH > 50)
            {
                ReleasesScroller.Height = scrollH;
                RightScroller.Height    = totalHeight - headerH - 2;
            }
        });
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (DataContext is not ReleaserDetailViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SelectedRelease != null)
                {
                    vm.OpenReleaseCommand.Execute(vm.SelectedRelease);
                    e.Handled = true;
                    return;
                }
                break;
            case Key.Down:
            case Key.Up:
                int idx = vm.SelectByOffset(e.Key == Key.Down ? 1 : -1);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
                if (idx >= 0 && vm.SelectedRelease != null)
                {
                    ReleasesScroller.UpdateLayout();
                    var container = FindContainerByDataContext(ReleasesScroller, vm.SelectedRelease);
                    container?.BringIntoView();
                    e.Handled = true;
                    return;
                }
                break;
        }

        var flatReleases = vm.PlatformGroups.SelectMany(g => g.Releases).ToList();
        ScrollKeyboardBehavior.HandleKeyWithSelection(e, ReleasesScroller, null,
            flatReleases, flatReleases.Count,
            idx =>
            {
                if (idx >= 0 && idx < flatReleases.Count)
                    vm.SelectedRelease = flatReleases[idx];
            });
    }

    private void BtnWebsite_Click(object sender, RoutedEventArgs e)
    {
        var url = (DataContext as ReleaserDetailViewModel)?.Releaser?.Website;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    /// <summary>
    /// Traverse le visual tree pour trouver le premier FrameworkElement
    /// dont le DataContext correspond à la référence donnée.
    /// <summary>Fait défiler la liste vers la release sélectionnée.
    /// Appelé depuis GlobalKeyboardService pour la navigation globale.</summary>
    public void ScrollToSelected()
    {
        if (DataContext is not ReleaserDetailViewModel vm || vm.SelectedRelease == null) return;
        Dispatcher.InvokeAsync(() =>
        {
            ReleasesScroller.UpdateLayout();
            var container = FindContainerByDataContext(ReleasesScroller, vm.SelectedRelease);
            container?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// Utilisé pour appeler BringIntoView() sur l'item sélectionné au clavier
    /// dans une liste imbriquée (PlatformGroups → Releases) où
    /// ItemContainerGenerator n'est pas directement accessible.
    /// </summary>
    private static FrameworkElement? FindContainerByDataContext(
        DependencyObject parent, object target)
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && ReferenceEquals(fe.DataContext, target))
                return fe;
            var found = FindContainerByDataContext(child, target);
            if (found != null) return found;
        }
        return null;
    }
}
