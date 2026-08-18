using System;
﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DemosceneDownloader.Services.DMS
{
    internal class Medium
    {
        private static readonly ushort MBITMASK = 0x3fff;
        public static ushort medium_text_loc;

        public static ushort Unpack_MEDIUM(byte[] input, byte[] output, ushort origsize)
        {
            GetBits getBits = new GetBits();

            ushort i, j, c;
            byte u;
            int outputPtr = 0;

            getBits.InitBitBuf(input);

            while (outputPtr < origsize) {
                if (getBits.GETBITS(1) != 0)
                {
                    getBits.DROPBITS(1);
                    output[outputPtr++] = Tables.text[medium_text_loc++ & MBITMASK] = (byte)getBits.GETBITS(8);
                    getBits.DROPBITS(8);
                }
                else
                {
                    getBits.DROPBITS(1);
                    c = getBits.GETBITS(8); getBits.DROPBITS(8);
                    j = (ushort)(Tables.d_code[c] + 3);
                    u = Tables.d_len[c];
                    c = (ushort)(((c << u) | getBits.GETBITS(u)) & 0xff); getBits.DROPBITS(u);
                    u = Tables.d_len[c];
                    c = (ushort)((Tables.d_code[c] << 8) | (((c << u) | getBits.GETBITS(u)) & 0xff)); getBits.DROPBITS(u);
                    i = (ushort)(medium_text_loc - c - 1);

                    while (j--!=0) output[outputPtr++] = Tables.text[medium_text_loc++ & MBITMASK] = Tables.text[i++ & MBITMASK];
                }
            }
            medium_text_loc = (ushort)((medium_text_loc + 66) & MBITMASK);

            return 0;
        }
    }
}
