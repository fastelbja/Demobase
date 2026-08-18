using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace DemoBase.App.ViewModels;

// ─── Nœud d'arborescence (façon explorateur Windows) ─────────────────────────

/// <summary>
/// Nœud de l'arborescence reconstruite à partir des chemins internes ("/") d'un ZIP
/// "Code Sources" — un nœud dossier par segment de chemin distinct, un nœud fichier par
/// entrée réelle du ZIP. Sert uniquement à l'affichage (TreeView côté vue) : c'est un
/// simple objet d'UI, pas un modèle métier.
/// </summary>
public class CodeSourceTreeNode
{
    public string Name     { get; set; } = string.Empty;
    public bool   IsFolder { get; set; }
    public string? FullPath { get; set; }   // chemin complet dans le ZIP — seulement pour les fichiers
    public long   Size      { get; set; }

    // List<T> (pas ObservableCollection) : l'arborescence est construite une fois par
    // LoadAsync() puis affichée telle quelle — jamais mutée après binding, donc pas
    // besoin de notifications de collection.
    public List<CodeSourceTreeNode> Children { get; set; } = [];

    public string SizeLabel => Size < 1024 ? $"{Size} o"
                             : Size < 1024 * 1024 ? $"{Size / 1024} Ko"
                             : $"{Size / 1024 / 1024:F1} Mo";

    /// <summary>Icône façon explorateur de fichiers — glyphe simple, pas d'icône par extension
    /// (resterait à faire si besoin un jour, mais suffisant pour se repérer dans l'arborescence).</summary>
    public string Icon => IsFolder ? "📁" : "📄";
}

// ─── ViewModel ────────────────────────────────────────────────────────────────

/// <summary>
/// Viewer pour l'onglet "Code Sources" : ouvre le ZIP d'un DatEntry "Sources Code" et
/// affiche son arborescence complète (dossiers/fichiers, façon explorateur Windows) à
/// gauche, avec aperçu texte colorisé (coloration syntaxique légère, voir
/// SimpleCodeHighlighter) du fichier sélectionné à droite. Calqué sur GraphicsViewerViewModel
/// (même pattern LoadAsync(zipPath) / ZipFile.OpenRead à chaque sélection), avec arborescence
/// au lieu d'une liste plate — demande utilisateur explicite ("format explorateur Windows
/// pour garder l'arborescence du fichier").
/// </summary>
public partial class CodeSourceViewerViewModel : ObservableObject, IDisposable
{
    // Fichiers ignorés lors du choix du fichier sélectionné par défaut (pas dans
    // l'arborescence elle-même — tout le contenu du ZIP y apparaît).
    private static readonly HashSet<string> LowPriorityDefaultExts =
        new(StringComparer.OrdinalIgnoreCase) { ".txt", ".nfo", ".diz", ".md" };

    // Taille max de texte chargé en mémoire/affiché — au-delà, aperçu tronqué (protège
    // l'UI contre un fichier "source" qui serait en fait une énorme ressource binaire
    // mal classée, ou un dump généré).
    private const long MaxPreviewBytes = 1_000_000; // ~1 Mo

    [ObservableProperty] private ObservableCollection<CodeSourceTreeNode> _rootNodes = [];
    [ObservableProperty] private CodeSourceTreeNode?                      _selectedNode;
    [ObservableProperty] private string                                  _currentText     = string.Empty;
    [ObservableProperty] private string                                  _currentFileName = string.Empty;
    [ObservableProperty] private string                                  _statusMessage   = string.Empty;
    [ObservableProperty] private bool                                    _isLoading;

    public bool HasEntries => RootNodes.Count > 0;

    partial void OnSelectedNodeChanged(CodeSourceTreeNode? value)
    {
        if (value != null && !value.IsFolder && !_selecting)
            _ = SelectFileAsync(value);
    }

    private string? _zipPath;
    private bool    _selecting;
    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        RootNodes    = [];
        CurrentText  = string.Empty;
        SelectedNode = null;
    }

    // ── Chargement ────────────────────────────────────────────────────────────

    public async Task LoadAsync(string zipPath)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _zipPath      = zipPath;
        IsLoading     = true;
        CurrentText   = string.Empty;
        SelectedNode  = null;
        StatusMessage = string.Empty;

        try
        {
            var roots = await Task.Run(() => ScanZip(zipPath), token);
            if (token.IsCancellationRequested) return;

            RootNodes = new ObservableCollection<CodeSourceTreeNode>(roots);
            OnPropertyChanged(nameof(HasEntries));

            if (RootNodes.Count == 0)
            {
                StatusMessage = "Aucun fichier trouvé dans cette archive.";
                return;
            }

            var first = FindFirstFile(RootNodes);
            if (first != null)
                await SelectFileAsync(first, token);
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

    /// <summary>Premier fichier "intéressant" à sélectionner par défaut à l'ouverture :
    /// on évite juste les .txt/.nfo/.diz/.md (souvent un readme, pas du code) si un
    /// autre fichier existe, sinon on prend ce qu'il y a.</summary>
    private static CodeSourceTreeNode? FindFirstFile(IEnumerable<CodeSourceTreeNode> nodes)
    {
        var allFiles = new List<CodeSourceTreeNode>();
        void Collect(IEnumerable<CodeSourceTreeNode> ns)
        {
            foreach (var n in ns)
            {
                if (n.IsFolder) Collect(n.Children);
                else allFiles.Add(n);
            }
        }
        Collect(nodes);

        return allFiles.FirstOrDefault(f =>
                   !LowPriorityDefaultExts.Contains(Path.GetExtension(f.Name)))
               ?? allFiles.FirstOrDefault();
    }

    /// <summary>Reconstruit l'arborescence dossiers/fichiers à partir des chemins internes
    /// ("/") du ZIP — un nœud dossier par segment distinct, triés dossiers d'abord puis
    /// fichiers, alphabétique insensible à la casse (comme l'explorateur Windows).
    /// Entièrement locale à l'appel (aucun état statique partagé) : ScanZip peut être
    /// exécutée en parallèle pour deux releases différentes (Task.Run) sans risque de
    /// corruption croisée entre deux chargements concurrents.</summary>
    private static List<CodeSourceTreeNode> ScanZip(string zipPath)
    {
        var root = new List<CodeSourceTreeNode>();
        // Un seul dictionnaire, clé = chemin complet du dossier ("A/B/C") → nœud déjà créé,
        // pour éviter de recréer/rechercher linéairement à chaque nouvelle entrée du ZIP.
        // "" (chaîne vide) désigne la racine elle-même, associée à "root".
        var folderByPath = new Dictionary<string, List<CodeSourceTreeNode>>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = root,
        };

        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var entry in zip.Entries.OrderBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // entrée dossier pure — ignorée

            var segments = entry.FullName.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0) continue;

            var pathSoFar = "";
            for (int i = 0; i < segments.Length - 1; i++)
            {
                var parentPath = pathSoFar;
                pathSoFar = pathSoFar.Length == 0 ? segments[i] : $"{pathSoFar}/{segments[i]}";

                if (!folderByPath.TryGetValue(pathSoFar, out var childList))
                {
                    var folder = new CodeSourceTreeNode { Name = segments[i], IsFolder = true };
                    folderByPath[parentPath].Add(folder);
                    childList = folder.Children;
                    folderByPath[pathSoFar] = childList;
                }
            }

            folderByPath[pathSoFar].Add(new CodeSourceTreeNode
            {
                Name     = segments[^1],
                IsFolder = false,
                FullPath = entry.FullName,
                Size     = entry.Length,
            });
        }

        foreach (var list in folderByPath.Values) SortInPlace(list);
        return root;
    }

    private static void SortInPlace(List<CodeSourceTreeNode> nodes) =>
        nodes.Sort((a, b) =>
        {
            if (a.IsFolder != b.IsFolder) return a.IsFolder ? -1 : 1;
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

    // ── Sélection d'un fichier ────────────────────────────────────────────────

    private async Task SelectFileAsync(CodeSourceTreeNode node, CancellationToken token = default)
    {
        if (node.IsFolder || node.FullPath == null || _zipPath == null) return;

        var zipSnapshot = _zipPath;

        _selecting      = true;
        SelectedNode    = node;
        _selecting      = false;
        CurrentText     = string.Empty;
        CurrentFileName = node.Name;
        StatusMessage   = string.Empty;
        IsLoading       = true;

        try
        {
            var (text, truncated) = await Task.Run(() => ReadTextFromZip(zipSnapshot, node.FullPath), token);
            if (token.IsCancellationRequested) return;

            if (text == null)
            {
                StatusMessage = "Fichier binaire — aperçu texte non disponible.";
                return;
            }

            CurrentText = truncated
                ? text + "\n\n… (fichier tronqué — aperçu limité à 1 Mo)"
                : text;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested)
                StatusMessage = $"Impossible de lire ce fichier : {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
                IsLoading = false;
        }
    }

    /// <summary>Lit une entrée du ZIP et la décode en texte. Retourne (null, false) si le
    /// contenu ressemble à du binaire (octets NUL / forte proportion de caractères de
    /// contrôle) plutôt que d'afficher du charabia.</summary>
    private static (string? Text, bool Truncated) ReadTextFromZip(string zipPath, string entryPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.GetEntry(entryPath)
            ?? throw new FileNotFoundException($"'{entryPath}' introuvable dans le ZIP.");

        using var stream = entry.Open();
        using var ms     = new MemoryStream();
        stream.CopyTo(ms);
        var data = ms.ToArray();

        if (data.Length == 0) return (string.Empty, false);
        if (LooksBinary(data)) return (null, false);

        bool truncated = data.Length > MaxPreviewBytes;
        var slice = truncated ? data[..(int)MaxPreviewBytes] : data;

        return (DecodeText(slice), truncated);
    }

    private static bool LooksBinary(byte[] data)
    {
        int checkLen = Math.Min(data.Length, 8000);
        int suspicious = 0;
        for (int i = 0; i < checkLen; i++)
        {
            byte b = data[i];
            if (b == 0) return true; // octet NUL — quasi certainement binaire
            if (b < 8 || (b > 13 && b < 32)) suspicious++;
        }
        return checkLen > 0 && suspicious * 10 > checkLen; // > 10% caractères de contrôle
    }

    private static string DecodeText(byte[] data)
    {
        // BOM UTF-8
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return Encoding.UTF8.GetString(data, 3, data.Length - 3);

        try
        {
            // Décodage UTF-8 strict : lève si des octets ne sont pas de l'UTF-8 valide.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            // Beaucoup de sources demoscene (ASM/BASIC/Pascal des années 80-90) sont en
            // CP437/Latin-1, pas en UTF-8 — Latin1 ne lève jamais (round-trip 1 octet = 1
            // caractère), c'est le filet de sécurité le plus sûr ici.
            return Encoding.Latin1.GetString(data);
        }
    }
}
