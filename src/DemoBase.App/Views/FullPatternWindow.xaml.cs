using DemoBase.App.ViewModels;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using TrackerPlayer.Core.Models;
using TrackerPlayer.UI.Controls;

namespace DemoBase.App.Views;

/// <summary>
/// Fenêtre plein-écran affichant le pattern du tracker en mode "vue complète" :
/// tous les canaux visibles simultanément sans défilement horizontal, en texte très
/// compact. Utile pour les modules qui encodent des graphiques par le biais des
/// patterns (le .xm de la capture d'écran utilisateur en est un exemple typique).
///
/// La fenêtre suit la lecture en temps réel (ligne courante surlignée, défilement
/// vertical si le pattern dépasse la hauteur disponible).
///
/// Fermeture : Échap ou clic sur ✕.
/// </summary>
public partial class FullPatternWindow : Window
{
    private readonly SoundtrackPlayerViewModel _vm;
    private readonly FullPatternControl        _control;

    public FullPatternWindow(SoundtrackPlayerViewModel vm)
    {
        InitializeComponent();
        _vm      = vm;
        _control = new FullPatternControl();

        PatternHost.Content = _control;
        UpdateTitle();

        _vm.PropertyChanged += OnVmPropertyChanged;
        Closed              += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _control.Detach();
        };

        // Ajuster la largeur dès que la fenêtre est chargée
        Loaded += (_, _) => AdjustWidth();

        UpdateControl();
    }

    /// <summary>
    /// Calcule et applique la largeur idéale de la fenêtre en fonction du nombre de canaux
    /// du module — avec des marges gauche/droite comme XMPlay.
    /// </summary>
    private void AdjustWidth()
    {
        int ch = _vm.Module?.Channels ?? _vm.CurrentPatternVm?.Channels ?? 0;
        if (ch <= 0) return;

        // Largeur par canal : 2 chars + espacement (≈ FontSz * 2.2 px à 96 DPI)
        const double chW      = 18.0;  // px par canal
        const double rowNumW  = 36.0;  // colonne numéro de ligne
        const double margin   = 20.0 * 2 + 60.0;  // bandeaux (2×Pad) + chrome fenêtre

        double idealW = rowNumW + ch * chW + margin;
        double screenW = System.Windows.SystemParameters.PrimaryScreenWidth;

        Width = Math.Max(480, Math.Min(idealW, screenW - 80));
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is
            nameof(SoundtrackPlayerViewModel.HighlightedRow) or
            nameof(SoundtrackPlayerViewModel.CurrentPatternVm) or
            nameof(SoundtrackPlayerViewModel.Title) or
            nameof(SoundtrackPlayerViewModel.Module) or
            nameof(SoundtrackPlayerViewModel.CurrentPatternIndex) or
            nameof(SoundtrackPlayerViewModel.CurrentOrderIndex))
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (e.PropertyName is nameof(SoundtrackPlayerViewModel.Title)
                                    or nameof(SoundtrackPlayerViewModel.CurrentPatternIndex)
                                    or nameof(SoundtrackPlayerViewModel.CurrentOrderIndex)
                                    or nameof(SoundtrackPlayerViewModel.Module))
                    UpdateTitle();
                else
                    UpdateControl();
            });
        }
    }

    private void UpdateControl()
        => _control.Update(_vm.CurrentPatternVm, _vm.HighlightedRow);

    private void UpdateTitle()
        => TitleLabel.Text = $"Full Pattern View  —  {_vm.Title}  —  " +
                             $"Pattern {_vm.CurrentPatternIndex + 1}/{_vm.Module?.Patterns.Count ?? 0}  " +
                             $"(Ordre {_vm.CurrentOrderIndex + 1}/{_vm.Module?.OrderList.Count ?? 0})  —  " +
                             $"{_vm.Module?.Channels ?? 0} channels";

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>
/// État des valeurs fantômes (ghost notes) précalculé depuis les patterns précédents
/// dans l'OrderList. Contient la dernière valeur active par canal et par type de donnée.
/// </summary>
public sealed class GhostState
{
    public int[] Effects { get; }
    public int[] Notes   { get; }
    public int[] Volumes { get; }

    public GhostState(int[] effects, int[] notes, int[] volumes)
    {
        Effects = effects;
        Notes   = notes;
        Volumes = volumes;
    }
}

// ─── Contrôle de rendu plein-pattern ─────────────────────────────────────────

public sealed class FullPatternControl : FrameworkElement
{
    private readonly DrawingVisual _visual = new();
    protected override int    VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;

    // ── État ──────────────────────────────────────────────────────────────────
    private PatternViewModel? _pattern;
    private int               _currentRow;

    // ── Cache du pattern pré-rendu ────────────────────────────────────────────
    private DrawingGroup?     _cachedDrawing;
    private PatternViewModel? _cachedRef;
    private double            _cachedW;

    // ── Throttle ──────────────────────────────────────────────────────────────
    private bool _renderPending;

    // ── DPI (mis à jour quand l'élément entre dans l'arbre visuel) ───────────
    private double _pixelsPerDip = 1.0;

    // ── Dimensions ────────────────────────────────────────────────────────────
    private const double RowH    = 15;
    private const double RowNumW = 36;
    private const double FontSz  = 10.0;
    // Bandeau noir autour du contenu des patterns (haut, bas, gauche, droite)
    private const double Pad     = 20.0;

    // ── Couleurs ──────────────────────────────────────────────────────────────
    private static readonly SolidColorBrush BrNote       = Frozen(0xCC, 0xCC, 0xCC);
    private static readonly SolidColorBrush BrEffect     = Frozen(0x00, 0xCC, 0x00); // vert vif
    private static readonly SolidColorBrush BrInstrument = Frozen(0x00, 0xCC, 0x00); // même vert : instrument seul
    private static readonly SolidColorBrush BrEmpty      = Frozen(0x1E, 0x1E, 0x1E);
    private static readonly SolidColorBrush BrRowNum     = Frozen(0x55, 0x55, 0x55);
    private static readonly SolidColorBrush BrRowBeat    = Frozen(0x44, 0x88, 0x44);
    private static readonly SolidColorBrush BrHlBg       = Frozen(0x00, 0x3A, 0x3A);
    private static readonly SolidColorBrush BrBeatBg     = Frozen(0x06, 0x0D, 0x06);
    private static readonly SolidColorBrush BrHlNote     = Brushes.White;
    private static readonly SolidColorBrush BrHlFx       = Frozen(0xFF, 0x44, 0xFF); // magenta

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var b2 = new SolidColorBrush(Color.FromRgb(r, g, b));
        b2.Freeze();
        return b2;
    }

    public FullPatternControl()
    {
        AddVisualChild(_visual);
        ClipToBounds = true;

        // Initialiser le typeface ICI (pas besoin d'arbre visuel).
        // _pixelsPerDip = 1.0 fonctionne pour la plupart des écrans ;
        // il est mis à jour avec la valeur exacte dans Loaded.
        _pixelsPerDip = 1.0;

        SizeChanged += (_, _) =>
        {
            _cachedRef     = null;  // force rebuild sur resize
            _renderPending = true;
        };
        Loaded += (_, _) =>
        {
            // Maintenant dans l'arbre visuel : DPI précis disponible.
            _pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            _cachedRef     = null;  // rebuild avec DPI correct
            _renderPending = true;
        };
        CompositionTarget.Rendering += OnCompositionRender;
    }

    public void Detach()
        => CompositionTarget.Rendering -= OnCompositionRender;

    private void OnCompositionRender(object? sender, EventArgs e)
    {
        if (!_renderPending) return;
        _renderPending = false;
        Render();
    }

    // ── API ───────────────────────────────────────────────────────────────────

    public void Update(PatternViewModel? pattern, int currentRow, GhostState? _unused = null)
    {
        if (_pattern != pattern) _cachedRef = null;
        _pattern       = pattern;
        _currentRow    = currentRow;
        _renderPending = true;
    }

    // compat overload (appelé depuis FullPatternWindow.UpdateControl)
    public void Update(PatternViewModel? pattern, int currentRow,
                       int[]? a, int[]? b, int[]? c)
        => Update(pattern, currentRow);

    // ── Rendu principal ───────────────────────────────────────────────────────

    private void Render()
    {
        double w = Math.Max(ActualWidth,  1);
        double h = Math.Max(ActualHeight, 1);

        using var dc = _visual.RenderOpen();
        dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, w, h));

        if (_pattern == null) return;

        // Zone de contenu avec bandeau noir sur les 4 côtés
        double contentW = Math.Max(1, w - Pad * 2);
        double contentH = Math.Max(1, h - Pad * 2);

        int    ch  = _pattern.Channels;
        double chW = Math.Max(1.0, (contentW - RowNumW) / ch);

        // Rebuild du cache si pattern ou largeur ont changé
        if (_cachedRef != _pattern || Math.Abs(_cachedW - contentW) > 0.5)
        {
            _cachedDrawing = BuildPatternDrawing(contentW, chW);
            if (_cachedDrawing != null)
            {
                _cachedRef = _pattern;
                _cachedW   = contentW;
            }
        }

        if (_cachedDrawing == null)
        {
            _renderPending = true;  // réessayer au prochain frame
            return;
        }

        // Offset Y pour centrer la ligne courante dans la zone de contenu
        double yOffset = Pad + contentH / 2.0 - _currentRow * RowH - RowH / 2.0;

        // Clip sur la zone de contenu uniquement (le bandeau noir reste visible)
        dc.PushClip(new RectangleGeometry(new Rect(Pad, Pad, contentW, contentH)));
        dc.PushTransform(new TranslateTransform(Pad, yOffset));
        dc.DrawDrawing(_cachedDrawing);
        dc.Pop();  // transform
        dc.Pop();  // clip

        // Surbrillance de la ligne courante (overlay)
        double hlY = Pad + contentH / 2.0 - RowH / 2.0;
        dc.DrawRectangle(BrHlBg, null, new Rect(Pad, hlY, contentW, RowH));

        DrawFt(dc, _currentRow.ToString("X3"), BrRowBeat, Pad + 2, hlY);

        double x = Pad + RowNumW;
        for (int c = 0; c < ch; c++)
        {
            var (txt, kind) = GetCellDisplay(_pattern.GetCell(_currentRow, c));
            var brush = kind switch
            {
                CellKind.Note       => BrHlNote,
                CellKind.Effect     => BrHlFx,
                CellKind.Instrument => BrHlFx,
                _                   => BrEmpty,
            };
            DrawFt(dc, txt, brush, x + 1, hlY);
            x += chW;
        }
    }

    private enum CellKind { Empty, Note, Instrument, Effect }

    private static (string text, CellKind kind) GetCellDisplay(PatternCell cell)
    {
        // Note réelle (priorité maximale)
        if (cell.Note > 0)
            return (cell.NoteString, CellKind.Note);

        // Effet (byte brut hex 2 chiffres : 0x15→"15", 0x0F→"0F")
        if (cell.Effect != 0 || cell.EffectParam != 0)
            return ($"{cell.Effect:X2}", CellKind.Effect);

        // Colonne volume (0x10-0x50 dans XM)
        if (cell.Volume >= 0)
            return ($"{cell.Volume:X2}", CellKind.Effect);

        // Instrument seul (sans note) — c'est ce qui génère les "21" visibles
        // dans OpenMPT/XMPlay pour les patterns graphiques qui encodent des pixels
        // via des changements d'instrument (retrigger, changement de timbre…)
        if (cell.Instrument > 0)
            return ($"{cell.Instrument:D2}", CellKind.Instrument);

        return ("---", CellKind.Empty);
    }

    // ── Construction du DrawingGroup (une fois par pattern) ───────────────────

    private DrawingGroup? BuildPatternDrawing(double w, double chW)
    {
        if (_pattern == null) return null;

        var group = new DrawingGroup();
        using var dc = group.Open();

        int rows = _pattern.Rows;
        int ch   = _pattern.Channels;

        for (int row = 0; row < rows; row++)
        {
            double y      = row * RowH;
            bool   isBeat = row % 4 == 0;

            if (isBeat)
                dc.DrawRectangle(BrBeatBg, null, new Rect(0, y, w, RowH));

            DrawFt(dc, row.ToString("X3"),
                   isBeat ? BrRowBeat : BrRowNum, 2, y);

            double x = RowNumW;
            for (int c = 0; c < ch; c++)
            {
                var (txt, kind) = GetCellDisplay(_pattern.GetCell(row, c));
                var brush = kind switch
                {
                    CellKind.Note       => BrNote,
                    CellKind.Effect     => BrEffect,
                    CellKind.Instrument => BrInstrument,
                    _                   => BrEmpty,
                };
                DrawFt(dc, txt, brush, x + 1, y);
                x += chW;
            }
        }
        return group;
    }

    // ── FormattedText avec cache ──────────────────────────────────────────────
    // FormattedText est plus robuste que GlyphRun : pas de prérequis d'arbre visuel,
    // pas de gestion manuelle des glyphes. Utilisé pour le DrawingGroup (une fois
    // par pattern) et pour l'overlay ligne courante (~65 appels/frame).

    private readonly Dictionary<(string, Color), FormattedText> _ftCache = new();
    private static readonly System.Globalization.CultureInfo    _cult     =
        System.Globalization.CultureInfo.InvariantCulture;
    private static readonly Typeface _typeface =
        new(new FontFamily("Consolas"), FontStyles.Normal,
            FontWeights.Normal, FontStretches.Normal);

    private void DrawFt(DrawingContext dc, string text, SolidColorBrush brush,
                        double x, double y)
    {
        if (string.IsNullOrEmpty(text)) return;

        var key = (text, brush.Color);
        if (!_ftCache.TryGetValue(key, out var ft))
        {
            if (_ftCache.Count > 2048) _ftCache.Clear();
            ft = new FormattedText(text, _cult, FlowDirection.LeftToRight,
                                   _typeface, FontSz, brush, _pixelsPerDip);
            _ftCache[key] = ft;
        }
        dc.DrawText(ft, new Point(x, y));
    }
}
