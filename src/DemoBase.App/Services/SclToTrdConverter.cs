using System;
using System.IO;
using System.Text;

namespace DemoBase.App.Services;

/// <summary>
/// Portage C# de scl2trd.c (Fuse — Dmitry Sanarin, Philip Kendall, Fredrick Meunier,
/// GPLv2+), fourni par l'utilisateur. Convertit une image disque .SCL en .TRD.
///
/// Pourquoi : ZEsarUX ne charge les disques TR-DOS que via --trd-file (.trd) ; le .scl
/// n'a pas de chemin de chargement direct en ligne de commande (doc officielle : "scl
/// files can be converted from file selector pressing space", donc seulement à la main
/// depuis le sélecteur interne). Cette classe automatise cette conversion avant chaque
/// lancement, pour que les releases distribuées en .scl fonctionnent aussi bien que
/// celles en .tap/.trd — cf. ZEsarUXLauncher.LaunchAsync.
///
/// Portage volontairement OCTET POUR OCTET fidèle à l'original, y compris son artefact
/// le plus étrange : le "compteur d'espace libre" est lu/écrit comme un entier 32 bits
/// (lsb2ui/ui2lsb) alors que le secteur système TR-DOS ne réserve que 2 octets (0x8E5,
/// 0x8E6) à cette valeur — les 2 octets suivants (0x8E7 = octet d'identification TR-DOS
/// 0x10, 0x8E8 = réservé 0x00) sont donc inclus dans la comparaison/décrémentation. Ça ne
/// change rien en pratique (aucune image ne s'approche de la limite qui ferait déborder
/// sur ces octets) mais on le garde tel quel plutôt que de "corriger" un comportement du
/// convertisseur de référence.
/// </summary>
public static class SclToTrdConverter
{
    private const int TRD_NAMEOFFSET    = 0x08F5;
    private const int TRD_DIRSTART      = 0x08E2;
    private const int TRD_DIRLEN        = 32;
    private const int TRD_MAXNAMELENGTH = 8;
    private const int BLOCKSIZE         = 10240;
    private const int TRD_TOTAL_SIZE    = BLOCKSIZE * 64; // 655 360 octets (80 pistes x 2 faces x 16 secteurs x 256 octets)

    // Offsets du secteur système TR-DOS (piste 0, secteur 8, qui commence à 0x800) :
    private const int OFF_FSEC  = 0x8E1; // premier secteur libre
    private const int OFF_FTRK  = 0x8E2; // première piste libre (= début du template ci-dessous)
    private const int OFF_FILES = 0x8E4; // nombre de fichiers sur le disque
    private const int OFF_FREE  = 0x8E5; // "espace libre" (voir remarque ci-dessus)

    /// <summary>
    /// Les TRD_DIRLEN (32) premiers octets du tableau template[34] original — les deux
    /// derniers octets du tableau C n'étaient de toute façon jamais copiés (memcpy limité
    /// à TRD_DIRLEN).
    /// </summary>
    private static readonly byte[] Template =
    {
        0x01, 0x16, 0x00, 0xF0,
        0x09, 0x10, 0x00, 0x00,
        0x20, 0x20, 0x20, 0x20,
        0x20, 0x20, 0x20, 0x20,
        0x20, 0x00, 0x00, 0x64,
        0x69, 0x73, 0x6B, 0x6E,
        0x61, 0x6D, 0x65, 0x00,
        0x00, 0x00, 0x46, 0x55,
    };

    /// <summary>
    /// Convertit <paramref name="sclPath"/> en <paramref name="trdPath"/>. Retourne
    /// (false, message) plutôt que de lever une exception — même convention que les
    /// autres services de DemoBase (ex. DbSetupDownloadService).
    /// </summary>
    public static (bool Success, string? Error) Convert(string sclPath, string trdPath)
    {
        try
        {
            if (!File.Exists(sclPath))
                return (false, $"Fichier SCL introuvable : {sclPath}");

            var scl = File.ReadAllBytes(sclPath);
            int pos = 0;

            byte[] ReadScl(int count)
            {
                if (count < 0 || pos + count > scl.Length)
                    throw new InvalidDataException("Fichier .scl tronqué ou invalide.");
                var buf = new byte[count];
                Array.Copy(scl, pos, buf, 0, count);
                pos += count;
                return buf;
            }

            // ── Signature "SINCLAIR" (8 octets, insensible à la casse) ──────────────
            var signature = Encoding.ASCII.GetString(ReadScl(8));
            if (!signature.Equals("SINCLAIR", StringComparison.OrdinalIgnoreCase))
                return (false, $"Signature .scl invalide : \"{signature}\" (attendu \"SINCLAIR\")");

            int blocks = ReadScl(1)[0];

            // ── En-têtes des fichiers (14 octets chacun) ─────────────────────────────
            var headers = new byte[blocks][];
            for (int x = 0; x < blocks; x++)
                headers[x] = ReadScl(14);

            // ── Image .trd vierge (655 360 octets à zéro) + secteur système ─────────
            var trd = new byte[TRD_TOTAL_SIZE];
            Array.Copy(Template, 0, trd, TRD_DIRSTART, TRD_DIRLEN);

            // Nom de disque "Fuse" (8 octets, complété de zéros) à la place du
            // texte "diskname" présent dans le template.
            Array.Clear(trd, TRD_NAMEOFFSET, TRD_MAXNAMELENGTH);
            var name = Encoding.ASCII.GetBytes("Fuse");
            Array.Copy(name, 0, trd, TRD_NAMEOFFSET, Math.Min(name.Length, TRD_MAXNAMELENGTH));

            // ── Copie de chaque fichier du .scl vers l'image .trd ───────────────────
            for (int x = 0; x < blocks; x++)
            {
                byte size = headers[x][13]; // nombre de secteurs (256 octets) du fichier
                uint free = BitConverter.ToUInt32(trd, OFF_FREE);

                if (free < size)
                    return (false, $"Image TRD pleine (espace insuffisant) à l'entrée {x}.");

                if (trd[OFF_FILES] > 127)
                    return (false, $"Image TRD pleine (128 fichiers maximum) à l'entrée {x}.");

                // Entrée de catalogue (16 octets) : en-tête .scl (14) + piste/secteur de
                // départ (2, recopiés depuis les pointeurs courants 0x8E1/0x8E2).
                int catalogOffset = trd[OFF_FILES] * 16;
                Array.Copy(headers[x], 0, trd, catalogOffset, 14);
                trd[catalogOffset + 0x0E] = trd[OFF_FSEC];
                trd[catalogOffset + 0x0F] = trd[OFF_FTRK];

                int left = size * 256;
                long fptr = trd[OFF_FTRK] * 4096L + trd[OFF_FSEC] * 256L;
                if (fptr + left > trd.Length)
                    return (false, $"Image TRD pleine (dépassement de la zone de données) à l'entrée {x}.");

                var data = ReadScl(left);
                Array.Copy(data, 0, trd, (int)fptr, left);

                trd[OFF_FILES]++;
                BitConverter.GetBytes(free - size).CopyTo(trd, OFF_FREE);

                // Avance le pointeur piste/secteur courant de `size` secteurs (16 par piste).
                while (size > 15) { trd[OFF_FTRK]++; size -= 16; }
                trd[OFF_FSEC] += size;
                while (trd[OFF_FSEC] > 15) { trd[OFF_FSEC] -= 16; trd[OFF_FTRK]++; }
            }

            var trdDir = Path.GetDirectoryName(trdPath);
            if (!string.IsNullOrEmpty(trdDir)) Directory.CreateDirectory(trdDir);

            File.WriteAllBytes(trdPath, trd);
            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, $"Erreur de conversion .scl→.trd : {ex.Message}");
        }
    }
}
