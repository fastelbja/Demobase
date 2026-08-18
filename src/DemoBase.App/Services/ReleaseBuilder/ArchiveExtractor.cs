#pragma warning disable CS8618, CS8625, CS0168
// ─────────────────────────────────────────────────────────────────────────────
// ArchiveExtractor — porté tel quel depuis HashService.cs (projet
// DemosceneDownloader_v3, "fortement testé et validé") : extraction
// d'archives (ZIP/RAR/7z/LHA/LZX/GZ/BZ2/TAR), détection de signature
// binaire, vérification CRC après extraction. Logique algorithmique
// INCHANGÉE — seules les parties spécifiques à l'outil d'origine ont été
// retirées (écriture MySQL, re-zip vers production_files, détection vidéo).
// ─────────────────────────────────────────────────────────────────────────────
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.GZip;
using ICSharpCode.SharpZipLib.Tar;
using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.Zip;
using SevenZipExtractor;
using DemosceneDownloader.Services;

namespace DemoBase.App.Services.ReleaseBuilder;

/// <summary>
/// Extraction d'archives multi-formats + calcul de hash, porté du projet de
/// référence fourni par l'utilisateur. Ne PAS modifier la logique interne de
/// ces méthodes (uniquement le câblage externe est adapté à DemoBase).
/// </summary>
public static class ArchiveExtractor
{
    /// <summary>Dispatch principal : extrait une archive selon son extension.
    /// ext attendu en minuscules, avec le point (ex. ".zip").</summary>
    public static bool ExtractAny(string sourceArchive, string dest, string ext)
    {
        if (ext == ".lzx")
        {
            try
            {
                var lzx = new DemosceneDownloader.Services.LZX { DestinationFolder = dest };
                int ret = lzx.ExtractArchive(sourceArchive);
                return ret == 0 || Directory.GetFiles(dest, "*.*", SearchOption.AllDirectories).Any();
            }
            catch { return false; }
        }
        if (sourceArchive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) || ext == ".tgz")
            return ExtractTarGz(sourceArchive, dest);
        if (ext == ".bz2")
            return ExtractTarBz2(sourceArchive, dest);

        return ExtractFile(sourceArchive, dest);
    }

    private static bool CheckFile(ref string filePath)
    {
        if (!File.Exists(filePath)) return false;

        string ext = Path.GetExtension(filePath).ToLower();
        if (ext == ".sb3") return true;

        // Lit la signature dans un bloc isolé pour fermer le handle AVANT File.Move
        string sig;
        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var br = new BinaryReader(fs))
        {
            byte[] buf = new byte[30];
            br.Read(buf, 0, Math.Min(30, (int)Math.Min(fs.Length, 30L)));
            sig = System.Text.Encoding.UTF8.GetString(buf).ToLower();
        }
        // Ici le FileStream est fermé — File.Move peut s'exécuter sans "used by another process"

        // PK = ZIP
        if (sig.StartsWith("pk") && ext is not (".mod" or ".apk" or ".ipa"))
        {
            if (ext is not (".zip" or ".rar" or ".7z" or ".lha" or ".lzh" or ".gz" or ".tar"))
            {
                string newPath = filePath + ".zip";
                File.Move(filePath, newPath); filePath = newPath;
            }
        }
        // LHA
        if (sig.Contains("-lh5-") || sig.Contains("-lh0"))
        {
            if (ext is not (".lzh" or ".lha"))
            { File.Move(filePath, filePath + ".lha"); filePath += ".lha"; }
        }
        // RAR
        if (sig.StartsWith("rar!") && ext != ".rar")
        { File.Move(filePath, filePath + ".rar"); filePath += ".rar"; }

        return true;
    }

    private static bool ExtractFile(string sourceArchive, string dest)
    {
        string ext = Path.GetExtension(sourceArchive).ToLower();

        // ZIP → SharpZipLib en priorité, fallback SevenZipExtractor si ça échoue
        // (ex: Imploding method=6 que SharpZipLib liste mais ne décompresse pas)
        if (ext == ".zip")
            return ExtractZip(sourceArchive, dest) || ExtractZipSevenZip(sourceArchive, dest);

        bool   isLHA            = false;
        string savedSourcePath  = sourceArchive;
        ArchiveFile? archive    = null;

        // LHA → renomme en .lzh (SevenZip le reconnaît mieux)
        if (ext == ".lha")
        {
            isLHA = true;
            string lzhPath = Path.ChangeExtension(sourceArchive, ".lzh");
            File.Move(sourceArchive, lzhPath, true);
            sourceArchive = lzhPath;
        }

        try
        {
            archive = new ArchiveFile(sourceArchive);

            foreach (SevenZipExtractor.Entry entry in archive.Entries)
            {
                // Collision dossier/fichier
                if (entry.IsFolder && File.Exists(Path.Combine(dest, entry.FileName)))
                    File.Move(Path.Combine(dest, entry.FileName),
                              Path.Combine(dest, entry.FileName + "_"), true);

                string? name = CheckForInvalidWindowsName(entry.FileName);

                // Fallback pour .gz sans nom lisible
                if (name is null && Path.GetExtension(sourceArchive).ToLower() == ".gz")
                {
                    name = Path.GetFileNameWithoutExtension(sourceArchive);
                    int dashIdx = name.LastIndexOf('-');
                    if (dashIdx >= 0) name = name[(dashIdx + 1)..];
                }

                if (name is null || name == "." || string.IsNullOrWhiteSpace(name) || GotEmptyFolder(name))
                    continue;

                // Filtre les noms corrompus (LHA malformé) : trop de caractères non-ASCII
                // Un vrai nom de fichier ne devrait pas avoir >30% de chars non-printable
                int nonPrint = name.Count(c => c < 32 || (c > 126 && c < 160));
                if (nonPrint > name.Length * 0.3) continue;

                // Protection contre le path traversal (..\ dans les noms LHA/LZX)
                // Normalise le chemin et vérifie qu'il reste bien sous dest\
                string destPath = Path.GetFullPath(Path.Combine(dest, name.Replace(" \\", "\\")));
                if (!destPath.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && !destPath.Equals(Path.GetFullPath(dest), StringComparison.OrdinalIgnoreCase))
                {
                    // Chemin hors de dest → utilise uniquement le nom de fichier (sans chemin)
                    string safeName = Path.GetFileName(name);
                    if (string.IsNullOrWhiteSpace(safeName)) continue;
                    destPath = Path.Combine(dest, safeName);
                }

                if (!entry.IsFolder && Directory.Exists(destPath)) destPath += "_";

                if (entry.IsFolder)
                {
                    Directory.CreateDirectory(ToLongPath(destPath));
                }
                else
                {
                    try
                    {
                        // Créer le dossier parent si nécessaire
                        string? parentDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(parentDir))
                            Directory.CreateDirectory(ToLongPath(parentDir));

                        entry.Extract(ToLongPath(destPath));

                        // Vérifie uniquement la taille
                        var fi = new FileInfo(ToLongPath(destPath));
                        if (fi.Length != (long)entry.Size)
                            throw new SevenZipException("Unable to open archive");
                    }
                    catch
                    {
                        if (File.Exists(ToLongPath(destPath))) try { File.Delete(ToLongPath(destPath)); } catch { }
                    }
                }
            }

            return true;
        }
        catch (SevenZipException)
        {
            return false; // Archive corrompue — le appelant gérera
        }
        catch (AccessViolationException)
        {
            return false; // Crash mémoire dans la DLL 7-zip native — archive corrompue
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("ExtractFile error: " + ex.Message);
            return false;
        }
        finally
        {
            archive?.Dispose();
            // Restitue le .lha original
            if (isLHA && File.Exists(sourceArchive))
                File.Move(sourceArchive, savedSourcePath, true);
        }
    }

    private static bool ExtractZip(string sourceArchive, string dest)
    {
        try
        {
            // Vérifie si le ZIP contient des flags 0x2000 (faux flag encryption) avant de charger
            // tout le fichier en mémoire — évite les corruptions sur les gros ZIPs (>100 Mo).
            bool needsPatch = ZipHasFalseEncryptionFlag(sourceArchive);

            Stream zipStream;
            byte[]? zipBytes = null;
            if (needsPatch)
            {
                zipBytes = File.ReadAllBytes(sourceArchive);
                StripFalseEncryptionFlag(zipBytes);
                zipStream = new MemoryStream(zipBytes);
            }
            else
            {
                zipStream = File.OpenRead(sourceArchive);
            }

            using var zip = new ZipFile(zipStream);
            string destFull = Path.GetFullPath(dest);
            int extracted   = 0;

            foreach (ZipEntry entry in zip)
            {
                if (!entry.IsFile) continue;

                // Protection path traversal
                string entryPath = Path.GetFullPath(Path.Combine(dest,
                    entry.Name.Replace('/', Path.DirectorySeparatorChar)));
                if (!entryPath.StartsWith(destFull + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase))
                {
                    string safe = Path.GetFileName(entry.Name);
                    if (string.IsNullOrWhiteSpace(safe)) continue;
                    entryPath = Path.Combine(dest, safe);
                }

                Directory.CreateDirectory(ToLongPath(Path.GetDirectoryName(entryPath)!));
                try
                {
                    using var entryStream = zip.GetInputStream(entry);
                    using var outStream   = File.Create(ToLongPath(entryPath));
                    entryStream.CopyTo(outStream);
                }
                catch (Exception ex)
                {
                    // Erreur de décompression → fichier invalide
                    var extLog = new Logger(Path.Combine(AppContext.BaseDirectory, "log_extract_errors.txt"));
                    extLog.Error($"ZIP partiel {Path.GetFileName(sourceArchive)} — {entry.Name}: {ex.Message}");
                    if (File.Exists(entryPath)) try { File.Delete(entryPath); } catch { }
                    continue;
                }

                // Vérification CRC après extraction — SharpZipLib ne vérifie pas pour les Bad CRC
                if (entry.Crc >= 0 && File.Exists(entryPath))
                {
                    uint actualCrc = ComputeCrc32File(entryPath);
                    uint expectedCrc = (uint)entry.Crc;
                    if (actualCrc != expectedCrc)
                    {
                        var extLog = new Logger(Path.Combine(AppContext.BaseDirectory, "log_extract_errors.txt"));
                        extLog.Error($"ZIP CRC mismatch {Path.GetFileName(sourceArchive)} — {entry.Name}: expected={expectedCrc:X8} actual={actualCrc:X8}");
                        try { File.Delete(entryPath); } catch { }
                        continue;
                    }
                }
                extracted++;
            }
            return extracted > 0;  // succès si au moins 1 fichier extrait
        }
        catch { return false; }
    }

    private static bool ExtractZipSevenZip(string sourceArchive, string dest)
    {
        try
        {
            // Lire les CRC depuis les headers ZIP (fiable, indépendant de SevenZipExtractor)
            var headerCrcs = ReadZipHeaderCrcs(sourceArchive);

            using var archive = new ArchiveFile(sourceArchive);
            string destFull   = Path.GetFullPath(dest);
            int extracted     = 0;

            foreach (var entry in archive.Entries)
            {
                if (entry.IsFolder) continue;

                string? name = CheckForInvalidWindowsName(entry.FileName);
                if (name is null || name == "." || string.IsNullOrWhiteSpace(name)) continue;

                string entryPath = Path.GetFullPath(Path.Combine(dest,
                    name.Replace('/', Path.DirectorySeparatorChar)));
                if (!entryPath.StartsWith(destFull + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase))
                    entryPath = Path.Combine(dest, Path.GetFileName(name));

                Directory.CreateDirectory(ToLongPath(Path.GetDirectoryName(entryPath)!));
                try
                {
                    using var outStream = File.Create(ToLongPath(entryPath));
                    entry.Extract(outStream);
                }
                catch
                {
                    if (File.Exists(entryPath)) try { File.Delete(entryPath); } catch { }
                    continue;
                }

                // Vérification CRC via le header ZIP — entry.CRC peut ne pas être fiable
                string normName = entry.FileName.Replace('\\', '/');
                if (headerCrcs.TryGetValue(normName, out uint expectedCrc) && expectedCrc != 0
                    && File.Exists(entryPath))
                {
                    uint actualCrc = ComputeCrc32File(entryPath);
                    if (actualCrc != expectedCrc)
                    {
                        var extLog = new Logger(Path.Combine(AppContext.BaseDirectory, "log_extract_errors.txt"));
                        extLog.Error($"ZIP(7z) CRC mismatch {Path.GetFileName(sourceArchive)} — {entry.FileName}: expected={expectedCrc:X8} actual={actualCrc:X8}");
                        try { File.Delete(entryPath); } catch { }
                        continue;
                    }
                }
                extracted++;
            }
            return extracted > 0;
        }
        catch { return false; }
    }

    private static Dictionary<string, uint> ReadZipHeaderCrcs(string zipPath)
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var fs = File.OpenRead(zipPath);
            byte[] buf = new byte[65536];
            int carry = 0;
            while (true)
            {
                int n = fs.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                int len = carry + n;
                for (int i = 0; i < len - 30; i++)
                {
                    if (buf[i] == 0x50 && buf[i+1] == 0x4B && buf[i+2] == 0x03 && buf[i+3] == 0x04)
                    {
                        uint crc      = (uint)(buf[i+14] | (buf[i+15]<<8) | (buf[i+16]<<16) | (buf[i+17]<<24));
                        int  fnLen    = buf[i+26] | (buf[i+27]<<8);
                        int  extraLen = buf[i+28] | (buf[i+29]<<8);
                        if (i + 30 + fnLen <= len)
                        {
                            string fname = System.Text.Encoding.UTF8
                                .GetString(buf, i + 30, fnLen).Replace('\\', '/');
                            result[fname] = crc;
                            i += 30 + fnLen + extraLen - 1; // sauter l'entrée
                        }
                    }
                }
                carry = Math.Min(30, len);
                Array.Copy(buf, len - carry, buf, 0, carry);
            }
        }
        catch { }
        return result;
    }

    private static bool IsZipTruncated(string zipPath)
    {
        try
        {
            long fileSize = new FileInfo(zipPath).Length;
            long declaredCompressedTotal = 0;
            using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(zipPath);
            foreach (ICSharpCode.SharpZipLib.Zip.ZipEntry entry in zip)
            {
                if (!entry.IsFile) continue;
                declaredCompressedTotal += entry.CompressedSize;
                if (declaredCompressedTotal > fileSize) return true;
            }
            return false;
        }
        catch { return false; }
    }

    private static bool ZipNeedsRepack(string zipPath)
    {
        try
        {
            // Lecture en streaming pour éviter de charger tout le fichier en mémoire
            using var fs = File.OpenRead(zipPath);
            const int bufSize = 65536;
            byte[] buf = new byte[bufSize];
            byte[] carry = new byte[10]; // chevauchement entre blocs
            int carryLen = 0;

            int n;
            while ((n = fs.Read(buf, 0, bufSize)) > 0)
            {
                // Combiner le carry avec le nouveau bloc
                byte[] block = new byte[carryLen + n];
                Array.Copy(carry, 0, block, 0, carryLen);
                Array.Copy(buf, 0, block, carryLen, n);

                for (int i = 0; i < block.Length - 9; i++)
                {
                    if (block[i] == 0x50 && block[i+1] == 0x4B &&
                        block[i+2] == 0x03 && block[i+3] == 0x04)
                    {
                        if (i + 10 > block.Length) break;
                        int method = block[i+8] | (block[i+9] << 8);
                        if (method != 0 && method != 8) return true;
                    }
                }

                // Garder les 10 derniers octets pour le prochain bloc
                carryLen = Math.Min(10, block.Length);
                Array.Copy(block, block.Length - carryLen, carry, 0, carryLen);
            }
            return false;
        }
        catch { return false; }
    }

    private static bool ZipHasFalseEncryptionFlag(string zipPath)
    {
        try
        {
            using var fs = File.OpenRead(zipPath);
            byte[] buf = new byte[65536];
            int carry = 0;
            const long maxScan = 10L * 1024 * 1024;
            long totalRead = 0;

            while (totalRead < maxScan)
            {
                int n = fs.Read(buf, carry, buf.Length - carry);
                if (n <= 0) break;
                int len = carry + n;
                totalRead += n;

                for (int i = 0; i < len - 10; i++)
                {
                    if (buf[i] == 0x50 && buf[i+1] == 0x4B && buf[i+2] == 0x03 && buf[i+3] == 0x04)
                    {
                        int flags = buf[i+6] | (buf[i+7] << 8);
                        if ((flags & 0x0001) == 0 && (flags & 0x2000) != 0)
                            return true;
                    }
                }
                carry = Math.Min(3, len);
                Array.Copy(buf, len - carry, buf, 0, carry);
            }
            return false;
        }
        catch { return false; }
    }

    private static void StripFalseEncryptionFlag(byte[] data)
    {
        // Local file headers : signature PK\x03\x04, flags à offset +6
        int pos = 0;
        while (true)
        {
            int idx = IndexOf(data, [0x50, 0x4B, 0x03, 0x04], pos);
            if (idx < 0 || idx + 8 > data.Length) break;
            int flagOffset = idx + 6;
            ushort flags = (ushort)(data[flagOffset] | (data[flagOffset + 1] << 8));
            // Ne touche que les entrées non chiffrées (bit 0 = 0) ayant le faux bit 0x2000
            if ((flags & 0x0001) == 0 && (flags & 0x2000) != 0)
            {
                flags &= unchecked((ushort)~0x2000);
                data[flagOffset]     = (byte)(flags & 0xFF);
                data[flagOffset + 1] = (byte)(flags >> 8);
            }
            pos = idx + 4;
        }
        // Central directory headers : signature PK\x01\x02, flags à offset +8
        pos = 0;
        while (true)
        {
            int idx = IndexOf(data, [0x50, 0x4B, 0x01, 0x02], pos);
            if (idx < 0 || idx + 10 > data.Length) break;
            int flagOffset = idx + 8;
            ushort flags = (ushort)(data[flagOffset] | (data[flagOffset + 1] << 8));
            if ((flags & 0x0001) == 0 && (flags & 0x2000) != 0)
            {
                flags &= unchecked((ushort)~0x2000);
                data[flagOffset]     = (byte)(flags & 0xFF);
                data[flagOffset + 1] = (byte)(flags >> 8);
            }
            pos = idx + 4;
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle, int start)
    {
        int limit = haystack.Length - needle.Length;
        for (int i = start; i <= limit; i++)
        {
            bool found = true;
            for (int j = 0; j < needle.Length; j++)
                if (haystack[i + j] != needle[j]) { found = false; break; }
            if (found) return i;
        }
        return -1;
    }

    private static string ToLongPath(string path)
    {
        if (path.Length <= 240) return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        // UNC path : \\server\share → \\?\UNC\server\share
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    private static string? CheckForInvalidWindowsName(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // Tronque au premier \x00 — certains LHA stockent des données après le null terminator
        int nullPos = name.IndexOf('\0');
        if (nullPos >= 0) name = name[..nullPos];

        if (string.IsNullOrWhiteSpace(name)) return null;

        // Remplace les caractères invalides Windows dans le chemin complet
        var invalidPath = Path.GetInvalidPathChars();
        foreach (char c in invalidPath)
            name = name.Replace(c, '_');

        // Vérifie chaque composant du chemin
        char sep = Path.DirectorySeparatorChar;
        string[] parts = name.Split(sep, StringSplitOptions.RemoveEmptyEntries);
        var validParts = new System.Collections.Generic.List<string>();
        var invalidFile = Path.GetInvalidFileNameChars().Except(new[] { sep, '/', ':' }).ToArray();

        foreach (string part in parts)
        {
            string p = part.TrimEnd(' ', '.'); // Windows interdit les espaces/points en fin de composant
            if (string.IsNullOrWhiteSpace(p)) continue;
            foreach (char c in invalidFile)
                p = p.Replace(c, '_');
            if (p.Length > 200) p = p[..200]; // composant trop long → tronquer
            validParts.Add(p);
        }

        if (validParts.Count == 0) return null;

        // Si le chemin complet est trop long (>220 chars) → aplatir : ne garder que le dernier composant
        string result = string.Join(sep.ToString(), validParts);
        if (result.Length > 220)
            result = validParts.Last();

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static bool GotEmptyFolder(string name)
        => name.Trim() == string.Empty || name == "." || name == "..";

    private static uint ComputeCrc32File(string filePath)
    {
        uint crc = 0xFFFFFFFF;
        using var fs = File.OpenRead(filePath);
        byte[] buf = new byte[65536];
        int n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
            for (int i = 0; i < n; i++)
                crc = Crc32Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static bool ExtractTarGz(string filePath, string dest)
    {
        int extracted = 0;

        // Vérifie le magic gzip (1f 8b) — certains fichiers .tar.gz sont en réalité
        // des tar non compressés mal nommés. Dans ce cas on tente un TarInputStream direct.
        bool isGzip = false;
        using (var probe = File.OpenRead(filePath))
            isGzip = probe.ReadByte() == 0x1F && probe.ReadByte() == 0x8B;

        Stream tarStream;
        Stream? gzStream = null;
        try
        {
            var fs = File.OpenRead(filePath);
            if (isGzip)
            {
                gzStream  = new GZipInputStream(fs);
                tarStream = new TarInputStream(gzStream!, System.Text.Encoding.UTF8);
            }
            else
            {
                tarStream = new TarInputStream(fs, System.Text.Encoding.UTF8);
            }

            var tar = (TarInputStream)tarStream;
            TarEntry? entry;
            while (true)
            {
                try { entry = tar.GetNextEntry(); }
                catch { break; }

                if (entry is null) break;

                // Protection path traversal : normalise et vérifie que le chemin reste sous dest
                string rawPath   = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                string entryPath = Path.GetFullPath(Path.Combine(dest, rawPath));
                if (!entryPath.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase))
                {
                    string safeName = Path.GetFileName(rawPath);
                    if (string.IsNullOrWhiteSpace(safeName)) continue;
                    entryPath = Path.Combine(dest, safeName);
                }

                Directory.CreateDirectory(ToLongPath(Path.GetDirectoryName(entryPath)!));
                try
                {
                    using var outFs = File.Create(ToLongPath(entryPath));
                    tar.CopyEntryContents(outFs);
                    extracted++;
                }
                catch { /* fichier tronqué → on garde ce qui a été écrit */ }
            }
            tarStream.Dispose();
            gzStream?.Dispose();
            fs.Dispose();
        }
        catch { /* stream corrompu → on retourne true si on a extrait quelque chose */ }

        return extracted > 0;
    }

    private static bool ExtractTarBz2(string filePath, string dest)
    {
        int extracted = 0;

        // Détecter si c'est vraiment du bzip2 (magic BZ) ou un tar nu mal nommé
        bool isBz2;
        using (var probe = File.OpenRead(filePath))
            isBz2 = probe.ReadByte() == 'B' && probe.ReadByte() == 'Z';

        try
        {
            var srcFs  = File.OpenRead(filePath);
            Stream tarInput = isBz2 ? (Stream)new BZip2InputStream(srcFs) : srcFs;
            using var tar = new TarInputStream(tarInput, System.Text.Encoding.UTF8);

            TarEntry? entry;
            while (true)
            {
                try { entry = tar.GetNextEntry(); }
                catch { break; }

                if (entry is null) break;

                // Protection path traversal : normalise et vérifie que le chemin reste sous dest
                string rawPath   = entry.Name.Replace('/', Path.DirectorySeparatorChar);
                string entryPath = Path.GetFullPath(Path.Combine(dest, rawPath));
                if (!entryPath.StartsWith(Path.GetFullPath(dest) + Path.DirectorySeparatorChar,
                                          StringComparison.OrdinalIgnoreCase))
                {
                    // Chemin hors de dest → utilise seulement le nom de fichier
                    string safeName = Path.GetFileName(rawPath);
                    if (string.IsNullOrWhiteSpace(safeName)) continue;
                    entryPath = Path.Combine(dest, safeName);
                }

                if (entry.IsDirectory)
                {
                    Directory.CreateDirectory(ToLongPath(entryPath));
                    continue;
                }

                Directory.CreateDirectory(ToLongPath(Path.GetDirectoryName(entryPath)!));
                try
                {
                    using var fs = File.Create(ToLongPath(entryPath));
                    tar.CopyEntryContents(fs);
                    extracted++;
                }
                catch { }
            }
            tar.Dispose();
            tarInput.Dispose();
            srcFs.Dispose();
        }
        catch { }

        return extracted > 0;
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            foreach (var f in Directory.GetFiles(path, "*.*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(path, true);
        }
        catch { }
    }

    private static string DoCRC32(string filePath)
    {
        uint crc = 0xFFFFFFFF;
        using var fs  = new BufferedStream(File.OpenRead(filePath), 1 << 20); // 1 Mo buffer
        byte[] buf    = new byte[1 << 20]; // 1 Mo
        int    n;
        while ((n = fs.Read(buf, 0, buf.Length)) > 0)
        {
            for (int i = 0; i < n; i++)
                crc = Crc32Table[(crc ^ buf[i]) & 0xFF] ^ (crc >> 8);
        }
        return (~crc).ToString("X8").ToLower();
    }

    private static string DoMD5(string filePath)
    {
        using var md5 = MD5.Create();
        using var fs  = File.OpenRead(filePath);
        return Convert.ToHexString(md5.ComputeHash(fs)).ToLowerInvariant();
    }

    private static string DoSHA1(string filePath)
    {
        using var sha = SHA1.Create();
        using var fs  = File.OpenRead(filePath);
        return Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
    }

    private static uint[] BuildCrc32Table()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int j = 0; j < 8; j++) c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            t[i] = c;
        }
        return t;
    }

    // ── Champ CRC32 partagé ──────────────────────────────────────────────────
    private static readonly uint[] Crc32Table = BuildCrc32Table();
}
