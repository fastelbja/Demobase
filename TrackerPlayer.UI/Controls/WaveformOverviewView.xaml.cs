using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using TrackerPlayer.Core.Players;

namespace TrackerPlayer.UI.Controls
{
    /// <summary>
    /// 2026-08-07, demande utilisateur : vue d'ensemble complète de la forme
    /// d'onde du morceau en cours, affichée sous l'oscilloscope. Contrairement à
    /// <see cref="OscilloscopeView"/> (60fps, N derniers samples), ce contrôle
    /// affiche une enveloppe min/max à résolution FIXE
    /// (<see cref="WaveformOverviewBuffer.Buckets"/>) sur toute la durée du
    /// morceau, avec un indicateur de position de lecture (playhead).
    ///
    /// Se remplit progressivement pendant la lecture pour les formats à synthèse
    /// temps réel (libopenmpt/UADE/ZXTune/SNDH — cf. WaveformOverviewBuffer côté
    /// player), ou apparaît déjà complète pour les fichiers audio préexistants
    /// (NativeAudioPlayer, décodés intégralement en arrière-plan). N'est jamais
    /// alimenté pour les musiques exécutables (ExeMusicPlayer) — pas de propriété
    /// WaveformOverview exposée pour ce format côté ViewModel, cf. son commentaire
    /// ("pas necessaire pour les musiques executables" — confirmation utilisateur
    /// explicite) : ce contrôle affiche alors simplement une ligne plate.
    ///
    /// Cadence de rafraîchissement volontairement plus légère que l'oscilloscope
    /// (DispatcherTimer ~10fps plutôt que CompositionTarget.Rendering 60fps) — une
    /// enveloppe d'ensemble n'a pas besoin d'être animée à la même fréquence
    /// qu'un oscilloscope temps réel ; ça évite d'ajouter un second contributeur
    /// à la pression GC déjà identifiée sur OscilloscopeView (cf. son commentaire
    /// sur le sous-échantillonnage).
    /// </summary>
    public partial class WaveformOverviewView : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────
        public static readonly DependencyProperty WaveformOverviewProperty =
            DependencyProperty.Register(nameof(WaveformOverview), typeof(WaveformOverviewBuffer),
                typeof(WaveformOverviewView), new PropertyMetadata(null));

        public static readonly DependencyProperty PositionSecondsProperty =
            DependencyProperty.Register(nameof(PositionSeconds), typeof(double),
                typeof(WaveformOverviewView), new PropertyMetadata(0.0));

        public static readonly DependencyProperty DurationSecondsProperty =
            DependencyProperty.Register(nameof(DurationSeconds), typeof(double),
                typeof(WaveformOverviewView), new PropertyMetadata(1.0));

        public static readonly DependencyProperty OscColorProperty =
            DependencyProperty.Register(nameof(OscColor), typeof(Color),
                typeof(WaveformOverviewView),
                new PropertyMetadata(Color.FromRgb(0x00, 0xFF, 0x88), (d, _) => ((WaveformOverviewView)d).RebuildPens()));

        public WaveformOverviewBuffer? WaveformOverview
        {
            get => (WaveformOverviewBuffer?)GetValue(WaveformOverviewProperty);
            set => SetValue(WaveformOverviewProperty, value);
        }

        public double PositionSeconds
        {
            get => (double)GetValue(PositionSecondsProperty);
            set => SetValue(PositionSecondsProperty, value);
        }

        public double DurationSeconds
        {
            get => (double)GetValue(DurationSecondsProperty);
            set => SetValue(DurationSecondsProperty, value);
        }

        public Color OscColor
        {
            get => (Color)GetValue(OscColorProperty);
            set => SetValue(OscColorProperty, value);
        }

        // ── Rendu ──────────────────────────────────────────────────────
        private DrawingVisual?     _visual;
        private DrawingVisualHost? _host;
        private Pen   _penEnvelope = MakePen(Color.FromRgb(0x00, 0xFF, 0x88), 1.0);
        private Pen   _penPlayhead = MakePen(Colors.White, 1.5);
        private Brush _fillBrush   = MakeFill(Color.FromRgb(0x00, 0xFF, 0x88));

        // Snapshots réutilisés à chaque tick — pas de réallocation.
        private readonly float[] _min = new float[WaveformOverviewBuffer.Buckets];
        private readonly float[] _max = new float[WaveformOverviewBuffer.Buckets];

        private DispatcherTimer? _timer;

        public WaveformOverviewView()
        {
            InitializeComponent();
            Loaded   += (_, _) => { BuildVisual(); StartTimer(); };
            Unloaded += (_, _) => StopTimer();
            SizeChanged += (_, _) => BuildVisual();
        }

        private void RebuildPens()
        {
            Color c = OscColor;
            _penEnvelope = MakePen(c, 1.0);
            _fillBrush   = MakeFill(c);
        }

        private static Pen MakePen(Color c, double w)
        {
            var p = new Pen(new SolidColorBrush(c), w);
            p.Freeze();
            return p;
        }

        private static Brush MakeFill(Color c)
        {
            var b = new SolidColorBrush(Color.FromArgb(90, c.R, c.G, c.B));
            b.Freeze();
            return b;
        }

        // ── Visual ────────────────────────────────────────────────────
        private void BuildVisual()
        {
            if (!IsLoaded) return;
            OverviewCanvas.Children.Clear();
            _visual = new DrawingVisual();
            _host   = new DrawingVisualHost(_visual)
            {
                Width  = OverviewCanvas.ActualWidth,
                Height = OverviewCanvas.ActualHeight
            };
            OverviewCanvas.Children.Add(_host);
        }

        // ── Boucle ~10fps (cf. commentaire de classe) ────────────────────
        private void StartTimer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100)
            };
            _timer.Tick += (_, _) => Render();
            _timer.Start();
            Render();
        }

        private void StopTimer()
        {
            _timer?.Stop();
            _timer = null;
        }

        private void Render()
        {
            if (_visual is null) return;

            double w = OverviewCanvas.ActualWidth;
            double h = OverviewCanvas.ActualHeight;
            if (w < 10 || h < 10) return;

            var overview = WaveformOverview;
            double midY = h / 2.0;

            using var dc = _visual.RenderOpen();
            dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

            if (overview is null || !overview.HasData)
            {
                // Rien à afficher encore (pas de piste chargée, ou pas encore de
                // données décodées) — ligne plate, cohérent avec l'oscilloscope
                // au repos.
                dc.DrawLine(_penEnvelope, new Point(0, midY), new Point(w, midY));
                return;
            }

            overview.CopySnapshot(_min, _max);
            int buckets = WaveformOverviewBuffer.Buckets;
            int lastBucket = Math.Min(overview.HighestBucket, buckets - 1);
            double scale = h / 2.0 * 0.9;
            double stepX = w / buckets;

            if (lastBucket >= 0)
            {
                // Polygone fermé : enveloppe max (aller) puis min (retour) —
                // technique standard de rendu de forme d'onde min/max.
                var geom = new StreamGeometry();
                using (var ctx = geom.Open())
                {
                    ctx.BeginFigure(new Point(0, midY - _max[0] * scale), isFilled: true, isClosed: true);
                    for (int i = 1; i <= lastBucket; i++)
                        ctx.LineTo(new Point(i * stepX, midY - _max[i] * scale), isStroked: true, isSmoothJoin: false);
                    for (int i = lastBucket; i >= 0; i--)
                        ctx.LineTo(new Point(i * stepX, midY - _min[i] * scale), isStroked: true, isSmoothJoin: false);
                }
                geom.Freeze();
                dc.DrawGeometry(_fillBrush, _penEnvelope, geom);
            }

            // Remplissage progressif (formats à synthèse temps réel) : ligne plate
            // au-delà du dernier bucket connu, plutôt qu'un vide noir trompeur qui
            // pourrait laisser croire à une erreur.
            if (lastBucket < buckets - 1)
                dc.DrawLine(_penEnvelope, new Point((lastBucket + 1) * stepX, midY), new Point(w, midY));

            // Indicateur de position de lecture.
            double dur = DurationSeconds > 0 ? DurationSeconds : 1;
            double ratio = Math.Clamp(PositionSeconds / dur, 0, 1);
            double px = ratio * w;
            dc.DrawLine(_penPlayhead, new Point(px, 0), new Point(px, h));
        }
    }
}
