using System;

namespace TrackerPlayer.Core.Decoders
{
    /// <summary>
    /// Décompresseur pour le format ICE! (alias "Pack-Ice", "Ice Packer"),
    /// un cruncher classique Atari ST écrit par "Axe of Superior".
    /// Très utilisé pour compresser les fichiers SNDH (musiques Atari ST)
    /// dans les archives de la scène demo.
    ///
    /// Port fidèle de l'algorithme de référence pack-ice par Lars Brinkhoff
    /// (https://github.com/larsbrinkhoff/pack-ice, fichier ice_decrunch.c),
    /// lui-même basé sur le decruncher original ICE v2.40 par Axe of Superior.
    ///
    /// FORMAT DE FICHIER :
    ///   Offset 0  (4 octets) : magic "ICE!" (v2.40+) ou "Ice!" (v2.35 et antérieures)
    ///   Offset 4  (4 octets) : longueur compressée, big-endian (= taille totale du fichier)
    ///   Offset 8  (4 octets) : longueur décompressée, big-endian
    ///   Offset 12...        : données compressées
    ///
    /// ALGORITHME :
    ///   LZ-like propriétaire. Le flux de bits est lu depuis la FIN du buffer
    ///   vers le DÉBUT (le dernier octet du fichier contient le premier
    ///   registre de bits). La sortie est également écrite à l'envers
    ///   (du dernier octet du buffer destination vers le premier).
    /// </summary>
    public static class IceDecruncher
    {
        /// <summary>Vérifie si les données commencent par la signature ICE! ou Ice!.</summary>
        public static bool IsIceData(byte[] data) => HasIceMagic(data);

        // Détection de signature : on accepte exactement "ICE!" ou "Ice!"
        // (les deux variantes connues du format, cf. justsolve.archiveteam.org/wiki/Pack-Ice).
        private static bool HasIceMagic(byte[] data)
        {
            if (data.Length < 12) return false;
            bool isUpper = data[0] == (byte)'I' && data[1] == (byte)'C' && data[2] == (byte)'E' && data[3] == (byte)'!';
            bool isMixed = data[0] == (byte)'I' && data[1] == (byte)'c' && data[2] == (byte)'e' && data[3] == (byte)'!';
            return isUpper || isMixed;
        }

        private static uint ReadUInt32BigEndian(byte[] data, int offset)
        {
            return ((uint)data[offset] << 24)
                 | ((uint)data[offset + 1] << 16)
                 | ((uint)data[offset + 2] << 8)
                 | data[offset + 3];
        }

        /// <summary>Longueur totale du bloc compressé (= taille du fichier ICE! complet, en-tête inclus).</summary>
        public static int GetCrunchedLength(byte[] data)
        {
            if (!HasIceMagic(data)) return 0;
            return (int)ReadUInt32BigEndian(data, 4);
        }

        /// <summary>Longueur du résultat une fois décompressé.</summary>
        public static int GetDecrunchedLength(byte[] data)
        {
            if (!HasIceMagic(data)) return 0;
            return (int)ReadUInt32BigEndian(data, 8);
        }

        /// <summary>
        /// Décompresse un buffer ICE!. Retourne null si les données ne sont pas
        /// reconnues comme un fichier ICE! valide.
        /// </summary>
        public static byte[]? Decrunch(byte[] data)
        {
            if (!HasIceMagic(data)) return null;

            int unpackedLength = GetDecrunchedLength(data);
            int crunchedLength = GetCrunchedLength(data);
            if (unpackedLength <= 0 || crunchedLength <= 0 || crunchedLength > data.Length)
                return null;

            var dest = new byte[unpackedLength];
            var state = new State
            {
                UnpackedStop = 0,
                Packed       = crunchedLength,   // pointeur juste après la fin du bloc compressé
            };

            // bits = *--packed  (dernier octet du bloc compressé)
            state.Packed -= 1;
            state.Bits    = data[state.Packed];
            state.Unpacked = unpackedLength;

            NormalBytes(data, dest, state);

            return dest;
        }

        private sealed class State
        {
            public int UnpackedStop;
            public int Unpacked;
            public int Packed;
            public int Bits;
        }

        private static int GetBit(byte[] data, State state)
        {
            int bit = (state.Bits & 0x80) != 0 ? 1 : 0;
            state.Bits = (state.Bits << 1) & 0xff;
            if (state.Bits == 0)
            {
                state.Packed -= 1;
                state.Bits = data[state.Packed];
                bit = (state.Bits & 0x80) != 0 ? 1 : 0;
                state.Bits = ((state.Bits << 1) & 0xff) + 1;
            }
            return bit;
        }

        private static int GetBits(byte[] data, State state, int n)
        {
            int bits = 0;
            while (n-- > 0)
                bits = (bits << 1) | GetBit(data, state);
            return bits;
        }

        private static readonly int[] DepackLengthBitsToGet   = { 0, 0, 1, 2, 10 };
        private static readonly int[] DepackLengthNumberToAdd = { 2, 3, 4, 6, 10 };

        private static int GetDepackLength(byte[] data, State state)
        {
            int i;
            for (i = 0; i < 4; i++)
            {
                if (GetBit(data, state) == 0) break;
            }
            // i vaut 4 si la boucle s'est terminée sans "break" (4 bits à 1 consécutifs)
            int bits = DepackLengthBitsToGet[i];
            int length = bits > 0 ? GetBits(data, state, bits) : 0;
            length += DepackLengthNumberToAdd[i];
            return length;
        }

        private static readonly int[] DepackOffsetBitsToGet   = { 8, 5, 12 };
        private static readonly int[] DepackOffsetNumberToAdd = { 31, -1, 287 };

        private static int GetDepackOffset(byte[] data, State state, int length)
        {
            int offset;
            if (length == 2)
            {
                int bits, add;
                if (GetBit(data, state) != 0)
                {
                    bits = 9;
                    add  = 0x3f;
                }
                else
                {
                    bits = 6;
                    add  = -1;
                }
                offset = GetBits(data, state, bits) + add;
            }
            else
            {
                int i;
                for (i = 0; i < 2; i++)
                {
                    if (GetBit(data, state) == 0) break;
                }
                int bits = DepackOffsetBitsToGet[i];
                int add  = DepackOffsetNumberToAdd[i];
                offset = GetBits(data, state, bits) + add;
                if (offset < 0)
                    offset -= (length - 2);
            }
            return offset;
        }

        private static readonly int[] DirectLengthBitsToGet   = { 1, 2, 2, 3, 8, 15 };
        private static readonly int[] DirectLengthAllOnes     = { 1, 3, 3, 7, 0xff, 0x7fff };
        private static readonly int[] DirectLengthNumberToAdd = { 1, 2, 5, 8, 15, 270, 270 };

        private static int GetDirectLength(byte[] data, State state)
        {
            int i = 0;
            int n = 0;
            for (i = 0; i < 6; i++)
            {
                n = GetBits(data, state, DirectLengthBitsToGet[i]);
                if (n != DirectLengthAllOnes[i]) break;
            }
            n += DirectLengthNumberToAdd[i];
            return n;
        }

        /// <summary>
        /// Copie n octets en arrière dans le même buffer (équivalent de memcpybwd
        /// du code C original) : dest[toIndex..] = dest[fromIndex..], copié de la
        /// fin vers le début pour gérer correctement les zones qui se chevauchent
        /// (comportement attendu par l'algorithme LZ : fromIndex peut être > toIndex
        /// avec chevauchement, ce qui crée un pattern répétitif).
        /// </summary>
        private static void MemcpyBackward(byte[] dest, int toIndex, int fromIndex, int n)
        {
            toIndex   += n;
            fromIndex += n;
            while (n-- > 0)
            {
                toIndex--;
                fromIndex--;
                dest[toIndex] = dest[fromIndex];
            }
        }

        private static void NormalBytes(byte[] data, byte[] dest, State state)
        {
            while (true)
            {
                if (GetBit(data, state) != 0)
                {
                    int length = GetDirectLength(data, state);
                    state.Packed   -= length;
                    state.Unpacked -= length;

                    if (state.Unpacked < state.UnpackedStop)
                        throw new InvalidOperationException(
                            "IceDecruncher: dépassement de buffer (copie directe) — fichier ICE! corrompu ou non supporté.");

                    Array.Copy(data, state.Packed, dest, state.Unpacked, length);
                }

                if (state.Unpacked > state.UnpackedStop)
                {
                    int length = GetDepackLength(data, state);
                    int offset = GetDepackOffset(data, state, length);

                    state.Unpacked -= length;
                    if (state.Unpacked < state.UnpackedStop)
                        throw new InvalidOperationException(
                            "IceDecruncher: dépassement de buffer (copie LZ) — fichier ICE! corrompu ou non supporté.");

                    MemcpyBackward(dest, state.Unpacked, state.Unpacked + length + offset, length);
                }
                else
                {
                    return;
                }
            }
        }
    }
}
