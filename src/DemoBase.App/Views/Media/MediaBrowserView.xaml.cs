using DemoBase.App.ViewModels;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Media;

public partial class MediaBrowserView : UserControl
{
    // 2026-07-30, retour utilisateur : "le téléchargement ne s'arrête plus. il doit se
    // contenter des elements visibles à l'écran" — débounce du recalcul de visibilité
    // pendant un scroll continu (évite de relire tout le visual tree à chaque pixel).
    private System.Windows.Threading.DispatcherTimer? _gfxVisibilityTimer;

    public MediaBrowserView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private MediaBrowserViewModel? Vm => DataContext as MediaBrowserViewModel;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MediaBrowserViewModel vm)
        {
            vm.Music.PropertyChanged += OnMusicVmPropertyChanged;
            vm.Graphics.PropertyChanged += OnGraphicsVmPropertyChanged;
        }
    }

    // Scroller vers l'item en cours de lecture quand SelectedItem change
    private void OnMusicVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MusicBrowserViewModel.SelectedItem)) return;
        var selected = Vm?.Music.SelectedItem;
        if (selected == null) return;

        Dispatcher.BeginInvoke(() =>
        {
            // Trouver le container visuel de l'item dans l'ItemsControl
            if (FindItemsControl() is not { } ic) return;
            var idx = Vm!.Music.Items.ToList().FindIndex(i => i.Id == selected.Id);
            if (idx < 0) return;
            var container = ic.ItemContainerGenerator.ContainerFromIndex(idx) as FrameworkElement;
            container?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // 2026-07-30, demande utilisateur : recalculer les téléchargements dès qu'une page
    // Graphics a fini de charger (premier chargement, "charger plus" par scroll infini,
    // ou nouveau filtre/recherche par nom d'artiste — IsLoading repasse à false dans
    // tous ces cas). DispatcherPriority.Loaded pour laisser le layout se stabiliser
    // (containers de l'ItemsControl générés) avant de mesurer leur position.
    private void OnGraphicsVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(GraphicsBrowserViewModel.IsLoading)) return;
        if (Vm?.Graphics.IsLoading != false) return;
        Dispatcher.BeginInvoke(ScheduleVisibleGraphicsDownloadCheck,
            System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private ItemsControl? FindItemsControl()
    {
        // Chercher le MusicItemsControl dans le visual tree
        return FindChild<ItemsControl>(this, "MusicItemsControl");
    }

    private static T? FindChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindChild<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    // ── Scroll infini — Graphics ──────────────────────────────────────────────
    private void GfxScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (Vm?.Graphics == null) return;
        if (sv.ScrollableHeight - sv.VerticalOffset < 400
            && Vm.Graphics.HasMore && !Vm.Graphics.IsLoading)
            _ = Vm.Graphics.LoadMoreCommand.ExecuteAsync(null);

        ScheduleVisibleGraphicsDownloadCheck();
    }

    /// <summary>Débounce (200 ms) du recalcul de visibilité pour ne pas relire le visual
    /// tree à chaque évènement ScrollChanged pendant un scroll continu.</summary>
    private void ScheduleVisibleGraphicsDownloadCheck()
    {
        if (_gfxVisibilityTimer == null)
        {
            _gfxVisibilityTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200),
            };
            _gfxVisibilityTimer.Tick += (_, _) =>
            {
                _gfxVisibilityTimer!.Stop();
                RequestVisibleGraphicsDownloads();
            };
        }
        _gfxVisibilityTimer.Stop();
        _gfxVisibilityTimer.Start();
    }

    /// <summary>
    /// 2026-07-30, retour utilisateur : "il doit se contenter des elements visibles à
    /// l'écran" — calcule quelles cartes de la grille Graphics sont réellement dans le
    /// viewport actuel de GfxScroller (géométrie des containers déjà générés par
    /// l'ItemsControl, non virtualisé — WrapPanel), avec une marge de préchargement de
    /// 300px au-dessus/en-dessous pour anticiper un léger scroll, puis ne demande le
    /// téléchargement (cf. GraphicsBrowserViewModel.RequestVisibleDownloads) QUE pour
    /// celles-là — plus pour la totalité d'une page de 80 vignettes comme avant.
    /// </summary>
    private void RequestVisibleGraphicsDownloads()
    {
        if (Vm?.Graphics == null) return;
        if (FindChild<ItemsControl>(this, "GfxGrid") is not { } ic) return;
        if (FindChild<ScrollViewer>(this, "GfxScroller") is not { } sv) return;

        const double preloadMargin = 300;
        var items = Vm.Graphics.Items;
        var visible = new List<GraphicCardViewModel>();

        for (int i = 0; i < items.Count; i++)
        {
            if (ic.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement fe) continue;
            if (!fe.IsVisible || fe.ActualHeight <= 0) continue;

            try
            {
                var top = fe.TransformToAncestor(sv).Transform(new Point(0, 0)).Y;
                var bottom = top + fe.ActualHeight;
                if (bottom >= -preloadMargin && top <= sv.ViewportHeight + preloadMargin)
                    visible.Add(items[i]);
            }
            catch (System.InvalidOperationException)
            {
                // Container pas (ou plus) dans le visual tree — ignoré.
            }
        }

        if (visible.Count > 0)
            Vm.Graphics.RequestVisibleDownloads(visible);
    }

    // ── Scroll infini — Music ─────────────────────────────────────────────────
    private void MusicScroller_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        if (Vm?.Music == null) return;
        if (sv.ScrollableHeight - sv.VerticalOffset < 400
            && Vm.Music.HasMore && !Vm.Music.IsLoading)
            _ = Vm.Music.LoadMoreCommand.ExecuteAsync(null);
    }
}
