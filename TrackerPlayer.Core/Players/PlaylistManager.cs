using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TrackerPlayer.Core.Players
{
    /// <summary>
    /// Gère la playlist pour le mode "Long Play" — lecture séquentielle de fichiers.
    ///
    /// REGROUPEMENT AUTOMATIQUE :
    /// Si plusieurs fichiers partagent le même préfixe (avant #, _, -, espace+numéro…)
    /// ils sont triés numériquement et regroupés ensemble.
    ///
    /// Exemples :
    ///   "Echo [LSD] - Joes#00.mod"   → groupe "Echo [LSD] - Joes", ordre 0
    ///   "Echo [LSD] - Joes#01.mod"   → groupe "Echo [LSD] - Joes", ordre 1
    ///   "mdat.intro_and_title"        → fichier unique (préfixe "mdat")
    ///   "song_part1.it"               → groupe "song_part", ordre 1
    ///   "song_part2.it"               → groupe "song_part", ordre 2
    /// </summary>
    public sealed class PlaylistManager
    {
        private readonly List<string> _files = new();
        private int _currentIndex = -1;

        public IReadOnlyList<string> Files  => _files;
        public int CurrentIndex            => _currentIndex;
        public bool HasFiles               => _files.Count > 0;
        public bool HasNext                => _currentIndex < _files.Count - 1;
        public bool HasPrev                => _currentIndex > 0;

        public string? CurrentFile =>
            (_currentIndex >= 0 && _currentIndex < _files.Count)
                ? _files[_currentIndex] : null;

        public string? NextFile =>
            (_currentIndex + 1 < _files.Count)
                ? _files[_currentIndex + 1] : null;

        /// <summary>
        /// Charge un ou plusieurs fichiers dans la playlist.
        /// Si plusieurs fichiers sont fournis, ils sont triés intelligemment par groupe+numéro.
        /// </summary>
        public void Load(IEnumerable<string> paths)
        {
            _files.Clear();
            _currentIndex = -1;

            var sorted = SortFiles(paths.Where(File.Exists).ToList());
            _files.AddRange(sorted);

            if (_files.Count > 0)
                _currentIndex = 0;
        }

        /// <summary>Avance au fichier suivant. Retourne true si possible.</summary>
        public bool MoveNext()
        {
            if (!HasNext) return false;
            _currentIndex++;
            return true;
        }

        /// <summary>Recule au fichier précédent. Retourne true si possible.</summary>
        public bool MovePrev()
        {
            if (!HasPrev) return false;
            _currentIndex--;
            return true;
        }

        /// <summary>Saute à un index spécifique.</summary>
        public bool MoveTo(int index)
        {
            if (index < 0 || index >= _files.Count) return false;
            _currentIndex = index;
            return true;
        }

        /// <summary>
        /// Trie les fichiers : d'abord par groupe (préfixe commun), puis numériquement.
        /// Ex : Joes#00, Joes#01, Joes#02 → triés dans cet ordre.
        /// Fichiers sans groupe → triés alphabétiquement à la fin.
        /// </summary>
        private static List<string> SortFiles(List<string> paths)
        {
            if (paths.Count <= 1) return paths;

            var entries = paths
                .Select(p => new { Path = p, Info = ParseFileName(p) })
                .ToList();

            // Regrouper par préfixe
            var groups = entries
                .GroupBy(e => e.Info.Prefix, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var result = new List<string>();
            foreach (var group in groups)
            {
                var sorted = group
                    .OrderBy(e => e.Info.Number)
                    .ThenBy(e => e.Info.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Path);
                result.AddRange(sorted);
            }
            return result;
        }

        /// <summary>
        /// Parse le nom de fichier pour extraire le préfixe et le numéro d'ordre.
        ///
        /// Patterns reconnus (ordre de priorité) :
        ///   "name#NN.ext"     → prefix="name", number=NN   (style Amiga LSD)
        ///   "name_NNN.ext"    → prefix="name_", number=NNN  (underscore+chiffres en fin)
        ///   "name NNN.ext"    → prefix="name ", number=NNN  (espace+chiffres en fin)
        ///   "name-NN.ext"     → prefix="name-", number=NN   (tiret+chiffres en fin)
        ///   "name(NN).ext"    → prefix="name", number=NN   (parens)
        ///   "nameNN.ext"      → prefix="name", number=NN   (chiffres collés en fin)
        ///   autres            → prefix=nom complet, number=0
        /// </summary>
        public static (string Prefix, int Number, string Name) ParseFileName(string path)
        {
            var name = Path.GetFileNameWithoutExtension(path);

            // Pattern 1 : "texte#NN" (style Amiga demo)
            var m = Regex.Match(name, @"^(.+?)#(\d+)\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Pattern 2 : "texte_NNN" (underscore + chiffres en fin)
            m = Regex.Match(name, @"^(.+[^0-9])_(\d+)\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Pattern 3 : "texte NNN" (espace + chiffres en fin)
            m = Regex.Match(name, @"^(.+[^0-9]) (\d+)\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Pattern 4 : "texte-NN" (tiret + chiffres en fin)
            m = Regex.Match(name, @"^(.+[^0-9])-(\d+)\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Pattern 5 : "texte(NN)" (parenthèses)
            m = Regex.Match(name, @"^(.+?)\((\d+)\)\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Pattern 6 : "texteNN" (chiffres collés en fin, min 2 chiffres)
            m = Regex.Match(name, @"^(.+?[^0-9])(\d{2,})\s*$");
            if (m.Success)
                return (m.Groups[1].Value, int.Parse(m.Groups[2].Value), name);

            // Aucun pattern → fichier unique, groupe = nom complet
            return (name, 0, name);
        }

        /// <summary>
        /// Détecte si une liste de fichiers forme une séquence LongPlay
        /// (même préfixe, numéros consécutifs).
        /// </summary>
        public static bool IsLongPlaySequence(IEnumerable<string> paths)
        {
            var list = paths.ToList();
            if (list.Count < 2) return false;

            var infos = list.Select(p => ParseFileName(p)).ToList();
            var prefixes = infos.Select(i => i.Prefix).Distinct(StringComparer.OrdinalIgnoreCase);
            return prefixes.Count() == 1;  // un seul préfixe commun
        }
    }
}
