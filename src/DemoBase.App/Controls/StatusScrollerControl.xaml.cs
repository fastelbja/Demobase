using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DemoBase.App.Controls;

// ─── StatusScrollerControl ────────────────────────────────────────────────────

public partial class StatusScrollerControl : System.Windows.Controls.UserControl
{
    // ── Singleton ─────────────────────────────────────────────────────────────
    public static StatusScrollerControl? Instance { get; set; }

    public static void Post(string message, bool isError = false, bool isWarning = false)
        => Instance?.Dispatcher.InvokeAsync(() =>
               Instance.EnqueueMessage(message, isError, isWarning));

    // ── State ─────────────────────────────────────────────────────────────────
    private string  _text        = "";
    private double  _scrollX     = 0;
    private double  _t           = 0;
    private double  _lastTick    = 0;
    private MsgType _currentType = MsgType.Info;

    private readonly Queue<(string text, MsgType type)> _queue = new();
    private readonly DispatcherTimer _timer;
    private          Typeface        _typeface;
    private const    double          FontSz = 11.5;
    private          double          _dpi   = 1.0;

    private enum MsgType { Info, Error, Warning }

    // ── Construction ──────────────────────────────────────────────────────────

    public StatusScrollerControl()
    {
        InitializeComponent();

        // 2026-07-31 : trouvé en implémentant le retour visible des DATs (retour
        // utilisateur, DatsUpdateService) — Instance n'était JAMAIS assigné nulle
        // part dans tout le code (vérifié par recherche globale), rendant TOUS les
        // appels existants à StatusScrollerControl.Post(...) inopérants depuis
        // toujours (Modland "piste introuvable", playlists "musique(s)
        // introuvable(s)", téléchargements ad-hoc...) — Post() fait juste
        // "Instance?.Dispatcher..." qui ne faisait donc jamais rien, silencieusement,
        // sans erreur. Le contrôle est déclaré une seule fois dans MainWindow.xaml
        // (x:Name="StatusScroller"), qui vit toute la durée de l'appli — assigner
        // Instance ici, dans le constructeur, est donc sûr et suffisant.
        Instance = this;

        _typeface = new Typeface("Courier New");

        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _timer.Tick += OnTick;

        Loaded += (_, _) =>
        {
            _dpi     = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _scrollX = ActualWidth;
            _timer.Start();
        };
        Unloaded          += (_, _) => _timer.Stop();
        IsVisibleChanged  += (_, e) =>
        {
            if ((bool)e.NewValue) _timer.Start(); else _timer.Stop();
        };
    }

    // ── Enfile un message ─────────────────────────────────────────────────────

    public void EnqueueMessage(string text, bool isError = false, bool isWarning = false)
    {
        var type   = isError ? MsgType.Error : isWarning ? MsgType.Warning : MsgType.Info;
        var prefix = type switch
        {
            MsgType.Error   => "  ✗  ",
            MsgType.Warning => "  ⚠  ",
            _               => "  »  ",
        };
        var msg = (prefix + text, type);  // trailing géré par OnTick

        // Erreurs et warnings interrompent immédiatement le message courant
        // Les infos normales s'ajoutent à la file
        if (isError || isWarning)
        {
            _queue.Clear();
            _text        = msg.Item1;
            _currentType = type;
            _scrollX     = ActualWidth;
        }
        else
        {
            _queue.Enqueue(msg);
        }
    }

    // ── Boucle ────────────────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64 / 1000.0;
        if (_lastTick == 0) _lastTick = now;
        double dt  = Math.Min(now - _lastTick, 0.05);
        _lastTick  = now;
        _t        += dt * 60;

        double speed = _currentType == MsgType.Error ? 2.8 : 1.8;
        _scrollX -= speed;

        // Quand le texte est complètement sorti de l'écran (+ largeur écran de marge)
        var textWidth = MeasureText(_text);
        if (_scrollX < -(textWidth + ActualWidth))
        {
            if (_queue.Count > 0)
            {
                var (txt, typ) = _queue.Dequeue();
                _text         = txt;
                _currentType  = typ;
                _scrollX      = ActualWidth;
            }
            else
            {
                // Pas de message suivant — vider sans reboucler
                _text    = "";
                _scrollX = ActualWidth;
            }
        }

        Render();
    }

    // ── Rendu en un seul DrawingContext ───────────────────────────────────────

    private void Render()
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        if (string.IsNullOrEmpty(_text)) return;

        using var dc = VisualHost.RenderOpen();

        double cy      = ActualHeight / 2.0;
        double charW   = FontSz * 0.65;
        int    total   = _text.Length;

        for (int i = 0; i < total; i++)
        {
            double cx = _scrollX + i * charW;
            if (cx < -charW || cx > ActualWidth + charW) continue;

            double sinY = Math.Sin((cx + _t * 2.5) * 0.045) * 5.5
                        + Math.Sin((cx + _t * 1.3) * 0.028) * 2.5;

            char ch = _text[i];

            Brush brush = _currentType switch
            {
                MsgType.Error   => new SolidColorBrush(Color.FromRgb(255, 90, 90)),
                MsgType.Warning => new SolidColorBrush(Color.FromRgb(255, 200, 60)),
                _               => HslBrush((cx * 0.45 + _t * 1.5) % 360),
            };

            var ft = new FormattedText(
                ch.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _typeface, FontSz, brush, _dpi);

            dc.DrawText(ft, new Point(cx, cy + sinY - FontSz / 2.0));
        }
    }

    private double MeasureText(string text)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _typeface, FontSz, Brushes.White, _dpi);
        return ft.Width;
    }

    private static SolidColorBrush HslBrush(double h)
    {
        h = ((h % 360) + 360) % 360;
        const double s = 1.0, l = 0.62;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;
        double r, g, b;
        if      (h < 60)  { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else              { r = c; g = 0; b = x; }
        return new SolidColorBrush(Color.FromRgb(
            (byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255)));
    }
}

// ─── ScrollerVisualHost ───────────────────────────────────────────────────────
// FrameworkElement qui expose un DrawingContext unique — zéro allocation par frame

public class ScrollerVisualHost : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public ScrollerVisualHost()
    {
        AddVisualChild(_visual);
        AddLogicalChild(_visual);
    }

    public DrawingContext RenderOpen() => _visual.RenderOpen();

    protected override int   VisualChildrenCount          => 1;
    protected override Visual GetVisualChild(int index)   => _visual;

    protected override Size MeasureOverride(Size _)  => new(0, 0);
    protected override Size ArrangeOverride(Size sz) => sz;
}
