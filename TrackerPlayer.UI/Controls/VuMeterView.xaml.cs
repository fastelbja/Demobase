using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace TrackerPlayer.UI.Controls
{
    /// <summary>
    /// VU-mètre style ProTracker — gradient vert→jaune→rouge.
    /// Chaque canal est représenté par une barre verticale.
    ///
    /// RENDU :
    ///   - Barres empilées de bas en haut
    ///   - Gradient : vert (#00CC00) → jaune (#CCCC00) → rouge (#CC0000)
    ///   - Fond sombre (#111111)
    ///   - Mise à jour via SetLevels(float[]) — valeurs 0.0f–1.0f
    ///   - Peak hold : le pic reste affiché 1 seconde puis redescend
    /// </summary>
    public partial class VuMeterView : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────────────
        public static readonly DependencyProperty ChannelCountProperty =
            DependencyProperty.Register(nameof(ChannelCount), typeof(int),
                typeof(VuMeterView), new PropertyMetadata(4, (d, _) => ((VuMeterView)d).RebuildVisuals()));

        public int ChannelCount
        {
            get => (int)GetValue(ChannelCountProperty);
            set => SetValue(ChannelCountProperty, value);
        }

        // ── Constantes visuelles ──────────────────────────────────────────────
        private const double BAR_GAP      = 2.0;   // gap entre barres
        private const double SEG_HEIGHT   = 3.0;   // hauteur d'un segment
        private const double SEG_GAP      = 1.0;   // gap entre segments
        private const double PEAK_WIDTH   = 3.0;   // épaisseur trait peak hold
        private const int    PEAK_HOLD_MS = 1000;  // durée peak hold en ms

        // Gradient PT : vert bas → jaune milieu → rouge haut
        // Seuils en proportion de la hauteur
        private const double YELLOW_THRESH = 0.65;  // 65% = début jaune
        private const double RED_THRESH    = 0.85;  // 85% = début rouge

        private static readonly Color ColGreen  = Color.FromRgb(0x00, 0xCC, 0x00);
        private static readonly Color ColYellow = Color.FromRgb(0xCC, 0xCC, 0x00);
        private static readonly Color ColRed    = Color.FromRgb(0xCC, 0x00, 0x00);
        private static readonly Brush BgBrush   = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11));

        // ── État ──────────────────────────────────────────────────────────────
        private float[] _levels    = Array.Empty<float>();
        private float[] _peaks     = Array.Empty<float>();
        private long[]  _peakTimes = Array.Empty<long>();

        private readonly DrawingVisual       _visual = new();
        private          DrawingVisualHost? _host;

        // ── Init ──────────────────────────────────────────────────────────────
        public VuMeterView()
        {
            InitializeComponent();
            Loaded   += (_, _) => RebuildVisuals();
            SizeChanged += (_, _) => Redraw();
        }

        private void EnsureVisual()
        {
            if (_host is null)
            {
                _host = new DrawingVisualHost(_visual);
                Canvas.SetLeft(_host, 0); Canvas.SetTop(_host, 0);
                RootCanvas.Children.Add(_host);
            }
        }

        private void RebuildVisuals() => RebuildVisuals(Math.Max(1, ChannelCount));

        private void RebuildVisuals(int n)
        {
            n = Math.Max(1, n);
            _levels    = new float[n];
            _peaks     = new float[n];
            _peakTimes = new long[n];
            Redraw();
        }

        // ── API publique ──────────────────────────────────────────────────────
        public void SetLevels(float[] levels)
        {
            if (levels is null) return;
            // Si les tableaux ne sont pas encore initialisés ou taille différente → réinitialiser
            if (_levels.Length != levels.Length)
                RebuildVisuals(levels.Length);
            int n = _levels.Length;
            long now = Environment.TickCount64;
            for (int i = 0; i < n; i++)
            {
                float v = Math.Clamp(levels[i], 0f, 1f);
                _levels[i] = v;
                if (v >= _peaks[i])
                {
                    _peaks[i]     = v;
                    _peakTimes[i] = now;
                }
                else if (now - _peakTimes[i] > PEAK_HOLD_MS)
                {
                    _peaks[i] = Math.Max(_peaks[i] - 0.02f, v);
                }
            }
            Redraw();
        }

        // ── Rendu ─────────────────────────────────────────────────────────────
        private void Redraw()
        {
            EnsureVisual();
            double W = Math.Max(ActualWidth,  RootCanvas.ActualWidth);
            double H = Math.Max(ActualHeight, RootCanvas.ActualHeight);
            if (W <= 0 || H <= 0) return;

            // Utiliser la longueur réelle de _levels pour éviter les IndexOutOfRange
            int n = _levels.Length;
            if (n <= 0) return;

            double barW = (W - BAR_GAP * (n + 1)) / n;
            if (barW < 1) barW = 1;

            using var dc = _visual.RenderOpen();

            // Fond global
            dc.DrawRectangle(BgBrush, null, new Rect(0, 0, W, H));

            for (int i = 0; i < n; i++)
            {
                double x0 = BAR_GAP + i * (barW + BAR_GAP);
                DrawBar(dc, x0, barW, H, _levels[i], _peaks[i]);
            }
        }

        private static void DrawBar(DrawingContext dc, double x, double barW, double H,
                                    float level, float peak)
        {
            // Nombre total de segments qui tiennent dans H
            double stride   = SEG_HEIGHT + SEG_GAP;
            int    totalSeg = (int)(H / stride);
            int    litSeg   = (int)(level * totalSeg);

            for (int s = 0; s < totalSeg; s++)
            {
                double proportion = (double)s / totalSeg;  // 0=bas, 1=haut
                double y = H - (s + 1) * stride + SEG_GAP;

                // Couleur du segment
                Color col = proportion < YELLOW_THRESH ? Lerp(ColGreen, ColYellow,
                                proportion / YELLOW_THRESH) :
                            proportion < RED_THRESH    ? Lerp(ColYellow, ColRed,
                                (proportion - YELLOW_THRESH) / (RED_THRESH - YELLOW_THRESH)) :
                                                         ColRed;

                bool lit = s < litSeg;
                if (!lit) col = DimColor(col, 0.18f);  // segments éteints = très sombre

                dc.DrawRectangle(new SolidColorBrush(col), null,
                                 new Rect(x, y, barW, SEG_HEIGHT));
            }

            // Peak hold — trait blanc/gris clair
            if (peak > 0.01f)
            {
                int peakSeg = (int)(peak * totalSeg);
                double peakY = H - (peakSeg + 1) * stride + SEG_GAP;
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)), null,
                                 new Rect(x, peakY, barW, PEAK_WIDTH));
            }
        }

        private static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private static Color DimColor(Color c, float factor) =>
            Color.FromRgb((byte)(c.R * factor), (byte)(c.G * factor), (byte)(c.B * factor));

        // ── DrawingVisualHost helper ──────────────────────────────────────────
        private sealed class DrawingVisualHost : FrameworkElement
        {
            private readonly DrawingVisual _v;
            public DrawingVisualHost(DrawingVisual v)
            { _v = v; AddVisualChild(v); AddLogicalChild(v); }
            protected override int    VisualChildrenCount   => 1;
            protected override Visual GetVisualChild(int _) => _v;
        }
    }
}
