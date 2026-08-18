using System.Windows.Controls;

namespace DemoBase.App.Views.Emulators;

public partial class BeebEmSettingsControl : UserControl
{
    public BeebEmSettingsControl()
    {
        InitializeComponent();
        var uid = Guid.NewGuid().ToString("N");
        foreach (var rb in FindVisualChildren<RadioButton>(this))
            rb.GroupName = $"beebModel_{uid}";
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject parent)
        where T : System.Windows.DependencyObject
    {
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var c in FindVisualChildren<T>(child)) yield return c;
        }
    }
}
