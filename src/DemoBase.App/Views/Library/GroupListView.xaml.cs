using System.Diagnostics;
using DemoBase.App.Behaviors;
using DemoBase.App.ViewModels.Library;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoBase.App.Views.Library;

public partial class GroupListView : UserControl
{
    private bool _isRestoring;

    public GroupListView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is GroupListViewModel oldVm) oldVm.ScrollRestoreRequested -= RestoreScroll;
            if (e.NewValue is GroupListViewModel newVm) newVm.ScrollRestoreRequested += RestoreScroll;
        };
    }

    // ── SearchBox : ↑↓ naviguent dans la liste, Entrée ouvre, Échap quitte ──

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not GroupListViewModel vm) return;
        switch (e.Key)
        {
            case Key.Down:
            case Key.Up:
                int idx = vm.SelectByOffset(e.Key == Key.Down ? 1 : -1);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
                if (idx >= 0) ScrollItemIntoView(idx, vm.Items.Count);
                e.Handled = true;
                break;
            case Key.Enter:
                if (vm.SelectedItem != null && vm.OpenGroupCommand.CanExecute(vm.SelectedItem))
                    vm.OpenGroupCommand.Execute(vm.SelectedItem);
                e.Handled = true;
                break;
            case Key.Escape:
                Focus();
                e.Handled = true;
                break;
        }
    }

    // ── Navigation clavier globale (hors SearchBox) ───────────────────────────

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (DataContext is not GroupListViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SelectedItem != null && vm.OpenGroupCommand.CanExecute(vm.SelectedItem))
                {
                    vm.OpenGroupCommand.Execute(vm.SelectedItem);
                    e.Handled = true;
                    return;
                }
                break;
            case Key.Down:
            case Key.Up:
                int idx = vm.SelectByOffset(e.Key == Key.Down ? 1 : -1);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
                if (idx >= 0) ScrollItemIntoView(idx, vm.Items.Count);
                e.Handled = true;
                return;
        }

        ScrollKeyboardBehavior.HandleKeyWithSelection(e, Scroll, vm.LoadMoreCommand,
            vm.Items, vm.TotalCount,
            idx => { vm.SelectedItem = idx < vm.Items.Count ? vm.Items[idx] : null; });
    }

    private void ScrollItemIntoView(int index, int total)
    {
        // Utiliser BringIntoView() via ItemContainerGenerator — identique à ReleaseListView.
        // Si les containers ne sont pas encore générés (virtualisation), fallback estimation.
        if (ItemsList.ItemContainerGenerator.Status
            == System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated)
        {
            var container = ItemsList.ItemContainerGenerator.ContainerFromIndex(index)
                as System.Windows.FrameworkElement;
            container?.BringIntoView();
        }
        else
        {
            ItemsList.UpdateLayout();
            var container = ItemsList.ItemContainerGenerator.ContainerFromIndex(index)
                as System.Windows.FrameworkElement;
            if (container != null)
                container.BringIntoView();
            else if (total > 1)
            {
                // Dernier recours : estimation mathématique
                double itemH = Scroll.ScrollableHeight / (total - 1);
                Scroll.ScrollToVerticalOffset(Math.Max(0, index * itemH - Scroll.ViewportHeight / 2));
            }
        }
    }

    private void Scroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;

        // Ne sauvegarder que si c'est l'utilisateur qui a scrollé (pas un layout/visibility change)
        if (e.VerticalChange != 0)
            Debug.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] GroupListView.ScrollChanged offset={sv.VerticalOffset:F0} change={e.VerticalChange:F0} IsVisible={IsVisible} restoring={_isRestoring}");
        if (!_isRestoring && IsVisible && e.VerticalChange != 0 && DataContext is GroupListViewModel vm)
            vm.SavedScrollOffset = sv.VerticalOffset;

        if (DataContext is GroupListViewModel vm2
            && !vm2.IsLoading && !vm2.IsLoadingMore && vm2.HasMorePages
            && sv.ScrollableHeight - sv.VerticalOffset < 200)
            _ = vm2.LoadMoreCommand.ExecuteAsync(null);
    }
    private void RestoreScroll()
    {
        if (DataContext is not GroupListViewModel vm || vm.SavedScrollOffset <= 0) return;

        void OnLayout(object? s, EventArgs ev)
        {
            if (Scroll.ScrollableHeight > 0)
            {
                Scroll.LayoutUpdated -= OnLayout;
                _isRestoring = true;
                Scroll.ScrollToVerticalOffset(vm.SavedScrollOffset);
                _isRestoring = false;
            }
        }
        if (Scroll.ScrollableHeight >= vm.SavedScrollOffset)
        {
            _isRestoring = true;
            Scroll.ScrollToVerticalOffset(vm.SavedScrollOffset);
            _isRestoring = false;
        }
        else
        {
            Scroll.LayoutUpdated += OnLayout;
        }
    }
}
