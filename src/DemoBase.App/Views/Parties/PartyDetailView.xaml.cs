using DemoBase.App.Behaviors;
using DemoBase.App.ViewModels.Releases;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DemoBase.App.Views.Parties;

public partial class PartyDetailView : UserControl
{
    public PartyDetailView()
    {
        InitializeComponent();
        Focusable = true;
        Loaded += (_, _) => Focus();

        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is PartyDetailViewModel oldVm)
                oldVm.ScrollResetRequested -= OnScrollResetRequested;
            if (e.NewValue is PartyDetailViewModel newVm)
                newVm.ScrollResetRequested += OnScrollResetRequested;
        };
    }

    /// <summary>Fait défiler la liste vers l'item sélectionné.
    /// Appelé depuis GlobalKeyboardService pour la navigation globale.</summary>
    public void ScrollToSelected()
    {
        if (DataContext is not PartyDetailViewModel vm || vm.SelectedPlacing == null) return;
        Dispatcher.InvokeAsync(() =>
        {
            Scroll.UpdateLayout();
            var container = FindContainerByDataContext(Scroll, vm.SelectedPlacing);
            container?.BringIntoView();
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnScrollResetRequested()
    {
        Dispatcher.InvokeAsync(() => Scroll.ScrollToTop(),
            System.Windows.Threading.DispatcherPriority.Background);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (DataContext is not PartyDetailViewModel vm) return;

        switch (e.Key)
        {
            case Key.Enter:
                if (vm.SelectedPlacing != null)
                {
                    vm.OpenReleaseCommand.Execute(vm.SelectedPlacing.ReleaseId);
                    e.Handled = true;
                    return;
                }
                break;
            case Key.Down:
            case Key.Up:
                vm.SelectByOffset(e.Key == Key.Down ? 1 : -1);
                ScrollKeyboardBehavior.HideMouseDuringKeyNav(this);
                if (vm.SelectedPlacing != null)
                {
                    Scroll.UpdateLayout();
                    var container = FindContainerByDataContext(Scroll, vm.SelectedPlacing);
                    container?.BringIntoView();
                }
                e.Handled = true;
                return;
        }

        var flatPlacings = vm.Competitions.SelectMany(c => c.Placings).ToList();
        ScrollKeyboardBehavior.HandleKeyWithSelection(e, Scroll, null,
            flatPlacings, flatPlacings.Count,
            idx =>
            {
                if (idx >= 0 && idx < flatPlacings.Count)
                    vm.SelectedPlacing = flatPlacings[idx];
            });
    }

    private void BtnWebsite_Click(object sender, RoutedEventArgs e)
    {
        var url = (DataContext as PartyDetailViewModel)?.Party?.Website;
        if (!string.IsNullOrWhiteSpace(url))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

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
