using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using TrackerPlayer.Core.Models;

namespace TrackerPlayer.UI.Controls
{
    public enum TrackerStyle { ProTracker, FastTracker2, ScreamTracker3, ImpulseTracker }

    /// <summary>
    /// Afficheur de pattern tracker.
    ///
    /// MODÈLE DE RENDU :
    ///   - Le canvas a exactement la taille de la vue (pas de scroll vertical).
    ///   - La ligne courante est TOUJOURS dessinée au centre vertical.
    ///   - À chaque changement de ligne, on redessine uniquement le contenu visible
    ///     (N lignes au-dessus + N lignes en dessous de la ligne courante).
    ///   - Les lignes hors du pattern (début / fin) sont affichées vides.
    ///   - Zéro scroll, zéro animation, zéro saut.
    /// </summary>
    public partial class PatternView : UserControl
    {
        // ── Dependency Properties ─────────────────────────────────────
        public static readonly DependencyProperty CurrentVmProperty =
            DependencyProperty.Register(nameof(CurrentVm), typeof(PatternViewModel),
                typeof(PatternView), new PropertyMetadata(null,
                    (d, _) => ((PatternView)d).OnVmChanged()));

        // VU-mètres PT : niveaux par canal (float 0-1), mis à jour par le ViewModel
        public static readonly DependencyProperty ChannelLevelsProperty =
            DependencyProperty.Register(nameof(ChannelLevels), typeof(float[]),
                typeof(PatternView), new PropertyMetadata(null, (d,_) => ((PatternView)d).Render()));
        public float[] ChannelLevels
        {
            get => (float[])GetValue(ChannelLevelsProperty);
            set => SetValue(ChannelLevelsProperty, value);
        }

        public static readonly DependencyProperty HighlightedRowProperty =
            DependencyProperty.Register(nameof(HighlightedRow), typeof(int),
                typeof(PatternView), new PropertyMetadata(0,
                    (d, e) => ((PatternView)d).OnRowChanged((int)e.NewValue)));

        public static readonly DependencyProperty TrackerStyleProperty =
            DependencyProperty.Register(nameof(TrackerStyle), typeof(TrackerStyle),
                typeof(PatternView), new PropertyMetadata(TrackerStyle.ProTracker,
                    (d, _) =>
                    {
                        var pv = (PatternView)d;
                        pv.InvalidateTextCaches(); // colonnes dépendent du style
                        pv.Render();
                    }));

        public PatternViewModel? CurrentVm    { get => (PatternViewModel?)GetValue(CurrentVmProperty);  set => SetValue(CurrentVmProperty, value);  }
        public int               HighlightedRow { get => (int)GetValue(HighlightedRowProperty);         set => SetValue(HighlightedRowProperty, value); }
        public TrackerStyle      TrackerStyle  { get => (TrackerStyle)GetValue(TrackerStyleProperty);   set => SetValue(TrackerStyleProperty, value); }

        // ── Métriques ─────────────────────────────────────────────────
        // Métriques de base (utilisées pour S3M, IT et les styles sans police bitmap)
        private const double ROW_H_BASE    = 16.0;
        private const double ROW_NUM_W_BASE = 32.0;
        private const double HEADER_H_BASE  = 22.0;
        private const double CH_PAD_BASE    = 6.0;
        private const double FS             = 11.0;  // police générique

        // Les polices bitmap PT et FT2 sont rendues à une taille agrandie
        // PT  : ×4 de la taille native  (8pt × 4 = 32pt)
        // FT2 : ×2 de la taille native  (8pt × 2 = 16pt) — demande utilisateur
        private const double BITMAP_SCALE     = 2.0;  // pour PT
        private const double FT2_BITMAP_SCALE = 2.0;  // pour FT2
        private const double PT_FS_NATIVE     = 8.0;
        private const double FT2_FS_NATIVE    = 8.0;

        // Métriques effectives selon le style courant
        private bool   IsBitmapStyle => TrackerStyle is TrackerStyle.ProTracker
                                                     or TrackerStyle.FastTracker2;
        private double BitmapScale   => TrackerStyle == TrackerStyle.ProTracker
                                        ? BITMAP_SCALE : FT2_BITMAP_SCALE;
        // FT2 : ROW_H = hauteur glyphe (16px) + padding vertical (5px) = 21px
        // PT  : ROW_H = ROW_H_BASE * BITMAP_SCALE / 2 (comportement existant)
        private double ROW_H
        {
            get
            {
                if (TrackerStyle == TrackerStyle.FastTracker2)
                    return FT2_FS_NATIVE * FT2_BITMAP_SCALE + 8.0;  // 24px
                return IsBitmapStyle ? ROW_H_BASE * BitmapScale / 2.0 : ROW_H_BASE;
            }
        }
        private double ROW_NUM_W
        {
            get
            {
                double base_w = IsBitmapStyle ? ROW_NUM_W_BASE * BitmapScale / 2.0 : ROW_NUM_W_BASE;
                return TrackerStyle == TrackerStyle.FastTracker2 ? base_w + 10.0 : base_w;
            }
        }
        private double HEADER_H => IsBitmapStyle ? HEADER_H_BASE * BitmapScale / 2.0 : HEADER_H_BASE;
        private double CH_PAD
        {
            get
            {
                double base_pad = IsBitmapStyle ? CH_PAD_BASE * BitmapScale / 2.0 : CH_PAD_BASE;
                // FT2 : espacement uniforme = CH_PAD_BASE*BitmapScale - 3px
                return TrackerStyle == TrackerStyle.FastTracker2 ? base_pad * 2 - 3.0 : base_pad;
            }
        }

        // ── Pinceaux ──────────────────────────────────────────────────
        private static readonly Brush BgNormal    = F(new SolidColorBrush(Color.FromRgb(0x0D,0x0F,0x14)));
        private static readonly Brush BgAlt       = F(new SolidColorBrush(Color.FromRgb(0x13,0x15,0x1C)));
        private static readonly Brush BgBeat      = F(new SolidColorBrush(Color.FromRgb(0x16,0x18,0x22)));
        private static readonly Brush BgHighlight = F(new SolidColorBrush(Color.FromRgb(0x0A,0x28,0x1A)));
        private static readonly Brush BgHeader    = F(new SolidColorBrush(Color.FromRgb(0x15,0x18,0x20)));
        private static readonly Pen   PenHL       = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x00,0xE5,0xA0)){Opacity=0.9}, 1.5));
        private static readonly Pen   PenSep      = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x22,0x26,0x32)), 1));
        private static readonly Pen   PenHdrBot   = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x25,0x2A,0x38)), 1));
        private static readonly Brush TxtDim      = F(new SolidColorBrush(Color.FromRgb(0x22,0x28,0x38)));
        private static readonly Brush TxtHeader   = F(new SolidColorBrush(Color.FromRgb(0x55,0x66,0x88)));

        // ProTracker
        private static readonly Brush PT_Row    = F(new SolidColorBrush(Color.FromRgb(0x44,0x55,0x99)));
        private static readonly Brush PT_RowHL  = F(new SolidColorBrush(Colors.White));
        private static readonly Brush PT_Note   = F(new SolidColorBrush(Color.FromRgb(0x44,0x88,0xFF)));
        private static readonly Brush PT_NoteHL = F(new SolidColorBrush(Colors.White));
        private static readonly Brush PT_Dash   = F(new SolidColorBrush(Color.FromRgb(0x33,0x44,0x77)));
        private static readonly Brush PT_Inst   = F(new SolidColorBrush(Color.FromRgb(0x22,0x66,0xCC)));
        private static readonly Brush PT_Fx     = F(new SolidColorBrush(Color.FromRgb(0x33,0x77,0xEE)));
        private static readonly Brush PT_Dim    = F(new SolidColorBrush(Color.FromRgb(0x1A,0x25,0x55)));
        private static readonly Brush PT_BgHL   = F(new SolidColorBrush(Colors.Black));
        // Texte noir pour la ligne de suivi PT
        private static readonly Brush PT_HlText = F(new SolidColorBrush(Colors.Black));
        private static readonly Pen   PT_PenHL    = FP(new Pen(Brushes.Red, 1.0));
        // Barre de suivi PT : 2px #BBBBBB + fond #888888 + 2px #555555
        private static readonly Brush PT_HlTop  = F(new SolidColorBrush(Color.FromRgb(0xBB,0xBB,0xBB)));
        private static readonly Brush PT_HlMid  = F(new SolidColorBrush(Color.FromRgb(0x88,0x88,0x88)));
        private static readonly Brush PT_HlBot  = F(new SolidColorBrush(Color.FromRgb(0x55,0x55,0x55)));
        // Séparateurs PT : 3 lignes #BBBBBB + #888888 + #555555
        // VU-mètre : brush gradient statique (créé une seule fois)
        private static readonly LinearGradientBrush _vuBrush = CreateVuBrush();
        private static LinearGradientBrush CreateVuBrush()
        {
            var b = new LinearGradientBrush
            {
                StartPoint = new Point(0, 1),
                EndPoint   = new Point(0, 0),
                MappingMode = BrushMappingMode.RelativeToBoundingBox,
            };
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0x00, 0xCC, 0x00), 0.0));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xCC, 0xCC, 0x00), 0.65));
            b.GradientStops.Add(new GradientStop(Color.FromRgb(0xCC, 0x00, 0x00), 1.0));
            b.Freeze();
            return b;
        }
        private static readonly Pen   PT_PenSep1 = FP(new Pen(new SolidColorBrush(Color.FromRgb(0xBB,0xBB,0xBB)), 1.0));
        private static readonly Pen   PT_PenSep2 = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x88,0x88,0x88)), 1.0));
        private static readonly Pen   PT_PenSep3 = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x55,0x55,0x55)), 1.0));

        // FastTracker 2 — couleurs exactes de la palette FT2 originale (Blues, palette 0)
        // PAL_PATTEXT  = #799AFF  → texte normal (notes, effets, numéros)
        // PAL_FORGRND  = #FFFFFF  → texte ligne sélectionnée
        // PAL_BLCKMRK  = #000045  → fond ligne sélectionnée
        // Les "dots" (champs vides) = PAL_PATTEXT très assombri (~25%)
        private static readonly Brush FT_Row     = F(new SolidColorBrush(Color.FromRgb(0x6D,0x96,0xD7)));
        private static readonly Brush FT_RowHL   = F(new SolidColorBrush(Colors.White));
        private static readonly Brush FT_RowBeat = F(new SolidColorBrush(Color.FromRgb(0xFF,0xFF,0xFF)));
        private static readonly Brush FT_Note    = F(new SolidColorBrush(Color.FromRgb(0x6D,0x96,0xD7)));
        private static readonly Brush FT_NoteHL  = F(new SolidColorBrush(Colors.White));
        private static readonly Brush FT_Inst    = F(new SolidColorBrush(Color.FromRgb(0x6D,0x96,0xD7)));
        private static readonly Brush FT_InstHL  = F(new SolidColorBrush(Colors.White));
        private static readonly Brush FT_Dots    = F(new SolidColorBrush(Color.FromRgb(0x1A,0x22,0x40)));
        private static readonly Brush FT_HdrBg   = F(new SolidColorBrush(Color.FromRgb(0x28,0x35,0x39)));
        private static readonly Brush FT_HdrTxt  = F(new SolidColorBrush(Color.FromRgb(0x79,0x9A,0xFF)));
        private static readonly Pen   FT_PenHL   = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x1C,0x30,0x55)), 0.0));

        // ScreamTracker 3
        private static readonly Brush ST_Row    = F(new SolidColorBrush(Color.FromRgb(0xAA,0x88,0x00)));
        private static readonly Brush ST_RowHL  = F(new SolidColorBrush(Color.FromRgb(0xFF,0xEE,0x00)));
        private static readonly Brush ST_RowBeat= F(new SolidColorBrush(Color.FromRgb(0xCC,0xAA,0x00)));
        private static readonly Brush ST_Note   = F(new SolidColorBrush(Color.FromRgb(0xFF,0xDD,0x00)));
        private static readonly Brush ST_NoteHL = F(new SolidColorBrush(Colors.White));
        private static readonly Brush ST_Inst   = F(new SolidColorBrush(Color.FromRgb(0xCC,0xAA,0x00)));
        private static readonly Brush ST_Vol    = F(new SolidColorBrush(Color.FromRgb(0xAA,0x88,0x00)));
        private static readonly Brush ST_Fx     = F(new SolidColorBrush(Color.FromRgb(0xBB,0x99,0x00)));
        private static readonly Brush ST_Dot    = F(new SolidColorBrush(Color.FromRgb(0x33,0x28,0x00)));
        private static readonly Brush ST_HdrBg  = F(new SolidColorBrush(Color.FromRgb(0x8B,0x73,0x00)));
        private static readonly Brush ST_HdrTxt = F(new SolidColorBrush(Color.FromRgb(0xFF,0xEE,0x88)));
        private static readonly Brush ST_BgHL   = F(new SolidColorBrush(Color.FromRgb(0x44,0x38,0x00)));
        private static readonly Pen   ST_PenSep = FP(new Pen(new SolidColorBrush(Color.FromRgb(0x55,0x44,0x00)), 2.0));

        // ImpulseTracker
        private static readonly Brush IT_Row    = F(new SolidColorBrush(Color.FromRgb(0x00,0x88,0x00)));
        private static readonly Brush IT_RowHL  = F(new SolidColorBrush(Color.FromRgb(0x00,0xFF,0x00)));
        private static readonly Brush IT_RowBeat= F(new SolidColorBrush(Color.FromRgb(0x00,0xCC,0x00)));
        private static readonly Brush IT_Note   = F(new SolidColorBrush(Color.FromRgb(0x00,0xFF,0x00)));
        private static readonly Brush IT_NoteHL = F(new SolidColorBrush(Colors.White));
        private static readonly Brush IT_Inst   = F(new SolidColorBrush(Color.FromRgb(0x00,0xCC,0x00)));
        private static readonly Brush IT_Vol    = F(new SolidColorBrush(Color.FromRgb(0x00,0xAA,0x00)));
        private static readonly Brush IT_Fx     = F(new SolidColorBrush(Color.FromRgb(0x00,0xCC,0x00)));
        private static readonly Brush IT_Dot    = F(new SolidColorBrush(Color.FromRgb(0x00,0x33,0x00)));
        private static readonly Brush IT_HdrBg  = F(new SolidColorBrush(Color.FromRgb(0x8B,0x69,0x14)));
        private static readonly Brush IT_HdrTxt = F(new SolidColorBrush(Color.FromRgb(0x00,0xEE,0x00)));
        private static readonly Brush IT_BgHL   = F(new SolidColorBrush(Color.FromRgb(0x4A,0x38,0x0A)));

        private static Brush F(SolidColorBrush b) { b.Freeze(); return b; }
        private static Pen   FP(Pen p)            { p.Freeze(); return p; }

        // ── Typo ──────────────────────────────────────────────────────
        // Police monospace générique (FT2, S3M, IT)
        private static readonly Typeface MonoFace =
            new(new FontFamily("Consolas, Courier New"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal);

        // Police ProTracker originale — chargée depuis la ressource embarquée.
        // Utilise la syntaxe pack URI avec fragment #FamilyName, qui est la méthode
        // correcte pour référencer une font embedded dans un assembly WPF.
        // Fallback sur Consolas si la police n'est pas disponible.
        private static readonly Typeface PTFace       = LoadFace("protracker-fix", "protracker-fix.ttf");
        // FT2 : deux polices bitmap originales extraites de font3BMP et font4BMP
        // FT2SmallFace  (FT2-Small)  : font3BMP 3×7px — numéros de ligne, instruments, effets
        // FT2NotesFace  (FT2-Notes)  : font4BMP 8×7px — notes (C-,C#,D-... avec glyphes composites)
        private static readonly Typeface FT2SmallFace = LoadFace("FT2-Small", "ft2font.ttf");
        private static readonly Typeface FT2NotesFace = LoadFace("FT2-Notes", "ft2font-notes.ttf");

        private static Typeface LoadFace(string familyName, string fileName)
        {
            // Méthode 1 : URI dossier + fragment #FamilyName (méthode WPF recommandée)
            foreach (var asm in new[] { "TrackerPlayer.UI", "TrackerPlayer.WPF" })
            {
                try
                {
                    var folderUri = new Uri(
                        $"pack://application:,,,/{asm};component/Assets/Fonts/#{familyName}");
                    var fam = new FontFamily(folderUri, familyName);
                    var tf  = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                    if (tf.TryGetGlyphTypeface(out _))
                        return tf;
                }
                catch { /* Fallback silencieux */ }

                // Méthode 2 : énumération GetFontFamilies sur le dossier
                try
                {
                    var folderUri = new Uri(
                        $"pack://application:,,,/{asm};component/Assets/Fonts/");
                    foreach (var fam in Fonts.GetFontFamilies(folderUri))
                    {
                        // Comparer le nom de famille (insensible à la casse)
                        var source = fam.Source;
                        if (source.EndsWith(fileName, StringComparison.OrdinalIgnoreCase)
                            || fam.FamilyNames.Values.Any(n =>
                                string.Equals(n, familyName, StringComparison.OrdinalIgnoreCase)))
                        {
                            var tf = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                            if (tf.TryGetGlyphTypeface(out _))
                                return tf;
                        }
                    }
                }
                catch { /* Fallback silencieux */ }
            }

            // Fallback Consolas en cas d'échec (on évite de logger pour ne pas ralentir le rendu)
            return new Typeface(new FontFamily("Consolas"), FontStyles.Normal,
                                FontWeights.Normal, FontStretches.Normal);
        }

        // ── State ─────────────────────────────────────────────────────
        private PatternViewModel?  _vm;
        private int                _currentRow  = -1;
        private int                _renderedRow = -2;   // dernière ligne réellement dessinée
        private bool               _renderPending = false;
        private DrawingVisual?     _bodyVisual;
        private DrawingVisualHost? _canvasHost;
        private DrawingVisual?     _headerVisual;
        private DrawingVisualHost? _headerHost;
        private double             _cachedChannelWidth;
        private double             _cachedCw1;

        public PatternView()
        {
            InitializeComponent();
            Loaded      += (_, _) => { InvalidateTextCaches(); FullRebuild(); StartRenderLoop(); };
            SizeChanged += (_, _) => { InvalidateTextCaches(); FullRebuild(); };
            Unloaded    += (_, _) => StopRenderLoop();
        }

        // Invalide les caches de FormattedText (police ou taille peut avoir changé).
        // Appelé uniquement au chargement et au redimensionnement — PAS à chaque
        // changement de pattern (OnVmChanged), car le cache est indexé par
        // (texte, brush, taille) et reste valide d'un pattern à l'autre : le
        // vocabulaire de notes/effets/numéros revient très souvent (notamment en
        // ProTracker), donc le vider à chaque pattern jetait inutilement des
        // FormattedText déjà calculés, causant une saccade perceptible à chaque
        // changement de pattern (d'où le motif périodique observé).
        // Force un redessin immédiat, indépendamment du mécanisme normal
        // (changement de ligne via OnCompositionRender, ou callback de
        // DependencyProperty). Utile quand la lecture est arrêtée : la ligne
        // ne change plus, donc OnCompositionRender ne redessine jamais, et
        // le binding de ChannelLevels peut ne pas propager à temps non plus.
        public void ForceRender() => Render();

        private void InvalidateTextCaches()
        {
            _ftCache.Clear();
            _ftNotesCache.Clear();
            _cachedChannelWidth = 0;
            _cachedCw1 = 0;
        }

        // ── Boucle de rendu 60fps via CompositionTarget ───────────────
        // Découple complètement le rendu du polling (20fps).
        // Render() n'est appelé que si la ligne a changé, une fois par frame GPU.
        public void StartRenderLoop()
        {
            CompositionTarget.Rendering += OnCompositionRender;
        }

        public void StopRenderLoop()
        {
            CompositionTarget.Rendering -= OnCompositionRender;
        }

        private void OnCompositionRender(object? sender, EventArgs e)
        {
            if (_renderPending && _currentRow != _renderedRow)
            {
                _renderedRow   = _currentRow;
                _renderPending = false;
                Render();
            }
        }

        private void OnVmChanged()
        {
            _vm = CurrentVm;
            _currentRow = _renderedRow = -1;
            _renderPending = false;
            FullRebuild();
        }

        // OnRowChanged : stocke la nouvelle ligne et marque un rendu nécessaire.
        // Le rendu effectif aura lieu au prochain frame GPU (CompositionTarget.Rendering).
        private void OnRowChanged(int row)
        {
            _currentRow    = row;
            _renderPending = true;
        }

        private void PatternScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
            => UpdateHeaderPosition();

        private void UpdateHeaderPosition()
        {
            if (_headerHost is null) return;
            // Le header est dans HeaderCanvas (fixe verticalement).
            // On le décale horizontalement pour suivre le scroll du PatternCanvas.
            Canvas.SetLeft(_headerHost, -PatternScroll.HorizontalOffset);
            Canvas.SetTop (_headerHost, 0);
        }

        // FullRebuild : appelé au chargement, changement de pattern ou redimensionnement.
        // Crée les DrawingVisual une seule fois, fixe les dimensions du canvas.
        private void FullRebuild()
        {
            if (!IsLoaded) return;

            PatternCanvas.Children.Clear();
            _bodyVisual = _headerVisual = null;
            _canvasHost = _headerHost   = null;

            bool isPT = TrackerStyle == TrackerStyle.ProTracker;
            bool isFT = TrackerStyle == TrackerStyle.FastTracker2;
            bool isIT = TrackerStyle == TrackerStyle.ImpulseTracker;
            bool isST = TrackerStyle == TrackerStyle.ScreamTracker3;

            double hdrH   = 0;  // header dans HeaderCanvas séparé
            double viewH  = Math.Max(ActualHeight - (isPT ? 0 : HEADER_H), ROW_H);
            double chW    = ChannelWidth();
            int    ch     = _vm?.Channels ?? 0;
            double rightW = isFT ? ROW_NUM_W : 0;
            double totalW = Math.Max(ActualWidth, ROW_NUM_W + ch * chW + rightW);
            // PT : ligne de suivi à 25% du haut → plus de lignes futures sous la barre
            int halfRowsAbove = isPT ? (int)Math.Ceiling(viewH * 0.5 / ROW_H) : (int)Math.Ceiling(viewH / 2.0 / ROW_H);
            int halfRowsBelow = isPT ? (int)Math.Ceiling(viewH * 0.75 / ROW_H) : halfRowsAbove;
            int halfRows = halfRowsAbove;
            double canvasH  = (halfRowsAbove + halfRowsBelow + 1) * ROW_H + hdrH;

            PatternCanvas.Width  = totalW;
            PatternCanvas.Height = canvasH;

            // Décale le ScrollViewer vers le bas pour libérer la zone du header
            PatternScroll.Margin = new Thickness(0, isPT ? 0 : HEADER_H, 0, 0);

            // Body visual — réutilisé à chaque tick via RenderOpen()
            _bodyVisual = new DrawingVisual();
            _canvasHost = new DrawingVisualHost(_bodyVisual) { Width = totalW, Height = canvasH };
            PatternCanvas.Children.Insert(0, _canvasHost);

            // Header visual — dans HeaderCanvas (fixe, hors du ScrollViewer)
            // Il ne scrolle PAS verticalement, mais suit le scroll horizontal
            if (!isPT)
            {
                HeaderCanvas.Width  = ActualWidth;
                HeaderCanvas.Height = HEADER_H;
                _headerVisual = new DrawingVisual();
                _headerHost   = new DrawingVisualHost(_headerVisual) { Width = totalW, Height = HEADER_H };
                HeaderCanvas.Children.Clear();
                HeaderCanvas.Children.Add(_headerHost);
                RenderHeader(totalW, isFT, isIT, isST, ch, chW);
            }
            else
            {
                HeaderCanvas.Width  = 0;
                HeaderCanvas.Height = 0;
                HeaderCanvas.Children.Clear();
            }

            Render();
        }

        // Render : redessine le contenu du body IN-PLACE via RenderOpen.
        // Le canvas et ses enfants ne changent PAS — WPF ne voit aucun
        // changement de layout, donc aucun flash ni saut.
        private void Render()
        {
            if (!IsLoaded || _bodyVisual is null) return;

            bool isPT = TrackerStyle == TrackerStyle.ProTracker;
            bool isFT = TrackerStyle == TrackerStyle.FastTracker2;
            bool isIT = TrackerStyle == TrackerStyle.ImpulseTracker;
            bool isST = TrackerStyle == TrackerStyle.ScreamTracker3;

            double hdrH    = 0;  // le header est dans HeaderCanvas, pas dans le body canvas
            double viewH   = Math.Max(ActualHeight - (isPT ? 0 : HEADER_H), ROW_H);
            if (_cachedChannelWidth == 0) _cachedChannelWidth = ChannelWidth();
            double chW     = _cachedChannelWidth;
            int    ch      = _vm?.Channels ?? 0;
            double rightW  = isFT ? ROW_NUM_W : 0;
            // 2026-07-31, retour utilisateur ("il faut limiter la longueur de la barre
            // grise protracker aux nombres de canaux effectifs (ici 4)") : totalW peut
            // dépasser largement la largeur réellement occupée par les canaux quand le
            // contrôle est plus large que le contenu (Math.Max avec ActualWidth, pensé
            // pour que le fond noir remplisse toute la zone visible même sur un module à
            // peu de canaux) — mais utiliser cette même largeur "étirée" pour la barre de
            // suivi PT (précédent correctif, "coupé au 4eme channel") l'étire alors AU-DELÀ
            // des canaux réels dès que le contrôle est plus large qu'eux. contentW ci-
            // dessous est la largeur "juste" (nombre de canaux réel), utilisée uniquement
            // pour cette barre — totalW reste inchangé pour le fond noir/les autres styles.
            double contentW = ROW_NUM_W + ch * chW + rightW;
            double totalW  = Math.Max(ActualWidth, contentW);

            // PT : halfRowsAbove=25% haut, halfRowsBelow=75% bas
            int halfRowsAbove_r = isPT ? (int)Math.Ceiling(viewH * 0.25 / ROW_H) : (int)Math.Ceiling(viewH / 2.0 / ROW_H);
            int halfRowsBelow_r = isPT ? (int)Math.Ceiling(viewH * 0.75 / ROW_H) : halfRowsAbove_r;
            double centerY  = hdrH + halfRowsAbove_r * ROW_H;
            double canvasH  = (halfRowsAbove_r + halfRowsBelow_r + 1) * ROW_H;

            if (_cachedCw1 == 0) _cachedCw1 = MakeText("0", PT_Note, CurFS).Width;
            double cw1 = _cachedCw1;

            // ── Canaux visibles seulement (optimisation critique pour les modules à
            // nombreux canaux, ex. XM 64 canaux) ──────────────────────────────────
            // Sans ce filtre, Render() appelle DrawCell() pour TOUS les canaux même
            // ceux scrollés hors-vue, ce qui peut représenter des milliers d'appels
            // FormattedText par frame pour un XM 64 canaux et geler l'interface.
            double scrollLeft  = PatternScroll.HorizontalOffset;
            double viewportW   = PatternScroll.ViewportWidth > 0 ? PatternScroll.ViewportWidth : ActualWidth;
            double channelAreaLeft = scrollLeft - ROW_NUM_W;
            int firstVisibleCh = ch > 0 ? Math.Max(0, (int)Math.Floor(channelAreaLeft / chW)) : 0;
            int lastVisibleCh  = ch > 0 ? Math.Min(ch - 1, (int)Math.Ceiling((channelAreaLeft + viewportW) / chW)) : -1;

            using var dc = _bodyVisual.RenderOpen();

            dc.DrawRectangle(
                (isPT || isFT || isIT || isST) ? Brushes.Black : BgNormal,
                null, new Rect(0, hdrH, totalW, canvasH));

            for (int i = -halfRowsAbove_r; i <= halfRowsBelow_r; i++)
            {
                int    patRow = _currentRow + i;
                double y      = centerY + i * ROW_H;
                bool   isHL   = (i == 0);
                bool   empty  = _vm is null || patRow < 0 || patRow >= _vm.Rows;

                DrawRowBackground(dc, isHL, empty, patRow, y, totalW, contentW, isPT, isFT, isIT, isST);
                DrawRowNumber(dc, isHL, empty, patRow, y, isPT, isFT, isIT, isST);

                if (isPT)
                {
                    dc.DrawLine(PT_PenSep1, new Point(ROW_NUM_W-1, 0), new Point(ROW_NUM_W-1, canvasH));
                    dc.DrawLine(PT_PenSep2, new Point(ROW_NUM_W,   0), new Point(ROW_NUM_W,   canvasH));
                    dc.DrawLine(PT_PenSep3, new Point(ROW_NUM_W+1, 0), new Point(ROW_NUM_W+1, canvasH));
                }
                else if (!isFT && !isIT)
                    dc.DrawLine(PenSep, new Point(ROW_NUM_W-1, y), new Point(ROW_NUM_W-1, y+ROW_H));

                if (!empty && _vm is not null)
                {
                    // Itère uniquement les canaux visibles dans le viewport horizontal.
                    // Les canaux à gauche/droite du scroll sont sautés entièrement —
                    // DrawCell() n'est jamais appelé pour eux.
                    for (int c = firstVisibleCh; c <= lastVisibleCh; c++)
                    {
                        double x = ROW_NUM_W + c * chW;
                        DrawCell(dc, _vm.GetCell(patRow, c), x, y, chW, isHL, cw1,
                                 isPT, isFT, isIT, isST);

                        if (isPT)
                        {
                            dc.DrawLine(PT_PenSep1, new Point(x+chW-1, 0), new Point(x+chW-1, canvasH));
                            dc.DrawLine(PT_PenSep2, new Point(x+chW,   0), new Point(x+chW,   canvasH));
                            dc.DrawLine(PT_PenSep3, new Point(x+chW+1, 0), new Point(x+chW+1, canvasH));
                        }
                        else if (isST)           dc.DrawLine(ST_PenSep, new Point(x+chW-1,y), new Point(x+chW-1,y+ROW_H));
                        else if (!isFT && !isIT) dc.DrawLine(PenSep,    new Point(x+chW-1,y), new Point(x+chW-1,y+ROW_H));
                    }
                    if (isFT)
                    {
                        double x = ROW_NUM_W + ch * chW;
                        string rs = patRow.ToString("X2");
                        Brush  rb = isHL ? FT_RowHL : (patRow%4==0 ? FT_RowBeat : FT_Row);
                        var rft = MakeNotesText(rs, rb);
                        dc.DrawText(rft, new Point(x+4, y+(ROW_H-rft.Height)/2));
                    }
                }
            }

            // ── VU-mètres PT : un par canal visible seulement ──
            if (isPT && ChannelLevels is { } lvls2)
            {
                for (int c2 = firstVisibleCh; c2 <= lastVisibleCh && c2 < lvls2.Length; c2++)
                {
                    float vuLevel = lvls2[c2];
                    double xVu  = ROW_NUM_W + c2 * chW;
                    double vuX  = xVu + CH_PAD + cw1 * 3;
                    DrawPTVuMeter(dc, vuLevel, vuX, 0, cw1, centerY + ROW_H);
                }
            }
        }

        // ── VU-mètre ProTracker ──────────────────────────────────────────────────
        // Dessiné dans l'espace du tiret "-" entre note et instrument.
        // level : 0.0-1.0, x/y : position, w/h : dimensions disponibles
        private static void DrawPTVuMeter(DrawingContext dc, float level, double x, double y, double w, double h)
        {
            if (level <= 0.001f) return;

            // Le gradient est FIXE sur toute la hauteur (vert bas → rouge haut).
            // La barre monte depuis le bas selon le niveau.
            // Pour afficher la partie basse du gradient : on dessine la barre pleine hauteur
            // puis on clippe en haut selon le niveau.
            double barH = Math.Max(1.0, h * level);
            double barY = y + h - barH + 1.0;  // +1px vers le bas

            // CORRECTIF (nettoyage allocation morte) : ce code construisait ici un
            // LinearGradientBrush + GradientStopCollection + 3 GradientStop À CHAQUE
            // appel (donc par canal, à chaque redessin du pattern) pour ne jamais
            // s'en servir — le dessin ci-dessous utilise en réalité le brush statique
            // _vuBrush (déjà créé une seule fois et freezé, cf. CreateVuBrush()).
            // Allocation pure perte, retirée.

            // Clip géométrique : on dessine le rectangle plein h avec le gradient fixe,
            // puis on clipe pour ne montrer que la portion basse (niveau)
            dc.PushClip(new RectangleGeometry(new Rect(x, barY, w, barH)));

            // Rectangle plein couvrant toute la hauteur pour que le gradient soit fixe
            dc.DrawRectangle(_vuBrush, null, new Rect(x, y + 1.0, w, h));

            dc.Pop();  // fin du clip
        }

        private static Color LerpColor(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromRgb(
                (byte)(a.R + (b.R - a.R) * t),
                (byte)(a.G + (b.G - a.G) * t),
                (byte)(a.B + (b.B - a.B) * t));
        }

        private void RenderHeader(double totalW, bool isFT, bool isIT, bool isST, int ch, double chW)
        {
            if (_headerVisual is null) return;
            using var dc = _headerVisual.RenderOpen();

            Brush hdrBg  = isFT ? FT_HdrBg : (isIT ? IT_HdrBg : (isST ? ST_HdrBg : BgHeader));
            Brush hdrTxt = isFT ? FT_HdrTxt : (isIT ? IT_HdrTxt : (isST ? ST_HdrTxt : TxtHeader));

            dc.DrawRectangle(hdrBg, null, new Rect(0, 0, totalW, HEADER_H));
            dc.DrawLine(PenHdrBot, new Point(0, HEADER_H-0.5), new Point(totalW, HEADER_H-0.5));
            dc.DrawRectangle(hdrBg, null, new Rect(0, 0, ROW_NUM_W, HEADER_H));

            double x = ROW_NUM_W;
            for (int c = 0; c < ch; c++)
            {
                if (!isFT && !isIT)
                    dc.DrawLine(isST ? ST_PenSep : PenSep, new Point(x,0), new Point(x,HEADER_H));
                string side  = (c%2==0) ? "L" : "R";
                string label = isFT ? (c+1).ToString()
                             : isIT ? (c+1).ToString("D2")
                             : isST ? $"{c+1:D2}: {side}{c/2+1}"
                             : $"CH {c+1:D2}";
                var ft = MakeText(label, hdrTxt, 10.0);
                dc.DrawText(ft, new Point(x+(chW-ft.Width)/2, (HEADER_H-ft.Height)/2));
                x += chW;
            }
            if (isFT) dc.DrawRectangle(FT_HdrBg, null, new Rect(x, 0, ROW_NUM_W, HEADER_H));
            UpdateHeaderPosition();
        }

        // ── Fond de ligne ─────────────────────────────────────────────
        private void DrawRowBackground(DrawingContext dc, bool isHL, bool empty,
            int r, double y, double totalW, double contentW,
            bool isPT, bool isFT, bool isIT, bool isST)
        {
            if (empty) return; // fond global déjà noir

            bool beat = r % 16 == 0;
            bool half = r % 8  == 0;

            if (isHL)
            {
                if (isPT)
                {
                    // 2026-07-31, retour utilisateur (capture d'écran d'un MOD 8 canaux,
                    // "Back To The Reality") : "il faudrait agrandir la ligne de suivi de
                    // pattern qui est ici coupé au 4eme channel. il faut l'allonger par le
                    // nombre de channels de la musique" — largeur codée en dur à 4 canaux
                    // (héritage du ProTracker DOS original, réellement limité à 4 pistes),
                    // ne suivait pas le nombre réel de canaux du module. Premier correctif :
                    // remplacé par `totalW`.
                    // 2026-07-31 (suite, capture d'écran "Apollo 404", un vrai MOD 4 canaux
                    // M.K. cette fois) : "il faut limiter la longueur de la barre grise
                    // protracker aux nombres de canaux effectifs (ici 4)" — `totalW` vaut
                    // `Math.Max(ActualWidth, contentW)` (cf. Render()), donc s'étire jusqu'à
                    // la largeur du CONTRÔLE dès qu'il est plus large que le contenu réel —
                    // correct pour le fond noir (qui doit remplir toute la zone visible),
                    // mais ça étirait alors la barre PT bien au-delà des 4 canaux réels d'un
                    // module qui en a peu, dans l'autre sens que le bug précédent. `contentW`
                    // (nouveau paramètre, largeur "juste" = ROW_NUM_W + ch*chW+rightW SANS le
                    // Math.Max) est la bonne largeur pour cette barre spécifiquement.
                    double ptBarW = contentW;
                    y += 3;  // décalage barre PT
                    // Fond #888888
                    dc.DrawRectangle(PT_HlMid, null, new Rect(0, y, ptBarW, ROW_H));
                    // 2px en haut #BBBBBB
                    dc.DrawRectangle(PT_HlTop, null, new Rect(0, y, ptBarW, 2.0));
                    // 2px en bas #555555
                    dc.DrawRectangle(PT_HlBot, null, new Rect(0, y + ROW_H - 2.0, ptBarW, 2.0));
                }
                else if (isFT)
                {
                    // Fond barre de suivi #1C3055
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x1C,0x30,0x55)), null,
                                     new Rect(0, y, totalW, ROW_H));
                    // Ligne du haut #385DA6 (1px)
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x38,0x5D,0xA6)), null,
                                     new Rect(0, y, totalW, 1.0));
                }
                else if (isIT) dc.DrawRectangle(IT_BgHL, null, new Rect(0, y, totalW, ROW_H));
                else if (isST) dc.DrawRectangle(ST_BgHL, null, new Rect(0, y, totalW, ROW_H));
                else
                {
                    dc.DrawRectangle(BgHighlight, null, new Rect(0, y, totalW, ROW_H));
                    dc.DrawLine(PenHL, new Point(0, y+0.5), new Point(totalW, y+0.5));
                }
            }
            else
            {
                Brush bg;
                if (isPT || isFT)  bg = Brushes.Black;
                else if (isIT)     bg = beat ? new SolidColorBrush(Color.FromRgb(0x0A,0x08,0x00)) : Brushes.Black;
                else if (isST)     bg = beat ? new SolidColorBrush(Color.FromRgb(0x20,0x18,0x00)) : Brushes.Black;
                else               bg = beat ? BgBeat : (half ? BgAlt : BgNormal);
                if (bg != Brushes.Black) dc.DrawRectangle(bg, null, new Rect(0, y, totalW, ROW_H));
            }
        }

        // ── Numéro de ligne ───────────────────────────────────────────
        private void DrawRowNumber(DrawingContext dc, bool isHL, bool empty,
            int r, double y, bool isPT, bool isFT, bool isIT, bool isST)
        {
            if (empty) return;

            bool  beat = r % 16 == 0;
            string rowStr;
            Brush  rowBrush;

            if (isFT)
            {
                // FT2 : row numbers via font4BMP (FT2-Notes), 2 chars hex, alignés à droite
                // FT2 met le numéro en blanc (FT_RowBeat) tous les 4 lignes (config.ptnLineLight)
                rowStr    = r.ToString("X2");
                bool lightRow = (r % 4 == 0);
                rowBrush  = isHL ? FT_RowHL : (lightRow ? FT_RowBeat : FT_Row);
                var ft2 = MakeNotesText(rowStr, rowBrush);
                dc.DrawText(ft2, new Point(ROW_NUM_W - ft2.Width - 2, y + (ROW_H - ft2.Height) / 2));
                return;
            }
            else if (isPT) { rowStr = r.ToString("D2"); rowBrush = isHL ? PT_HlText : PT_Row; }
            else if (isIT) { rowStr = r.ToString("X3"); rowBrush = isHL ? IT_RowHL : (beat ? IT_RowBeat : IT_Row); }
            else if (isST) { rowStr = r.ToString("D2"); rowBrush = isHL ? ST_RowHL : (beat ? ST_RowBeat : ST_Row); }
            else           { rowStr = r.ToString("X2"); rowBrush = isHL ? GetNoteColor() : GetRowColor(); }

            var ft = MakeText(rowStr, rowBrush, CurFS);
            dc.DrawText(ft, new Point(ROW_NUM_W - ft.Width - 4, y + (ROW_H - ft.Height) / 2));
        }

        // ── Header ────────────────────────────────────────────────────
        private void DrawHeader(DrawingContext? dc, double totalW, double hdrH,
            bool isFT, bool isIT, bool isST, int ch, double chW)
        {
            if (_vm is null && ch == 0) return;

            var dv = new DrawingVisual();
            using (var hdc = dv.RenderOpen())
            {
                Brush hdrBg  = isFT ? FT_HdrBg : (isIT ? IT_HdrBg : (isST ? ST_HdrBg : BgHeader));
                Brush hdrTxt = isFT ? FT_HdrTxt : (isIT ? IT_HdrTxt : (isST ? ST_HdrTxt : TxtHeader));

                hdc.DrawRectangle(hdrBg, null, new Rect(0, 0, totalW, hdrH));
                hdc.DrawLine(PenHdrBot, new Point(0, hdrH-0.5), new Point(totalW, hdrH-0.5));
                hdc.DrawRectangle(hdrBg, null, new Rect(0, 0, ROW_NUM_W, hdrH));

                double x = ROW_NUM_W;
                for (int c = 0; c < ch; c++)
                {
                    if (!isFT && !isIT)
                        dc?.DrawLine(isST ? ST_PenSep : PenSep, new Point(x,0), new Point(x,hdrH));
                    else
                        hdc.DrawLine(isST ? ST_PenSep : PenSep, new Point(x,0), new Point(x,hdrH));

                    string side  = (c % 2 == 0) ? "L" : "R";
                    string label = isFT ? (c+1).ToString()
                                 : isIT ? (c+1).ToString("D2")
                                 : isST ? $"{c+1:D2}: {side}{c/2+1}"
                                 : $"CH {c+1:D2}";
                    var ft = MakeText(label, hdrTxt, 10.0);
                    hdc.DrawText(ft, new Point(x+(chW-ft.Width)/2, (hdrH-ft.Height)/2));
                    x += chW;
                }
                if (isFT) hdc.DrawRectangle(FT_HdrBg, null, new Rect(x, 0, ROW_NUM_W, hdrH));
            }

            _headerHost = new DrawingVisualHost(dv) { Width = totalW, Height = hdrH };
            PatternCanvas.Children.Add(_headerHost);
            UpdateHeaderPosition();
        }

        // ── Cellule ───────────────────────────────────────────────────
        private void DrawCell(DrawingContext dc, PatternCell cell,
            double x, double y, double chW, bool hl, double cw1,
            bool isPT, bool isFT, bool isIT, bool isST)
        {
            double cx = x + CH_PAD;

            // Si libopenmpt a fourni la string brute, on l'utilise directement.
            // C'est la représentation EXACTE du tracker d'origine.
            if (!string.IsNullOrEmpty(cell.RawString))
            {
                DrawRawCellString(dc, cell.RawString, cx, y, chW, hl, cw1,
                                  isPT, isFT, isIT, isST);
                return;
            }
            bool hasNote  = cell.Note > 0;
            bool hasInstr = cell.Instrument > 0;
            bool hasVol   = cell.Volume >= 0;
            bool hasFx    = cell.Effect > 0 || cell.EffectParam > 0;

            if (isPT)
            {
                Brush nb = hl ? PT_HlText : (hasNote  ? PT_Note : PT_Dim);
                Brush db = hl ? PT_HlText : PT_Dash;
                Brush ib = hl ? PT_HlText : (hasInstr ? PT_Inst : PT_Dim);
                Brush fb = hl ? PT_HlText : (hasFx    ? PT_Fx   : PT_Dim);
                dt(dc, hasNote  ? cell.NoteString : "---",                   nb, cx, y); cx += cw1*3;
                dt(dc, "-",                                                   db, cx, y); cx += cw1;
                dt(dc, hasInstr ? cell.Instrument.ToString("X2") : "00",     ib, cx, y); cx += cw1*2;
                dt(dc, hasFx    ? $"{cell.Effect:X1}{cell.EffectParam:X2}" : "000", fb, cx, y);
            }
            else if (isFT)
            {
                // FT2 mode >6 channels : Note(font7) + Instr(1-2hex, font3) + Efx(3hex, font3)
                // La zone note est FIXE (comme dans FT2 original) pour aligner instr et effet.
                Brush nb = hl ? FT_NoteHL : (hasNote  ? FT_Note : FT_Dots);
                Brush ib = hl ? FT_InstHL : (hasInstr ? FT_Inst : FT_Dots);
                // Effet : toujours en bleu (FT_Note), même quand vide (000)
                Brush fb = hl ? FT_NoteHL : FT_Note;

                // Zone note FIXE = advance du glyphe composite = 32px
                // Note pleine : composite lettre+alt (font7) + octave (font3)
                // Note vide   : glyphe U+E010 = 6 points (glyphes 18+19+20 font7)
                // L'octave s'affiche directement après le composite, sans la zone fixe
                // mais noteZoneW garantit l'alignement de l'instrument en dessous.
                var emptyNoteText = MakeNotesText("", FT_Dots);
                // noteZoneW = ch1(6px) + ch2(6px) + ch3(6px, offset 10) = 16px bitmap
                // En WPF: composite(32px) - overlap(4.6px) + octave(16px) = 43.4px SANS CH_PAD
                // L'instr vient directement après l'octave, sans gap supplémentaire
                const double FT2_OCT_OVERLAP = 4.6;
                var sampleOct = MakeNotesText("4", FT_Note);
                var sampleCmp = MakeNotesText("", FT_Note);
                // CH_PAD = FT2_GAP (espacement uniforme note→instr, rownum→note, inter-canaux)
                double noteZoneW = sampleCmp.Width - FT2_OCT_OVERLAP + sampleOct.Width + CH_PAD;

                if (hasNote)
                {
                    // Glyphe composite lettre+alt (font7), puis octave (font7)
                    // L'octave chevauche l'altération de 2px bitmap = ~4.6px (comme drawNoteSmall FT2)
                    string noteComposite = FT2NoteString(cell.NoteString);
                    string octave        = cell.NoteString.Length > 2 ? cell.NoteString.Substring(2) : "";
                    var nft = MakeNotesText(noteComposite, nb);
                    dc.DrawText(nft, new Point(cx, y + (ROW_H - nft.Height) / 2));
                    if (octave.Length > 0)
                    {
                        var oft = MakeNotesText(octave, nb);
                        dc.DrawText(oft, new Point(cx + nft.Width - FT2_OCT_OVERLAP, y + (ROW_H - oft.Height) / 2));
                    }
                }
                else
                {
                    // Note vide : 6 points (U+E010), décalée de 3px à droite pour centrage
                    dc.DrawText(emptyNoteText, new Point(cx + 3.0, y + (ROW_H - emptyNoteText.Height) / 2));
                }
                cx += noteZoneW;  // avance fixe

                // Instrument : nibble haut omis si 0 (FT2 original)
                // Instrument et effet sont collés (pas de CH_PAD entre eux)
                if (hasInstr)
                {
                    int chr1 = cell.Instrument >> 4;
                    int chr2 = cell.Instrument & 0xF;
                    if (chr1 > 0)
                        dt(dc, $"{chr1:X}", ib, cx, y);
                    cx += cw1;
                    dt(dc, $"{chr2:X}", ib, cx, y);
                    cx += cw1;
                }
                else
                {
                    cx += cw1 * 2;  // réserver la place même si vide
                }

                // Effet : 3 chars hex (type + param_hi + param_lo)
                // FT2 affiche toujours les 3 chars (000 si vide)
                dt(dc, $"{cell.Effect:X}{(cell.EffectParam>>4):X}{(cell.EffectParam&0xF):X}", fb, cx, y);
            }
            else if (isIT)
            {
                Brush nb = hl ? IT_NoteHL : (hasNote  ? IT_Note : IT_Dot);
                Brush ib = hl ? IT_NoteHL : (hasInstr ? IT_Inst : IT_Dot);
                Brush vb = hl ? IT_NoteHL : (hasVol   ? IT_Vol  : IT_Dot);
                Brush fb = hl ? IT_NoteHL : (hasFx    ? IT_Fx   : IT_Dot);
                // IT effects: libopenmpt returns 1=A, 2=B... 26=Z
                string fxStr = hasFx && cell.Effect >= 1 && cell.Effect <= 26
                    ? $"{(char)('A' + cell.Effect - 1)}{cell.EffectParam:X2}"
                    : ".";
                dt(dc, hasNote  ? cell.NoteString                : ".",  nb, cx, y); cx += cw1*4;
                dt(dc, hasInstr ? cell.Instrument.ToString("D2") : ".",  ib, cx, y); cx += cw1*3;
                dt(dc, hasVol   ? cell.Volume.ToString("D2")     : ".",  vb, cx, y); cx += cw1*3;
                dt(dc, fxStr,                                             fb, cx, y);
            }
            else // ST et autres
            {
                Brush nb = hl ? ST_NoteHL : (hasNote  ? ST_Note : ST_Dot);
                Brush ib = hl ? ST_NoteHL : (hasInstr ? ST_Inst : ST_Dot);
                Brush vb = hl ? ST_NoteHL : (hasVol   ? ST_Vol  : ST_Dot);
                Brush fb = hl ? ST_NoteHL : (hasFx    ? ST_Fx   : ST_Dot);
                dt(dc, hasNote  ? cell.NoteString                : "...", nb, cx, y); cx += cw1*4;
                dt(dc, hasInstr ? cell.Instrument.ToString("D2") : "00",  ib, cx, y); cx += cw1*3;
                dt(dc, hasVol   ? cell.Volume.ToString("D2")     : "..",  vb, cx, y); cx += cw1*3;
                dt(dc, hasFx    ? $"{cell.Effect:X1}{cell.EffectParam:X2}" : "...", fb, cx, y);
            }
        }


        // ── Rendu chaîne brute libopenmpt ────────────────────────────
        // Dessine la chaîne pré-formatée libopenmpt avec coloration par champ.
        // Format : "G#3 02 964" (MOD) ou "G#3 02 v40 F64" (XM) etc.
        private void DrawRawCellString(DrawingContext dc, string raw,
            double cx, double y, double chW, bool hl, double cw1,
            bool isPT, bool isFT, bool isIT, bool isST)
        {
            // La font FT2-Pattern a des glyphes identiques pour majuscules et minuscules.
            // On force les majuscules partout pour correspondre au rendu original FT2.
            var parts = raw.Trim().ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) return;

            bool emptyNote  = parts[0] is "---" or "..." or "^^" or "~~";
            bool emptyInstr = parts.Length < 2 || parts[1] is "00" or "..";

            Brush noteBrush  = hl ? GetHlColor() : (emptyNote  ? GetDimColor() : GetNoteColor());
            Brush instrBrush = hl ? GetHlColor() : (emptyInstr ? GetDimColor() : GetInstrColor());

            // Token 0 : note
            var ft0 = MakeText(parts[0], noteBrush, CurFS);
            dc.DrawText(ft0, new Point(cx, y + (ROW_H - ft0.Height) / 2));
            cx += cw1 * (parts[0].Length + 1);

            if (parts.Length < 2) return;

            // Token 1 : instrument
            var ft1 = MakeText(parts[1], instrBrush, CurFS);
            dc.DrawText(ft1, new Point(cx, y + (ROW_H - ft1.Height) / 2));
            cx += cw1 * (parts[1].Length + 1);

            // Tokens restants : volume et/ou effet
            for (int i = 2; i < parts.Length; i++)
            {
                bool isEmpty = parts[i].All(c => c == '.' || c == '0');
                Brush b = hl       ? GetHlColor()
                    : isEmpty      ? GetDimColor()
                    : (i == parts.Length - 1) ? GetFxColor()
                    : GetVolColor();
                var ft = MakeText(parts[i], b, CurFS);
                dc.DrawText(ft, new Point(cx, y + (ROW_H - ft.Height) / 2));
                cx += cw1 * (parts[i].Length + 1);
            }
        }

        private Brush GetHlColor() => TrackerStyle switch {
            TrackerStyle.ProTracker     => PT_NoteHL,
            TrackerStyle.FastTracker2   => FT_NoteHL,
            TrackerStyle.ImpulseTracker => IT_NoteHL,
            _                           => ST_NoteHL };

        private Brush GetDimColor() => TrackerStyle switch {
            TrackerStyle.ProTracker     => PT_Dim,
            TrackerStyle.FastTracker2   => FT_Dots,
            TrackerStyle.ImpulseTracker => IT_Dot,
            _                           => ST_Dot };

        // ── DrawText helper ───────────────────────────────────────────
        private Brush GetRowColor()   => TrackerStyle switch {
            TrackerStyle.FastTracker2   => FT_Row,
            TrackerStyle.ScreamTracker3 => ST_Row,
            TrackerStyle.ImpulseTracker => IT_Row,
            _                           => PT_Row  };
        private Brush GetNoteColor()  => TrackerStyle switch {
            TrackerStyle.FastTracker2   => FT_Note,
            TrackerStyle.ScreamTracker3 => ST_Note,
            TrackerStyle.ImpulseTracker => IT_Note,
            _                           => PT_Note };
        private Brush GetInstrColor() => TrackerStyle switch {
            TrackerStyle.ScreamTracker3 => ST_Inst,
            TrackerStyle.ImpulseTracker => IT_Inst,
            _                           => FT_Inst };
        private Brush GetVolColor()   => TrackerStyle switch {
            TrackerStyle.ScreamTracker3 => ST_Vol,
            TrackerStyle.ImpulseTracker => IT_Vol,
            _                           => FT_Note };
        private Brush GetFxColor()    => TrackerStyle switch {
            TrackerStyle.FastTracker2   => FT_Note,
            TrackerStyle.ScreamTracker3 => ST_Fx,
            TrackerStyle.ImpulseTracker => IT_Fx,
            _                           => PT_Fx   };

        // Couleurs header (nécessaires pour DrawHeader)
        private static readonly Brush IT_HdrBg2 = F(new SolidColorBrush(Color.FromRgb(0x8B,0x69,0x14)));
        private static readonly Brush IT_HdrTxt2= F(new SolidColorBrush(Color.FromRgb(0x00,0xEE,0x00)));
        private static readonly Brush ST_HdrBg2 = F(new SolidColorBrush(Color.FromRgb(0x8B,0x73,0x00)));
        private static readonly Brush ST_HdrTxt2= F(new SolidColorBrush(Color.FromRgb(0xFF,0xEE,0x88)));

        // ── Conversion note → glyphe PUA FT2 ─────────────────────────
        // La font FT2-Pattern contient des glyphes composites pour les notes
        // aux codepoints PUA U+E000–U+E00B (C-,C#,D-,D#,E-,F-,F#,G-,G#,A-,A#,B-)
        private static readonly string[] FT2NoteGlyphs =
            ["","","","","","",
             "","","","","",""];
        private static readonly string[] FT2NoteNames =
            ["C-","C#","D-","D#","E-","F-","F#","G-","G#","A-","A#","B-"];

        // Retourne le glyphe PUA pour une note FT2 (ex: "C-4" → "4")
        private static string FT2NoteString(string noteStr)
        {
            if (noteStr.Length < 3) return noteStr;
            var notePart = noteStr.Substring(0, 2);
            var octave   = noteStr.Substring(2);
            var idx = Array.IndexOf(FT2NoteNames, notePart);
            return idx >= 0 ? FT2NoteGlyphs[idx] : noteStr.Substring(0, 2);  // composite lettre+alt seulement, octave séparé
        }

        // ── DrawText helper ───────────────────────────────────────────
        private void dt(DrawingContext dc, string t, Brush b, double x, double y)
        {
            var ft = MakeText(t, b, CurFS);
            dc.DrawText(ft, new Point(x, y + (ROW_H - ft.Height) / 2));
        }

        // Cache des FormattedText pour éviter la recréation à chaque frame
        private readonly Dictionary<(string, int, double), FormattedText> _ftCache = new();

        private FormattedText MakeText(string t, Brush b, double size)
        {
            var key = (t, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b), size);
            if (_ftCache.TryGetValue(key, out var cached)) return cached;
            if (_ftCache.Count > 2048) _ftCache.Clear();  // limite augmentée : 512 trop petit pour les modules 32-64 canaux
            var ft = new FormattedText(t, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                GetFace(), size, b, 96.0);
            _ftCache[key] = ft;
            return ft;
        }

        // Retourne la police appropriée selon le style courant
        // Police pour le texte courant (effets, numéros, instruments)
        private Typeface GetFace() => TrackerStyle switch
        {
            TrackerStyle.ProTracker   => PTFace,
            TrackerStyle.FastTracker2 => FT2SmallFace,
            _                         => MonoFace
        };

        // Police pour les notes FT2 (glyphes composites font4BMP)
        private Typeface GetNotesFace() => FT2NotesFace;

        // FormattedText avec police de notes FT2 (taille native font4BMP)
        private readonly Dictionary<(string, int), FormattedText> _ftNotesCache = new();

        private FormattedText MakeNotesText(string t, Brush b)
        {
            var key = (t, System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(b));
            if (_ftNotesCache.TryGetValue(key, out var cached)) return cached;
            if (_ftNotesCache.Count > 256) _ftNotesCache.Clear();
            var ft = new FormattedText(t, System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, GetNotesFace(), FT2_FS_NATIVE * FT2_BITMAP_SCALE, b, 96.0);
            _ftNotesCache[key] = ft;
            return ft;
        }

        // Taille de police effective selon le style
        private double CurFS => TrackerStyle switch
        {
            TrackerStyle.ProTracker   => PT_FS_NATIVE  * BITMAP_SCALE,
            TrackerStyle.FastTracker2 => FT2_FS_NATIVE * FT2_BITMAP_SCALE,
            _                         => FS
        };

        private double ChannelWidth()
        {
            double cw = MakeText("0", PT_Note, CurFS).Width;
            return TrackerStyle switch
            {
                TrackerStyle.ProTracker     => cw * 9  + CH_PAD * 2 + 4,
                // Note composite (12px) + gap + instr (2 chars) + gap + efx (3 chars)
                TrackerStyle.FastTracker2   => MakeNotesText("", FT_Note).Width + CH_PAD * 2 + cw * (2 + 1 + 3) + CH_PAD * 2 + 4,
                TrackerStyle.ImpulseTracker => cw * 13 + CH_PAD * 2 + 4,
                TrackerStyle.ScreamTracker3 => cw * 13 + CH_PAD * 2 + 4,
                _                           => 120
            };
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    public class PatternViewModel
    {
        private readonly PatternCell[,] _cells;
        public int Rows         { get; }
        public int Channels     { get; }
        /// <summary>Index du pattern dans <see cref="TrackerModule.Patterns"/> — permet de
        /// retrouver sa position dans l'OrderList pour calculer les ghost notes.</summary>
        public int PatternIndex { get; }
        public PatternViewModel(TrackerPattern p)
        { Rows = p.Rows; Channels = p.Channels; _cells = p.Cells; PatternIndex = p.Index; }
        public PatternCell GetCell(int row, int ch) => _cells[row, ch];
    }

    internal sealed class DrawingVisualHost : FrameworkElement
    {
        private readonly DrawingVisual _v;
        public DrawingVisualHost(DrawingVisual v)
        { _v = v; AddVisualChild(v); AddLogicalChild(v); }
        protected override int    VisualChildrenCount   => 1;
        protected override Visual GetVisualChild(int _) => _v;
    }
}
