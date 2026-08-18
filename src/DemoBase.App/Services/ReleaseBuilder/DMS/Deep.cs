using System;
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class Deep
    {
        private static readonly ushort DBITMASK = 0x3fff;
        private static readonly ushort F = 60;
        private static readonly ushort THRESOLD = 2;
        private static readonly ushort N_CHAR = (ushort)(256 - THRESOLD + F);
        private static readonly ushort T = (ushort)(N_CHAR * 2 - 1);
        private static readonly ushort R = (ushort)(T - 1);
        private static readonly ushort MAX_FREQ = 0x8000;

        private static readonly ushort[] freq = new ushort[T + 1];
        private static readonly ushort[] prnt = new ushort[T + N_CHAR];
        private static readonly ushort[] son = new ushort[T];

        public static int init_deep_tabs;
        public static int deep_text_loc;

        private static readonly GetBits getBits = new GetBits();
        private static void Init_DEEP_Tabs()
        {
            ushort i, j;

            for (i = 0; i < N_CHAR; i++)
            {
                freq[i] = 1;
                son[i] = (ushort)(i + T);
                prnt[i + T] = i;
            }
            i = 0; j = N_CHAR;
            while (j <= R)
            {
                freq[j] = (ushort)(freq[i] + freq[i + 1]);
                son[j] = i;
                prnt[i] = prnt[i + 1] = j;
                i += 2; j++;
            }
            freq[T] = 0xffff;
            prnt[R] = 0;

            init_deep_tabs = 0;
        }

        public static ushort Unpack_DEEP(byte[] input, byte[] output, ushort origsize)
        {
            ushort i, j, c;
            int outend;
            int outputPtr = 0;

            getBits.InitBitBuf(input);

            if (init_deep_tabs != 0) Init_DEEP_Tabs();

            outend = origsize;
            while (outputPtr < outend)
            {
                c = DecodeChar();
                if (c < 256)
                {
                    output[outputPtr++] = Tables.text[deep_text_loc++ & DBITMASK] = (byte)c;
                }
                else
                {
                    j = (ushort)(c - 255 + THRESOLD);
                    i = (ushort)(deep_text_loc - DecodePosition() - 1);
                    while (j-- != 0) output[outputPtr++] = Tables.text[deep_text_loc++ & DBITMASK] = Tables.text[i++ & DBITMASK];
                }
            }

            deep_text_loc = (ushort)(deep_text_loc + 60 & DBITMASK);

            return 0;
        }

        private static ushort DecodeChar()
        {
            ushort c;

            c = son[R];

            /* travel from root to leaf, */
            /* choosing the smaller child node (son[]) if the read bit is 0, */
            /* the bigger (son[]+1} if 1 */
            while (c < T)
            {
                c = son[c + getBits.GETBITS(1)];
                getBits.DROPBITS(1);
            }
            c -= T;
            update(c);
            return c;
        }

        private static ushort DecodePosition()
        {
            ushort i, j, c;

            i = getBits.GETBITS(8);
            getBits.DROPBITS(8);
            c = (ushort)(Tables.d_code[i] << 8);
            j = Tables.d_len[i];
            i = (ushort)(((i << j) | getBits.GETBITS(j)) & 0xff);
            getBits.DROPBITS(j);

            return (ushort)(c | i);
        }

        private static void reconst()
        {
            ushort i, j, k, f, l;

            /* collect leaf nodes in the first half of the table */
            /* and replace the freq by (freq + 1) / 2. */
            j = 0;
            for (i = 0; i < T; i++)
            {
                if (son[i] >= T)
                {
                    freq[j] = (ushort)((freq[i] + 1) / 2);
                    son[j] = son[i];
                    j++;
                }
            }
            /* begin constructing tree by connecting sons */
            for (i = 0, j = N_CHAR; j < T; i += 2, j++)
            {
                k = (ushort)(i + 1);
                f = freq[j] = (ushort)(freq[i] + freq[k]);
                for (k = (ushort)(j - 1); f < freq[k]; k--) ;
                k++;
                l = (ushort)((j - k) * 2);
                Array.Copy(freq, k, freq , k + 1, l);
                freq[k] = f;
                Array.Copy(son, k, son,  k + 1, l);
                son[k] = i;
            }
            /* connect prnt */
            for (i = 0; i < T; i++)
            {
                if ((k = son[i]) >= T)
                {
                    prnt[k] = i;
                }
                else
                {
                    prnt[k] = prnt[k + 1] = i;
                }
            }
        }

        private static void update(ushort c)
        {
            ushort i, j, k, l;

            if (freq[R] == MAX_FREQ)
            {
                reconst();
            }
            c = prnt[c + T];
            do
            {
                k = ++freq[c];

                /* if the order is disturbed, exchange nodes */
                if (k > freq[l = (ushort)(c + 1)])
                {
                    while (k > freq[++l]) ;
                    l--;
                    freq[c] = freq[l];
                    freq[l] = k;

                    i = son[c];
                    prnt[i] = l;
                    if (i < T) prnt[i + 1] = l;

                    j = son[l];
                    son[l] = i;

                    prnt[j] = c;
                    if (j < T) prnt[j + 1] = c;
                    son[c] = j;

                    c = l;
                }
            } while ((c = prnt[c]) != 0); /* repeat up to root */
        }
    }
}
