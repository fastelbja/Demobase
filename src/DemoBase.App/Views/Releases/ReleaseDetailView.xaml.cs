using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Releases;

public partial class ReleaseDetailView : UserControl
{
    public ReleaseDetailView()
    {
        InitializeComponent();

        // Revenir sur l'onglet Infos à chaque changement de release
        DataContextChanged += (_, e) =>
        {
            if (MainTabs != null)
                MainTabs.SelectedIndex = 0;

            // Vue Singleton : écouter IsLoading pour détecter un nouveau chargement
            if (e.OldValue is DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel oldVm)
                oldVm.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel newVm)
                newVm.PropertyChanged += OnVmPropertyChanged;
        };

        // Revenir sur Screenshots quand on clique sur l'onglet Médias
        MainTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source != MainTabs || MainTabs.SelectedIndex != 2) return;

            // MediaTabs est dans un TabItem — le chercher dans le visual tree
            var mediaTabs = FindName("MediaTabs") as TabControl
                         ?? FindVisualChild<TabControl>(MainTabs.SelectedContent as System.Windows.DependencyObject);
            if (mediaTabs != null)
                mediaTabs.SelectedIndex = 0;
        };
    }

    private static T? FindVisualChild<T>(System.Windows.DependencyObject? parent)
        where T : System.Windows.DependencyObject
    {
        if (parent == null) return null;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T result) return result;
            var found = FindVisualChild<T>(child);
            if (found != null) return found;
        }
        return null;
    }
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // Quand IsLoading passe à true = nouvelle release chargée → revenir sur Info
        if (e.PropertyName == nameof(DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel.IsLoading)
            && sender is DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel vm
            && vm.IsLoading)
        {
            Dispatcher.Invoke(() => { if (MainTabs != null) MainTabs.SelectedIndex = 0; });
        }
    }

    /// <summary>
    /// Clic sur le titre d'un soundtrack dans l'onglet Media : ouvre la release
    /// correspondante. Utilise un handler en code-behind plutôt qu'un MouseBinding
    /// car RelativeSource AncestorType=UserControl ne se résout pas correctement
    /// depuis un InputBinding placé dans un DataTemplate imbriqué.
    /// </summary>
    private void SoundtrackTitle_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not int soundtrackId) return;
        if (DataContext is not DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel vm) return;

        vm.OpenSoundtrackReleaseCommand.Execute(soundtrackId);
    }

    /// <summary>
    /// Clic sur une release dans l'onglet "Used In" : ouvre la release correspondante.
    /// Même raison que SoundtrackTitle_MouseLeftButtonUp : MouseBinding + RelativeSource
    /// ne fonctionne pas de façon fiable dans un DataTemplate imbriqué.
    /// </summary>
    private void UsedInRelease_MouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe) return;
        if (fe.Tag is not int releaseId) return;
        if (DataContext is not DemoBase.App.ViewModels.Releases.ReleaseDetailViewModel vm) return;

        vm.OpenReleaseCommand.Execute(releaseId);
    }
}
