using DemoBase.App.ViewModels;
using DemoBase.Data;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DemoBase.App.Views.Media;

public partial class SlideshowWindow : Window
{
    private readonly List<GraphicCardViewModel> _items;
    private readonly int                        _duration; // ms
    private int    _idx;
    private bool   _paused;
    private bool   _skipRequested;
    private bool   _running = true;

    private readonly DispatcherTimer _timer;

    private static readonly HashSet<string> _wpfExts =
        new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".tiff", ".tif" };

    public SlideshowWindow(List<GraphicCardViewModel> items, int startIndex, AppPreferences prefs)
    {
        InitializeComponent();
        _items    = items;
        _idx      = startIndex;
        _duration = Math.Max(1, prefs.SlideshowDurationSeconds) * 1000;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += OnTick;

        KeyDown += OnKeyDown;
        MouseLeftButtonDown  += (_, _) => Skip(1);
        MouseRightButtonDown += (_, _) => Skip(-1);

        Loaded += (_, _) => _ = RunAsync();
    }

    // ── Boucle principale ────────────────────────────────────────────────────

    private async Task RunAsync()
    {
        while (_running)
        {
            var card  = _items[_idx];
            var image = LoadImage(card);

            if (image != null)
            {
                MainImage.Source = image;
                TitleText.Text   = card.Release.Title;
                AuthorText.Text  = card.Release.AuthorNames;
                CounterText.Text = $"{_idx + 1} / {_items.Count}";

                _skipRequested = false;
                ProgressBar.Value = 0;
                _timer.Start();

                var elapsed = 0;
                while (elapsed < _duration && !_skipRequested && _running)
                {
                    await Task.Delay(50);
                    if (!_paused) elapsed += 50;
                }
                _timer.Stop();
            }

            if (!_running) break;
            _idx = (_idx + 1) % _items.Count;
        }
        if (_running) Close();
    }

    private void OnTick(object? s, EventArgs e)
    {
        // mis à jour par RunAsync directement
    }

    // ── Chargement image depuis le cache ─────────────────────────────────────

    private static BitmapImage? LoadImage(GraphicCardViewModel card)
    {
        if (string.IsNullOrEmpty(card.ThumbPath) || !File.Exists(card.ThumbPath))
            return null;
        try
        {
            // Chercher une image full-res dans le même dossier de cache
            var dir     = Path.GetDirectoryName(card.ThumbPath)!;
            var imgPath = Directory.GetFiles(dir)
                .Where(f => _wpfExts.Contains(Path.GetExtension(f)))
                .FirstOrDefault() ?? card.ThumbPath;

            var bytes = File.ReadAllBytes(imgPath);
            var bmp   = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.None;
            bmp.StreamSource  = new System.IO.MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Skip(int direction)
    {
        if (_paused) { _paused = false; PauseIndicator.Visibility = Visibility.Collapsed; }
        _idx = ((_idx + direction - 1) % _items.Count + _items.Count) % _items.Count;
        _skipRequested = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _running = false;
                _skipRequested = true;
                Close();
                break;
            case Key.Space:
                _paused = !_paused;
                PauseIndicator.Visibility = _paused ? Visibility.Visible : Visibility.Collapsed;
                break;
            case Key.Right: case Key.Down:  Skip(1);  break;
            case Key.Left:  case Key.Up:    Skip(-1); break;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _running = false;
        _skipRequested = true;
        _timer.Stop();
        base.OnClosed(e);
    }
}
