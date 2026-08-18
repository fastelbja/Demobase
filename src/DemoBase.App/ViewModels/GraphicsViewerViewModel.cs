using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DemoBase.App.ViewModels;

// ─── Extensions image supportées ─────────────────────────────────────────────

public static class ImageExtensions
{
    // Formats natifs WPF / WIC
    private static readonly HashSet<string> NativeFormats =
    [
        ".bmp", ".jpg", ".jpeg", ".png", ".gif", ".tiff", ".tif",
        ".wdp", ".ico",
    ];

    // Formats lisibles via décodeur natif C# (PCX…)
    private static readonly HashSet<string> GdiFormats =
    [
        ".pcx",   // PC Paintbrush — commun sur DOS/PC demoscene
        ".tga",   // Targa — fréquent sur Amiga/PC
        ".pcc",   // PC Paintbrush variant
    ];

    // Formats ANSI art — rendu natif C# via AnsiRenderer
    // 2026-07-30, retour utilisateur : releases "ASCII Art" (fréquentes sur Amiga) —
    // un seul fichier .txt/.nfo/.diz, SANS codes couleur ANSI, mais AnsiRenderer
    // affiche déjà très bien du texte "brut" (police CP437 fixe, un code ANSI en
    // moins ne change rien au rendu). Avant : .txt/.nfo/.diz étaient dans Excluded,
    // donc une release Graphics ne contenant QUE ce type de fichier n'affichait rien
    // du tout ("ces fichiers textes n'apparaissent pas quand ils sont seuls"). Cette
    // liste ne s'applique QU'à ce viewer (GraphicsViewer, actif uniquement pour les
    // releases Supertype=="graphics", cf. ReleaseViewModels.ShowGraphicsAsync) — pas
    // de risque de faire apparaître le readme.txt d'une Production/Music comme "image".
    private static readonly HashSet<string> AnsiFormats =
    [
        ".ans", ".asc", ".ascii",
        ".txt", ".nfo", ".diz",
    ];

    // Sous-ensemble de AnsiFormats qui n'est PAS l'œuvre elle-même mais un fichier
    // d'info/crédits accompagnant généralement une vraie image — utilisé uniquement
    // pour la priorité de sélection automatique (cf. LoadAsync), pas pour le rendu
    // (identique, via AnsiRenderer, pour .ans/.asc/.ascii comme pour .txt/.nfo/.diz).
    private static readonly HashSet<string> TextInfoFormats = [".txt", ".nfo", ".diz"];

    // Formats demoscene (via recoil2png)
    private static readonly HashSet<string> SceneFormats =
    [
        ".lbm", ".ilbm", ".iff",
        ".scr",
        ".pi", ".pi1", ".pi2", ".pi3",   // Degas / Degas Elite (Atari ST) — 2026-07-30
        ".pc1", ".pc2", ".pc3",          // Degas Elite compressé (Atari ST)
        ".neo",
        ".img", ".cel", ".bbm",
    ];

    private static readonly HashSet<string> Excluded =
    [
        ".dsk", ".bin", ".t64", ".d64", ".prg",
        ".adf", ".hdf", ".ipf", ".st",
        ".tap", ".tzx",
        ".exe", ".com", ".bat",
        ".dat",
        ".mod", ".s3m", ".xm", ".it",
        // .txt/.nfo/.diz retirés d'ici (2026-07-30) — désormais dans AnsiFormats,
        // affichables comme texte/ASCII art via AnsiRenderer.
    ];

    public static bool IsNative(string filename)
        => NativeFormats.Contains(Path.GetExtension(filename).ToLowerInvariant());
    public static bool IsGdi(string filename)
        => GdiFormats.Contains(Path.GetExtension(filename).ToLowerInvariant());
    public static bool IsAnsi(string filename)
        => AnsiFormats.Contains(Path.GetExtension(filename).ToLowerInvariant());
    public static bool IsTextInfo(string filename)
        => TextInfoFormats.Contains(Path.GetExtension(filename).ToLowerInvariant());
    public static bool IsScene(string filename)
        => SceneFormats.Contains(Path.GetExtension(filename).ToLowerInvariant());
    public static bool IsDisplayable(string filename)
        => IsNative(filename) || IsGdi(filename) || IsAnsi(filename) || IsScene(filename);
    public static bool IsExcluded(string filename)
        => Excluded.Contains(Path.GetExtension(filename).ToLowerInvariant());
}

// ─── DTO entrée ZIP ───────────────────────────────────────────────────────────

public class ImageEntryDto
{
    public string Name      { get; set; } = string.Empty;
    public string FullPath  { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long   Size      { get; set; }
    public bool   IsNative  { get; set; }
    public bool   IsGdi     { get; set; }
    public bool   IsAnsi    { get; set; }
    // 2026-07-30 : .txt/.nfo/.diz — rendu identique à IsAnsi (même AnsiRenderer),
    // mais jamais prioritaire à la sélection automatique face à une vraie image
    // (cf. ImageExtensions.TextInfoFormats et GraphicsViewerViewModel.LoadAsync).
    public bool   IsTextInfo { get; set; }
    public string SizeLabel => Size < 1024 ? $"{Size} B"
                             : Size < 1024 * 1024 ? $"{Size / 1024} KB"
                             : $"{Size / 1024 / 1024:F1} MB";
}

// ─── ViewModel ────────────────────────────────────────────────────────────────

public partial class GraphicsViewerViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private List<ImageEntryDto>  _entries = [];
    [ObservableProperty] private ImageEntryDto?       _selectedEntry;

    partial void OnSelectedEntryChanged(ImageEntryDto? value)
    {
        if (value != null && !_selecting)
            _ = SelectEntryAsync(value);
    }

    [ObservableProperty] private BitmapSource?  _currentImage;
    [ObservableProperty] private string         _statusMessage = string.Empty;
    [ObservableProperty] private bool           _isLoading;

    // ── Animation GIF ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool   _isAnimating;
    [ObservableProperty] private bool   _animPaused;
    [ObservableProperty] private int    _frameCount;
    [ObservableProperty] private int    _currentFrameIndex; // 1-based pour l'UI

    public string AnimLabel => FrameCount > 1
        ? $"Frame {CurrentFrameIndex}/{FrameCount}"
        : string.Empty;

    private List<(BitmapSource Frame, int DelayMs)>? _animFrames;
    private DispatcherTimer?                          _animTimer;
    private int                                       _animIndex;

    partial void OnCurrentFrameIndexChanged(int value)
        => OnPropertyChanged(nameof(AnimLabel));
    partial void OnFrameCountChanged(int value)
        => OnPropertyChanged(nameof(AnimLabel));

    [RelayCommand]
    private void ToggleAnimation()
    {
        if (_animTimer == null) return;
        AnimPaused = !AnimPaused;
        if (AnimPaused) _animTimer.Stop();
        else            _animTimer.Start();
    }

    private void StopAnimation()
    {
        _animTimer?.Stop();
        _animTimer  = null;
        _animFrames = null; // libère les BitmapSource (GC peut les collecter)
        IsAnimating       = false;
        AnimPaused        = false;
        FrameCount        = 0;
        CurrentFrameIndex = 0;
    }

    private void StartAnimation(List<(BitmapSource Frame, int DelayMs)> frames)
    {
        StopAnimation();
        if (frames.Count <= 1)
        {
            CurrentImage = frames[0].Frame;
            return;
        }

        _animFrames       = frames;
        _animIndex        = 0;
        FrameCount        = frames.Count;
        CurrentFrameIndex = 1;
        IsAnimating       = true;
        CurrentImage      = frames[0].Frame;

        _animTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(frames[0].DelayMs)
        };
        _animTimer.Tick += (_, _) =>
        {
            if (_animFrames == null) return;
            _animIndex        = (_animIndex + 1) % _animFrames.Count;
            CurrentImage      = _animFrames[_animIndex].Frame;
            CurrentFrameIndex = _animIndex + 1;
            _animTimer!.Interval = TimeSpan.FromMilliseconds(_animFrames[_animIndex].DelayMs);
        };
        _animTimer.Start();
    }

    // ─────────────────────────────────────────────────────────────────────────

    private string? _zipPath;
    private string? _recoil2PngPath;
    private bool    _selecting;
    private CancellationTokenSource? _cts;

    public bool HasEntries => Entries.Count > 0;

    public void SetRecoilPath(string? path) => _recoil2PngPath = path;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        StopAnimation();
        CurrentImage  = null;
        Entries       = [];
    }

    // ── Chargement ────────────────────────────────────────────────────────────

    public async Task LoadAsync(string zipPath)
    {
        // Annuler ET disposer le CTS précédent
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        StopAnimation();
        _zipPath      = zipPath;
        IsLoading     = true;
        CurrentImage  = null;
        SelectedEntry = null;
        StatusMessage = string.Empty;

        try
        {
            var entries = await Task.Run(() => ScanZip(zipPath), token);
            if (token.IsCancellationRequested) return;

            Entries = entries;
            OnPropertyChanged(nameof(HasEntries));

            if (entries.Count == 0)
            {
                StatusMessage = "Aucune image trouvée dans ce fichier.";
                return;
            }

            // 2026-07-30, retour utilisateur : .txt/.nfo/.diz sont maintenant affichables
            // (cf. AnsiFormats) mais ne doivent JAMAIS passer devant une vraie image à la
            // sélection automatique — ce sont le plus souvent des crédits/infos qui
            // accompagnent l'image principale, pas l'œuvre elle-même (contrairement à un
            // vrai .ans/.asc d'art ANSI, qui reste prioritaire comme avant).
            var first = entries.FirstOrDefault(e => e.IsNative)
                     ?? entries.FirstOrDefault(e => e.IsGdi)
                     ?? entries.FirstOrDefault(e => e.IsAnsi && !e.IsTextInfo)
                     ?? entries.FirstOrDefault(e => !e.IsTextInfo)
                     ?? entries.First();

            await SelectEntryAsync(first, token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusMessage = $"Erreur : {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    private static List<ImageEntryDto> ScanZip(string zipPath)
    {
        var result    = new List<ImageEntryDto>();
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            if (ImageExtensions.IsExcluded(entry.Name)) continue;
            if (!ImageExtensions.IsDisplayable(entry.Name)) continue;
            if (!seenPaths.Add(entry.FullName)) continue;

            result.Add(new ImageEntryDto
            {
                Name      = entry.FullName,
                FullPath  = entry.FullName,
                Extension = Path.GetExtension(entry.Name).ToLowerInvariant(),
                Size      = entry.Length,
                IsNative   = ImageExtensions.IsNative(entry.Name),
                IsGdi      = ImageExtensions.IsGdi(entry.Name),
                IsAnsi     = ImageExtensions.IsAnsi(entry.Name),
                IsTextInfo = ImageExtensions.IsTextInfo(entry.Name),
            });
        }
        return result;
    }

    // ── Sélection d'une image ─────────────────────────────────────────────────

    [RelayCommand]
    private async Task SelectEntryAsync(ImageEntryDto? entry)
        => await SelectEntryAsync(entry, CancellationToken.None);

    private async Task SelectEntryAsync(ImageEntryDto? entry, CancellationToken token)
    {
        if (entry == null || _zipPath == null) return;

        var zipSnapshot = _zipPath;

        StopAnimation();
        _selecting    = true;
        SelectedEntry = entry;
        _selecting    = false;
        CurrentImage  = null;
        StatusMessage = string.Empty;
        IsLoading     = true;

        try
        {
            if (token.IsCancellationRequested) return;

            var ext = entry.Extension;

            if (entry.IsAnsi)
            {
                var bmp = await RenderAnsiFromZipAsync(zipSnapshot, entry.FullPath, token);
                if (!token.IsCancellationRequested) CurrentImage = bmp;
            }
            else if (entry.IsNative && ext == ".gif")
            {
                // GIF : détecter si animé et jouer l'animation
                var frames = await Task.Run(
                    () => DecodeGifFrames(zipSnapshot, entry.FullPath), token);
                if (!token.IsCancellationRequested)
                {
                    if (frames.Count > 1)
                        StartAnimation(frames);
                    else
                        CurrentImage = frames.Count > 0 ? frames[0].Frame : null;
                }
            }
            else if (entry.IsNative)
            {
                var bmp = await Task.Run(
                    () => LoadBitmapFromZip(zipSnapshot, entry.FullPath), token);
                if (!token.IsCancellationRequested) CurrentImage = bmp;
            }
            else if (entry.IsGdi)
            {
                // 2026-07-31, retour utilisateur ("les .pcx ne s'affichent pas. est-ce
                // que ça passe par recoil ?") : non — IsGdi routait EXCLUSIVEMENT vers
                // le décodeur PCX natif ci-dessous (LoadGdiBitmapFromZip/DecodePcx), qui
                // ne gère que 1 plan×8bpp (palette 256 couleurs) et 3 plans×8bpp (24
                // bits) — pas les variantes EGA/16 couleurs (4 plans×1bpp, palette EGA
                // 48 octets dans l'en-tête, pas la palette 768 octets en fin de fichier
                // utilisée pour le cas 8bpp), qui lèvent une exception ("Format PCX non
                // supporté : 4 plane(s) × 1 bpp") sans jamais essayer recoil2png, même
                // configuré. Repli ajouté : si le décodeur natif échoue ET que
                // recoil2png est configuré, on retente via ConvertWithRecoil (déjà
                // utilisé pour les formats SceneFormats ci-dessous) avant d'abandonner.
                BitmapSource? bmp;
                try
                {
                    bmp = await Task.Run(
                        () => LoadGdiBitmapFromZip(zipSnapshot, entry.FullPath), token);
                }
                catch (Exception) when (!string.IsNullOrEmpty(_recoil2PngPath)
                                         && File.Exists(_recoil2PngPath))
                {
                    bmp = await Task.Run(
                        () => ConvertWithRecoil(zipSnapshot, entry, _recoil2PngPath!), token);
                }
                if (!token.IsCancellationRequested) CurrentImage = bmp;
            }
            else if (!string.IsNullOrEmpty(_recoil2PngPath) && File.Exists(_recoil2PngPath))
            {
                var bmp = await Task.Run(
                    () => ConvertWithRecoil(zipSnapshot, entry, _recoil2PngPath!), token);
                if (!token.IsCancellationRequested) CurrentImage = bmp;
            }
            else
            {
                StatusMessage = $"Format {entry.Extension.ToUpperInvariant()} — aperçu non disponible.\n" +
                                "Configurez recoil2png.exe dans les Préférences pour afficher ce format.";
                return;
            }

            if (!token.IsCancellationRequested && CurrentImage == null && !IsAnimating)
                StatusMessage = $"Impossible de décoder {entry.Name}.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusMessage = $"Impossible d'afficher l'image : {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    // ── GIF animé ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Décode toutes les frames d'un GIF et leurs délais.
    /// Délai stocké dans les métadonnées : /grctlext/Delay (centièmes de seconde).
    /// </summary>
    private static List<(BitmapSource Frame, int DelayMs)> DecodeGifFrames(
        string zipPath, string entryPath)
    {
        byte[] data;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry(entryPath)
                ?? throw new FileNotFoundException($"'{entryPath}' introuvable dans le ZIP.");
            using var stream = entry.Open();
            using var ms     = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        using var gifStream = new MemoryStream(data);
        var decoder = new GifBitmapDecoder(
            gifStream,
            BitmapCreateOptions.PreservePixelFormat,
            BitmapCacheOption.OnLoad);

        var result = new List<(BitmapSource, int)>();
        // Limiter à 200 frames max — les GIFs très longs peuvent dépasser 100 MB
        const int MaxFrames = 200;
        foreach (var frame in decoder.Frames.Take(MaxFrames))
        {
            int delayMs = 100; // défaut 100ms si métadonnées absentes
            try
            {
                if (frame.Metadata is BitmapMetadata meta)
                {
                    var delayObj = meta.GetQuery("/grctlext/Delay");
                    if (delayObj is ushort d)
                        delayMs = Math.Max(20, d * 10); // centièmes de sec → ms
                }
            }
            catch { /* métadonnées absentes → délai par défaut */ }

            var frozen = frame.Clone();
            frozen.Freeze();
            result.Add((frozen, delayMs));
        }
        return result;
    }

    // ── Chargement natif WPF ─────────────────────────────────────────────────

    private static BitmapSource LoadBitmapFromZip(string zipPath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryPath)
            ?? throw new FileNotFoundException($"'{entryPath}' introuvable dans le ZIP.");

        using var stream = entry.Open();
        var ms = new MemoryStream();
        stream.CopyTo(ms);
        ms.Position = 0;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption  = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = ms;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    // ── Décodeur PCX natif C# ────────────────────────────────────────────────

    private static BitmapSource? LoadGdiBitmapFromZip(string zipPath, string entryPath)
    {
        byte[] data;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.GetEntry(entryPath)
                ?? throw new FileNotFoundException($"'{entryPath}' introuvable dans le ZIP.");
            using var stream = entry.Open();
            using var ms     = new MemoryStream();
            stream.CopyTo(ms);
            data = ms.ToArray();
        }

        var ext = Path.GetExtension(entryPath).ToLowerInvariant();
        return ext switch
        {
            ".pcx" => DecodePcx(data),
            _      => DecodePcx(data),
        };
    }

    private static BitmapSource? DecodePcx(byte[] data)
    {
        if (data.Length < 128) throw new Exception("Fichier PCX trop court.");
        if (data[0] != 0x0A)  throw new Exception("Signature PCX invalide.");

        int bpp     = data[3];
        int xmin    = data[4]  | (data[5]  << 8);
        int ymin    = data[6]  | (data[7]  << 8);
        int xmax    = data[8]  | (data[9]  << 8);
        int ymax    = data[10] | (data[11] << 8);
        int nplanes = data[65];
        int bpl     = data[66] | (data[67] << 8);

        int width  = xmax - xmin + 1;
        int height = ymax - ymin + 1;
        int stride = nplanes * bpl;

        if (width <= 0 || height <= 0 || width > 8192 || height > 8192)
            throw new Exception($"Dimensions PCX invalides : {width}×{height}.");

        // Décompression RLE
        var scanlines = new byte[height * stride];
        int src = 128; int dst = 0;

        while (dst < scanlines.Length && src < data.Length)
        {
            byte b = data[src++];
            if ((b & 0xC0) == 0xC0)
            {
                int  count = b & 0x3F;
                byte val   = src < data.Length ? data[src++] : (byte)0;
                int  end   = Math.Min(dst + count, scanlines.Length);
                while (dst < end) scanlines[dst++] = val;
            }
            else { scanlines[dst++] = b; }
        }

        var pixels = new byte[width * height * 4];

        if (nplanes == 1 && bpp == 8)
        {
            byte[] palette = new byte[768];
            if (data.Length >= 769 && data[data.Length - 769] == 0x0C)
                Array.Copy(data, data.Length - 768, palette, 0, 768);

            for (int y = 0; y < height; y++)
            {
                int rowSrc = y * stride; int rowDst = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int idx = scanlines[rowSrc + x] * 3;
                    int p   = rowDst + x * 4;
                    pixels[p + 2] = palette[idx];
                    pixels[p + 1] = palette[idx + 1];
                    pixels[p + 0] = palette[idx + 2];
                    pixels[p + 3] = 255;
                }
            }
        }
        else if (nplanes == 3 && bpp == 8)
        {
            for (int y = 0; y < height; y++)
            {
                int rowSrc = y * stride; int rowDst = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int p   = rowDst + x * 4;
                    pixels[p + 2] = scanlines[rowSrc + x];
                    pixels[p + 1] = scanlines[rowSrc + bpl + x];
                    pixels[p + 0] = scanlines[rowSrc + 2 * bpl + x];
                    pixels[p + 3] = 255;
                }
            }
        }
        // 2026-07-31, retour utilisateur ("les .pcx ne s'affichent pas" puis, après le
        // repli recoil2png, "voici un pcx qui ne s'affiche pas. recoil ne produit
        // rien") : mode EGA/VGA 16 couleurs (1 à 4 plans × 1bpp) — format PCX très
        // courant (DOS, "MODE 12h"), pas du tout exotique, juste absent des deux cas
        // gérés ci-dessus. Vérifié par script Python sur le fichier BABYFACE.PCX fourni
        // (640×480, 4 plans × 1bpp) : décodage RLE exact (153600 octets décompressés =
        // taille attendue pile), image reconstituée cohérente (visage/portrait avec
        // fond à cœurs, aucune erreur). Palette : PAS celle de 768 octets en fin de
        // fichier (réservée au cas 8bpp ci-dessus, absente ici — vérifié, pas de
        // marqueur 0x0C) mais celle de 48 octets (16 couleurs RGB) dans l'en-tête à
        // l'offset 16, comme l'exige le format pour ce mode. Bit de poids faible =
        // plan 0 (convention standard PCX/EGA, confirmée par le rendu correct).
        else if (bpp == 1 && nplanes is >= 1 and <= 4)
        {
            var palette = new byte[16 * 3];
            Array.Copy(data, 16, palette, 0, Math.Min(48, data.Length - 16));

            for (int y = 0; y < height; y++)
            {
                int rowBase = y * stride;
                int rowDst  = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int byteIdx = x / 8;
                    int bitIdx  = 7 - (x % 8);
                    int idx = 0;
                    for (int p = 0; p < nplanes; p++)
                    {
                        int planeOff = rowBase + p * bpl + byteIdx;
                        int bit = (scanlines[planeOff] >> bitIdx) & 1;
                        idx |= bit << p;
                    }
                    int pi = idx * 3;
                    int p2 = rowDst + x * 4;
                    pixels[p2 + 2] = palette[pi];
                    pixels[p2 + 1] = palette[pi + 1];
                    pixels[p2 + 0] = palette[pi + 2];
                    pixels[p2 + 3] = 255;
                }
            }
        }
        else
        {
            throw new Exception(
                $"Format PCX non supporté : {nplanes} plane(s) × {bpp} bpp.");
        }

        var bitmap = BitmapSource.Create(
            width, height, 96, 96,
            System.Windows.Media.PixelFormats.Bgra32,
            null, pixels, width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    // ── ANSI art ─────────────────────────────────────────────────────────────

    private static async Task<BitmapSource?> RenderAnsiFromZipAsync(
        string zipPath, string entryPath, CancellationToken token)
    {
        byte[] rawBytes = await Task.Run(() =>
        {
            using var zip    = ZipFile.OpenRead(zipPath);
            var zipEntry     = zip.GetEntry(entryPath)
                ?? throw new FileNotFoundException($"'{entryPath}' introuvable dans le ZIP.");
            using var stream = zipEntry.Open();
            using var ms     = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }, token);

        if (token.IsCancellationRequested) return null;

        return await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            DemoBase.App.Services.AnsiRenderer.RenderFromBytes(rawBytes));
    }

    // ── recoil2png ────────────────────────────────────────────────────────────

    private static BitmapImage? ConvertWithRecoil(
        string zipPath, ImageEntryDto entry, string recoilExe)
    {
        var tempDir = Path.Combine(
            DemoBase.App.Services.WorkingPaths.GetSubdir("Recoil"),
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var srcPath = Path.Combine(tempDir, Path.GetFileName(entry.FullPath));
        var pngPath = Path.ChangeExtension(srcPath, ".png");

        try
        {
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                var zipEntry = zip.GetEntry(entry.FullPath);
                if (zipEntry == null) return null;
                zipEntry.ExtractToFile(srcPath, overwrite: true);
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName               = recoilExe,
                Arguments              = $"\"{srcPath}\"",
                WorkingDirectory       = tempDir,
                CreateNoWindow         = true,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            proc?.WaitForExit(10_000);

            if (!File.Exists(pngPath))
                throw new Exception("recoil2png n'a pas produit de fichier PNG.");

            var ms = new MemoryStream(File.ReadAllBytes(pngPath));
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
