#pragma warning disable CS8618
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class Heavy
    {
        private static readonly int NC = 510;
        private static readonly int NPT = 20;
        private static readonly int N1 = 510;
        private static readonly int OFFSET = 253;

        private static readonly ushort[] left = new ushort[2 * NC - 1];
        private static readonly ushort[] right = new ushort[2 * NC - 1 + 9];
        private static readonly byte[] c_len = new byte[NC];
        private static readonly byte[] pt_len = new byte[NPT];

        private static readonly ushort[] c_table = new ushort[4096];
        private static readonly ushort[] pt_table = new ushort[256];
        private static ushort np;
        public static ushort heavy_text_loc;
        public static ushort heavy_lastlen;

        private static GetBits getBits = new GetBits();

        private static short c;
        private static ushort n, tblsiz, len, depth, maxdepth, avail;
        private static ushort codeword, bit, TabErr;
        private static byte[] blen;
        private static ushort[] tbl;

        public static ushort Unpack_HEAVY(byte[] input, byte[] output, byte flags, ushort origsize)
        {
            ushort j, i, c, bitmask;
            int outputPtr = 0;

            /*  Heavy 1 uses a 4Kb dictionary,  Heavy 2 uses 8Kb  */
            if ((flags & 8)!=0)
            {
                np = 15;
                bitmask = 0x1fff;
            }
            else
            {
                np = 14;
                bitmask = 0x0fff;
            }

            getBits.InitBitBuf(input);

            if ((flags & 2)!=0)
            {
                if (read_tree_c()!=0) return 1;
                if (read_tree_p()!=0) return 2;
            }

            int outendPtr = origsize;

            while (outputPtr < outendPtr) 
            {
                c = decode_c();
                if (c < 256)
                {
                    output[outputPtr++] = Tables.text[heavy_text_loc++ & bitmask] = (byte)c;
                }
                else
                {
                    j = (ushort)(c - OFFSET);
                    i = (ushort)(heavy_text_loc - decode_p() - 1);
                    while (j--!=0) output[outputPtr++] = Tables.text[heavy_text_loc++ & bitmask] = Tables.text[i++ & bitmask];
                }
            }

            return 0;
        }

        private static ushort decode_c()
        {
            ushort i, j, m;

            j = c_table[getBits.GETBITS(12)];
            if (j < N1)
            {
                getBits.DROPBITS(c_len[j]);
            }
            else
            {
                getBits.DROPBITS(12);
                i = getBits.GETBITS(16);
                m = 0x8000;
                do
                {
                    if ((i & m)!=0) j = right[j];
                    else j = left[j];
                    m >>= 1;
                } while (j >= N1);
                getBits.DROPBITS(c_len[j] - 12);
            }
            return j;
        }

        private static ushort decode_p()
        {
            ushort i, j, m;

            j = pt_table[getBits.GETBITS(8)];
            if (j < np)
            {
                getBits.DROPBITS(pt_len[j]);
            }
            else
            {
                getBits.DROPBITS(8);
                i = getBits.GETBITS(16);
                m = 0x8000;
                do
                {
                    if ((i & m)!=0) j = right[j];
                    else j = left[j];
                    m >>= 1;
                } while (j >= np);
                getBits.DROPBITS(pt_len[j] - 8);
            }

            if (j != np - 1)
            {
                if (j > 0)
                {
                    j = (ushort)(getBits.GETBITS(i = (ushort)(j - 1)) | (1U << (j - 1)));
                    getBits.DROPBITS(i);
                }
                heavy_lastlen = j;
            }

            return heavy_lastlen;

        }

        private static ushort read_tree_c()
        {
            ushort i, n;

            n = getBits.GETBITS(9);
            getBits.DROPBITS(9);
            if (n > 0)
            {
                for (i = 0; i < n; i++)
                {
                    c_len[i] = (byte)getBits.GETBITS(5);
                    getBits.DROPBITS(5);
                }
                for (i = n; i < 510; i++) c_len[i] = 0;
                if (make_table(510, c_len, 12, c_table) != 0) return 1;
            }
            else
            {
                n = getBits.GETBITS(9);
                getBits.DROPBITS(9);
                for (i = 0; i < 510; i++) c_len[i] = 0;
                for (i = 0; i < 4096; i++) c_table[i] = n;
            }
            return 0;
        }

        private static ushort read_tree_p()
        {
            ushort i, n;

            n = getBits.GETBITS(5);
            getBits.DROPBITS(5);
            if (n > 0)
            {
                for (i = 0; i < n; i++)
                {
                    pt_len[i] = (byte)getBits.GETBITS(4);
                    getBits.DROPBITS(4);
                }
                for (i = n; i < np; i++) pt_len[i] = 0;
                if (make_table(np, pt_len, 8, pt_table)!=0) return 1;
            }
            else
            {
                n = getBits.GETBITS(5);
                getBits.DROPBITS(5);
                for (i = 0; i < np; i++) pt_len[i] = 0;
                for (i = 0; i < 256; i++) pt_table[i] = n;
            }
            return 0;
        }

        private static ushort make_table(ushort nchar, byte[] bitlen, ushort tablebits, ushort[] table)
        {
            n = avail = nchar;
            blen = bitlen;
            tbl = table;
            tblsiz = (ushort)(1U << tablebits);
            bit = (ushort)(tblsiz / 2);
            maxdepth = (ushort)(tablebits + 1);
            depth = len = 1;
            c = -1;
            codeword = 0;
            TabErr = 0;
            mktbl();    /* left subtree */
            if (TabErr!=0) return TabErr;
            mktbl();    /* right subtree */
            if (TabErr!=0) return TabErr;
            if (codeword != tblsiz) return 5;
            return 0;
        }

        private static ushort mktbl()
        {
            ushort i = 0;

            if (TabErr!=0) return 0;

            if (len == depth)
            {
                while (++c < n)
                    if (blen[c] == len)
                    {
                        i = codeword;
                        codeword += bit;
                        if (codeword > tblsiz)
                        {
                            TabErr = 1;
                            return 0;
                        }
                        while (i < codeword) tbl[i++] = (ushort)c;
                        return (ushort)c;
                    }
                c = -1;
                len++;
                bit >>= 1;
            }
            depth++;
            if (depth < maxdepth)
            {
                mktbl();
                mktbl();
            }
            else if (depth > 32)
            {
                TabErr = 2;
                return 0;
            }
            else
            {
                if ((i = avail++) >= 2 * n - 1)
                {
                    TabErr = 3;
                    return 0;
                }
                left[i] = mktbl();
                right[i] = mktbl();
                if (codeword >= tblsiz)
                {
                    TabErr = 4;
                    return 0;
                }
                if (depth == maxdepth) tbl[codeword++] = i;
            }
            depth--;
            return i;
        }
    }
}
