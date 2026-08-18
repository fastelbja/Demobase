using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoBase.App.Behaviors;

/// <summary>
/// Helper statique — appelé depuis le PreviewKeyDown de la UserControl parente.
/// Gère PageDown/PageUp/End/Home sur le ScrollViewer cible et déclenche
/// LoadMoreCommand quand on approche du bas.
/// </summary>
public static class ScrollKeyboardBehavior
{
    /// <summary>
    /// Cache le curseur de la souris immédiatement et le restaure au premier
    /// MouseMove. Appeler depuis tout gestionnaire de navigation clavier
    /// (↑↓ dans une liste) pour éviter que IsMouseOver d'un item aléatoire
    /// n'écrase visuellement le highlight de l'item sélectionné au clavier.
    /// </summary>
    public static void HideMouseDuringKeyNav(FrameworkElement root)
    {
        Mouse.OverrideCursor = Cursors.None;

        // Restaurer sur la MainWindow avec handledEventsToo=true — garantit que
        // le MouseMove est capturé même si un contrôle enfant l'a marqué Handled.
        var window = Application.Current?.MainWindow;
        if (window == null) return;

        void Restore(object sender, MouseEventArgs e)
        {
            Mouse.OverrideCursor = null;
            window.RemoveHandler(UIElement.MouseMoveEvent,
                new MouseEventHandler(Restore));
        }
        window.AddHandler(UIElement.MouseMoveEvent,
            new MouseEventHandler(Restore), handledEventsToo: true);
    }
    /// <summary>
    /// À appeler dans PreviewKeyDown de la UserControl.
    /// </summary>
    public static void HandleKey(KeyEventArgs e, ScrollViewer sv, ICommand? loadMoreCmd)
    {
        switch (e.Key)
        {
            case Key.PageDown:
                sv.ScrollToVerticalOffset(sv.VerticalOffset + sv.ViewportHeight * 0.9);
                TryLoadMore(sv, loadMoreCmd);
                e.Handled = true;
                break;

            case Key.PageUp:
                sv.ScrollToVerticalOffset(sv.VerticalOffset - sv.ViewportHeight * 0.9);
                e.Handled = true;
                break;

            case Key.End:
                sv.ScrollToBottom();
                TryLoadMore(sv, loadMoreCmd);
                e.Handled = true;
                break;

            case Key.Home:
                sv.ScrollToTop();
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Variante de HandleKey qui met aussi à jour la sélection après PageDown/PageUp/Home/End.
    /// <paramref name="items"/> = liste aplatie de tous les items affichés (peu importe un
    /// éventuel regroupement visuel — group headers, etc.) ; <paramref name="selectAt"/>
    /// reçoit l'index (0-based dans cette même liste) à sélectionner.
    ///
    /// PageDown/PageUp sélectionnent l'item réellement visible en haut/bas du viewport
    /// après le scroll, en mesurant la position RÉELLE des containers déjà matérialisés
    /// (cf. FindVisibleItemIndex) — PAS une estimation par hauteur fixe. Une estimation
    /// (ancienne implémentation : itemCount/itemHeight) dérive de plus en plus de la
    /// réalité au fur et à mesure qu'on descend dans la liste, dès que les items ne font
    /// pas rigoureusement tous la même hauteur : au bout de plusieurs PageDown, la
    /// sélection finit par pointer sur un item plus bas que ce qui est réellement visible,
    /// d'où un saut inattendu vers le bas au ↑ suivant (retour utilisateur du 2026-07-29).
    /// </summary>
    /// <summary>
    /// Au-delà de ce nombre total d'items (pas seulement chargés — le total réel de la
    /// liste, filtres compris), la touche Fin est désactivée (2026-07-29, retour
    /// utilisateur après un appui accidentel sur une liste de ~340 000 releases non
    /// filtrée) : sauter directement à la fin forcerait potentiellement le chargement
    /// de très nombreuses pages à la suite. Page ↓ reste disponible pour avancer par
    /// paliers, contrôlés, quelle que soit la taille de la liste.
    /// </summary>
    private const int MaxTotalForEndKey = 1000;

    public static void HandleKeyWithSelection(
        KeyEventArgs e, ScrollViewer sv, ICommand? loadMoreCmd,
        System.Collections.IList items, int totalCount, Action<int> selectAt)
    {
        if (items.Count == 0) return;

        switch (e.Key)
        {
            case Key.PageDown:
            {
                double newOffset = Math.Min(
                    sv.VerticalOffset + sv.ViewportHeight * 0.9,
                    sv.ScrollableHeight);
                sv.ScrollToVerticalOffset(newOffset);
                TryLoadMore(sv, loadMoreCmd);
                sv.UpdateLayout();
                int idx = FindVisibleItemIndex(sv, items, fromBottom: true);
                if (idx >= 0) selectAt(idx);
                e.Handled = true;
                break;
            }
            case Key.PageUp:
            {
                double newOffset = Math.Max(0, sv.VerticalOffset - sv.ViewportHeight * 0.9);
                sv.ScrollToVerticalOffset(newOffset);
                sv.UpdateLayout();
                int idx = FindVisibleItemIndex(sv, items, fromBottom: false);
                if (idx >= 0) selectAt(idx);
                e.Handled = true;
                break;
            }
            case Key.End:
                if (totalCount > MaxTotalForEndKey)
                {
                    DemoBase.App.Controls.StatusScrollerControl.Post(
                        string.Format(
                            DemoBase.App.Services.LocalizationService.Get("ScrollEnd_TooManyItems"),
                            totalCount),
                        isWarning: true);
                    e.Handled = true;
                    break;
                }
                sv.ScrollToBottom();
                TryLoadMore(sv, loadMoreCmd);
                sv.UpdateLayout();
                selectAt(items.Count - 1);
                e.Handled = true;
                break;

            case Key.Home:
                sv.ScrollToTop();
                sv.UpdateLayout();
                selectAt(0);
                e.Handled = true;
                break;
        }
    }

    /// <summary>
    /// Cherche, parmi les containers visuels déjà matérialisés sous <paramref name="sv"/>
    /// (peu importe l'imbrication — group headers, etc.), l'item de <paramref name="items"/>
    /// le plus proche du bord haut (fromBottom=false) ou bas (fromBottom=true) du viewport
    /// visible du ScrollViewer, à partir de sa position RÉELLE (TransformToAncestor), pas
    /// d'une estimation. Retourne -1 si rien n'est trouvé.
    /// </summary>
    private static int FindVisibleItemIndex(ScrollViewer sv, System.Collections.IList items, bool fromBottom)
    {
        int    bestIndex = -1;
        double bestY     = fromBottom ? double.NegativeInfinity : double.PositiveInfinity;

        void Walk(DependencyObject parent, object? parentDataContext)
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child   = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                var childFe = child as FrameworkElement;
                var childDc = childFe?.DataContext;
                // "Frontière" d'item = premier élément du visual tree qui porte un
                // DataContext différent de son parent (le DataContext est hérité par
                // tous les descendants d'un même DataTemplate — on ne veut mesurer que
                // la racine de chaque item, pas chacun de ses enfants internes).
                bool isBoundary = childDc != null && !ReferenceEquals(childDc, parentDataContext);

                if (isBoundary)
                {
                    int idx = items.IndexOf(childDc);
                    if (idx >= 0)
                    {
                        double top;
                        try { top = childFe!.TransformToAncestor(sv).Transform(new Point(0, 0)).Y; }
                        catch { continue; }
                        double bottom  = top + childFe!.ActualHeight;
                        bool   visible = bottom > 1 && top < sv.ViewportHeight - 1;
                        if (visible && ((fromBottom && top >= bestY) || (!fromBottom && top <= bestY)))
                        {
                            bestY     = top;
                            bestIndex = idx;
                        }
                        continue; // rien de plus à trouver à l'intérieur de cet item
                    }
                }
                Walk(child, isBoundary ? childDc : parentDataContext);
            }
        }
        Walk(sv, null);
        return bestIndex;
    }

    private static void TryLoadMore(ScrollViewer sv, ICommand? cmd)
    {
        if (cmd == null) return;
        // Petit délai pour laisser le scroll se terminer avant de mesurer
        sv.Dispatcher.InvokeAsync(() =>
        {
            if (sv.ScrollableHeight - sv.VerticalOffset < sv.ViewportHeight * 1.5
                && cmd.CanExecute(null))
                cmd.Execute(null);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
