using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;

namespace DemoBase.App.Views.Releases;

public partial class LinkRow : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(LinkRow), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty UrlProperty =
        DependencyProperty.Register(nameof(Url), typeof(string), typeof(LinkRow), new PropertyMetadata(string.Empty));

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public string Url   { get => (string)GetValue(UrlProperty);   set => SetValue(UrlProperty, value); }

    public LinkRow() => InitializeComponent();

    private void Hyperlink_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(Url))
            Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
    }
}
