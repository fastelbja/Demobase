using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DemoBase.App;

public partial class SplashWindow : Window
{
    private const int DurationMs = 3000;
    private readonly DispatcherTimer _timer;
    private readonly DateTime        _start;

    public SplashWindow()
    {
        InitializeComponent();
        LoadImage();
        _start = DateTime.Now;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        _timer.Start();
    }

    private void LoadImage()
    {
        try
        {
            // Essai 1 : ressource embarquée
            var uri = new Uri("pack://application:,,,/DemoBase.App;component/Assets/SplashScreen.png");
            SplashImage.Source = new BitmapImage(uri);
        }
        catch
        {
            try
            {
                // Fallback : fichier à côté de l'exe
                var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "SplashScreen.png");
                if (System.IO.File.Exists(path))
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource   = new Uri(path);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    SplashImage.Source = bmp;
                }
            }
            catch { /* image non trouvée, fond noir */ }
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var elapsed = (DateTime.Now - _start).TotalMilliseconds;
        if (elapsed >= DurationMs)
        {
            _timer.Stop();
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
