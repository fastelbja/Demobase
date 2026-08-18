using DemoBase.App.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DemoBase.App.Views.Releases;

public partial class GraphicsViewerView : UserControl
{
    public GraphicsViewerView() => InitializeComponent();

    private void BtnRealSize_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not GraphicsViewerViewModel vm) return;
        if (vm.CurrentImage == null) return;

        var win = new ImageFullscreenWindow(vm.CurrentImage,
            vm.SelectedEntry?.Name ?? "Image");
        win.Owner = Window.GetWindow(this);
        win.Show();
    }
}

/// <summary>
/// Fenêtre d'affichage image en taille réelle avec scroll et zoom.
/// Fermeture : ESC ou clic sur ✕.
/// </summary>
public class ImageFullscreenWindow : Window
{
    private readonly ScrollViewer _scroll;
    private readonly System.Windows.Controls.Image _img;
    private double _zoomFactor = 1.0;
    private const double ZoomStep = 0.05;

    public ImageFullscreenWindow(BitmapSource bitmap, string title)
    {
        Title         = title;
        Background    = new SolidColorBrush(Color.FromRgb(20, 20, 20));
        WindowState   = WindowState.Maximized;
        ShowInTaskbar = true;
        ResizeMode    = ResizeMode.CanResize;

        // Toolbar : infos + zoom + fermer
        var toolbar = new DockPanel { Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)) };
        toolbar.SetValue(DockPanel.DockProperty, Dock.Top);
        toolbar.Height = 38;

        var lblSize = new TextBlock
        {
            Text              = $"{bitmap.PixelWidth} × {bitmap.PixelHeight} px",
            Foreground        = Brushes.Silver,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(12, 0, 0, 0),
        };
        DockPanel.SetDock(lblSize, Dock.Left);
        toolbar.Children.Add(lblSize);

        var lblZoom = new TextBlock
        {
            Text              = "100%",
            Foreground        = Brushes.Silver,
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(16, 0, 0, 0),
            Name              = "LblZoom",
        };
        DockPanel.SetDock(lblZoom, Dock.Left);
        toolbar.Children.Add(lblZoom);

        var btnClose = new Button
        {
            Content           = "✕",
            Background        = Brushes.Transparent,
            Foreground        = Brushes.Silver,
            BorderThickness   = new Thickness(0),
            FontSize          = 14,
            Width             = 38,
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        DockPanel.SetDock(btnClose, Dock.Right);
        btnClose.Click += (_, _) => Close();
        toolbar.Children.Add(btnClose);

        var btnZoomOut = new Button { Content = "−", Background = Brushes.Transparent,
            Foreground = Brushes.Silver, BorderThickness = new Thickness(0),
            FontSize = 14, Width = 32, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Stretch };
        DockPanel.SetDock(btnZoomOut, Dock.Right);
        btnZoomOut.Click += (_, _) => ApplyZoom(-ZoomStep, lblZoom);
        toolbar.Children.Add(btnZoomOut);

        var btnZoomIn = new Button { Content = "+", Background = Brushes.Transparent,
            Foreground = Brushes.Silver, BorderThickness = new Thickness(0),
            FontSize = 14, Width = 32, Cursor = Cursors.Hand, VerticalAlignment = VerticalAlignment.Stretch };
        DockPanel.SetDock(btnZoomIn, Dock.Right);
        btnZoomIn.Click += (_, _) => ApplyZoom(+ZoomStep, lblZoom);
        toolbar.Children.Add(btnZoomIn);

        var btn1x = new Button { Content = "1:1", Background = Brushes.Transparent,
            Foreground = Brushes.Silver, BorderThickness = new Thickness(0),
            FontSize = 11, Padding = new Thickness(8, 0, 8, 0), Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Stretch };
        DockPanel.SetDock(btn1x, Dock.Right);
        btn1x.Click += (_, _) => { _zoomFactor = 1.0; UpdateZoom(lblZoom); };
        toolbar.Children.Add(btn1x);

        // Image — Stretch.None + LayoutTransform pour un zoom pixel-perfect.
        // LayoutTransform (pas RenderTransform) pour que le ScrollViewer
        // réagisse à la taille zoomée et affiche les bonnes barres de défilement.
        _img = new System.Windows.Controls.Image
        {
            Source              = bitmap,
            Stretch             = Stretch.None,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment   = VerticalAlignment.Center,
            LayoutTransform     = new ScaleTransform(1.0, 1.0),
        };
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.HighQuality);

        // Wrapper Grid : centrage de l'image quand elle est plus petite que le viewport,
        // scroll quand elle est plus grande. MinWidth/MinHeight = taille du viewport.
        var wrapper = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment   = VerticalAlignment.Stretch,
        };
        wrapper.Children.Add(_img);

        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
            Content                       = wrapper,
            Background                    = Brushes.Transparent,
        };

        // Adapter MinWidth/MinHeight du wrapper à la taille visible du ScrollViewer
        // → image centrée quand elle est plus petite, scrollable quand elle est plus grande
        _scroll.SizeChanged += (_, _) =>
        {
            wrapper.MinWidth  = _scroll.ViewportWidth;
            wrapper.MinHeight = _scroll.ViewportHeight;
        };

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        root.Children.Add(toolbar);
        root.Children.Add(_scroll);
        Content = root;

        KeyDown += OnKeyDown;
        // Zoom molette
        _scroll.PreviewMouseWheel += OnMouseWheel;
    }

    private void ApplyZoom(double delta, TextBlock lbl)
    {
        _zoomFactor = Math.Clamp(_zoomFactor + delta, 0.05, 8.0);
        UpdateZoom(lbl);
    }

    private void UpdateZoom(TextBlock lbl)
    {
        if (_img.LayoutTransform is ScaleTransform st)
        {
            st.ScaleX = _zoomFactor;
            st.ScaleY = _zoomFactor;
        }
        lbl.Text = $"{_zoomFactor * 100:F0}%";
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;
        // Trouver le TextBlock du zoom dans la toolbar
        var lbl = FindLblZoom();
        ApplyZoom(e.Delta > 0 ? ZoomStep : -ZoomStep, lbl);
    }

    private TextBlock FindLblZoom()
    {
        // Parcourir la toolbar pour trouver le label zoom
        if (Content is DockPanel dp)
            foreach (var child in dp.Children)
                if (child is DockPanel toolbar)
                    foreach (var c in toolbar.Children)
                        if (c is TextBlock tb && tb.Name == "LblZoom")
                            return tb;
        return new TextBlock(); // fallback
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        var lbl = FindLblZoom();
        switch (e.Key)
        {
            case Key.Escape:       Close(); break;
            case Key.Add:
            case Key.OemPlus:      ApplyZoom(+ZoomStep, lbl); e.Handled = true; break;
            case Key.Subtract:
            case Key.OemMinus:     ApplyZoom(-ZoomStep, lbl); e.Handled = true; break;
            case Key.D1:           _zoomFactor = 1.0; UpdateZoom(lbl); e.Handled = true; break;
            case Key.D2:           _zoomFactor = 2.0; UpdateZoom(lbl); e.Handled = true; break;
        }
    }
}
