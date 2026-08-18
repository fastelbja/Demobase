using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrackerPlayer.Core.Players;

namespace TrackerPlayer.UI.Controls
{
    /// <summary>
    /// Oscilloscope stéréo — affiche les waveforms L et R en temps réel.
    ///
    /// RENDU :
    ///   - Canvas divisé en deux bandes horizontales (L en haut, R en bas)
    ///   - Chaque bande affiche la waveform des N derniers samples
    ///   - Rendu via DrawingVisual + CompositionTarget.Rendering (60fps)
    ///   - Couleur selon le style tracker (vert = IT, bleu = PT/FT2, or = S3M)
    /// </summary>
    public partial class OscilloscopeView : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────
        public static readonly DependencyProperty SampleBufferProperty =
            DependencyProperty.Register(nameof(SampleBuffer), typeof(SampleRingBuffer),
                typeof(OscilloscopeView),
                new PropertyMetadata(null, (d, _) => ((OscilloscopeView)d).OnBufferChanged()));

        public static readonly DependencyProperty OscColorProperty =
            DependencyProperty.Register(nameof(OscColor), typeof(Color),
                typeof(OscilloscopeView),
                new PropertyMetadata(Color.FromRgb(0x00, 0xFF, 0x88), (d, _) => ((OscilloscopeView)d).RebuildPens()));

        public SampleRingBuffer? SampleBuffer
        {
            get => (SampleRingBuffer?)GetValue(SampleBufferProperty);
            set => SetValue(SampleBufferProperty, value);
        }

        public Color OscColor
        {
            get => (Color)GetValue(OscColorProperty);
            set => SetValue(OscColorProperty, value);
        }

        // ── Rendu ──────────────────────────────────────────────────────
        private DrawingVisual?     _visual;
        private DrawingVisualHost? _host;
        private Pen   _penWave   = MakePen(Color.FromRgb(0x00, 0xFF, 0x88), 1.0);
        private Pen   _penGrid   = MakePen(Color.FromRgb(0x11, 0x22, 0x11), 1.0);
        private Pen   _penZero   = MakePen(Color.FromRgb(0x22, 0x44, 0x22), 1.0);
        private Brush _bgBrush   = Brushes.Black;
        private Brush _labelBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x66, 0x33));

        private float[] _bufL = Array.Empty<float>();
        private float[] _bufR = Array.Empty<float>();

        // ── Métriques ──────────────────────────────────────────────────
        private const int   SAMPLE_COUNT = 1024;  // samples affichés par canal
        private const double GAP         = 4.0;    // espace entre les deux bandes
        private const double LABEL_W     = 20.0;   // largeur étiquette L/R

        // Cache FormattedText — créés une fois, réutilisés jusqu'à changement de couleur.
        // (Évite 2 FormattedText + 2 Typeface alloués et GC'd 60×/seconde)
        private FormattedText? _ftL;
        private FormattedText? _ftR;

        public OscilloscopeView()
        {
            InitializeComponent();
            _bufL = new float[SAMPLE_COUNT];
            _bufR = new float[SAMPLE_COUNT];
            Loaded   += (_, _) => { BuildVisual(); StartLoop(); };
            Unloaded += (_, _) => StopLoop();
            SizeChanged += (_, _) => BuildVisual();
        }

        private void OnBufferChanged() { /* rien — le loop lit en continu */ }

        private void RebuildPens()
        {
            Color c    = OscColor;
            Color dim  = Color.FromArgb(80,  c.R, c.G, c.B);
            Color grid = Color.FromArgb(40,  c.R, c.G, c.B);
            _penWave    = MakePen(c,    1.2);
            _penZero    = MakePen(dim,  1.0);
            _penGrid    = MakePen(grid, 1.0);
            _labelBrush = new SolidColorBrush(dim);
            ((SolidColorBrush)_labelBrush).Freeze();
            // Invalider le cache FormattedText (couleur a changé)
            _ftL = null;
            _ftR = null;
        }

        private static Pen MakePen(Color c, double w)
        {
            var p = new Pen(new SolidColorBrush(c), w);
            p.Freeze();
            return p;
        }

        // ── Visual ────────────────────────────────────────────────────
        private void BuildVisual()
        {
            if (!IsLoaded) return;
            OscCanvas.Children.Clear();
            _visual = new DrawingVisual();
            _host   = new DrawingVisualHost(_visual)
            {
                Width  = OscCanvas.ActualWidth,
                Height = OscCanvas.ActualHeight
            };
            OscCanvas.Children.Add(_host);
        }

        // ── Boucle 60fps ──────────────────────────────────────────────
        private void StartLoop() => CompositionTarget.Rendering += OnFrame;
        private void StopLoop()  => CompositionTarget.Rendering -= OnFrame;

        private void OnFrame(object? sender, EventArgs e)
        {
            if (_visual is null || SampleBuffer is null) return;

            double w = OscCanvas.ActualWidth;
            double h = OscCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            SampleBuffer.ReadLast(SAMPLE_COUNT, _bufL, _bufR);

            // FormattedText cachés (créés une seule fois par couleur)
            _ftL ??= MakeLabel("L");
            _ftR ??= MakeLabel("R");

            double bandH = (h - GAP) / 2.0;

            using var dc = _visual.RenderOpen();

            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawLine(_penGrid, new Point(0, bandH + GAP / 2), new Point(w, bandH + GAP / 2));

            // StreamGeometry créées et freezées par frame — nécessaire car WPF's render
            // thread accède aux DrawingVisual depuis un thread séparé. Une géométrie non
            // freezée modifiée sur le thread UI pendant la composition corromprait le rendu.
            DrawChannel(dc, _bufL, LABEL_W, 0,          w - LABEL_W, bandH, _ftL);
            DrawChannel(dc, _bufR, LABEL_W, bandH + GAP, w - LABEL_W, bandH, _ftR);
        }

        private FormattedText MakeLabel(string text)
            => new(text,
                System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface("Consolas"), 10, _labelBrush, 96);

        private void DrawChannel(DrawingContext dc, float[] buf,
            double x, double y, double w, double h, FormattedText ft)
        {
            double midY  = y + h / 2.0;
            double scale = h / 2.0 * 0.9;
            double step  = w / (buf.Length - 1);

            dc.DrawLine(_penZero, new Point(x, midY), new Point(x + w, midY));

            double g = scale * 0.5;
            dc.DrawLine(_penGrid, new Point(x, midY - g), new Point(x + w, midY - g));
            dc.DrawLine(_penGrid, new Point(x, midY + g), new Point(x + w, midY + g));

            // Sous-échantillonnage — CORRECTIF pression mémoire/GC : avec
            // SAMPLE_COUNT=1024 fixe, on émettait jusqu'ici 1024 segments de ligne
            // par canal, à CHAQUE frame (60fps), soit jusqu'à 1024×2×60 ≈ 123 000
            // segments/seconde en StreamGeometry neuves — alors que le widget ne
            // fait souvent que quelques centaines de pixels de large : la plupart
            // de ces segments sont sub-pixel (plusieurs points par pixel), donc
            // strictement invisibles à l'écran, et ne servaient qu'à générer de la
            // pression GC continue (dents-de-scie mémoire observées en session,
            // au point de quasi-geler l'appli sur une lecture prolongée). On plafonne
            // maintenant à ~2 points par pixel de largeur réellement affichée — la
            // géométrie reste crée-et-freezée par frame (nécessaire, cf. note
            // thread-safety ci-dessous), mais avec un nombre de segments proportionnel
            // à ce qui peut réellement être vu, pas à SAMPLE_COUNT.
            int maxPoints  = Math.Max(32, (int)(w * 2));
            int stride     = Math.Max(1, buf.Length / maxPoints);
            int lastIndex  = buf.Length - 1;

            // StreamGeometry créée et freezée chaque frame — obligatoire pour la
            // thread-safety avec le render thread WPF (composition en arrière-plan).
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                ctx.BeginFigure(
                    new Point(x, midY - buf[0] * scale),
                    isFilled: false, isClosed: false);
                for (int i = stride; i < lastIndex; i += stride)
                    ctx.LineTo(new Point(x + i * step, midY - buf[i] * scale),
                               isStroked: true, isSmoothJoin: false);
                // Toujours inclure le dernier sample, pour que la courbe atteigne
                // bien le bord droit même quand lastIndex n'est pas un multiple de stride.
                ctx.LineTo(new Point(x + lastIndex * step, midY - buf[lastIndex] * scale),
                           isStroked: true, isSmoothJoin: false);
            }
            geom.Freeze();
            dc.DrawGeometry(null, _penWave, geom);

            dc.DrawText(ft, new Point(2, midY - ft.Height / 2));
        }
    }


}
