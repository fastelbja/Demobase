using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DemoBase.App.Controls;

// ─── DemoEffectControl ────────────────────────────────────────────────────────
// UserControl affichant des effets démo rotatifs dans la sidebar.
// Effets pixel → WriteableBitmap  |  Effets vecteur → DrawingVisual

public partial class DemoEffectControl : UserControl
{
    // ── Config ────────────────────────────────────────────────────────────────
    private const int    W                = 200;
    private const int    H                = 180;
    private const double EFFECT_DURATION  = 60.0;  // secondes par effet

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly WriteableBitmap _bmp;
    private readonly int[]           _pixels = new int[W * H];

    // Buffers réutilisés pour les effets "vecteur" (DrawVisual) — fix 2026-07-24.
    // Avant ce fix, OnTick() allouait un NOUVEAU DrawingVisual + un NOUVEAU
    // RenderTargetBitmap(200x180) À CHAQUE TICK (DispatcherTimer à 16ms, donc
    // ~60 fois/seconde) pour la moitié environ des effets (Dot Tunnel, Vector
    // Cube, Lens/Starfield, DNA Helix, Lissajous, Spirograph, Koch, Lorenz,
    // Double Pendulum, Fourier Epicycles, Sierpinski, Clifford, Barnsley Fern,
    // Harmonograph — chacun actif 60s avant rotation). RenderTargetBitmap
    // encapsule une surface D3D non managée : en allouer/abandonner un par
    // frame, EN CONTINU tant que ce contrôle (toujours visible dans la
    // sidebar) est affiché, produit une pression GC massive et constante —
    // c'est très probablement la cause du "ça tourne en boucle, la RAM monte
    // petit à petit" observé alors même qu'aucun test (émulateur/tracker)
    // n'était en cours. Fix : un seul DrawingVisual et un seul
    // RenderTargetBitmap créés ici, réutilisés à chaque tick (RenderOpen() sur
    // le même DrawingVisual remplace son contenu ; Clear() + Render() sur le
    // même RenderTargetBitmap évite toute nouvelle allocation de surface).
    private readonly DrawingVisual       _visualBuffer = new();
    private readonly RenderTargetBitmap  _vectorRtb    =
        new(W, H, 96, 96, PixelFormats.Pbgra32);

    private readonly DispatcherTimer _timer;
    private          double          _t         = 0;
    private          double          _lastTick  = 0;
    private          double          _effectAge = 0;
    private          int             _effectIdx = 0;
#pragma warning disable CS0414
    private          bool            _initialized = false;
#pragma warning restore CS0414

    // ── Effets ────────────────────────────────────────────────────────────────
    private readonly List<DemoEffect> _effects;

    // ── Fire state ────────────────────────────────────────────────────────────
    private byte[]? _fireBuf;

    // ── Stars state ───────────────────────────────────────────────────────────
    private record Star(double X, double Y, double Z);
#pragma warning disable CS0414
    private Star[]? _stars;
#pragma warning restore CS0414

    // ── Bobs state ────────────────────────────────────────────────────────────
    private record Bob(double OX, double OY, double FX, double FY, float Hue);
    private Bob[]? _bobs;

    // ── Metaballs state ───────────────────────────────────────────────────────
    private record MBall(double OX, double OY, double FX, double FY, float Hue);
    private MBall[]? _mballs;

    // ── Shadebobs state ───────────────────────────────────────────────────────
    private record Shbob(double OX, double OY, double FX, double FY, float Hue);
    private Shbob[]? _shbobs;

    // ── Sinus scroller state ──────────────────────────────────────────────────
    private const string ScrollMsg = "  * GREETINGS TO ALL SCENERS * DEMOSCENE FOREVER * KEEP CODING * KEEP MAKING ART * ";
    private double _scrollX = 0;

    // ─────────────────────────────────────────────────────────────────────────

    public DemoEffectControl()
    {
        InitializeComponent();

        _bmp = new WriteableBitmap(W, H, 96, 96, PixelFormats.Bgr32, null);
        EffectImage.Source = _bmp;

        _effects = BuildEffects();
        // Ordre aléatoire à chaque démarrage
        var rng = new Random();
        for (int i = _effects.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (_effects[i], _effects[j]) = (_effects[j], _effects[i]);
        }

        // 2026-07-25 : passage de 16ms (60 fps) à 33ms (~30 fps) — retour utilisateur,
        // "prendre le minimum de CPU". L'animation de chaque effet est basée sur le
        // temps réel écoulé (_t += dt*60 dans OnTick, dt = delta réel en secondes),
        // PAS sur le nombre de ticks — donc diviser la fréquence de rafraîchissement
        // par 2 ne ralentit PAS les effets, ça réduit juste le nombre de fois où le
        // même travail par-pixel (30-40k pixels, plusieurs Sin/Cos/Sqrt chacun) est
        // refait par seconde. Pour un widget décoratif en coin de sidebar, 30 fps est
        // visuellement indissociable de 60 fps mais coûte moitié moins de CPU.
        _timer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _timer.Tick += OnTick;

        // 2026-07-24 : Loaded démarrait le timer 60fps inconditionnellement, sans
        // vérifier la visibilité. Comme le contrôle démarre Visibility="Collapsed"
        // dans MainWindow.xaml (affiché seulement si l'utilisateur active les effets
        // démo), et qu'IsVisibleChanged ne se déclenche que sur une VRAIE transition
        // Collapsed<->Visible, les utilisateurs ayant laissé les effets désactivés
        // se retrouvaient avec la boucle de rendu (DispatcherPriority.Render, 16ms)
        // qui tournait quand même en tâche de fond pendant toute la session — du
        // travail UI thread gaspillé, cause plausible de ralentissements perçus.
        Loaded   += (_, _) => { if (IsVisible) _timer.Start(); _initialized = true; OnLoadedAttachWindowState(); };
        Unloaded += (_, _) => { _timer.Stop(); DetachWindowState(); };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue && !IsHostWindowMinimized()) _timer.Start();
            else                                               _timer.Stop();
        };

        // Clic = effet suivant
        MouseLeftButtonDown += (_, _) => NextEffect();
    }

    // ── Pause quand la fenêtre est réduite ────────────────────────────────────
    // 2026-07-25 : IsVisibleChanged (ci-dessus) ne se déclenche PAS quand on réduit
    // la fenêtre principale dans la barre des tâches — Visibility du contrôle reste
    // "Visible" au sens WPF (c'est un état de fenêtre OS, pas une propriété de
    // l'arbre visuel). Résultat : la boucle de rendu (par-pixel sur ~36 000 px,
    // plusieurs Sin/Cos/Sqrt par pixel pour beaucoup d'effets) continuait à tourner
    // en tâche de fond même fenêtre réduite, sans aucun affichage visible — CPU pur
    // gaspillé. Fix : s'abonner à Window.StateChanged pour arrêter/relancer le timer.
    private Window? _hostWindow;

    private void OnLoadedAttachWindowState()
    {
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow != null)
        {
            _hostWindow.StateChanged += OnHostWindowStateChanged;
            // Cas limite : contrôle chargé alors que la fenêtre est déjà réduite
            // (ex. effets activés dynamiquement pendant que l'appli est en barre
            // des tâches) — le Start() du bloc Loaded ci-dessus vient de tourner
            // sans le savoir, on corrige immédiatement.
            if (IsHostWindowMinimized()) _timer.Stop();
        }
    }

    private void DetachWindowState()
    {
        if (_hostWindow != null)
            _hostWindow.StateChanged -= OnHostWindowStateChanged;
        _hostWindow = null;
    }

    private void OnHostWindowStateChanged(object? sender, EventArgs e)
    {
        if (IsHostWindowMinimized()) _timer.Stop();
        else if (IsVisible)          _timer.Start();
    }

    private bool IsHostWindowMinimized() => _hostWindow?.WindowState == WindowState.Minimized;

    // ─── Boucle principale ────────────────────────────────────────────────────

    private void OnTick(object? sender, EventArgs e)
    {
        var now = Environment.TickCount64 / 1000.0;
        if (_lastTick == 0) _lastTick = now;
        var dt = Math.Min(now - _lastTick, 0.05);
        _lastTick  = now;
        _t         += dt * 60;
        _effectAge += dt;

        if (_effectAge >= EFFECT_DURATION) NextEffect();

        var effect = _effects[_effectIdx];

        // Rendu pixel
        Array.Clear(_pixels, 0, _pixels.Length);
        effect.DrawPixels?.Invoke(_pixels, _t);

        if (effect.DrawPixels != null)
        {
            _bmp.Lock();
            System.Runtime.InteropServices.Marshal.Copy(_pixels, 0, _bmp.BackBuffer, W * H);
            _bmp.AddDirtyRect(new Int32Rect(0, 0, W, H));
            _bmp.Unlock();
        }
        else if (effect.DrawVisual != null)
        {
            using (var dc = _visualBuffer.RenderOpen())
                effect.DrawVisual(dc, _t);

            _vectorRtb.Clear();
            _vectorRtb.Render(_visualBuffer);

            _bmp.Lock();
            _vectorRtb.CopyPixels(new Int32Rect(0, 0, W, H), _bmp.BackBuffer, _bmp.BackBufferStride * H, _bmp.BackBufferStride);
            _bmp.AddDirtyRect(new Int32Rect(0, 0, W, H));
            _bmp.Unlock();
        }

        var remaining = (int)(EFFECT_DURATION - _effectAge);
        EffectLabel.Text = $"{effect.Name}  [{_effectIdx + 1}/{_effects.Count}]  {remaining}s";
    }

    private void NextEffect()
    {
        _effectIdx = (_effectIdx + 1) % _effects.Count;
        _effectAge = 0;
        _t         = 0;
        ResetState();
    }

    private void ResetState()
    {
        _fireBuf    = null;
        _stars      = null;
        _bobs       = null;
        _mballs     = null;
        _shbobs     = null;
        _scrollX    = 0;
        _matrixPos  = null;
        _matrixSpd  = null;
        _matrixChr  = null;
        _wlX = _wlY = _wlVX = _wlVY = null;
        _wlHue      = null;
        _starX = _starY = _starZ = Array.Empty<double>();
        _lx = 0.1; _ly = 0; _lz = 0;
        _lorenzPts.Clear();
        _dp1 = Math.PI*0.6; _dp2 = Math.PI*0.8; _dv1 = 0; _dv2 = 0;
        _dpTrail.Clear();
        // Nouveaux effets
        _rdInit   = false; _rdA = _rdB = _rdA2 = _rdB2 = null;
        _px = _py = _pvx = _pvy = _page = null; _pcol = null;
        _cliffordPts.Clear(); _clx = 0.1; _cly = 0;
        _fernPts.Clear(); _fx = 0; _fy = 0;
    }

    // ─── Helpers couleurs ─────────────────────────────────────────────────────

    private static int Rgb(int r, int g, int b) =>
        (Math.Clamp(r, 0, 255) << 16) | (Math.Clamp(g, 0, 255) << 8) | Math.Clamp(b, 0, 255);

    private static int Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
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
        return Rgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }

    private static void SetPix(int[] buf, int x, int y, int col)
    {
        if (x >= 0 && x < W && y >= 0 && y < H)
            buf[y * W + x] = col;
    }

    private static int GetPix(int[] buf, int x, int y) =>
        buf[((y + H) % H) * W + ((x + W) % W)];

    // ─── Construction des effets ──────────────────────────────────────────────

    private List<DemoEffect> BuildEffects() => new()
    {
        new("COPPER BARS",      DrawCopperBars,   null),
        new("PLASMA",           DrawPlasma,        null),
        new("SINUS SCROLLER",   DrawSinusScroller, null),
        new("STARFIELD 3D",     DrawStarfield,     null),
        new("FIRE",             DrawFire,          null),
        new("ROTOZOOMER",       DrawRotozoomer,    null),
        new("DOT TUNNEL",       null,              DrawDotTunnel),
        new("BOBS",             DrawBobs,          null),
        new("VECTOR CUBE",      null,              DrawVectorCube),
        new("METABALLS",        DrawMetaballs,     null),
        new("COLOR CYCLING",    DrawColorCycling,  null),
        new("RASTER LINES",     DrawRasterLines,   null),
        new("SHADEBOBS",        DrawShadebobs,     null),
        new("LENS / STARFIELD", null,              DrawLensFlare),
        new("TWISTER",          DrawTwister,       null),
        new("MOIRE",            DrawMoire,         null),
        new("TUNNEL TEXTURE",   DrawTunnelTexture, null),
        new("BUMP SPHERE",      DrawBumpSphere,    null),
        new("WATER RIPPLE",     DrawWaterRipple,   null),
        new("VORONOI",          DrawVoronoi,       null),
        new("MATRIX RAIN",      DrawMatrixRain,    null),
        new("KALEIDOSCOPE",     DrawKaleidoscope,  null),
        new("AURORA",           DrawAurora,        null),
        new("HYPNO SPIRAL",     DrawHypnoSpiral,   null),
        new("DNA HELIX",        null,              DrawDnaHelix),
        new("LISSAJOUS 3D",     null,              DrawLissajous),
        new("SPIROGRAPH",       null,              DrawSpirograph),
        new("KOCH SNOWFLAKE",   null,              DrawKoch),
        new("LORENZ ATTRACTOR", null,              DrawLorenz),
        new("DOUBLE PENDULUM",  null,              DrawDoublePendulum),
        new("WANDERING LINES",   DrawWanderingLines, null),
        new("JULIA SET",          DrawJuliaSet,       null),
        new("REACTION DIFFUSION", DrawReactionDiff,   null),
        new("FOURIER EPICYCLES",  null,               DrawFourierEpicycles),
        new("SIERPINSKI",         null,               DrawSierpinski),
        new("PARTICLE FOUNTAIN",  DrawParticles,      null),
        new("NEWTON FRACTAL",     DrawNewtonFractal,  null),
        new("CLIFFORD ATTRACTOR", null,               DrawClifford),
        new("BARNSLEY FERN",      null,               DrawBarnsleyFern),
        new("HARMONOGRAPH",       null,               DrawHarmonograph),
    };

    // ─── COPPER BARS ──────────────────────────────────────────────────────────

    private void DrawCopperBars(int[] buf, double t)
    {
        int nBars = 7;
        double barH = (double)H / nBars;
        for (int y = 0; y < H; y++)
        {
            double fy = y / (double)H;
            double brightness = 0;
            double hue = 0;
            for (int i = 0; i < nBars; i++)
            {
                double center = (i + 0.5) * barH + Math.Sin(t * 0.07 + i * 1.1) * 18;
                double dist   = Math.Abs(y - center);
                double b      = Math.Max(0, 1 - dist / (barH * 0.55));
                if (b > brightness) { brightness = b; hue = (i * 51 + t * 2) % 360; }
            }
            int col = Hsl(hue, 1.0, brightness * 0.75);
            for (int x = 0; x < W; x++) buf[y * W + x] = col;
        }
    }

    // ─── PLASMA ───────────────────────────────────────────────────────────────

    private void DrawPlasma(int[] buf, double t)
    {
        const double scale = 0.06;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double v = Math.Sin(x * scale + t * 0.05)
                     + Math.Sin(y * scale * 0.8 + t * 0.04)
                     + Math.Sin((x + y) * scale * 0.5 + t * 0.06)
                     + Math.Sin(Math.Sqrt(Math.Pow(x - W / 2.0, 2) + Math.Pow(y - H / 2.0, 2)) * scale - t * 0.07);
            double n = (v + 4) / 8.0;
            int r = (int)(Math.Sin(n * Math.PI * 2) * 127 + 128);
            int g = (int)(Math.Sin(n * Math.PI * 2 + 2.09) * 127 + 128);
            int b = (int)(Math.Sin(n * Math.PI * 2 + 4.19) * 127 + 128);
            buf[y * W + x] = Rgb(r, g, b);
        }
    }

    // ─── SINUS SCROLLER ───────────────────────────────────────────────────────

    private void DrawSinusScroller(int[] buf, double t)
    {
        // fond étoilé
        var rng = new Random(42);
        for (int i = 0; i < 60; i++)
        {
            int sx = rng.Next(W), sy = rng.Next(H);
            int bright = 80 + rng.Next(100);
            buf[sy * W + sx] = Rgb(bright, bright, bright);
        }
        // texte sinus
        _scrollX -= 2.5;
        if (_scrollX < -ScrollMsg.Length * 10) _scrollX = W;

        for (int ci = 0; ci < ScrollMsg.Length + W / 10 + 2; ci++)
        {
            double cx = _scrollX + ci * 10;
            if (cx < -12 || cx > W + 12) continue;
            double cy = H / 2.0 + Math.Sin((cx + t * 2) * 0.07) * 40
                                 + Math.Sin((cx + t) * 0.04) * 20;
            char ch = ScrollMsg[((ci % ScrollMsg.Length) + ScrollMsg.Length) % ScrollMsg.Length];
            double hue = (cx * 0.8 + t * 2) % 360;
            DrawChar(buf, (int)cx, (int)cy, ch, hue);
        }
    }

    private static readonly bool[,] _charDot = {
        {false,true,true,true,false},
        {true,false,false,false,true},
        {true,false,false,false,true},
        {true,true,true,true,true},
        {true,false,false,false,true},
        {true,false,false,false,true},
        {true,false,false,false,true},
    };

    private void DrawChar(int[] buf, int px, int py, char ch, double hue)
    {
        // Rendu mini-pixel simple (3×5) pour les caractères ASCII
        int col = Hsl(hue, 1.0, 0.65);
        byte[] bits = GetCharBits(ch);
        for (int row = 0; row < 5; row++)
        for (int col2 = 0; col2 < 7; col2++)
        {
            if ((bits[row] & (1 << (6 - col2))) != 0)
            {
                int dx = px + col2 - 3, dy = py + row - 2;
                if (dx >= 0 && dx < W && dy >= 0 && dy < H)
                    buf[dy * W + dx] = col;
            }
        }
    }

    private static byte[] GetCharBits(char c)
    {
        return c switch
        {
            'A' => new byte[] { 0b0011100, 0b0100010, 0b1000001, 0b1111111, 0b1000001 },
            'B' => new byte[] { 0b1111110, 0b1000001, 0b1111110, 0b1000001, 0b1111110 },
            'C' => new byte[] { 0b0111110, 0b1000000, 0b1000000, 0b1000000, 0b0111110 },
            'D' => new byte[] { 0b1111100, 0b1000010, 0b1000001, 0b1000010, 0b1111100 },
            'E' => new byte[] { 0b1111111, 0b1000000, 0b1111100, 0b1000000, 0b1111111 },
            'F' => new byte[] { 0b1111111, 0b1000000, 0b1111100, 0b1000000, 0b1000000 },
            'G' => new byte[] { 0b0111110, 0b1000000, 0b1001111, 0b1000001, 0b0111110 },
            'H' => new byte[] { 0b1000001, 0b1000001, 0b1111111, 0b1000001, 0b1000001 },
            'I' => new byte[] { 0b1111111, 0b0001000, 0b0001000, 0b0001000, 0b1111111 },
            'K' => new byte[] { 0b1000110, 0b1011000, 0b1100000, 0b1011000, 0b1000110 },
            'L' => new byte[] { 0b1000000, 0b1000000, 0b1000000, 0b1000000, 0b1111111 },
            'M' => new byte[] { 0b1000001, 0b1100011, 0b1010101, 0b1001001, 0b1000001 },
            'N' => new byte[] { 0b1000001, 0b1100001, 0b1010001, 0b1001001, 0b1000111 },
            'O' => new byte[] { 0b0111110, 0b1000001, 0b1000001, 0b1000001, 0b0111110 },
            'P' => new byte[] { 0b1111110, 0b1000001, 0b1111110, 0b1000000, 0b1000000 },
            'R' => new byte[] { 0b1111110, 0b1000001, 0b1111110, 0b1001000, 0b1000110 },
            'S' => new byte[] { 0b0111110, 0b1000000, 0b0111110, 0b0000001, 0b1111110 },
            'T' => new byte[] { 0b1111111, 0b0001000, 0b0001000, 0b0001000, 0b0001000 },
            'U' => new byte[] { 0b1000001, 0b1000001, 0b1000001, 0b1000001, 0b0111110 },
            'V' => new byte[] { 0b1000001, 0b1000001, 0b0100010, 0b0010100, 0b0001000 },
            'W' => new byte[] { 0b1000001, 0b1001001, 0b1010101, 0b1100011, 0b1000001 },
            'X' => new byte[] { 0b1000001, 0b0100010, 0b0011100, 0b0100010, 0b1000001 },
            'Y' => new byte[] { 0b1000001, 0b0100010, 0b0011100, 0b0001000, 0b0001000 },
            'Z' => new byte[] { 0b1111111, 0b0000110, 0b0011000, 0b0110000, 0b1111111 },
            '*' => new byte[] { 0b0100010, 0b0011100, 0b1111111, 0b0011100, 0b0100010 },
            _   => new byte[] { 0b0000000, 0b0000000, 0b0000000, 0b0000000, 0b0000000 },
        };
    }

    // ─── STARFIELD 3D ─────────────────────────────────────────────────────────

    private double[] _starX = Array.Empty<double>();
    private double[] _starY = Array.Empty<double>();
    private double[] _starZ = Array.Empty<double>();

    private void DrawStarfield(int[] buf, double t)
    {
        const int N = 250;
        if (_starX.Length != N)
        {
            _starX = new double[N]; _starY = new double[N]; _starZ = new double[N];
            var rng = new Random(7);
            for (int i = 0; i < N; i++)
            {
                _starX[i] = (rng.NextDouble() - 0.5) * 2000;
                _starY[i] = (rng.NextDouble() - 0.5) * 2000;
                _starZ[i] = rng.NextDouble() * 800 + 1;
            }
        }
        int cx = W / 2, cy = H / 2;
        for (int i = 0; i < N; i++)
        {
            _starZ[i] -= 3.5;
            if (_starZ[i] <= 0)
            {
                var rng2 = new Random(i + (int)t);
                _starX[i] = (rng2.NextDouble() - 0.5) * 2000;
                _starY[i] = (rng2.NextDouble() - 0.5) * 2000;
                _starZ[i] = 800;
            }
            double px = cx + _starX[i] / _starZ[i] * 250;
            double py = cy + _starY[i] / _starZ[i] * 250;
            int ix = (int)px, iy = (int)py;
            if (ix < 0 || ix >= W || iy < 0 || iy >= H) continue;
            int bright = Math.Clamp((int)((1 - _starZ[i] / 800.0) * 255), 0, 255);
            buf[iy * W + ix] = Rgb(bright, bright, bright);
        }
    }

    // ─── FIRE ─────────────────────────────────────────────────────────────────

    private static readonly int[] _firePal = BuildFirePal();
    private static int[] BuildFirePal()
    {
        var p = new int[256];
        for (int i = 0; i < 256; i++)
            p[i] = Rgb(Math.Min(255, i * 3), Math.Max(0, Math.Min(255, (i - 80) * 3)), Math.Max(0, Math.Min(255, (i - 160) * 3)));
        return p;
    }

    private void DrawFire(int[] buf, double t)
    {
        if (_fireBuf == null || _fireBuf.Length != W * H)
            _fireBuf = new byte[W * H];
        var rng = new Random((int)t);
        for (int x = 0; x < W; x++)
            _fireBuf[(H - 1) * W + x] = rng.NextDouble() < 0.55 ? (byte)255 : (byte)0;
        for (int y = 0; y < H - 1; y++)
        for (int x = 0; x < W; x++)
        {
            int v = (_fireBuf[(y + 1) * W + ((x - 1 + W) % W)]
                   + _fireBuf[(y + 1) * W + x]
                   + _fireBuf[(y + 1) * W + ((x + 1) % W)]
                   + _fireBuf[Math.Min(H - 1, y + 2) * W + x]) >> 2;
            _fireBuf[y * W + x] = (byte)Math.Max(0, v - 1);
        }
        for (int i = 0; i < W * H; i++) buf[i] = _firePal[_fireBuf[i]];
    }

    // ─── ROTOZOOMER ───────────────────────────────────────────────────────────

    private static readonly int[] _rotoTex = BuildRotoTex();
    private const int RTW = 128;
    private static int[] BuildRotoTex()
    {
        var tex = new int[RTW * RTW];
        for (int y = 0; y < RTW; y++)
        for (int x = 0; x < RTW; x++)
        {
            int v = ((x ^ y) & 15) * 16;
            tex[y * RTW + x] = Rgb(v, (x * 2) % 256, (y * 2) % 256);
        }
        return tex;
    }

    private void DrawRotozoomer(int[] buf, double t)
    {
        double angle = t * 0.02;
        double scale = 1.5 + Math.Sin(t * 0.03) * 0.8;
        double cos = Math.Cos(angle) * scale, sin = Math.Sin(angle) * scale;
        int cx = W / 2, cy = H / 2;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx = x - cx, dy = y - cy;
            int u = ((int)(cos * dx - sin * dy + t) & (RTW - 1) + RTW) % RTW;
            int v = ((int)(sin * dx + cos * dy + t * 0.7) & (RTW - 1) + RTW) % RTW;
            buf[y * W + x] = _rotoTex[v * RTW + u];
        }
    }

    // ─── DOT TUNNEL (DrawingContext) ──────────────────────────────────────────

    private void DrawDotTunnel(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        double cx = W / 2.0, cy = H / 2.0;
        for (int ring = 0; ring < 20; ring++)
        {
            double depth = ((ring / 20.0 + t * 0.008) % 1.0 + 1.0) % 1.0;
            double r     = 10 + depth * 380;
            double alpha = depth * 0.9;
            int    nDots = Math.Max(4, (int)(20 * depth + 4));
            double hue   = (ring * 18 + t * 3) % 360;
            for (int i = 0; i < nDots; i++)
            {
                double a  = i / (double)nDots * Math.PI * 2 + t * 0.02 * (1 - depth);
                double px = cx + Math.Cos(a) * r;
                double py = cy + Math.Sin(a) * r * 0.5;
                double sz = Math.Max(0.8, (1 - depth) * 3.5);
                var    col = HslBrush(hue, 1.0, 0.7, alpha);
                dc.DrawEllipse(col, null, new Point(px, py), sz, sz);
            }
        }
    }

    // ─── BOBS ─────────────────────────────────────────────────────────────────

    private void DrawBobs(int[] buf, double t)
    {
        // Fade
        for (int i = 0; i < buf.Length; i++)
        {
            int c = buf[i];
            int r = Math.Max(0, ((c >> 16) & 0xFF) - 18);
            int g = Math.Max(0, ((c >> 8)  & 0xFF) - 18);
            int b = Math.Max(0, ( c        & 0xFF) - 18);
            buf[i] = (r << 16) | (g << 8) | b;
        }
        if (_bobs == null)
        {
            var rng = new Random(13);
            _bobs = new Bob[18];
            for (int i = 0; i < 18; i++)
                _bobs[i] = new Bob(rng.NextDouble() * Math.PI * 2, rng.NextDouble() * Math.PI * 2,
                                   0.5 + rng.NextDouble() * 1.5, 0.5 + rng.NextDouble() * 1.5, i * 20f);
        }
        int cx = W / 2, cy = H / 2;
        foreach (var b in _bobs)
        {
            double bx = cx + Math.Sin(t * 0.04 * b.FX + b.OX) * W * 0.4;
            double by = cy + Math.Sin(t * 0.04 * b.FY + b.OY) * H * 0.38;
            int    r  = 12;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > r) continue;
                double brightness = (1 - dist / r) * 0.9;
                int    col        = Hsl((b.Hue + t) % 360, 1.0, brightness * 0.8);
                int    px         = (int)bx + dx, py = (int)by + dy;
                if (px >= 0 && px < W && py >= 0 && py < H)
                {
                    int old = buf[py * W + px];
                    int nr  = Math.Min(255, ((old >> 16) & 0xFF) + ((col >> 16) & 0xFF));
                    int ng  = Math.Min(255, ((old >> 8)  & 0xFF) + ((col >> 8)  & 0xFF));
                    int nb  = Math.Min(255, ( old        & 0xFF) + ( col        & 0xFF));
                    buf[py * W + px] = (nr << 16) | (ng << 8) | nb;
                }
            }
        }
    }

    // ─── VECTOR CUBE (DrawingContext) ─────────────────────────────────────────

    private static readonly int[][] CubeEdges =
    {
        new int[]{0,1},new int[]{1,2},new int[]{2,3},new int[]{3,0},
        new int[]{4,5},new int[]{5,6},new int[]{6,7},new int[]{7,4},
        new int[]{0,4},new int[]{1,5},new int[]{2,6},new int[]{3,7},
    };

    private static readonly double[][] CubeVerts =
    {
        new double[]{-1.0,-1,-1},new double[]{1,-1,-1},new double[]{1,1,-1},new double[]{-1,1,-1},
        new double[]{-1,-1, 1},new double[]{1,-1, 1},new double[]{1,1, 1},new double[]{-1,1, 1},
    };

    private void DrawVectorCube(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        double ax = t * 0.02, ay = t * 0.017, az = t * 0.013;
        double cx2 = Math.Cos(ax), sx2 = Math.Sin(ax);
        double cy2 = Math.Cos(ay), sy2 = Math.Sin(ay);
        double cz2 = Math.Cos(az), sz2 = Math.Sin(az);

        (double, double, double) Rot(double x, double y, double z)
        {
            double y2 = y * cx2 - z * sx2, z2 = y * sx2 + z * cx2;
            double x3 = x * cy2 + z2 * sy2; z2 = z2 * cy2 - x * sy2;
            double x4 = x3 * cz2 - y2 * sz2, y4 = x3 * sz2 + y2 * cz2;
            return (x4, y4, z2);
        }

        var proj = new Point[8];
        double scale = 1.5 + Math.Sin(t * 0.015) * 0.4;
        for (int i = 0; i < 8; i++)
        {
            var (rx, ry, rz) = Rot(CubeVerts[i][0] * scale, CubeVerts[i][1] * scale, CubeVerts[i][2] * scale);
            double fov = 260.0 / (rz + 5);
            proj[i] = new Point(W / 2.0 + rx * fov, H / 2.0 + ry * fov);
        }
        for (int i = 0; i < CubeEdges.Length; i++)
        {
            double hue = (i * 30 + t * 2) % 360;
            var pen = new Pen(HslBrush(hue, 1.0, 0.65, 1.0), 1.5);
            dc.DrawLine(pen, proj[CubeEdges[i][0]], proj[CubeEdges[i][1]]);
        }

        // Second objet : tore filaire
        var torePts = new Point[16 * 8];
        for (int u = 0; u < 16; u++)
        for (int v = 0; v < 8; v++)
        {
            double a   = u / 16.0 * Math.PI * 2, b2 = v / 8.0 * Math.PI * 2;
            double tx  = (2 + 0.55 * Math.Cos(b2)) * Math.Cos(a);
            double ty  = (2 + 0.55 * Math.Cos(b2)) * Math.Sin(a);
            double tz  = 0.55 * Math.Sin(b2);
            var (rx, ry, rz) = Rot(tx, ty, tz);
            double fov2 = 180.0 / (rz + 6);
            torePts[u * 8 + v] = new Point(W / 2.0 + rx * fov2, H / 2.0 + ry * fov2);
        }
        var torePen = new Pen(HslBrush((t * 3) % 360, 1.0, 0.5, 0.5), 0.8);
        for (int u = 0; u < 16; u++)
        for (int v = 0; v < 8; v++)
        {
            dc.DrawLine(torePen, torePts[u * 8 + v], torePts[u * 8 + (v + 1) % 8]);
            dc.DrawLine(torePen, torePts[u * 8 + v], torePts[((u + 1) % 16) * 8 + v]);
        }
    }

    // ─── METABALLS ────────────────────────────────────────────────────────────

    private void DrawMetaballs(int[] buf, double t)
    {
        if (_mballs == null)
        {
            var rng = new Random(99);
            _mballs = new MBall[6];
            for (int i = 0; i < 6; i++)
                _mballs[i] = new MBall(rng.NextDouble() * Math.PI * 2, rng.NextDouble() * Math.PI * 2,
                                       0.4 + rng.NextDouble(), 0.4 + rng.NextDouble(), i * 60f);
        }
        var balls = new (double x, double y, float hue)[_mballs.Length];
        for (int i = 0; i < _mballs.Length; i++)
        {
            var b = _mballs[i];
            balls[i] = (W / 2.0 + Math.Sin(t * 0.03 * b.FX + b.OX) * W * 0.33,
                        H / 2.0 + Math.Sin(t * 0.03 * b.FY + b.OY) * H * 0.33,
                        b.Hue);
        }
        const double r2 = 100 * 100;
        for (int y = 0; y < H; y += 2)
        for (int x = 0; x < W; x += 2)
        {
            double sum = 0, hr = 0, hg = 0, hb = 0;
            foreach (var b in balls)
            {
                double dx = x - b.x, dy = y - b.y;
                double w = r2 / (dx * dx + dy * dy + 1);
                sum += w;
                hr  += Math.Sin(b.hue * Math.PI / 180) * w;
                hg  += Math.Sin((b.hue + 120) * Math.PI / 180) * w;
                hb  += Math.Sin((b.hue + 240) * Math.PI / 180) * w;
            }
            if (sum > 2)
            {
                double br = Math.Min(255, (sum - 2) * 80);
                int    r  = (int)Math.Min(255, (0.5 + hr / sum * 0.5) * br);
                int    g  = (int)Math.Min(255, (0.5 + hg / sum * 0.5) * br);
                int    b  = (int)Math.Min(255, (0.5 + hb / sum * 0.5) * br);
                int    col = Rgb(r, g, b);
                for (int dy = 0; dy < 2; dy++)
                for (int dx = 0; dx < 2; dx++)
                    SetPix(buf, x + dx, y + dy, col);
            }
        }
    }

    // ─── COLOR CYCLING ────────────────────────────────────────────────────────

    private void DrawColorCycling(int[] buf, double t)
    {
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double v = Math.Sin(x * 0.045) * Math.Sin(y * 0.045)
                     + Math.Sin((x - y) * 0.035 + t * 0.02);
            double idx = (v + 2) / 4.0;
            buf[y * W + x] = Hsl((idx * 360 + t * 3) % 360, 1.0, 0.5);
        }
    }

    // ─── RASTER LINES ─────────────────────────────────────────────────────────

    private void DrawRasterLines(int[] buf, double t)
    {
        for (int y = 0; y < H; y++)
        {
            double wave = Math.Sin(y * 0.05 + t * 0.04) * 40
                        + Math.Sin(y * 0.02 - t * 0.025) * 60;
            double hue  = (y * 0.9 + wave + t * 2) % 360;
            double l    = 0.35 + Math.Sin(y * 0.1 + t * 0.05) * 0.18;
            int    col  = (y % 3 == 0) ? 0 : Hsl(hue, 1.0, l);
            for (int x = 0; x < W; x++) buf[y * W + x] = col;
        }
    }

    // ─── SHADEBOBS ────────────────────────────────────────────────────────────

    private void DrawShadebobs(int[] buf, double t)
    {
        // Fade
        for (int i = 0; i < buf.Length; i++)
        {
            int c = buf[i];
            int r = Math.Max(0, ((c >> 16) & 0xFF) - 12);
            int g = Math.Max(0, ((c >> 8)  & 0xFF) - 12);
            int b = Math.Max(0, ( c        & 0xFF) - 12);
            buf[i] = (r << 16) | (g << 8) | b;
        }
        if (_shbobs == null)
        {
            var rng = new Random(55);
            _shbobs = new Shbob[5];
            for (int i = 0; i < 5; i++)
                _shbobs[i] = new Shbob(rng.NextDouble() * Math.PI * 2, rng.NextDouble() * Math.PI * 2,
                                       0.4 + rng.NextDouble(), 0.4 + rng.NextDouble(), i * 72f);
        }
        int cx = W / 2, cy = H / 2;
        foreach (var b in _shbobs)
        {
            double bx = cx + Math.Sin(t * 0.035 * b.FX + b.OX) * W * 0.42;
            double by = cy + Math.Sin(t * 0.035 * b.FY + b.OY) * H * 0.42;
            int    r  = 45;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                double dist = Math.Sqrt(dx * dx + dy * dy);
                if (dist > r) continue;
                double f  = 1 - dist / r;
                double h1 = (b.Hue + t * 1.5) % 360;
                double h2 = (b.Hue + 120 + t) % 360;
                double hm = h1 * f + h2 * (1 - f);
                int    col = Hsl(hm, 1.0, f * 0.7);
                int    px2 = (int)bx + dx, py2 = (int)by + dy;
                if (px2 >= 0 && px2 < W && py2 >= 0 && py2 < H)
                {
                    int old = buf[py2 * W + px2];
                    int nr  = Math.Min(255, ((old >> 16) & 0xFF) + ((col >> 16) & 0xFF));
                    int ng  = Math.Min(255, ((old >> 8)  & 0xFF) + ((col >> 8)  & 0xFF));
                    int nb  = Math.Min(255, ( old        & 0xFF) + ( col        & 0xFF));
                    buf[py2 * W + px2] = (nr << 16) | (ng << 8) | nb;
                }
            }
        }
    }

    // ─── LENS FLARE (DrawingContext) ──────────────────────────────────────────

    private void DrawLensFlare(DrawingContext dc, double t)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 0, 10)), null, new Rect(0, 0, W, H));
        double sx = W / 2.0 + Math.Cos(t * 0.018) * W * 0.32;
        double sy = H / 2.0 + Math.Sin(t * 0.013) * H * 0.32;
        for (int ray = 0; ray < 24; ray++)
        {
            double a   = ray / 24.0 * Math.PI * 2 + t * 0.01;
            double len = 50 + Math.Sin(t * 0.05 + ray) * 22;
            double hue = (ray * 15 + t * 3) % 360;
            var    pen = new Pen(HslBrush(hue, 1.0, 0.7, 0.4), 1.0);
            dc.DrawLine(pen, new Point(sx, sy), new Point(sx + Math.Cos(a) * len, sy + Math.Sin(a) * len));
        }
        var gr = new RadialGradientBrush(
            Color.FromArgb(230, 255, 255, 200),
            Color.FromArgb(0,   0,   0,   0))
        { RadiusX = 1, RadiusY = 1, Center = new Point(0.5, 0.5), GradientOrigin = new Point(0.5, 0.5) };
        dc.DrawEllipse(gr, null, new Point(sx, sy), 60, 60);
    }

    // ─── TWISTER ──────────────────────────────────────────────────────────────

    private void DrawTwister(int[] buf, double t)
    {
        for (int y = 0; y < H; y++)
        {
            double twist = Math.Sin(y * 0.06 + t * 0.05) * 0.7;
            double width = (Math.Cos(y * 0.06 + t * 0.05) * 0.5 + 0.5) * W * 0.35 + 8;
            double cx    = W / 2.0 + Math.Sin(t * 0.03 + y * 0.02) * W * 0.2;
            double hue   = (y * 1.2 + t * 3) % 360;
            for (int x = 0; x < W; x++)
            {
                double dx   = Math.Abs(x - cx);
                double edge = width - dx;
                if (edge < 0) continue;
                double bright = Math.Min(1.0, edge / 12.0);
                buf[y * W + x] = Hsl(hue, 1.0, bright * 0.7);
            }
        }
    }

    // ─── MOIRE ────────────────────────────────────────────────────────────────

    private void DrawMoire(int[] buf, double t)
    {
        double cx1 = W / 2.0 + Math.Cos(t * 0.02) * W * 0.2;
        double cy1 = H / 2.0 + Math.Sin(t * 0.018) * H * 0.2;
        double cx2 = W / 2.0 + Math.Cos(t * 0.015 + 2) * W * 0.2;
        double cy2 = H / 2.0 + Math.Sin(t * 0.022 + 1) * H * 0.2;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double d1 = Math.Sqrt((x - cx1) * (x - cx1) + (y - cy1) * (y - cy1));
            double d2 = Math.Sqrt((x - cx2) * (x - cx2) + (y - cy2) * (y - cy2));
            double v  = (Math.Sin(d1 * 0.3 - t * 0.1) + Math.Sin(d2 * 0.3 + t * 0.08)) * 0.5;
            double n  = (v + 1) / 2.0;
            buf[y * W + x] = Hsl((n * 240 + t * 2) % 360, 1.0, 0.4 + n * 0.3);
        }
    }

    // ─── TUNNEL TEXTURE ───────────────────────────────────────────────────────

    private void DrawTunnelTexture(int[] buf, double t)
    {
        int cx = W / 2, cy = H / 2;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx = x - cx, dy = y - cy;
            double dist  = Math.Sqrt(dx * dx + dy * dy) + 0.001;
            double angle = Math.Atan2(dy, dx);
            double u     = (angle / Math.PI + 1.0) * 64 + t * 0.8;
            double v     = (64.0 / dist) + t * 1.2;
            int    ui    = ((int)u & 63), vi = ((int)v & 63);
            int    check = ((ui >> 3) ^ (vi >> 3)) & 1;
            double bright = Math.Min(1.0, 30.0 / dist);
            double hue    = (angle * 57.3 + t * 3) % 360;
            buf[y * W + x] = check == 1 ? Hsl(hue, 1.0, bright * 0.65)
                                        : Hsl((hue + 180) % 360, 0.8, bright * 0.3);
        }
    }

    // ─── BUMP SPHERE ──────────────────────────────────────────────────────────

    private void DrawBumpSphere(int[] buf, double t)
    {
        double lx = Math.Cos(t * 0.04) * 0.7, ly = -0.5, lz = 0.7;
        double llen = Math.Sqrt(lx*lx + ly*ly + lz*lz);
        lx /= llen; ly /= llen; lz /= llen;
        int cx = W / 2, cy = H / 2;
        int R  = Math.Min(W, H) / 2 - 8;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx = x - cx, dy = y - cy;
            double d2 = dx*dx + dy*dy;
            if (d2 > R*R) { buf[y*W+x] = 0; continue; }
            double nz   = Math.Sqrt(R*R - d2) / R;
            double nx   = dx / R, ny = dy / R;
            double bump = Math.Sin(nx * 12 + t * 0.08) * Math.Sin(ny * 10 - t * 0.06) * 0.25;
            double bnx  = nx + bump, bny = ny + bump, bnz = nz;
            double blen = Math.Sqrt(bnx*bnx + bny*bny + bnz*bnz);
            bnx /= blen; bny /= blen; bnz /= blen;
            double diff = Math.Max(0, bnx*lx + bny*ly + bnz*lz);
            double spec = Math.Pow(Math.Max(0, bnz*0.5 + diff*0.5), 12) * 0.6;
            double hue  = (bnx * 60 + bny * 40 + t * 2) % 360;
            int    r    = (int)(Hsl(hue,0.9,diff*0.5) >> 16 & 0xFF);
            int    g    = (int)(Hsl(hue,0.9,diff*0.5) >> 8  & 0xFF);
            int    b    = (int)(Hsl(hue,0.9,diff*0.5)       & 0xFF);
            r = Math.Min(255, r + (int)(spec*255));
            g = Math.Min(255, g + (int)(spec*255));
            b = Math.Min(255, b + (int)(spec*255));
            buf[y*W+x] = Rgb(r,g,b);
        }
    }

    // ─── WATER RIPPLE ─────────────────────────────────────────────────────────

    private void DrawWaterRipple(int[] buf, double t)
    {
        double cx = W/2.0 + Math.Sin(t*0.025)*W*0.2;
        double cy = H/2.0 + Math.Cos(t*0.02)*H*0.2;
        double cx2= W/2.0 + Math.Cos(t*0.018+1)*W*0.15;
        double cy2= H/2.0 + Math.Sin(t*0.022+2)*H*0.15;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double d1 = Math.Sqrt((x-cx)*(x-cx)+(y-cy)*(y-cy));
            double d2 = Math.Sqrt((x-cx2)*(x-cx2)+(y-cy2)*(y-cy2));
            double u  = x + Math.Sin(d1*0.25 - t*0.12)*6 + Math.Sin(d2*0.2 + t*0.1)*4;
            double v2 = y + Math.Cos(d1*0.25 - t*0.12)*6 + Math.Cos(d2*0.2 + t*0.1)*4;
            int ui    = Math.Clamp((int)u, 0, W-1);
            int vi    = Math.Clamp((int)v2, 0, H-1);
            double hue = (ui * 0.8 + vi * 0.5 + t * 2) % 360;
            double l   = 0.35 + Math.Sin(d1*0.15 - t*0.1)*0.15 + Math.Sin(d2*0.12+t*0.08)*0.1;
            buf[y*W+x] = Hsl(hue, 0.9, l);
        }
    }

    // ─── VORONOI ──────────────────────────────────────────────────────────────

    private static readonly (double ox, double oy, double fx, double fy, float hue)[] _voronoiSeeds;
    static DemoEffectControl()
    {
        var rng = new Random(42);
        _voronoiSeeds = new (double,double,double,double,float)[14];
        for (int i = 0; i < 14; i++)
            _voronoiSeeds[i] = (rng.NextDouble()*Math.PI*2, rng.NextDouble()*Math.PI*2,
                                0.3+rng.NextDouble()*0.8,   0.3+rng.NextDouble()*0.8, i*26f);
    }

    private void DrawVoronoi(int[] buf, double t)
    {
        var pts = new (double x, double y, float hue)[_voronoiSeeds.Length];
        for (int i = 0; i < _voronoiSeeds.Length; i++)
        {
            var s = _voronoiSeeds[i];
            pts[i] = (W/2.0+Math.Sin(t*0.03*s.fx+s.ox)*W*0.42,
                      H/2.0+Math.Sin(t*0.03*s.fy+s.oy)*H*0.42, s.hue);
        }
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double d1 = double.MaxValue, d2 = double.MaxValue;
            float  best = 0;
            foreach (var p in pts)
            {
                double d = (x-p.x)*(x-p.x)+(y-p.y)*(y-p.y);
                if (d < d1) { d2 = d1; d1 = d; best = p.hue; }
                else if (d < d2) d2 = d;
            }
            double edge = Math.Sqrt(d2) - Math.Sqrt(d1);
            double l    = edge < 4 ? 0.08 : 0.35 + Math.Sin(Math.Sqrt(d1)*0.15 - t*0.06)*0.12;
            buf[y*W+x]  = Hsl((best + t*2) % 360, 0.85, l);
        }
    }

    // ─── MATRIX RAIN ──────────────────────────────────────────────────────────

    private int[]?  _matrixPos;
    private int[]?  _matrixSpd;
    private char[]? _matrixChr;
    private const int MCOLS = 25;

    private void DrawMatrixRain(int[] buf, double t)
    {
        // Fade to black
        for (int i = 0; i < buf.Length; i++)
        {
            int c = buf[i];
            int g = Math.Max(0, ((c >> 8) & 0xFF) - 20);
            buf[i] = g << 8;
        }
        if (_matrixPos == null)
        {
            var rng = new Random(77);
            _matrixPos = new int[MCOLS]; _matrixSpd = new int[MCOLS]; _matrixChr = new char[MCOLS];
            for (int i = 0; i < MCOLS; i++)
            {
                _matrixPos[i] = rng.Next(H);
                _matrixSpd[i] = 1 + rng.Next(3);
            }
        }
        int colW = W / MCOLS;
        var rng2 = new Random((int)t);
        for (int ci = 0; ci < MCOLS; ci++)
        {
            _matrixPos![ci] += _matrixSpd![ci];
            if (_matrixPos[ci] > H + 20) _matrixPos[ci] = -rng2.Next(H/2);
            int px = ci * colW + colW/2 - 3;
            int py = _matrixPos[ci];
            // Tête blanche
            if (py >= 0 && py < H) DrawMatrixChar(buf, px, py, 0xFFFFFF);
            // Traîne verte
            for (int tr = 1; tr < 12; tr++)
            {
                int ty = py - tr * 7;
                if (ty < 0 || ty >= H) continue;
                int g = Math.Max(0, 200 - tr * 18);
                DrawMatrixChar(buf, px, ty, g << 8);
            }
        }
    }

    private void DrawMatrixChar(int[] buf, int px, int py, int col)
    {
        char[] chars = { '0','1','A','B','Z','X','*','#','@','!','$','%' };
        var rng = new Random(px + py);
        char ch = chars[rng.Next(chars.Length)];
        byte[] bits = GetCharBits(ch);
        for (int row = 0; row < 5; row++)
        for (int c2 = 0; c2 < 7; c2++)
        {
            if ((bits[row] & (1 << (6-c2))) != 0)
            {
                int dx = px+c2-3, dy = py+row-2;
                if (dx>=0&&dx<W&&dy>=0&&dy<H) buf[dy*W+dx] = col;
            }
        }
    }

    // ─── KALEIDOSCOPE ─────────────────────────────────────────────────────────

    private void DrawKaleidoscope(int[] buf, double t)
    {
        int cx = W/2, cy = H/2;
        const int SEGS = 8;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx = x-cx, dy = y-cy;
            double r  = Math.Sqrt(dx*dx+dy*dy);
            double a  = Math.Atan2(dy, dx);
            double segA = Math.PI*2/SEGS;
            double na   = ((a % segA) + segA) % segA;
            if (na > segA/2) na = segA - na;
            double ux = Math.Cos(na)*r + t*0.5;
            double uy = Math.Sin(na)*r + t*0.3;
            double v  = Math.Sin(ux*0.04)*Math.Sin(uy*0.04)
                      + Math.Sin((ux-uy)*0.03)
                      + Math.Sin(r*0.05 - t*0.05);
            buf[y*W+x] = Hsl((v*60+r*0.5+t*2)%360, 1.0, 0.45+v*0.1);
        }
    }

    // ─── AURORA ───────────────────────────────────────────────────────────────

    private void DrawAurora(int[] buf, double t)
    {
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double ny   = y / (double)H;
            double wave = Math.Sin(x*0.03 + t*0.025)*0.15
                        + Math.Sin(x*0.018 - t*0.018)*0.1
                        + Math.Sin(x*0.05  + t*0.03)*0.05;
            double band = Math.Exp(-Math.Pow((ny - 0.45 - wave)*4, 2));
            double band2= Math.Exp(-Math.Pow((ny - 0.6  + wave)*5, 2))*0.5;
            double total= Math.Min(1.0, band + band2);
            if (total < 0.01) { buf[y*W+x]=0; continue; }
            double hue  = 130 + Math.Sin(x*0.02 + t*0.015)*30
                              + Math.Sin(x*0.008 - t*0.01)*20;
            double shimmer = 0.7 + Math.Sin(x*0.1+y*0.07+t*0.12)*0.3;
            buf[y*W+x] = Hsl(hue % 360, 0.9, total * shimmer * 0.6);
        }
    }

    // ─── HYPNO SPIRAL ─────────────────────────────────────────────────────────

    private void DrawHypnoSpiral(int[] buf, double t)
    {
        int cx = W/2, cy = H/2;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double dx   = x-cx, dy = y-cy;
            double r    = Math.Sqrt(dx*dx+dy*dy);
            double a    = Math.Atan2(dy,dx);
            double spin = a + r*0.06 - t*0.04;
            double v    = Math.Sin(spin*6)*0.5 + 0.5;
            double v2   = Math.Sin(r*0.15 - t*0.08)*0.5+0.5;
            double mix  = v*0.6+v2*0.4;
            buf[y*W+x]  = Hsl((mix*180 + r*0.4 + t*3)%360, 1.0, 0.3+mix*0.4);
        }
    }

    // ─── WANDERING LINES ──────────────────────────────────────────────────────

    private double[]? _wlX, _wlY, _wlVX, _wlVY;
    private int[]?    _wlHue;

    private void DrawWanderingLines(int[] buf, double t)
    {
        // Fade
        for (int i = 0; i < buf.Length; i++)
        {
            int c = buf[i];
            int r = Math.Max(0, ((c>>16)&0xFF)-8);
            int g = Math.Max(0, ((c>>8) &0xFF)-8);
            int b = Math.Max(0, ( c     &0xFF)-8);
            buf[i] = (r<<16)|(g<<8)|b;
        }
        const int N = 8;
        if (_wlX == null)
        {
            var rng = new Random(21);
            _wlX = new double[N]; _wlY = new double[N];
            _wlVX= new double[N]; _wlVY= new double[N]; _wlHue = new int[N];
            for (int i=0;i<N;i++)
            {
                _wlX[i]=rng.NextDouble()*W; _wlY[i]=rng.NextDouble()*H;
                _wlVX[i]=(rng.NextDouble()-0.5)*3; _wlVY[i]=(rng.NextDouble()-0.5)*3;
                _wlHue[i]=rng.Next(360);
            }
        }
        for (int i=0;i<N;i++)
        {
            double px=_wlX![i], py=_wlY![i];
            _wlVX![i] += (Math.Sin(t*0.04+i)*0.3);
            _wlVY![i] += (Math.Cos(t*0.035+i*1.3)*0.3);
            double spd = Math.Sqrt(_wlVX[i]*_wlVX[i]+_wlVY[i]*_wlVY[i]);
            if (spd > 4) { _wlVX[i]*=4/spd; _wlVY[i]*=4/spd; }
            _wlX[i] = (_wlX[i]+_wlVX[i]+W)%W;
            _wlY[i] = (_wlY[i]+_wlVY[i]+H)%H;
            _wlHue![i] = (_wlHue[i]+1)%360;
            // Tracer une ligne entre ancienne et nouvelle pos
            int steps=20;
            for (int s=0;s<steps;s++)
            {
                double fx=px+((_wlX[i]-px+W*1.5)%W - W*0.5)*s/steps;
                double fy=py+((_wlY[i]-py+H*1.5)%H - H*0.5)*s/steps;
                double br=0.5+0.5*(double)s/steps;
                SetPix(buf,(int)fx,(int)fy, Hsl(_wlHue[i],1.0,br*0.7));
            }
        }
    }

    // ─── DNA HELIX (DrawingContext) ───────────────────────────────────────────

    private void DrawDnaHelix(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        double cx = W/2.0;
        const int STEPS=60;
        var p1 = new Point[STEPS]; var p2 = new Point[STEPS];
        for (int i=0;i<STEPS;i++)
        {
            double f  = i/(double)(STEPS-1);
            double y2 = f*H;
            double a  = f*Math.PI*4 + t*0.05;
            double r2 = 38 + Math.Sin(f*Math.PI)*20;
            p1[i] = new Point(cx + Math.Cos(a)*r2,  y2);
            p2[i] = new Point(cx + Math.Cos(a+Math.PI)*r2, y2);
        }
        // Barreaux
        for (int i=0;i<STEPS;i+=2)
        {
            double f   = i/(double)(STEPS-1);
            double hue = (f*360 + t*3) % 360;
            var pen = new Pen(HslBrush(hue,1.0,0.6,0.7),1.5);
            dc.DrawLine(pen, p1[i], p2[i]);
            dc.DrawEllipse(HslBrush(hue,1.0,0.8,1.0), null, p1[i],3,3);
            dc.DrawEllipse(HslBrush((hue+180)%360,1.0,0.8,1.0), null, p2[i],3,3);
        }
        // Brins
        for (int i=0;i<STEPS-1;i++)
        {
            double f   = i/(double)(STEPS-1);
            double z1  = Math.Cos(f*Math.PI*4 + t*0.05);
            double z2  = Math.Cos((i+1)/(double)(STEPS-1)*Math.PI*4 + t*0.05);
            double alp = 0.5+z1*0.5;
            dc.DrawLine(new Pen(HslBrush(200,0.9,0.7,alp),2.0), p1[i], p1[i+1]);
            dc.DrawLine(new Pen(HslBrush(30,0.9,0.7,0.5+z2*0.5),2.0), p2[i], p2[i+1]);
        }
    }

    // ─── LISSAJOUS 3D (DrawingContext) ────────────────────────────────────────

    private void DrawLissajous(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        const int N = 800;
        double cx=W/2.0, cy=H/2.0;
        double a=3, b=2, c=1, d=t*0.01, e2=t*0.007;
        Point? prev = null;
        for (int i=0;i<N;i++)
        {
            double f   = i/(double)N * Math.PI*2;
            double x3D = Math.Sin(a*f+d)*60;
            double y3D = Math.Sin(b*f)*50;
            double z3D = Math.Sin(c*f+e2)*40;
            double ax2 = t*0.008, ay2 = t*0.011;
            double x2  = x3D*Math.Cos(ay2) - z3D*Math.Sin(ay2);
            double z2  = x3D*Math.Sin(ay2) + z3D*Math.Cos(ay2);
            double y2  = y3D*Math.Cos(ax2) - z2*Math.Sin(ax2);
            double fov = 300/(z2+200);
            var pt = new Point(cx+x2*fov, cy+y2*fov);
            double hue = (i/(double)N*360+t*4)%360;
            if (prev.HasValue)
                dc.DrawLine(new Pen(HslBrush(hue,1.0,0.65,0.85),1.2), prev.Value, pt);
            prev = pt;
        }
    }

    // ─── SPIROGRAPH (DrawingContext) ──────────────────────────────────────────

    private void DrawSpirograph(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        double cx=W/2.0, cy=H/2.0;
        double R=65, r=t*0.003%40+15, d2=r*0.8;
        const int N=1200;
        Point? prev=null;
        for (int i=0;i<N;i++)
        {
            double f   = i/(double)N*Math.PI*2*Math.Max(1,(int)(R/Math.Max(1,r)));
            double x2  = (R-r)*Math.Cos(f) + d2*Math.Cos((R-r)/Math.Max(0.1,r)*f);
            double y2  = (R-r)*Math.Sin(f) - d2*Math.Sin((R-r)/Math.Max(0.1,r)*f);
            var pt = new Point(cx+x2, cy+y2);
            double hue = (i/(double)N*360 + t*3)%360;
            if (prev.HasValue)
                dc.DrawLine(new Pen(HslBrush(hue,1.0,0.6,0.9),1.0), prev.Value, pt);
            prev = pt;
        }
    }

    // ─── KOCH SNOWFLAKE (DrawingContext) ──────────────────────────────────────

    private void DrawKoch(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        double cx=W/2.0, cy=H/2.0-10;
        double size=55 + Math.Sin(t*0.02)*15;
        int depth=(int)(t*0.01%5)+1; depth=Math.Clamp(depth,1,5);
        double hue=(t*2)%360;
        var pts = new List<Point>();
        for (int i=0;i<3;i++)
        {
            double a=i/3.0*Math.PI*2 - Math.PI/2;
            pts.Add(new Point(cx+Math.Cos(a)*size, cy+Math.Sin(a)*size));
        }
        pts.Add(pts[0]);
        for (int d=0;d<depth;d++)
        {
            var np=new List<Point>();
            for (int i=0;i<pts.Count-1;i++)
            {
                var p0=pts[i]; var p1=pts[i+1];
                var a2=new Point(p0.X+(p1.X-p0.X)/3, p0.Y+(p1.Y-p0.Y)/3);
                var b2=new Point(p0.X+(p1.X-p0.X)*2/3, p0.Y+(p1.Y-p0.Y)*2/3);
                double mx=(a2.X+b2.X)/2, my=(a2.Y+b2.Y)/2;
                double dx=(b2.X-a2.X)*Math.Sqrt(3)/2, dy=(b2.Y-a2.Y)*Math.Sqrt(3)/2;
                var tip=new Point(mx-dy, my+dx);
                np.Add(p0); np.Add(a2); np.Add(tip); np.Add(b2);
            }
            np.Add(pts[pts.Count-1]); pts=np;
        }
        for (int i=0;i<pts.Count-1;i++)
        {
            double frac=i/(double)(pts.Count-1);
            dc.DrawLine(new Pen(HslBrush((hue+frac*120)%360,1.0,0.6,0.9),1.0), pts[i], pts[i+1]);
        }
    }

    // ─── LORENZ ATTRACTOR (DrawingContext) ────────────────────────────────────

    private double _lx=0.1, _ly=0, _lz=0;
    private readonly List<(double x,double y,double z)> _lorenzPts = new();

    private void DrawLorenz(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        const double sigma=10, rho=28, beta=2.667, dt=0.005;
        for (int i=0;i<8;i++)
        {
            double dx=sigma*(_ly-_lx)*dt;
            double dy=(_lx*(rho-_lz)-_ly)*dt;
            double dz=(_lx*_ly-beta*_lz)*dt;
            _lx+=dx; _ly+=dy; _lz+=dz;
            _lorenzPts.Add((_lx,_ly,_lz));
        }
        if (_lorenzPts.Count>1200) _lorenzPts.RemoveRange(0,8);
        double cx=W/2.0, cy=H/2.0;
        double ax2=t*0.007, ay2=t*0.011;
        Point? prev=null;
        for (int i=0;i<_lorenzPts.Count;i++)
        {
            var (x3,y3,z3)=_lorenzPts[i];
            double nx=x3*Math.Cos(ay2)-z3*Math.Sin(ay2);
            double nz=x3*Math.Sin(ay2)+z3*Math.Cos(ay2);
            double ny=y3*Math.Cos(ax2)-nz*Math.Sin(ax2);
            var pt=new Point(cx+nx*2.8, cy+(ny-25)*2.2);
            double hue=(i/(double)_lorenzPts.Count*280+t*4)%360;
            if (prev.HasValue)
                dc.DrawLine(new Pen(HslBrush(hue,1.0,0.6,0.7),0.8), prev.Value, pt);
            prev=pt;
        }
    }

    // ─── DOUBLE PENDULUM (DrawingContext) ─────────────────────────────────────

    private double _dp1=Math.PI*0.6, _dp2=Math.PI*0.8, _dv1=0, _dv2=0;
    private readonly List<Point> _dpTrail = new();

    private void DrawDoublePendulum(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0,0,W,H));
        const double g=9.8, l1=50, l2=45, m1=1, m2=1, dt=0.04;
        for (int step=0;step<4;step++)
        {
            double d   = _dp2-_dp1;
            double sd  = Math.Sin(d), cd=Math.Cos(d);
            double den = (2*m1+m2-m2*Math.Cos(2*d));
            double a1  = (-g*(2*m1+m2)*Math.Sin(_dp1) - m2*g*Math.Sin(_dp1-2*_dp2)
                         - 2*sd*m2*(_dv2*_dv2*l2 + _dv1*_dv1*l1*cd)) / (l1*den);
            double a2  = (2*sd*((_dv1*_dv1*l1*(m1+m2)) + g*(m1+m2)*Math.Cos(_dp1)
                         + _dv2*_dv2*l2*m2*cd)) / (l2*den);
            _dv1+=a1*dt; _dv2+=a2*dt;
            _dp1+=_dv1*dt; _dp2+=_dv2*dt;
        }
        double ox=W/2.0, oy=H/3.0;
        double x1=ox+l1*Math.Sin(_dp1), y1=oy+l1*Math.Cos(_dp1);
        double x2=x1+l2*Math.Sin(_dp2), y2=y1+l2*Math.Cos(_dp2);
        _dpTrail.Add(new Point(x2,y2));
        if (_dpTrail.Count>300) _dpTrail.RemoveAt(0);
        for (int i=1;i<_dpTrail.Count;i++)
        {
            double f   = i/(double)_dpTrail.Count;
            double hue = (f*360+t*4)%360;
            dc.DrawLine(new Pen(HslBrush(hue,1.0,0.65,f*0.9),1.0), _dpTrail[i-1], _dpTrail[i]);
        }
        var armPen = new Pen(new SolidColorBrush(Color.FromRgb(180,180,180)),2.0);
        dc.DrawLine(armPen, new Point(ox,oy), new Point(x1,y1));
        dc.DrawLine(armPen, new Point(x1,y1), new Point(x2,y2));
        dc.DrawEllipse(Brushes.White, null, new Point(ox,oy),4,4);
        dc.DrawEllipse(HslBrush(120,1.0,0.7,1.0), null, new Point(x1,y1),5,5);
        dc.DrawEllipse(HslBrush(30,1.0,0.7,1.0),  null, new Point(x2,y2),5,5);
    }

    // ─── Helper DrawingContext ─────────────────────────────────────────────────

    // ─── JULIA SET ────────────────────────────────────────────────────────────

    private void DrawJuliaSet(int[] buf, double t)
    {
        // Paramètre c qui orbite sur la cardioïde de Mandelbrot
        double cr = 0.7885 * Math.Cos(t * 0.012);
        double ci = 0.7885 * Math.Sin(t * 0.012);
        double zoom = 1.4 + Math.Sin(t * 0.007) * 0.3;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double zr = (x - W / 2.0) / (W * 0.38 * zoom);
            double zi = (y - H / 2.0) / (H * 0.48 * zoom);
            int i;
            // MAX réduit de 80 à 50 le 2026-07-25 (perf, retour utilisateur) — les
            // pixels "à l'intérieur" de l'ensemble (jamais divergents) sont ceux qui
            // consomment TOUTES les itérations ; sur un widget de 200×180 la perte de
            // détail au-delà de 50 itérations est imperceptible.
            const int MAX = 50;
            for (i = 0; i < MAX; i++)
            {
                double zr2 = zr * zr - zi * zi + cr;
                double zi2 = 2 * zr * zi + ci;
                zr = zr2; zi = zi2;
                if (zr * zr + zi * zi > 4) break;
            }
            if (i == MAX) { buf[y * W + x] = 0; continue; }
            // Smooth coloring
            double smooth = i - Math.Log(Math.Log(Math.Sqrt(zr*zr+zi*zi))) / Math.Log(2);
            double hue = (smooth * 7 + t * 2) % 360;
            buf[y * W + x] = Hsl(hue, 1.0, 0.5);
        }
    }

    // ─── REACTION DIFFUSION (Gray-Scott) ─────────────────────────────────────

    private double[]? _rdA, _rdB, _rdA2, _rdB2;
    private bool _rdInit = false;

    private void DrawReactionDiff(int[] buf, double t)
    {
        if (_rdA == null || !_rdInit)
        {
            _rdA = new double[W * H]; _rdB = new double[W * H];
            _rdA2 = new double[W * H]; _rdB2 = new double[W * H];
            var rng = new Random(42);
            for (int i = 0; i < W * H; i++) { _rdA[i] = 1.0; _rdB[i] = 0.0; }
            // Quelques graines
            for (int s = 0; s < 8; s++)
            {
                int sx = rng.Next(10, W - 10), sy = rng.Next(10, H - 10);
                for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int idx = Math.Clamp(sy+dy,0,H-1)*W + Math.Clamp(sx+dx,0,W-1);
                    _rdB[idx] = 1.0;
                }
            }
            _rdInit = true;
        }
        // Paramètres spots/labyrinthes animés
        double feed = 0.055 + Math.Sin(t * 0.004) * 0.008;
        double kill = 0.062 + Math.Cos(t * 0.003) * 0.005;
        const double dA = 1.0, dB = 0.5, dt2 = 1.0;
        // Effet le plus coûteux du fichier : grille complète (W×H) parcourue plusieurs
        // fois PAR FRAME, chaque cellule lisant 8 voisins avec index enroulés (modulo).
        // Réduit de 4 à 2 itérations/frame le 2026-07-25 (retour utilisateur, "minimum
        // de CPU") — la simulation Gray-Scott évolue un peu plus lentement mais reste
        // visuellement animée ; combiné au passage à 30 fps (cf. constructeur), ce
        // effet coûte désormais ~4x moins qu'avant sur ce fichier.
        for (int iter = 0; iter < 2; iter++)
        {
            for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int idx = y * W + x;
                double a = _rdA![idx], b = _rdB![idx];
                // Laplacien
                double la = -a
                    + (_rdA[((y-1+H)%H)*W+x] + _rdA[((y+1)%H)*W+x]
                    +  _rdA[y*W+(x-1+W)%W]   + _rdA[y*W+(x+1)%W]) * 0.2
                    + (_rdA[((y-1+H)%H)*W+(x-1+W)%W] + _rdA[((y-1+H)%H)*W+(x+1)%W]
                    +  _rdA[((y+1)%H)*W+(x-1+W)%W]   + _rdA[((y+1)%H)*W+(x+1)%W]) * 0.05;
                double lb = -b
                    + (_rdB![((y-1+H)%H)*W+x] + _rdB![((y+1)%H)*W+x]
                    +  _rdB![y*W+(x-1+W)%W]   + _rdB![y*W+(x+1)%W]) * 0.2
                    + (_rdB![((y-1+H)%H)*W+(x-1+W)%W] + _rdB![((y-1+H)%H)*W+(x+1)%W]
                    +  _rdB![((y+1)%H)*W+(x-1+W)%W]   + _rdB![((y+1)%H)*W+(x+1)%W]) * 0.05;
                double abb = a * b * b;
                _rdA2![idx] = Math.Clamp(a + (dA*la - abb + feed*(1-a))*dt2, 0, 1);
                _rdB2![idx] = Math.Clamp(b + (dB*lb + abb - (kill+feed)*b)*dt2, 0, 1);
            }
            (_rdA, _rdA2) = (_rdA2!, _rdA!);
            (_rdB, _rdB2) = (_rdB2!, _rdB!);
        }
        for (int i = 0; i < W * H; i++)
        {
            double v = Math.Clamp(_rdA![i] - _rdB![i], 0, 1);
            buf[i] = Hsl((v * 200 + t * 2) % 360, 1.0, 0.2 + v * 0.5);
        }
    }

    // ─── FOURIER EPICYCLES (DrawingContext) ───────────────────────────────────

    private void DrawFourierEpicycles(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        // Coefficients de Fourier pour dessiner un cœur
        (double freq, double amp, double phase)[] coefs =
        {
            (1,  35, 0),   (2, 20, Math.PI/4), (3, 10, Math.PI/2),
            (4,  6, Math.PI), (5, 4, 0), (7, 3, Math.PI/3), (9, 2, 0),
        };
        double cx = W / 2.0, cy = H / 2.0;
        double px = cx, py = cy;
        double timeScale = t * 0.02;
        for (int ci = 0; ci < coefs.Length; ci++)
        {
            var (freq, amp, phase) = coefs[ci];
            double angle = freq * timeScale + phase;
            double nx = px + amp * Math.Cos(angle);
            double ny = py + amp * Math.Sin(angle);
            double hue = (ci * 40 + t * 2) % 360;
            // Cercle guide
            dc.DrawEllipse(null, new Pen(HslBrush(hue, 0.6, 0.4, 0.3), 0.5),
                new Point(px, py), amp, amp);
            // Bras
            dc.DrawLine(new Pen(HslBrush(hue, 1.0, 0.7, 0.8), 1.0),
                new Point(px, py), new Point(nx, ny));
            px = nx; py = ny;
        }
        // Point final lumineux
        dc.DrawEllipse(Brushes.White, null, new Point(px, py), 3, 3);
        // Tracer la courbe résultante (N points)
        const int N = 200;
        Point? prev = null;
        for (int i = 0; i < N; i++)
        {
            double ts = (timeScale - i * Math.PI * 2 / N + Math.PI * 200) % (Math.PI * 2);
            double qx = cx, qy = cy;
            foreach (var (freq, amp, phase) in coefs)
            {
                qx += amp * Math.Cos(freq * ts + phase);
                qy += amp * Math.Sin(freq * ts + phase);
            }
            var pt = new Point(qx, qy);
            double hue = (i / (double)N * 360 + t * 3) % 360;
            if (prev.HasValue)
                dc.DrawLine(new Pen(HslBrush(hue, 1.0, 0.65, 0.7), 1.0), prev.Value, pt);
            prev = pt;
        }
    }

    // ─── SIERPINSKI TRIANGLE (DrawingContext) ─────────────────────────────────

    private void DrawSierpinski(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        double cx = W / 2.0, cy = H / 2.0;
        double size = 70 + Math.Sin(t * 0.015) * 15;
        double rot  = t * 0.008;
        // Sommets du triangle de base
        Point[] verts = new Point[3];
        for (int i = 0; i < 3; i++)
        {
            double a = i / 3.0 * Math.PI * 2 - Math.PI / 2 + rot;
            verts[i] = new Point(cx + Math.Cos(a) * size, cy + Math.Sin(a) * size);
        }
        void DrawTri(Point a, Point b, Point c, int depth, double hue)
        {
            if (depth == 0)
            {
                var geo = new StreamGeometry();
                using (var ctx2 = geo.Open())
                {
                    ctx2.BeginFigure(a, true, true);
                    ctx2.LineTo(b, true, false);
                    ctx2.LineTo(c, true, false);
                }
                dc.DrawGeometry(HslBrush(hue % 360, 1.0, 0.5, 0.85), null, geo);
                return;
            }
            Point ab = new((a.X+b.X)/2, (a.Y+b.Y)/2);
            Point bc = new((b.X+c.X)/2, (b.Y+c.Y)/2);
            Point ca = new((c.X+a.X)/2, (c.Y+a.Y)/2);
            DrawTri(a, ab, ca, depth-1, hue + 30);
            DrawTri(ab, b, bc, depth-1, hue + 60);
            DrawTri(ca, bc, c, depth-1, hue + 90);
        }
        int maxDepth = (int)(t * 0.005 % 5) + 1;
        maxDepth = Math.Clamp(maxDepth, 1, 5);
        DrawTri(verts[0], verts[1], verts[2], maxDepth, (t * 3) % 360);
    }

    // ─── PARTICLE FOUNTAIN ────────────────────────────────────────────────────

    private const int NPART = 300;
    private double[]? _px, _py, _pvx, _pvy;
    private double[]? _page;
    private int[]?    _pcol;

    private void DrawParticles(int[] buf, double t)
    {
        // Fade
        for (int i = 0; i < buf.Length; i++)
        {
            int c = buf[i];
            int r = Math.Max(0, ((c>>16)&0xFF) - 15);
            int g = Math.Max(0, ((c>>8) &0xFF) - 15);
            int b = Math.Max(0, ( c     &0xFF) - 15);
            buf[i] = (r<<16)|(g<<8)|b;
        }
        if (_px == null)
        {
            _px = new double[NPART]; _py = new double[NPART];
            _pvx = new double[NPART]; _pvy = new double[NPART];
            _page = new double[NPART]; _pcol = new int[NPART];
            var rng = new Random(7);
            for (int i = 0; i < NPART; i++) ResetParticle(i, rng, true);
        }
        var rng2 = new Random((int)(t * 3));
        double ox = W / 2.0 + Math.Sin(t * 0.025) * W * 0.25;
        for (int i = 0; i < NPART; i++)
        {
            _page![i] += 1;
            if (_page[i] > 120) ResetParticle(i, rng2, false);
            _pvx![i] += (rng2.NextDouble() - 0.5) * 0.3;
            _pvy![i] += 0.18; // gravité
            _px![i]  += _pvx[i];
            _py![i]  += _pvy[i];
            int ix = (int)_px[i], iy = (int)_py[i];
            if (ix < 0 || ix >= W || iy < 0 || iy >= H) continue;
            double life = 1.0 - _page[i] / 120.0;
            double hue  = (_pcol![i] + _page[i]) % 360;
            buf[iy * W + ix] = Hsl(hue, 1.0, life * 0.8);
        }
        // Source : point brillant
        SetPix(buf, (int)ox, H - 20, 0xFFFFFF);
    }

    private void ResetParticle(int i, Random rng, bool randomPos)
    {
        double ox = W / 2.0;
        _px![i]   = randomPos ? rng.NextDouble() * W : ox + (rng.NextDouble()-0.5)*6;
        _py![i]   = randomPos ? rng.NextDouble() * H : H - 20;
        double angle = -Math.PI / 2 + (rng.NextDouble()-0.5) * 1.8;
        double speed = 2 + rng.NextDouble() * 4;
        _pvx![i]  = Math.Cos(angle) * speed;
        _pvy![i]  = Math.Sin(angle) * speed;
        _page![i] = randomPos ? rng.NextDouble() * 120 : 0;
        _pcol![i] = rng.Next(360);
    }

    // ─── NEWTON FRACTAL ───────────────────────────────────────────────────────

    // Racines de z^3-1, sorties de la boucle par-pixel le 2026-07-25 (perf) : cette
    // petite table était réallouée (new double[]) à CHAQUE itération de la boucle de
    // convergence, pour CHAQUE pixel — jusqu'à 40 × 36 000 px × 30 fps allocations/s
    // dans le pire cas, une pression GC totalement inutile puisque le contenu est
    // constant. Même principe que le fix RenderTargetBitmap-par-tick du 2026-07-24.
    private static readonly double[] NewtonRoots = { 1, 0, -0.5, 0.866, -0.5, -0.866 };

    private void DrawNewtonFractal(int[] buf, double t)
    {
        // f(z) = z^3 - 1 → 3 racines
        double zoom = 1.8 + Math.Sin(t * 0.008) * 0.4;
        double rotT = t * 0.005;
        double cosR = Math.Cos(rotT), sinR = Math.Sin(rotT);
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            double zr = (x - W/2.0) / (W * 0.4 / zoom);
            double zi = (y - H/2.0) / (H * 0.45 / zoom);
            // Rotation légère
            double tzr = zr*cosR - zi*sinR, tzi = zr*sinR + zi*cosR;
            zr = tzr; zi = tzi;
            int root = -1; int iter;
            // 40 → 25 le 2026-07-25 (perf) : au-delà, gain de netteté imperceptible
            // sur un widget de 200×180.
            const int MAX_ITER = 25;
            for (iter = 0; iter < MAX_ITER; iter++)
            {
                double r2 = zr*zr + zi*zi;
                if (r2 < 1e-10) break;
                // z^3
                double zr3 = zr*(zr*zr - 3*zi*zi);
                double zi3 = zi*(3*zr*zr - zi*zi);
                // z^3 - 1
                double nr = zr3 - 1, ni = zi3;
                // 3z^2
                double dr = 3*(zr*zr - zi*zi), di = 6*zr*zi;
                // Division (z^3-1)/(3z^2)
                double denom = dr*dr + di*di + 1e-10;
                double qr = (nr*dr + ni*di)/denom;
                double qi = (ni*dr - nr*di)/denom;
                zr -= qr; zi -= qi;
                // Racine la plus proche
                for (int ri = 0; ri < 3; ri++)
                {
                    double dr2 = zr-NewtonRoots[ri*2], di2 = zi-NewtonRoots[ri*2+1];
                    if (dr2*dr2+di2*di2 < 0.001) { root = ri; break; }
                }
                if (root >= 0) break;
            }
            if (root < 0) { buf[y*W+x] = 0; continue; }
            double hue = root * 120.0 + t * 2;
            double l   = 0.3 + (1.0 - iter/(double)MAX_ITER) * 0.45;
            buf[y*W+x] = Hsl(hue % 360, 1.0, l);
        }
    }

    // ─── CLIFFORD ATTRACTOR (DrawingContext) ──────────────────────────────────

    private readonly List<(double x, double y)> _cliffordPts = new();
    private double _clx = 0.1, _cly = 0;

    private void DrawClifford(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        // Paramètres qui évoluent lentement
        double a = -1.7 + Math.Sin(t * 0.004) * 0.3;
        double b =  1.8 + Math.Cos(t * 0.003) * 0.3;
        double c =  1.9 + Math.Sin(t * 0.005) * 0.2;
        double d = -0.4 + Math.Cos(t * 0.006) * 0.3;
        // Ajouter des points
        for (int i = 0; i < 600; i++)
        {
            double nx = Math.Sin(a * _cly) + c * Math.Cos(a * _clx);
            double ny = Math.Sin(b * _clx) + d * Math.Cos(b * _cly);
            _clx = nx; _cly = ny;
            _cliffordPts.Add((nx, ny));
        }
        if (_cliffordPts.Count > 18000) _cliffordPts.RemoveRange(0, 600);
        double scale = 38.0;
        double cx = W / 2.0, cy = H / 2.0;
        for (int i = 0; i < _cliffordPts.Count; i++)
        {
            var (px, py) = _cliffordPts[i];
            double sx = cx + px * scale;
            double sy = cy + py * scale;
            if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
            double hue = (i / (double)_cliffordPts.Count * 300 + t * 3) % 360;
            double alpha = Math.Min(1.0, i / 3000.0);
            dc.DrawEllipse(HslBrush(hue, 1.0, 0.65, alpha * 0.4), null,
                new Point(sx, sy), 0.7, 0.7);
        }
    }

    // ─── BARNSLEY FERN (DrawingContext) ───────────────────────────────────────

    private readonly List<(double x, double y)> _fernPts = new();
    private double _fx = 0, _fy = 0;

    private void DrawBarnsleyFern(DrawingContext dc, double t)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0, 5, 0)), null, new Rect(0, 0, W, H));
        var rng = new Random((int)t);
        for (int i = 0; i < 2000; i++)
        {
            double r = rng.NextDouble();
            double nx, ny;
            if      (r < 0.01) { nx = 0;                    ny = 0.16 * _fy; }
            else if (r < 0.86) { nx = 0.85*_fx + 0.04*_fy; ny = -0.04*_fx + 0.85*_fy + 1.6; }
            else if (r < 0.93) { nx = 0.20*_fx - 0.26*_fy; ny =  0.23*_fx + 0.22*_fy + 1.6; }
            else                { nx =-0.15*_fx + 0.28*_fy; ny =  0.26*_fx + 0.24*_fy + 0.44; }
            _fx = nx; _fy = ny;
            _fernPts.Add((nx, ny));
        }
        if (_fernPts.Count > 60000) _fernPts.RemoveRange(0, 2000);
        double scaleX = W * 0.085, scaleY = H * 0.088;
        double offX = W * 0.48, offY = H * 0.97;
        for (int i = 0; i < _fernPts.Count; i++)
        {
            var (px, py) = _fernPts[i];
            double sx = offX + px * scaleX;
            double sy = offY - py * scaleY;
            if (sx < 0 || sx >= W || sy < 0 || sy >= H) continue;
            double greenShade = 0.3 + (py / 10.0) * 0.5;
            double hue = 110 + Math.Sin(t * 0.02 + py * 0.3) * 15;
            dc.DrawEllipse(HslBrush(hue, 0.9, Math.Clamp(greenShade, 0.2, 0.75), 0.6),
                null, new Point(sx, sy), 0.6, 0.6);
        }
    }

    // ─── HARMONOGRAPH (DrawingContext) ────────────────────────────────────────

    private void DrawHarmonograph(DrawingContext dc, double t)
    {
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, W, H));
        // Paramètres des pendules qui dérivent lentement
        double f1 = 2 + Math.Sin(t * 0.003) * 0.02;
        double f2 = 3 + Math.Cos(t * 0.004) * 0.02;
        double f3 = 2 + Math.Cos(t * 0.005) * 0.02;
        double f4 = 3 + Math.Sin(t * 0.002) * 0.02;
        double p1 = t * 0.005;
        double p2 = Math.PI / 4 + t * 0.003;
        const double d1 = 0.0005; // amortissement (d2 non utilisé)
        const int N = 3000;
        double cx = W / 2.0, cy = H / 2.0;
        double amp = Math.Min(W, H) * 0.42;
        Point? prev = null;
        for (int i = 0; i < N; i++)
        {
            double s   = i * 0.04;
            double decay = Math.Exp(-d1 * s);
            double x2  = amp * decay * (Math.Sin(f1*s + p1)*0.6 + Math.Sin(f3*s)*0.4);
            double y2  = amp * decay * (Math.Sin(f2*s + p2)*0.6 + Math.Sin(f4*s)*0.4);
            var pt = new Point(cx + x2, cy + y2);
            double hue = (i / (double)N * 360 + t * 2) % 360;
            double alpha = Math.Min(1.0, (double)i / 200) * decay * 0.8;
            if (prev.HasValue && alpha > 0.05)
                dc.DrawLine(new Pen(HslBrush(hue, 1.0, 0.6, alpha), 0.8), prev.Value, pt);
            prev = pt;
        }
    }

    private static SolidColorBrush HslBrush(double h, double s, double l, double a)
    {
        h = ((h % 360) + 360) % 360;
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
        return new SolidColorBrush(Color.FromArgb(
            (byte)(a * 255), (byte)((r + m) * 255),
            (byte)((g + m) * 255), (byte)((b + m) * 255)));
    }
}

internal record DemoEffect(
    string Name,
    Action<int[], double>?  DrawPixels,
    Action<DrawingContext, double>? DrawVisual);
