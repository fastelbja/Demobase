using System.Diagnostics;
using DemoBase.App.Behaviors;
using DemoBase.App.ViewModels.Releases;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoBase.App.Views.Releases;

public partial class ReleaseListView : UserControl
{
    private bool _isRestoring;

    private ReleaseListViewModel? _vm;

    public ReleaseListView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ReleaseListViewModel oldVm)
            {
                oldVm.ScrollResetRequested   -= OnScrollResetRequested;
                oldVm.ScrollRestoreRequested -= RestoreScroll;
            }
            _vm = e.NewValue as ReleaseListViewModel;
            if (_vm != null)
            {
                _vm.ScrollResetRequested   += OnScrollResetRequested;
                _vm.ScrollRestoreRequested += RestoreScroll;
            }
        };

        DataContextChanged += (_, e) =>
        {
            if (e.NewValue is not ReleaseListViewModel vm || vm.SavedScrollOffset <= 0) return;

            void TryRestore(object? s, System.Windows.SizeChangedEventArgs ev)
            {
                if (ListScrollViewer.ScrollableHeight >= vm.SavedScrollOffset)
                {
                    ListScrollViewer.SizeChanged -= TryRestore;
                    ListScrollViewer.ScrollToVerticalOffset(vm.SavedScrollOffset);
                }
            }
            if (ListScrollViewer.ScrollableHeight >= vm.SavedScrollOffset)
            {
                _isRestoring = true;
                ListScrollViewer.ScrollToVerticalOffset(vm.SavedScrollOffset);
                _isRestoring = false;
            }
            else
                ListScrollViewer.SizeChanged += TryRestore;
        };
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                // Déléguer la navigation à la liste (même logique que OnPreviewKeyDown)
                int offset = e.Key == Key.Down ? 1 : -1;
                int newIndex = _vm.SelectByOffset(offset);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
                if (newIndex >= 0)
                    ScrollItemIntoView(newIndex);
                e.Handled = true;
                break;

            case Key.Escape:
                // Quitter la SearchBox → rendre le focus à la liste
                SearchBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                Focus();
                e.Handled = true;
                break;

            case Key.Enter:
                // Entrée depuis la SearchBox → rendre le focus à la liste
                Focus();
                e.Handled = true;
                break;
        }
    }
    private void OnScrollResetRequested()
    {
        Dispatcher.InvokeAsync(() => ListScrollViewer.ScrollToTop(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_vm == null) return;

        // Flèches haut/bas : naviguer entre les releases
        if (e.Key == Key.Up || e.Key == Key.Down)
        {
            int offset = e.Key == Key.Down ? 1 : -1;
            int newIndex = _vm.SelectByOffset(offset);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
            if (newIndex >= 0)
                ScrollItemIntoView(newIndex);
            e.Handled = true;
            return;
        }

        ScrollKeyboardBehavior.HandleKeyWithSelection(e, ListScrollViewer, _vm.LoadMoreCommand,
            _vm.Releases, _vm.TotalCount,
            idx => _vm.SelectAt(idx));
    }

    /// <summary>Fait défiler la liste pour que l'item à l'index donné soit visible.
    /// Appelé depuis GlobalKeyboardService pour la navigation clavier globale.</summary>
    public void ScrollToIndex(int index) => ScrollItemIntoView(index);

    /// <summary>Fait défiler la liste pour que l'item à l'index donné soit visible.</summary>
    private void ScrollItemIntoView(int index)
    {
        // Avec VirtualizingStackPanel, on peut demander BringIndexIntoView
        if (ReleasesList.ItemContainerGenerator.Status
            == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            var container = ReleasesList.ItemContainerGenerator.ContainerFromIndex(index)
                as FrameworkElement;
            container?.BringIntoView();
        }
        else
        {
            // Fallback : estimer la position par hauteur moyenne
            ReleasesList.UpdateLayout();
            var container = ReleasesList.ItemContainerGenerator.ContainerFromIndex(index)
                as FrameworkElement;
            container?.BringIntoView();
        }
    }

    private void ListScrollViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv || _vm == null) return;

        // Ne sauvegarder que si ce n'est pas une restauration en cours
        if (!_isRestoring)
            _vm.SavedScrollOffset = sv.VerticalOffset;

        if (_vm.IsLoading || _vm.IsLoadingMore || !_vm.HasMorePages) return;
        if (sv.ScrollableHeight - sv.VerticalOffset < 200)
            _ = _vm.LoadMoreCommand.ExecuteAsync(null);
    }
    private void RestoreScroll()
    {
        if (_vm == null || _vm.SavedScrollOffset <= 0) return;
        void OnLayout(object? s, EventArgs ev)
        {
            if (ListScrollViewer.ScrollableHeight > 0)
            {
                ListScrollViewer.LayoutUpdated -= OnLayout;
                _isRestoring = true;
                ListScrollViewer.ScrollToVerticalOffset(_vm.SavedScrollOffset);
                _isRestoring = false;
            }
        }
        if (ListScrollViewer.ScrollableHeight >= _vm.SavedScrollOffset)
        {
            _isRestoring = true;
            ListScrollViewer.ScrollToVerticalOffset(_vm.SavedScrollOffset);
            _isRestoring = false;
        }
        else
            ListScrollViewer.LayoutUpdated += OnLayout;
    }
}
