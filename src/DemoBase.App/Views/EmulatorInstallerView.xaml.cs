using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace DemoBase.App.Views;

public partial class EmulatorInstallerView : UserControl
{
    // Seul constructeur : requis par le compilateur XAML (InitializeComponent) et
    // utilisé tel quel par EmulatorsPage (DataContext fixé séparément après
    // instanciation XAML). Ne JAMAIS ajouter de surcharge paramétrée silencieuse
    // ici — un tel constructeur ne serait jamais appelé depuis XAML et tout câblage
    // qui y serait fait (comme l'auto-scroll précédemment) resterait mort sans
    // erreur de compilation, ce qui a causé un bug difficile à diagnostiquer.
    public EmulatorInstallerView() => InitializeComponent();

    /// <summary>
    /// Fait défiler la liste jusqu'à l'émulateur dont le téléchargement vient de
    /// démarrer. Appelée depuis l'extérieur (EmulatorsPage) car c'est là que le
    /// ViewModel est réellement assigné — voir le commentaire du constructeur.
    /// </summary>
    public void ScrollToItem(DemoBase.App.ViewModels.EmulatorInstallItemViewModel item, int attemptsLeft = 5)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (ListScrollViewer is null || EmulatorsList is null) return;

            var container = EmulatorsList.ItemContainerGenerator
                .ContainerFromItem(item) as FrameworkElement;

            if (container is null)
            {
                if (attemptsLeft > 0)
                    ScrollToItem(item, attemptsLeft - 1);
                return;
            }

            try
            {
                var transform = container.TransformToAncestor(ListScrollViewer);
                var topLeft   = transform.Transform(new Point(0, 0));
                var targetY   = ListScrollViewer.VerticalOffset + topLeft.Y;
                var centered  = targetY - (ListScrollViewer.ViewportHeight / 2) + (container.ActualHeight / 2);
                ListScrollViewer.ScrollToVerticalOffset(Math.Max(0, centered));
            }
            catch (InvalidOperationException) { /* agrément visuel uniquement */ }
        }, DispatcherPriority.ContextIdle);
    }
}
